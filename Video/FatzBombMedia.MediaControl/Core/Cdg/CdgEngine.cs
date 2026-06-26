using System.Buffers;
using System.Diagnostics;
using System.IO;
using FatzBombMedia.MediaControl.Core.Abstractions;

namespace FatzBombMedia.MediaControl.Core.Cdg;

/// <summary>
/// Pure C# CDG engine. Decodes packets into an ARGB frame buffer and raises FrameInvalidated.
/// No WPF types. Optimized for performance with specialized components.
/// </summary>
public sealed class CdgEngine : ICdgEngine, IDisposable
{
    #region Private Fields
    
    // Core rendering components
    private readonly int[] _frameArgb = new int[CdgConstants.TotalPixels];
    private readonly byte[] _pixelColors = new byte[CdgConstants.TotalPixels];
    private readonly byte[] _tempPixelColors = new byte[CdgConstants.TotalPixels];
    private readonly CdgColorManager _colorManager = new();
    private readonly CdgPacketProcessor _packetProcessor;
    private uint _backgroundWashArgb;
    private readonly Lock _renderLock = new();
    
    // Packet processing state
    private int _startPacketIndex;
    private int _lastRenderLogSecond = -1;
    private CdgPacket[] _packets = [];
    private bool _disposed;
    
    // Event declarations for state and lifecycle
    public event EventHandler<CdgEngineState>? StateChanged;
    public event EventHandler? Opened;
    public event EventHandler? Closed;
    public event EventHandler<double>? PositionChanged;

    // Event for orchestration
    public event EventHandler? PlaybackStarted;

    private CdgEngineState _state;
    public CdgEngineState State
    {
        get => _state;
        private set
        {
            if (_state != value)
            {
                _state = value;
                StateChanged?.Invoke(this, _state);
            }
        }
    }

    private double _positionSeconds;
    public double PositionSeconds
    {
        get => _positionSeconds;
        set
        {
            if (Math.Abs(_positionSeconds - value) > 0.001)
            {
                _positionSeconds = value;
                PositionChanged?.Invoke(this, _positionSeconds);
            }
        }
    }
    
    #endregion

    #region Constructor
    
    public CdgEngine()
    {
        // Initialize the packet processor with our buffers and color manager
        _packetProcessor = new CdgPacketProcessor(_pixelColors, _tempPixelColors, _colorManager);
        _backgroundWashArgb = _colorManager.BackgroundWashArgb;
    }
    
    #endregion

    #region Public Properties (ICdgEngine Implementation)
    
    /// <inheritdoc/>
    public int FrameWidth => CdgConstants.FrameWidth;
    
    /// <inheritdoc/>
    public int FrameHeight => CdgConstants.FrameHeight;
    
    /// <inheritdoc/>
    public ReadOnlyMemory<int> FrameArgb => _frameArgb;

    /// <inheritdoc/>
    public uint BackgroundWashArgb
    {
        get
        {
            lock (_renderLock)
            {
                return _backgroundWashArgb;
            }
        }
    }

    /// <inheritdoc/>
    public byte BackgroundOpacity
    {
        get => _colorManager.BackgroundOpacity;
        set
        {
            bool washChanged;
            lock (_renderLock)
            {
                _colorManager.BackgroundOpacity = value;
                washChanged = RenderFrameBuffer();
            }

            NotifyBackgroundWashChangedIfNeeded(washChanged);
            InvalidateFrame();
        }
    }
    
    /// <inheritdoc/>
    public uint? BackgroundOverrideArgb
    {
        get => _colorManager.BackgroundOverrideArgb;
        set
        {
            bool washChanged;
            lock (_renderLock)
            {
                _colorManager.BackgroundOverrideArgb = value;
                washChanged = RenderFrameBuffer();
            }

            NotifyBackgroundWashChangedIfNeeded(washChanged);
            InvalidateFrame();
        }
    }

    /// <inheritdoc/>
    public double TotalDurationSeconds => _packets.Length / (double)CdgConstants.PacketsPerSecond;
    
    /// <inheritdoc/>
    public event EventHandler? FrameInvalidated;

    /// <inheritdoc/>
    public event EventHandler? BackgroundWashChanged;
    
    #endregion

    /// <inheritdoc/>
    public void CopyFrameArgb(Span<int> destination)
    {
        lock (_renderLock)
        {
            _frameArgb.AsSpan().CopyTo(destination);
        }
    }

    #region Public Methods (ICdgEngine Implementation)
    
    /// <inheritdoc/>
    public void LoadPaired(string filePath, int audioStreamHandle)
    {
        Load(filePath);
    }
    
    /// <inheritdoc/>
    public void LoadPaired(IArchiveEntryInfo archiveEntry, int audioStreamHandle)
    {
        Load(archiveEntry);
    }
    
