using FatzBombMedia.MediaControl.Core.Abstractions;
using System.Buffers;
using System.Diagnostics;

namespace FatzBombMedia.MediaControl.Core.Cdg;

/// <summary>
/// Active CDG engine backed by the CDG indexed state model. Packet replay updates
/// <see cref="CdgStateMachine"/>, and live renderers consume
/// <see cref="CdgRenderSnapshot"/> directly.
/// </summary>
public sealed class CdgEngine : ICdgEngine, ICdgSnapshotSource, IDisposable
{
    private readonly CdgStateMachine _cdgState = new();
    private readonly Lock _renderLock = new();

    private CdgPacket[] _packets = [];
    private int _startPacketIndex;
    private int _lastRenderLogSecond = -1;
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
        _packets = [.. InitializeCdgStream(ms)];
        ResetInternal();
        State = CdgEngineState.Ready;
        Debug.WriteLine($"[CdgEngine] Loaded CDG file '{Path.GetFileName(filePath)}' with {_packets.Length} packets, duration {TotalDurationSeconds:F2}s");
        InvalidateFrame();
        Opened?.Invoke(this, EventArgs.Empty);
    }

    public void Load(IArchiveEntryInfo archiveEntry)
    {
        using var ms = new MemoryStream();
        archiveEntry.WriteTo(ms);
        ms.Position = 0;
        _packets = [.. InitializeCdgStream(ms)];
        ResetInternal();
        State = CdgEngineState.Ready;
        Debug.WriteLine($"[CdgEngine] Loaded CDG archive entry '{archiveEntry.Key}' with {_packets.Length} packets, duration {TotalDurationSeconds:F2}s");
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

            var rebuiltFromStart = false;
            if (forceRebuild || endIndex < _startPacketIndex)
            {
                ResetPacketStateForReplay();
                rebuiltFromStart = true;
            }

            var hasChanges = false;
            for (; _startPacketIndex < endIndex && _startPacketIndex < _packets.Length; _startPacketIndex++)
            {
                if (_cdgState.ProcessPacket(_packets[_startPacketIndex]))
                {
                    hasChanges = true;
                }
            }

            if (hasChanges || rebuiltFromStart || (endIndex == 0 && _packets.Length > 0))
            {
                washChanged = RefreshBackgroundWashUnsafe();
                var logSecond = (int)seconds;
                if (logSecond != _lastRenderLogSecond)
                {
                    _lastRenderLogSecond = logSecond;
                    Debug.WriteLine($"[CdgEngine] Frame invalidated at {seconds:F3}s, packet {endIndex}/{_packets.Length}, changed={hasChanges}, rebuilt={rebuiltFromStart}, revision={_cdgState.Revision}");
                }

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
            _lastRenderLogSecond = -1;
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
        _packets = [];
        State = CdgEngineState.Closed;
        Closed?.Invoke(this, EventArgs.Empty);
    }
}
