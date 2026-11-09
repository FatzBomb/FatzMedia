using System.Diagnostics;
using System.Runtime.InteropServices;
using FatzMedia.Video.Core.Abstractions;
using ManagedBass;
using MBass = ManagedBass.Bass;

namespace FatzMedia.Video.Core.Audio;

/// <summary>An audio engine whose BASS channel the output watchdog looks after.</summary>
interface IBassOutputClient
{
    /// <summary>The engine's play handle while it intends to be playing; 0 otherwise.</summary>
    int PlayingHandle { get; }

    /// <summary>
    /// Resumes the engine's channel at its position when the engine still intends to play and BASS
    /// reports the channel as paused by its device. Never touches a channel the host paused or
    /// stopped.
    /// </summary>
    /// <returns>False when the channel is still parked after the attempt.</returns>
    bool ResumeChannelPausedByDevice();
}

/// <summary>
/// Notices and recovers a lost BASS output (ENG-032): another application taking the device in
/// exclusive mode, or a USB device unplugged and plugged back. BASS then pauses the output and
/// its channels (<c>BASS_ACTIVE_PAUSED_DEVICE</c>) and nothing resumes them by itself.
/// </summary>
/// <remarks>
/// All players share the process-wide BASS devices, so this is one process-wide watchdog: a
/// single timer, running only while some engine is playing, samples each device once a second,
/// and <see cref="AudioOutputRecoveryTracker"/> decides what to do. The device is restarted once
/// per process (no thundering herd); each engine resumes only its own channel. This class is the
/// thin BASS layer over that pure unit. Platform neutral: BASS only, no OS calls.
/// <para>
/// Locks: <see cref="Gate"/> guards in-memory state only and is never held across a BASS call
/// that may wait for BASS threads; <see cref="RestoreGate"/> serializes BASS_Start/BASS_Init.
/// Engines call in while holding their own transport lock, so the watchdog never calls an engine
/// while holding either lock.
/// </para>
/// </remarks>
static class BassOutputWatchdog
{
    /// <summary><c>BASS_ACTIVE_PAUSED_DEVICE</c>: not in ManagedBass 4.0.2's <see cref="PlaybackState"/>.</summary>
    internal const int ActivePausedDevice = 4;

    internal const string DiagnosticCategory = "AudioOutput";

    internal const string OutputLostMessage = "Audio output unavailable (another application may be using the device exclusively); retrying";

    /// <summary><c>BASS_ERROR_REINIT</c>: not in ManagedBass 4.0.2's <see cref="Errors"/>.</summary>
    const int ErrorReinit = 11;

    /// <summary><c>BASS_DEVICE_REINIT</c>: not in ManagedBass 4.0.2's <see cref="DeviceInitFlags"/>.</summary>
    const DeviceInitFlags DeviceReinit = (DeviceInitFlags)128;

    const int FallbackSampleRate = 44100;
    const int MaxStepsPerTick = 8;

    static readonly Lock Gate = new();
    static readonly Lock RestoreGate = new();
    static readonly HashSet<IBassOutputClient> Clients = new(ReferenceEqualityComparer.Instance);
    static readonly Dictionary<int, DeviceState> Devices = [];
    static readonly long StartTimestamp = Stopwatch.GetTimestamp();
    static Timer? _timer;
    static int _ticking;

    /// <summary>
    /// Raised (process-wide) when a device's output becomes unavailable or available again.
    /// Players forward it as <see cref="IPlayerSession.AudioDeviceChanged"/>.
    /// </summary>
    internal static event Action<AudioDeviceChangeInfo>? OutputAvailabilityChanged;

    static TimeSpan Now => Stopwatch.GetElapsedTime(StartTimestamp);

    /// <summary>Watches the client while it intends to be playing.</summary>
    public static void Register(IBassOutputClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        lock (Gate)
        {
            if (!Clients.Add(client) || _timer is not null)
                return;

            // The timer is process-wide: it must not capture the registering player's execution
            // context (its diagnostic owner), or every report would reach only that player.
            using (ExecutionContext.SuppressFlow())
                _timer = new Timer(static _ => Tick(), null, AudioOutputRecoveryTracker.DefaultRetryInterval, AudioOutputRecoveryTracker.DefaultRetryInterval);
        }
    }

