using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Cdg;
using Fa = global::FatzMedia.Audio;

namespace FatzMedia.Video.Audio;

/// <summary>
/// CD+G drawn by FatzAudio (037A slice 3), behind the same <see cref="ICdgEngine"/> and
/// <see cref="ICdgSnapshotSource"/> contract as our <see cref="CdgEngine"/>, so every renderer draws it
/// as today: the classic 300x216 picture as colour indices and a palette, the palette applied on the GPU.
/// </summary>
/// <remarks>
/// <para>The graphics ride on a FatzAudio push stream that never gets audio and is never played: it
/// only carries the packs, so the CD+G does not depend on which stream plays the song (a file, an
/// archive entry, a stand-alone <c>.cdg</c> on our own clock) and the song is not parsed twice.
/// FatzAudio replays from its checkpoints, so a seek costs at most 10 s of packs.</para>
/// <para>The time comes from the player as before (the audio position plus our CD+G offset), passed
/// to FatzAudio as an explicit time with its own offset at 0. FatzAudio counts packs with ceil, ours
/// with floor (<see cref="ToTime"/> asks for the middle of our last pack), so both engines show the
/// same pack at the same position.</para>
/// <para>Colours: FatzAudio's palette, with our background opacity and override applied as
/// <see cref="CdgEngine"/> applies them (<see cref="FatzAudioMappings.ToCdgPaletteArgb"/>).</para>
/// </remarks>
sealed partial class FatzAudioCdgEngine : ICdgEngine, ICdgSnapshotSource
{
    const string Category = "FatzAudioCdgEngine";
    const int Width = CdgConstants.FrameWidth;
    const int Height = CdgConstants.FrameHeight;
    static readonly Fa.AudioFormat CarrierFormat = new(Fa.SampleFormat.Float32, 44100, 2);
    static readonly Fa.CdgSurfaceOptions SurfaceOptions = new() { PixelFormat = Fa.CdgPixelFormat.Indexed8 };

    readonly Fa.AudioEngine _engine;
    readonly Lock _renderLock = new();
    readonly byte[] _indices = new byte[Width * Height];
    readonly Fa.CdgColor[] _colors = new Fa.CdgColor[CdgConstants.ColorCount];
    readonly int[] _paletteArgb = new int[CdgConstants.ColorCount];
    readonly List<CdgDirtyRect> _dirtyRects = [];

    Fa.PushAudioStream? _carrier;
    Fa.CdgSurface? _surface;
    long _revision;
    long _paletteRevision;
    long _dirtyBaseRevision = -1;
    ulong _surfacePaletteRevision;
    bool _copyWholeFrame;
    int _backgroundIndex = -1;
    int _borderIndex = -1;
    byte _backgroundOpacity = 100;
    uint? _backgroundOverride;
    uint _backgroundWashArgb;
    CdgRenderSnapshot? _lastSnapshot;
    bool _renderFailureReported;
    bool _disposed;

