using FatzBomb.MediaControl.Core.Abstractions;
using System.Buffers;

namespace FatzBomb.MediaControl.Core.Cdg;

/// <summary>
/// Active CDG engine backed by the CDG indexed state model. Packet replay updates
/// <see cref="CdgStateMachine"/>, and live renderers consume
/// <see cref="CdgRenderSnapshot"/> directly.
/// </summary>
public sealed class CdgEngine : ICdgEngine, ICdgSnapshotSource, IDisposable
{
    private readonly CdgStateMachine _cdgState = new();
    private readonly Lock _renderLock = new();

    /// <summary>
    /// Replay checkpoint spacing: 10 s of packets. Stage 022A slice 4: a seek used to replay every
    /// packet from the start of the song under the render lock (about 72,000 for 4 minutes), which
    /// blocked the render loop for up to 15 ms per seek in the owner's capture. A seek now restores
    /// the nearest checkpoint at or before its target and replays at most this many packets.
    /// </summary>
    internal const int CheckpointIntervalPackets = 10 * CdgConstants.PacketsPerSecond;

    private CdgPacket[] _packets = [];
    // Slot n holds the state after packets [0, n * CheckpointIntervalPackets), built at load; slot
    // 0 is the reset state and stays null. About 65 KB each: 1.6 MB for a 4-minute song.
    private CdgReplayCheckpoint?[] _checkpoints = [null];
    private int _startPacketIndex;
    private uint _backgroundWashArgb;
    private bool _disposed;

    public CdgEngine() => _backgroundWashArgb = _cdgState.BackgroundWashArgb;

    public int FrameWidth => CdgConstants.FrameWidth;
    public int FrameHeight => CdgConstants.FrameHeight;
    public uint BackgroundWashArgb
    {
        get
        {
            lock (_renderLock)
                return _backgroundWashArgb;
        }
    }

    public byte BackgroundOpacity
    {
        get => _cdgState.BackgroundOpacity;
        set
        {
            bool washChanged;
            lock (_renderLock)
            {
                _cdgState.BackgroundOpacity = value;
                washChanged = RefreshBackgroundWashUnsafe();
            }

            NotifyBackgroundWashChangedIfNeeded(washChanged);
            InvalidateFrame();
        }
    }

    public uint? BackgroundOverrideArgb
    {
        get => _cdgState.BackgroundOverrideArgb;
        set
        {
            bool washChanged;
            lock (_renderLock)
            {
                _cdgState.BackgroundOverrideArgb = value;
                washChanged = RefreshBackgroundWashUnsafe();
            }

            NotifyBackgroundWashChangedIfNeeded(washChanged);
            InvalidateFrame();
        }
    }

    public double TotalDurationSeconds => _packets.Length / (double)CdgConstants.PacketsPerSecond;

    public event EventHandler? FrameInvalidated;
    public event EventHandler? BackgroundWashChanged;
    public event EventHandler<CdgEngineState>? StateChanged;
    public event EventHandler? Opened;
    public event EventHandler? Closed;
    public event EventHandler? PlaybackStarted;

    public CdgEngineState State
    {
        get;
        private set
        {
            if (field != value)
            {
                field = value;
                StateChanged?.Invoke(this, field);
            }
        }
    }

    public double PositionSeconds
    {
        get;
        set
        {
            if (Math.Abs(field - value) > 0.001)
            {
                field = value;
            }
        }
    }

    public bool TryCreateCdgSnapshot(bool consumeDirtyRegions, out CdgRenderSnapshot snapshot)
    {
        lock (_renderLock)
        {
            snapshot = _cdgState.CreateSnapshot(consumeDirtyRegions);
            return true;
        }
    }

    public void LoadPaired(string filePath, int audioStreamHandle) => Load(filePath);
    public void LoadPaired(IArchiveEntryInfo archiveEntry, int audioStreamHandle) => Load(archiveEntry);