    /// <summary>Stops watching the client (paused, stopped, closed or disposed).</summary>
    public static void Unregister(IBassOutputClient client)
    {
        Timer? timer = null;
        var abandonedOutage = false;
        lock (Gate)
        {
            if (!Clients.Remove(client) || Clients.Count > 0)
                return;

            // Nothing is playing any more: stop retrying.
            timer = _timer;
            _timer = null;
            foreach (var device in Devices.Values)
                abandonedOutage |= device.Tracker.OutageReported;
            Devices.Clear();
        }

        timer?.Dispose();
        if (abandonedOutage)
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, DiagnosticCategory, "Audio output recovery stopped: nothing is playing any more.");
    }

    /// <summary>
    /// Called by an engine whose <c>BASS_ChannelPlay</c> failed. Restarts the channel's output
    /// device (at most once a second, shared with the watchdog) so the caller can retry the play.
    /// </summary>
    /// <returns>True when the output was restarted.</returns>
    public static bool TryRestoreOutputForPlay(int playHandle, int fallbackDevice, int sampleRateHint)
    {
        try
        {
            var device = MBass.ChannelGetDevice(playHandle);
            if (device <= 0)
                device = fallbackDevice;
            if (device <= 0)
                return false;

            DeviceState state;
            lock (Gate)
            {
                state = GetDeviceState(device);
                if (sampleRateHint > 0)
                    state.SampleRate = sampleRateHint;
                if (!state.Tracker.CanAttempt(Now))
                    return false;
            }

            lock (RestoreGate)
            {
                var started = StartOutput(device);
                lock (Gate)
                    state.Tracker.RecordExternalAttempt(reinit: false, started, Now);
                if (started)
                    return true;

                var reinitialized = ReinitOutput(device, state);
                lock (Gate)
                    state.Tracker.RecordExternalAttempt(reinit: true, reinitialized, Now);
                return reinitialized;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }

    /// <summary>True for a <c>BASS_ChannelPlay</c> error that a lost or paused output explains.</summary>
    public static bool IsOutputUnavailableError(Errors error) =>
        error is Errors.Start or Errors.Busy or Errors.Device or Errors.Driver or Errors.Unknown || (int)error == ErrorReinit;

    /// <summary>A Play failure message that says why when the output is unavailable.</summary>
    public static string DescribePlayFailure(Errors error) => IsOutputUnavailableError(error)
        ? $"Audio output unavailable (another application may be using the device exclusively); playback could not start ({error})."
        : $"Audio playback could not start ({error}).";

    static void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
            return;

        try
        {
            IBassOutputClient[] clients;
            lock (Gate)
                clients = [.. Clients];

            Dictionary<int, List<Entry>> byDevice = [];
            foreach (var client in clients)
            {
                var handle = client.PlayingHandle;
                if (handle == 0)
                    continue;

                var device = MBass.ChannelGetDevice(handle);
                if (device <= 0)
                    continue;

                if (!byDevice.TryGetValue(device, out var entries))
                    byDevice[device] = entries = [];
                entries.Add(new Entry(client, handle));
            }

            foreach ((var device, var entries) in byDevice)
                RecoverDevice(device, entries);
        }
        catch (Exception ex)
        {
            // BASS missing (tests, failed bootstrap) or a native failure: try again next tick.
            Debug.WriteLine($"[BassOutputWatchdog] Tick failed: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    static void RecoverDevice(int device, List<Entry> entries)
    {
        DeviceState state;
        lock (Gate)
            state = GetDeviceState(device);

        for (var step = 0; step < MaxStepsPerTick; step++)
        {
            var observation = Observe(device, entries, state);
            AudioOutputRecoveryAction action;
            lock (Gate)
                action = state.Tracker.Decide(observation, Now);

            if (action == AudioOutputRecoveryAction.None)
                return;

            var succeeded = Execute(action, device, entries, state);
            lock (Gate)
                state.Tracker.Record(action, succeeded, Now);
        }
    }

    static bool Execute(AudioOutputRecoveryAction action, int device, List<Entry> entries, DeviceState state)
    {
        switch (action)
        {
            case AudioOutputRecoveryAction.ReportLost:
                Report(device, lost: true);
                return true;

            case AudioOutputRecoveryAction.Start:
                lock (RestoreGate)
                    return StartOutput(device);

            case AudioOutputRecoveryAction.Reinit:
                lock (RestoreGate)
                    return ReinitOutput(device, state);

            case AudioOutputRecoveryAction.ResumeChannels:
                var resumed = true;
                foreach (var entry in entries)
                {
                    if (entry.Client.PlayingHandle == entry.Handle
                        && (int)MBass.ChannelIsActive(entry.Handle) == ActivePausedDevice)
                    {
                        resumed &= entry.Client.ResumeChannelPausedByDevice();
                    }
                }

                return resumed;

            case AudioOutputRecoveryAction.ReportRecovered:
                Report(device, lost: false);
                return true;

            default:
                return true;
        }
    }

    static AudioOutputObservation Observe(int device, List<Entry> entries, DeviceState state)
    {
        var playing = 0;
        var parked = 0;
        foreach (var entry in entries)
        {
            // An engine paused or stopped by the host since the tick began no longer counts.
            if (entry.Client.PlayingHandle != entry.Handle)
                continue;

            playing++;
            if ((int)MBass.ChannelIsActive(entry.Handle) == ActivePausedDevice)
                parked++;
        }

        var output = ReadOutputStatus(device);
        if (output == BassOutputStatus.Started && parked == 0 && MBass.GetInfo(out var info) && info.SampleRate > 0)
        {
            lock (Gate)
                state.SampleRate = info.SampleRate;
        }

        return new AudioOutputObservation(output, playing, parked);
    }

    static bool StartOutput(int device)
    {
        if (!BassAudioOutputDevices.TrySetCurrentDevice(device, DiagnosticCategory, raiseDiagnostic: false))
            return false;

        if (!MBass.Start())
        {
            Debug.WriteLine($"[BassOutputWatchdog] BASS_Start on device {device} failed: {MBass.LastError}");
            return false;
        }

        // BASS_Start can succeed while the device is still unavailable.
        return ReadOutputStatus(device) != BassOutputStatus.Stopped;
    }

    static bool ReinitOutput(int device, DeviceState state)
    {
        int sampleRate;
        lock (Gate)
            sampleRate = state.SampleRate > 0 ? state.SampleRate : FallbackSampleRate;

        // BASS_DEVICE_REINIT keeps the device's channels; it needs a real device number, never -1.
        if (!MBass.Init(device, sampleRate, DeviceReinit))
        {
            Debug.WriteLine($"[BassOutputWatchdog] BASS_Init(REINIT) on device {device} failed: {MBass.LastError}");
            return false;
        }

        return ReadOutputStatus(device) != BassOutputStatus.Stopped;
    }

    static BassOutputStatus ReadOutputStatus(int device) =>
        BassAudioOutputDevices.TrySetCurrentDevice(device, DiagnosticCategory, raiseDiagnostic: false)
            ? BassIsStarted.Read()
            : BassOutputStatus.Unknown;

    static void Report(int device, bool lost)
    {
        var name = MBass.GetDeviceInfo(device, out var info) && !string.IsNullOrWhiteSpace(info.Name)
            ? info.Name
            : $"device {device}";
        if (lost)
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, DiagnosticCategory, $"{OutputLostMessage} (output: {name}).");
        else
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, DiagnosticCategory, $"Audio output available again; playback resumed (output: {name}).");

        var change = new AudioDeviceChangeInfo(
            lost ? AudioDeviceChangeKind.DeviceRemoved : AudioDeviceChangeKind.DeviceAdded,
            null,
            name,
            lost ? OutputLostMessage : null,
            0);
        try
        {
            OutputAvailabilityChanged?.Invoke(change);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BassOutputWatchdog] Availability subscriber failed: {ex.Message}");
        }
    }

    static DeviceState GetDeviceState(int device)
    {
        if (!Devices.TryGetValue(device, out var state))
            Devices[device] = state = new DeviceState();
        return state;
    }

    readonly record struct Entry(IBassOutputClient Client, int Handle);

    sealed class DeviceState
    {
        public AudioOutputRecoveryTracker Tracker { get; } = new();

        public int SampleRate { get; set; }
    }

    /// <summary>
    /// <c>BASS_IsStarted</c>, which ManagedBass 4.0.2 does not wrap. It is resolved through the
    /// ManagedBass assembly's own library resolution, so it calls the same loaded BASS module
    /// (the Linux package installs a DllImport resolver on that assembly only).
    /// </summary>
    static unsafe class BassIsStarted
    {
        static readonly delegate* unmanaged<uint> Function = Resolve();

        // DWORD BASS_IsStarted(void): 0 = not started (stopped, paused, or paused by BASS when
        // the device became unavailable); 1 = started and active; 2 = started but inactive
        // (nothing playing, BASS_CONFIG_DEV_NONSTOP off); 3 = interrupted (iOS).
        public static BassOutputStatus Read()
        {
            if (Function == null)
                return BassOutputStatus.Unknown;

            return Function() switch
            {
                1 or 2 => BassOutputStatus.Started,
                _ => BassOutputStatus.Stopped,
            };
        }

        static delegate* unmanaged<uint> Resolve()
        {
            try
            {
                return NativeLibrary.TryLoad("bass", typeof(MBass).Assembly, null, out var library)
                    && NativeLibrary.TryGetExport(library, "BASS_IsStarted", out var export)
                    ? (delegate* unmanaged<uint>)export
                    : null;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or BadImageFormatException)
            {
                return null;
            }
        }
    }
}
