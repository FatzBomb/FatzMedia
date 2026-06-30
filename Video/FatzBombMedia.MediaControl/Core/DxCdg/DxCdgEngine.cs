using FatzBombMedia.MediaControl.Core.Abstractions;
using System.Buffers;
using System.Diagnostics;

namespace FatzBombMedia.MediaControl.Core.DxCdg;

/// <summary>
/// Active CDG engine backed by the DXCDG indexed state model. Packet replay updates
/// <see cref="DxCdgStateMachine"/>, and live renderers consume
/// <see cref="DxCdgRenderSnapshot"/> directly.
/// </summary>
public sealed class DxCdgEngine : ICdgEngine, IDxCdgSnapshotSource, IDisposable
{
    private readonly DxCdgStateMachine _dxCdgState = new();
    private readonly Lock _renderLock = new();

    private DxCdgPacket[] _packets = [];
    private int _startPacketIndex;
    private int _lastRenderLogSecond = -1;
    private uint _backgroundWashArgb;
    private bool _disposed;

    public DxCdgEngine() => _backgroundWashArgb = _dxCdgState.BackgroundWashArgb;

    public int FrameWidth => DxCdgConstants.FrameWidth;
    public int FrameHeight => DxCdgConstants.FrameHeight;
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
        get => _dxCdgState.BackgroundOpacity;
        set
        {
            bool washChanged;
            lock (_renderLock)
            {
                _dxCdgState.BackgroundOpacity = value;
                washChanged = RefreshBackgroundWashUnsafe();
            }

            NotifyBackgroundWashChangedIfNeeded(washChanged);
            InvalidateFrame();
        }
    }

    public uint? BackgroundOverrideArgb
    {
        get => _dxCdgState.BackgroundOverrideArgb;
        set
        {
            bool washChanged;
            lock (_renderLock)
            {
                _dxCdgState.BackgroundOverrideArgb = value;
                washChanged = RefreshBackgroundWashUnsafe();
            }

            NotifyBackgroundWashChangedIfNeeded(washChanged);
            InvalidateFrame();
        }
    }

    public double TotalDurationSeconds => _packets.Length / (double)DxCdgConstants.PacketsPerSecond;

    public event EventHandler? FrameInvalidated;
    public event EventHandler? BackgroundWashChanged;
    public event EventHandler<DxCdgEngineState>? StateChanged;
    public event EventHandler? Opened;
    public event EventHandler? Closed;
    public event EventHandler<double>? PositionChanged;
    public event EventHandler? PlaybackStarted;

    public DxCdgEngineState State
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
                PositionChanged?.Invoke(this, field);
            }
        }
    }

    public bool TryCreateDxCdgSnapshot(bool consumeDirtyRegions, out DxCdgRenderSnapshot snapshot)
    {
        lock (_renderLock)
        {
            snapshot = _dxCdgState.CreateSnapshot(consumeDirtyRegions);
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
        State = DxCdgEngineState.Ready;
        Debug.WriteLine($"[DxCdgEngine] Loaded CDG file '{Path.GetFileName(filePath)}' with {_packets.Length} packets, duration {TotalDurationSeconds:F2}s");
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
        State = DxCdgEngineState.Ready;
        Debug.WriteLine($"[DxCdgEngine] Loaded CDG archive entry '{archiveEntry.Key}' with {_packets.Length} packets, duration {TotalDurationSeconds:F2}s");
        InvalidateFrame();
        Opened?.Invoke(this, EventArgs.Empty);
    }

    public void Play()
    {
        State = DxCdgEngineState.Playing;
        PlaybackStarted?.Invoke(this, EventArgs.Empty);
    }

    public void Pause() => State = DxCdgEngineState.Paused;

    public void Stop()
    {
        Reset();
        State = DxCdgEngineState.Stopped;
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
            var endIndex = (long)(seconds * DxCdgConstants.PacketsPerSecond);
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
                if (_dxCdgState.ProcessPacket(_packets[_startPacketIndex]))
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
                    Debug.WriteLine($"[DxCdgEngine] Frame invalidated at {seconds:F3}s, packet {endIndex}/{_packets.Length}, changed={hasChanges}, rebuilt={rebuiltFromStart}, revision={_dxCdgState.Revision}");
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
        _dxCdgState.Reset();
        _startPacketIndex = 0;
    }

    private void InvalidateFrame() => FrameInvalidated?.Invoke(this, EventArgs.Empty);

    private bool RefreshBackgroundWashUnsafe()
    {
        DxCdgRenderSnapshot snapshot = _dxCdgState.CreateSnapshot(consumeDirtyRegions: false);
        if (snapshot.BackgroundWashArgb == _backgroundWashArgb)
        {
            return false;
        }

        _backgroundWashArgb = snapshot.BackgroundWashArgb;
        return true;
    }

    private void NotifyBackgroundWashChangedIfNeeded(bool changed)
    {
        if (changed)
        {
            BackgroundWashChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static IEnumerable<DxCdgPacket> InitializeCdgStream(Stream cdgStream)
    {
        if (cdgStream == null) yield break;
        if (cdgStream.CanSeek && cdgStream.Length < DxCdgConstants.PacketSize) yield break;

        var buffer = ArrayPool<byte>.Shared.Rent(DxCdgConstants.PacketSize * 64);
        var buffered = 0;

        try
        {
            while (true)
            {
                var read = cdgStream.Read(buffer, buffered, buffer.Length - buffered);
                if (read == 0 && buffered < DxCdgConstants.PacketSize) break;

                buffered += read;
                var full = buffered / DxCdgConstants.PacketSize;

                for (var i = 0; i < full; i++)
                {
                    var offset = i * DxCdgConstants.PacketSize;
                    yield return new DxCdgPacket(new ReadOnlySpan<byte>(buffer, offset, DxCdgConstants.PacketSize));
                }

                var leftover = buffered % DxCdgConstants.PacketSize;
                if (leftover > 0)
                {
                    Buffer.BlockCopy(buffer, full * DxCdgConstants.PacketSize, buffer, 0, leftover);
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
        State = DxCdgEngineState.Closed;
        Closed?.Invoke(this, EventArgs.Empty);
    }
}