    public void Load(string filePath)
    {
        using FileStream fs = File.OpenRead(filePath);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        ms.Position = 0;
        SetPackets([.. InitializeCdgStream(ms)]);
        ResetInternal();
        State = CdgEngineState.Ready;
        DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "CdgEngine", $"Loaded CDG file '{Path.GetFileName(filePath)}' with {_packets.Length} packets, duration {TotalDurationSeconds:F2}s");
        InvalidateFrame();
        Opened?.Invoke(this, EventArgs.Empty);
    }

    public void Load(IArchiveEntryInfo archiveEntry)
    {
        using var ms = new MemoryStream();
        archiveEntry.WriteTo(ms);
        ms.Position = 0;
        SetPackets([.. InitializeCdgStream(ms)]);
        ResetInternal();
        State = CdgEngineState.Ready;
        DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "CdgEngine", $"Loaded CDG archive entry '{archiveEntry.Key}' with {_packets.Length} packets, duration {TotalDurationSeconds:F2}s");
        InvalidateFrame();
        Opened?.Invoke(this, EventArgs.Empty);
    }

    public void Play()
    {
        State = CdgEngineState.Playing;
        PlaybackStarted?.Invoke(this, EventArgs.Empty);
    }

    public void Pause() => State = CdgEngineState.Paused;

    public void Stop()
    {
        Reset();
        State = CdgEngineState.Stopped;
    }

    public void Reset()
    {
        ResetInternal();
        InvalidateFrame();
    }

    public void RenderAtSeconds(double seconds) => RenderAtSeconds(seconds, forceRebuild: false);

    public void RenderAtSeconds(double seconds, bool forceRebuild)
    {
        var shouldInvalidate = false;
        var washChanged = false;

        lock (_renderLock)
        {
            var endIndex = (long)(seconds * CdgConstants.PacketsPerSecond);
            endIndex = Math.Clamp(endIndex, 0, _packets.Length);

            // Going back, or forward past a checkpoint: start from the nearest checkpoint at or before
            // the target. Otherwise the current state is already exact and replay just continues,
            // so a forced (authoritative) render at the current position replays nothing.
            var slot = Math.Min((int)(endIndex / CheckpointIntervalPackets), _checkpoints.Length - 1);
            var restored = false;
            if (endIndex < _startPacketIndex || slot * CheckpointIntervalPackets > _startPacketIndex)
            {
                RestoreCheckpointUnsafe(slot);
                restored = true;
            }

            var hasChanges = false;
            for (; _startPacketIndex < endIndex && _startPacketIndex < _packets.Length; _startPacketIndex++)
            {
                if (_cdgState.ProcessPacket(_packets[_startPacketIndex]))
                {
                    hasChanges = true;
                }
            }

            if (hasChanges || restored || forceRebuild || (endIndex == 0 && _packets.Length > 0))
            {
                washChanged = RefreshBackgroundWashUnsafe();
                shouldInvalidate = true;
            }
        }

        PositionSeconds = seconds;
        NotifyBackgroundWashChangedIfNeeded(washChanged);

        if (shouldInvalidate)
        {
            InvalidateFrame();
        }
    }

    private void ResetInternal()
    {
        bool washChanged;
        lock (_renderLock)
        {
            ResetPacketStateForReplay();
            washChanged = RefreshBackgroundWashUnsafe();
        }

        PositionSeconds = 0;
        NotifyBackgroundWashChangedIfNeeded(washChanged);
    }

    private void ResetPacketStateForReplay()
    {
        _cdgState.Reset();
        _startPacketIndex = 0;
    }

    private void RestoreCheckpointUnsafe(int slot)
    {
        if (_checkpoints[slot] is { } checkpoint)
        {
            _cdgState.RestoreReplayCheckpoint(checkpoint);
            _startPacketIndex = slot * CheckpointIntervalPackets;
        }
        else
        {
            ResetPacketStateForReplay();
        }
    }

    /// <summary>
    /// Replays the whole song once on a scratch state machine, outside the render lock, keeping a
    /// copy every <see cref="CheckpointIntervalPackets"/> (about 50 ms for a 4-minute song).
    /// </summary>
    private static CdgReplayCheckpoint?[] BuildCheckpoints(CdgPacket[] packets)
    {
        var checkpoints = new CdgReplayCheckpoint?[(packets.Length / CheckpointIntervalPackets) + 1];
        var replay = new CdgStateMachine();
        for (var index = 0; index < packets.Length; index++)
        {
            if (index > 0 && index % CheckpointIntervalPackets == 0)
                checkpoints[index / CheckpointIntervalPackets] = replay.CaptureReplayCheckpoint();

            _ = replay.ProcessPacket(packets[index]);
        }

        if (packets.Length > 0 && packets.Length % CheckpointIntervalPackets == 0)
            checkpoints[^1] = replay.CaptureReplayCheckpoint();

        return checkpoints;
    }

    /// <summary>
    /// Swaps in a song's packets and checkpoints and resets the replay state in one lock, so no
    /// render ever runs the new packets over the old song's state.
    /// </summary>
    private void SetPackets(CdgPacket[] packets)
    {
        CdgReplayCheckpoint?[] checkpoints = BuildCheckpoints(packets);
        lock (_renderLock)
        {
            _packets = packets;
            _checkpoints = checkpoints;
            ResetPacketStateForReplay();
        }
    }

    private void InvalidateFrame() => FrameInvalidated?.Invoke(this, EventArgs.Empty);

    private bool RefreshBackgroundWashUnsafe()
    {
        var washArgb = _cdgState.BackgroundWashArgb;
        if (washArgb == _backgroundWashArgb)
        {
            return false;
        }

        _backgroundWashArgb = washArgb;
        return true;
    }

    private void NotifyBackgroundWashChangedIfNeeded(bool changed)
    {
        if (changed)
        {
            BackgroundWashChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static IEnumerable<CdgPacket> InitializeCdgStream(Stream cdgStream)
    {
        if (cdgStream == null) yield break;
        if (cdgStream.CanSeek && cdgStream.Length < CdgConstants.PacketSize) yield break;

        var buffer = ArrayPool<byte>.Shared.Rent(CdgConstants.PacketSize * 64);
        var buffered = 0;

        try
        {
            while (true)
            {
                var read = cdgStream.Read(buffer, buffered, buffer.Length - buffered);
                if (read == 0 && buffered < CdgConstants.PacketSize) break;

                buffered += read;
                var full = buffered / CdgConstants.PacketSize;

                for (var i = 0; i < full; i++)
                {
                    var offset = i * CdgConstants.PacketSize;
                    yield return new CdgPacket(new ReadOnlySpan<byte>(buffer, offset, CdgConstants.PacketSize));
                }

                var leftover = buffered % CdgConstants.PacketSize;
                if (leftover > 0)
                {
                    Buffer.BlockCopy(buffer, full * CdgConstants.PacketSize, buffer, 0, leftover);
                }

                buffered = leftover;
                if (read == 0) break;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Pause();
        ResetInternal();
        SetPackets([]);
        State = CdgEngineState.Closed;
        Closed?.Invoke(this, EventArgs.Empty);
    }
}