    /// <param name="engine">The FatzAudio engine the graphics ride on.</param>
    /// <param name="scaling">The scaled picture's options; off (the default) draws the classic picture.</param>
    public FatzAudioCdgEngine(Fa.AudioEngine engine, CdgScalingOptions scaling = default)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _scaling = scaling;
        lock (_renderLock)
            ResetPictureLocked();
    }

    public int FrameWidth => Width;
    public int FrameHeight => Height;

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
        get
        {
            lock (_renderLock)
                return _backgroundOpacity;
        }
        set => SetPresentation(() =>
        {
            var opacity = Math.Min(value, (byte)100);
            if (opacity == _backgroundOpacity)
                return false;

            _backgroundOpacity = opacity;
            return true;
        });
    }

    public uint? BackgroundOverrideArgb
    {
        get
        {
            lock (_renderLock)
                return _backgroundOverride;
        }
        set => SetPresentation(() =>
        {
            if (_backgroundOverride == value)
                return false;

            _backgroundOverride = value;
            return true;
        });
    }

    public double TotalDurationSeconds { get; private set; }

    public CdgEngineState State
    {
        get;
        private set
        {
            if (field == value)
                return;

            field = value;
            StateChanged?.Invoke(this, value);
        }
    }

    public event EventHandler? FrameInvalidated;
    public event EventHandler? BackgroundWashChanged;
    public event EventHandler<CdgEngineState>? StateChanged;
    public event EventHandler? Opened;
    public event EventHandler? Closed;
    public event EventHandler? PlaybackStarted;


    public void Load(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        LoadCore(File.ReadAllBytes(filePath), Path.GetFileName(filePath));
    }

    public void Load(IArchiveEntryInfo archiveEntry)
    {
        ArgumentNullException.ThrowIfNull(archiveEntry);
        using var buffer = new MemoryStream();
        archiveEntry.WriteTo(buffer);
        LoadCore(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), archiveEntry.Key);
    }

    void LoadCore(ReadOnlySpan<byte> cdg, string label)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var (carrier, surface, packs) = CreateCarrier(cdg, label);
        Fa.CdgSurface? scaled;
        try
        {
            scaled = carrier is null ? null : CreateScaledSurface(carrier, label);
        }
        catch
        {
            carrier?.Dispose();
            throw;
        }

        Fa.PushAudioStream? oldCarrier;
        bool washChanged;
        lock (_renderLock)
        {
            if (_disposed)
            {
                // Disposed while the carrier was built: nothing will ever free it.
                carrier?.Dispose();
                throw new ObjectDisposedException(GetType().FullName);
            }

            oldCarrier = _carrier;
            _carrier = carrier;
            _surface = surface;
            _scaledSurface = scaled;
            _scaledFrames = [];
            _renderFailureReported = false;
            TotalDurationSeconds = packs / (double)CdgConstants.PacketsPerSecond;
            washChanged = ResetPictureLocked();
        }

        // Disposing the stream frees its surface too.
        oldCarrier?.Dispose();
        State = CdgEngineState.Ready;
        DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, Category, $"Loaded CDG '{label}' into FatzAudio with {packs} packets, duration {packs / (double)CdgConstants.PacketsPerSecond:F2}s");
        NotifyBackgroundWashChanged(washChanged);
        InvalidateFrame();
        Opened?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// A push stream holding the graphics and an indexed surface on it; no stream for data with no
    /// whole pack (an empty song, as <see cref="CdgEngine"/> loads it).
    /// </summary>
    (Fa.PushAudioStream? Carrier, Fa.CdgSurface? Surface, long Packs) CreateCarrier(ReadOnlySpan<byte> cdg, string label)
    {
        if (cdg.Length < CdgConstants.PacketSize)
            return (null, null, 0);

        var carrier = _engine.CreatePushStream(CarrierFormat);
        try
        {
            carrier.LoadCdg(cdg);
            // The first surface builds the seek checkpoints (a few ms) here, not on the render loop.
            var surface = carrier.CreateCdgSurface(SurfaceOptions);
            return (carrier, surface, carrier.CdgInfo.PackCount);
        }
        catch (Fa.AudioException ex) when (ex.ErrorCode == Fa.ErrorCode.UnsupportedFormat)
        {
            carrier.Dispose();
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, Category, $"CDG '{label}' holds no whole CD+G pack.");
            return (null, null, 0);
        }
        catch
        {
            carrier.Dispose();
            throw;
        }
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

    /// <summary>Back to a blank picture, as <see cref="CdgEngine"/> resets; the next render draws the whole frame.</summary>
    public void Reset()
    {
        bool washChanged;
        lock (_renderLock)
            washChanged = ResetPictureLocked();

        NotifyBackgroundWashChanged(washChanged);
        InvalidateFrame();
    }

    public void RenderAtSeconds(double seconds) => RenderAtSeconds(seconds, forceRebuild: false);

    public void RenderAtSeconds(double seconds, bool forceRebuild)
    {
        var changed = false;
        var washChanged = false;
        lock (_renderLock)
        {
            _lastTime = ToTime(seconds);
            if (_surface is { } surface && !_disposed)
            {
                try
                {
                    changed = ApplyFrameLocked(surface.UpdateAt(_lastTime));
                }
                catch (ObjectDisposedException)
                {
                    // The engine is closing.
                }
                catch (Fa.AudioException ex)
                {
                    // Never into the presentation loop: keep the last picture, say so once per song.
                    if (!_renderFailureReported)
                    {
                        _renderFailureReported = true;
                        DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, Category, $"FatzAudio could not draw the CD+G ({ex.ErrorCode}); the picture holds.", innerException: ex);
                    }
                }

                if (changed)
                    washChanged = RefreshWashLocked();
            }
        }

        NotifyBackgroundWashChanged(washChanged);
        if (changed || forceRebuild)
            InvalidateFrame();
    }

    /// <summary>
    /// The time to ask FatzAudio for: it shows packs [0, ceil(t x 300)), we show floor(position x 300)
    /// (<see cref="CdgEngine.PacketCountAt"/>), so the middle of our last pack's slot gives the same
    /// pack at every position, pack boundaries included. Before the start is the start.
    /// </summary>
    internal static TimeSpan ToTime(double seconds)
    {
        // Our pack count, then the middle of that pack's slot: ceil lands on the same count however
        // the time rounds to TimeSpan ticks (100 ns), so no boundary case differs.
        var packs = Math.Min(CdgEngine.PacketCountAt(seconds), MaxPacks);
        return packs > 0 ? TimeSpan.FromSeconds((packs - 0.5) / CdgConstants.PacketsPerSecond) : TimeSpan.Zero;
    }

    const long MaxPacks = 10_000_000L * CdgConstants.PacketsPerSecond; // 1e7 s, as CdgEngine caps a position

    /// <summary>Copies what changed into our frame; true when the picture or its colours changed.</summary>
    bool ApplyFrameLocked(Fa.CdgFrame frame)
    {
        var pixelsChanged = false;
        if (_copyWholeFrame)
        {
            _dirtyRects.Clear(); // the whole frame replaces whatever was pending
            CopyRect(frame, new Fa.CdgRect(0, 0, Width, Height));
            _copyWholeFrame = false;
            pixelsChanged = true;
        }
        else if (frame.Changed && !frame.Dirty.IsEmpty)
        {
            CopyRect(frame, frame.Dirty);
            pixelsChanged = true;
        }

        var colorsChanged = frame.PaletteRevision != _surfacePaletteRevision
            || frame.BackgroundIndex != _backgroundIndex
            || frame.BorderIndex != _borderIndex;
        if (colorsChanged)
        {
            _surfacePaletteRevision = frame.PaletteRevision;
            frame.Palette.CopyTo(_colors);
            _backgroundIndex = frame.BackgroundIndex;
            _borderIndex = frame.BorderIndex;
            RefreshPaletteLocked();
        }

        if (!pixelsChanged && !colorsChanged)
            return false;

        _revision++;
        return true;
    }

    void CopyRect(Fa.CdgFrame frame, Fa.CdgRect rect)
    {
        var x = Math.Clamp(rect.X, 0, Width);
        var y = Math.Clamp(rect.Y, 0, Height);
        var right = Math.Clamp(rect.X + rect.Width, x, Math.Min(Width, frame.Width));
        var bottom = Math.Clamp(rect.Y + rect.Height, y, Math.Min(Height, frame.Height));
        if (right <= x || bottom <= y)
            return;

        var pixels = frame.Pixels;
        for (var row = y; row < bottom; row++)
            pixels.Slice((row * frame.Stride) + x, right - x).CopyTo(_indices.AsSpan((row * Width) + x, right - x));

        _dirtyRects.Add(new CdgDirtyRect(x, y, right - x, bottom - y));
    }

    public bool TryCreateCdgSnapshot(bool consumeDirtyRegions, out CdgRenderSnapshot snapshot)
    {
        lock (_renderLock)
        {
            // Scaled: the picture is the overlay (DrawOverlay); the snapshot carries only the wash.
            snapshot = _scaledSurface is null ? CreateSnapshotLocked(consumeDirtyRegions) : CreateWashOnlySnapshotLocked();
            return true;
        }
    }

    /// <summary>
    /// An immutable snapshot, as <see cref="CdgStateMachine.CreateSnapshot(bool)"/> makes them: the
    /// previous one again when nothing changed, its buffers shared when only its dirty list is stale.
    /// </summary>
    CdgRenderSnapshot CreateSnapshotLocked(bool consumeDirtyRegions)
    {
        var previous = _lastSnapshot;
        if (previous is not null
            && _dirtyRects.Count == 0
            && previous.Revision == _revision
            && previous.PaletteRevision == _paletteRevision
            && previous.BackgroundWashArgb == _backgroundWashArgb
            && previous.BackgroundColorIndex == _backgroundIndex)
        {
            if (!previous.HasDirtyRegions)
                return previous;

            _lastSnapshot = new CdgRenderSnapshot(_revision, _paletteRevision, previous.ColorIndices, previous.PaletteArgb, _backgroundWashArgb, _backgroundIndex, []);
            return _lastSnapshot;
        }

        ReadOnlyMemory<int> palette = previous is not null && previous.PaletteRevision == _paletteRevision
            ? previous.PaletteArgb
            : _paletteArgb.ToArray();
        CdgDirtyRect[] dirty = [.. _dirtyRects];
        var dirtyBaseRevision = _dirtyBaseRevision;
        if (consumeDirtyRegions)
        {
            _dirtyRects.Clear();
            _dirtyBaseRevision = _revision;
        }

        _lastSnapshot = new CdgRenderSnapshot(_revision, _paletteRevision, _indices.ToArray(), palette, _backgroundWashArgb, _backgroundIndex, dirty)
        {
            DirtyBaseRevision = dirtyBaseRevision,
        };
        return _lastSnapshot;
    }

    /// <summary>A blank picture with no colours yet; the next frame from FatzAudio is copied whole.</summary>
    bool ResetPictureLocked()
    {
        Array.Clear(_indices);
        Array.Clear(_colors);
        _backgroundIndex = -1;
        _borderIndex = -1;
        _surfacePaletteRevision = ulong.MaxValue; // the next frame's colours are taken whatever its revision
        _copyWholeFrame = true;
        _lastTime = TimeSpan.Zero; // the scaled picture is blank too until the next render
        _dirtyRects.Clear();
        _dirtyRects.Add(CdgDirtyRect.FullFrame);
        RefreshPaletteLocked();
        _revision++;
        return RefreshWashLocked();
    }

    /// <summary>Applies a presentation setting; <paramref name="apply"/> returns false when the value is already set.</summary>
    void SetPresentation(Func<bool> apply)
    {
        bool washChanged;
        lock (_renderLock)
        {
            if (!apply())
                return;

            RefreshPaletteLocked();
            _revision++;
            washChanged = RefreshWashLocked();
        }

        NotifyBackgroundWashChanged(washChanged);
        InvalidateFrame();
    }

    void RefreshPaletteLocked()
    {
        FatzAudioMappings.ToCdgPaletteArgb(_colors, _backgroundIndex, _borderIndex, FatzAudioMappings.ToCdgOpacityAlpha(_backgroundOpacity), _backgroundOverride, _paletteArgb);
        _paletteRevision++;
    }

    bool RefreshWashLocked()
    {
        var wash = FatzAudioMappings.ToCdgBackgroundWashArgb(_colors, _backgroundIndex, FatzAudioMappings.ToCdgOpacityAlpha(_backgroundOpacity), _backgroundOverride);
        if (wash == _backgroundWashArgb)
            return false;

        _backgroundWashArgb = wash;
        return true;
    }

    void InvalidateFrame() => FrameInvalidated?.Invoke(this, EventArgs.Empty);

    void NotifyBackgroundWashChanged(bool changed)
    {
        if (changed)
            BackgroundWashChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        Fa.PushAudioStream? carrier;
        bool washChanged;
        lock (_renderLock)
        {
            if (_disposed)
                return;

            _disposed = true;
            carrier = _carrier;
            _carrier = null;
            _surface = null;
            _scaledSurface = null;
            TotalDurationSeconds = 0;
            washChanged = ResetPictureLocked();
        }

        carrier?.Dispose();
        NotifyBackgroundWashChanged(washChanged);
        State = CdgEngineState.Closed;
        Closed?.Invoke(this, EventArgs.Empty);
    }
}