    /// <inheritdoc/>
    public void Load(string filePath)
    {
        using var fs = File.OpenRead(filePath);
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
    
    /// <inheritdoc/>
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
    
    /// <inheritdoc/>
    public void Play()
    {
        State = CdgEngineState.Playing;
        PlaybackStarted?.Invoke(this, EventArgs.Empty);
    }
    
    /// <inheritdoc/>
    public void Pause()
    {
        State = CdgEngineState.Paused;
    }
    
    /// <inheritdoc/>
    public void Stop()
    {
        Reset();
        State = CdgEngineState.Stopped;
    }
    
    /// <inheritdoc/>
    public void Reset()
    {
        ResetInternal();
        InvalidateFrame();
    }
    
    /// <inheritdoc/>
    public void RenderAtSeconds(double seconds) => RenderAtSeconds(seconds, forceRebuild: false);

    /// <inheritdoc/>
    public void RenderAtSeconds(double seconds, bool forceRebuild)
    {
        var shouldInvalidate = false;
        var washChanged = false;

        lock (_renderLock)
        {
            // Convert seconds to packet count
            long endIndex = (long)(seconds * CdgConstants.PacketsPerSecond);
            endIndex = Math.Clamp(endIndex, 0, _packets.Length);
            
            var rebuiltFromStart = false;

            // Handle backwards seeking. CDG frames are stateful: color tables, background,
            // scroll scratch buffers, and pixel indices must all be replayed from a clean state.
            if (forceRebuild || endIndex < _startPacketIndex)
            {
                ResetPacketStateForReplay();
                rebuiltFromStart = true;
            }
            
            // Process packets from current position to target position - ONLY fire event if pixels changed
            bool hasChanges = false;
            for (; _startPacketIndex < endIndex && _startPacketIndex < _packets.Length; _startPacketIndex++)
            {
                // Use the packet processor's change detection (matching old implementation)
                if (_packetProcessor.ProcessPacket(_packets[_startPacketIndex]))
                    hasChanges = true;
            }
            
            // CRITICAL: Only update UI if packets actually changed pixels (like old implementation)
            if (hasChanges || rebuiltFromStart || (endIndex == 0 && _packets.Length > 0)) // Always render first/rebuilt frame
            {
                // Update color/LUT caches and render frame.
                washChanged = RenderFrameBuffer();
                var logSecond = (int)seconds;
                if (logSecond != _lastRenderLogSecond)
                {
                    _lastRenderLogSecond = logSecond;
                    Debug.WriteLine($"[CdgEngine] Frame invalidated at {seconds:F3}s, packet {endIndex}/{_packets.Length}, changed={hasChanges}, rebuilt={rebuiltFromStart}");
                }

                shouldInvalidate = true;
            }
        }

        PositionSeconds = seconds;
        NotifyBackgroundWashChangedIfNeeded(washChanged);

        if (shouldInvalidate)
        {
            InvalidateFrame(); // This triggers UI update
        }
    }
    
    #endregion

    #region Private Methods - Core Logic (Simplified!)
    
    private void ResetInternal()
    {
        bool washChanged;
        lock (_renderLock)
        {
            ResetPacketStateForReplay();
            _lastRenderLogSecond = -1;
            washChanged = RenderFrameBuffer();
        }

        PositionSeconds = 0;
        NotifyBackgroundWashChangedIfNeeded(washChanged);
    }

    private void ResetPacketStateForReplay()
    {
        Array.Clear(_pixelColors, 0, _pixelColors.Length);
        Array.Clear(_tempPixelColors, 0, _tempPixelColors.Length);
        _colorManager.Reset();
        _startPacketIndex = 0;
    }
    
    private void InvalidateFrame() => FrameInvalidated?.Invoke(this, EventArgs.Empty);
    
    private bool RenderFrameBuffer()
    {
        _colorManager.UpdateLutIfNeeded();
        var washChanged = RefreshBackgroundWashCacheUnsafe();
        var colorLut = _colorManager.ColorLut;
        var frameSpan = _frameArgb.AsSpan();
        var pixelSpan = _pixelColors.AsSpan();
        var backgroundColorIndex = _colorManager.BackgroundColorIndex;
        
        // Optimized pixel rendering using spans
        for (int i = 0; i < frameSpan.Length; i++)
        {
            var index = pixelSpan[i];
            // Erase background pixels. The background wash is drawn as a full-viewport
            // layer; keeping background opaque in the lyric bitmap creates visible seams
            // when the CDG is centred at native aspect over background media.
            frameSpan[i] = (index == 0 || index == backgroundColorIndex) ? 0 : colorLut[index];
        }

        return washChanged;
    }

    private bool RefreshBackgroundWashCacheUnsafe()
    {
        _colorManager.UpdateBackgroundWashIfNeeded();
        var newWashArgb = _colorManager.BackgroundWashArgb;
        if (newWashArgb == _backgroundWashArgb)
        {
            return false;
        }

        _backgroundWashArgb = newWashArgb;
        return true;
    }

    private void NotifyBackgroundWashChangedIfNeeded(bool changed)
    {
        if (changed)
        {
            BackgroundWashChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    
    #endregion

    #region Private Methods - Stream Processing
    
    private static IEnumerable<CdgPacket> InitializeCdgStream(Stream cdgStream)
    {
        if (cdgStream == null) yield break;
        if (cdgStream.CanSeek && cdgStream.Length < CdgConstants.PacketSize) yield break;
        
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CdgConstants.PacketSize * 64);
        int buffered = 0;
        
        try
        {
            while (true)
            {
                int read = cdgStream.Read(buffer, buffered, buffer.Length - buffered);
                if (read == 0 && buffered < CdgConstants.PacketSize) break;
                
                buffered += read;
                int full = buffered / CdgConstants.PacketSize;
                
                for (int i = 0; i < full; i++)
                {
                    int off = i * CdgConstants.PacketSize;
                    yield return new CdgPacket(new ReadOnlySpan<byte>(buffer, off, CdgConstants.PacketSize));
                }
                
                int leftover = buffered % CdgConstants.PacketSize;
                if (leftover > 0)
                    Buffer.BlockCopy(buffer, full * CdgConstants.PacketSize, buffer, 0, leftover);
                
                buffered = leftover;
                if (read == 0) break;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
    
    #endregion

    #region IDisposable Implementation

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

    #endregion
}
