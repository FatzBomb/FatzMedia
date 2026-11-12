using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FatzMedia.Video.Core.Cdg;
using FatzMedia.Video.Core.Subtitle;
using Fa = global::FatzMedia.Audio;

namespace FatzMedia.Video.Audio;

/// <summary>
/// The scaled CD+G picture (037A, <see cref="CdgScalingOptions"/>): a second FatzAudio surface on the same carrier
/// draws full colour at a whole-number multiple of the screen, smoothed and with optional effects, and the
/// presentation shows it on the subtitle layer (<see cref="IKaraokeOverlaySource"/>), which every renderer
/// composites. The indexed surface keeps running for the background wash and the change notices, and the snapshot
/// then carries the wash only. The picture is drawn on the presentation thread for the position last rendered
/// (render-ahead would hand back a late frame with no notice that a newer one is ready): about 1 ms for a full
/// frame at 1080p without effects, 15-25 ms with them, a fraction of that for a typical change. The options can
/// change while a song plays (<see cref="ICdgScalingTarget"/>): the surface is made the first time scaling is on
/// and kept for the song, and turning scaling off shows the classic picture again.
/// </summary>
sealed partial class FatzAudioCdgEngine : IKaraokeOverlaySource, ICdgScalingTarget
{
    // Guarded by _renderLock.
    CdgScalingOptions _scaling;
    // Guarded by _renderLock (the reference); the surface itself is drawn on the presentation thread only.
    Fa.CdgSurface? _scaledSurface;
    // The song's scaled surface could not be made: not tried again (and reported again) on every options change.
    // Guarded by _renderLock.
    bool _scaledSurfaceFailed;
    TimeSpan _lastTime;
    // The height of the largest screen showing the picture (0: unknown, drawn for 1080). Guarded by _renderLock.
    int _displayHeight;
    // Which surface revision each pooled buffer holds, so a buffer that comes back gets only the change.
    ConditionalWeakTable<byte[], HeldFrame> _scaledFrames = [];

    sealed class HeldFrame
    {
        public ulong Revision;
        public ulong LayoutRevision;
    }

    /// <summary>Whether the scaled picture is shown. Caller holds _renderLock.</summary>
    bool ScaledLocked => _scaledSurface is not null && _scaling.Enabled && !_disposed;

    public CdgScalingOptions Scaling
    {
        get
        {
            lock (_renderLock)
                return _scaling;
        }
        set
        {
            lock (_renderLock)
            {
                var before = _scaling;
                if (before == value)
                    return;

                var wasScaled = ScaledLocked;
                _scaling = value;
                if (_disposed)
                    return;

                if (value.Enabled && _scaledSurface is null && !_scaledSurfaceFailed && _carrier is { } carrier)
                {
                    // Made under the lock, so a song loaded meanwhile cannot get a surface of the old carrier.
                    _scaledSurface = CreateScaledSurface(carrier, "the song");
                    _scaledSurfaceFailed = _scaledSurface is null;
                    _scaledFrames = [];
                }
                else if (_scaledSurface is { } surface && (ToLook(before) != ToLook(value)))
                {
                    ApplyLook(surface, value);
                }

                // The classic picture before and after: nothing on screen changed.
                if (!wasScaled && !ScaledLocked)
                    return;

                if (wasScaled != ScaledLocked)
                    SwitchPictureLocked();
            }

            InvalidateFrame();
        }
    }

    public int DisplayHeight
    {
        get
        {
            lock (_renderLock)
                return _displayHeight;
        }
        set
        {
            lock (_renderLock)
                _displayHeight = Math.Max(0, value);
        }
    }

    bool IKaraokeOverlaySource.DrawsOverlay
    {
        get
        {
            lock (_renderLock)
                return ScaledLocked;
        }
    }

    SubtitleRasterResult? IKaraokeOverlaySource.DrawOverlay(TimeSpan time, int canvasWidth, int canvasHeight, Func<int, Memory<byte>> rentPixels, out bool changed)
    {
        changed = false;
        Fa.CdgSurface surface;
        TimeSpan at;
        ConditionalWeakTable<byte[], HeldFrame> held;
        Fa.PixelSize size;
        lock (_renderLock)
        {
            if (!ScaledLocked)
                return null;

            surface = _scaledSurface!;
            at = _lastTime;
            held = _scaledFrames;
            size = FatzAudioMappings.ToCdgRenderSize(_scaling.Scale, _displayHeight);
        }

        try
        {
            if (surface.PresentationSize != size)
                surface.PresentationSize = size;

            // The layout can change while drawing (the Auto safety zone switches when the song first draws in the
            // border, or the options change on another thread): then read it again, so the frame is placed with the
            // geometry it was drawn for.
            for (var attempt = 0; ; attempt++)
            {
                var layout = surface.GetLayout();
                var stride = layout.Width * 4;
                var length = stride * layout.Height;
                if (length <= 0)
                    return null;

                var memory = rentPixels(length);
                HeldFrame? frame = null;
                ulong heldRevision = 0;
                if (MemoryMarshal.TryGetArray<byte>(memory, out var segment) && segment.Offset == 0 && segment.Array is { } array)
                {
                    frame = held.GetOrCreateValue(array);
                    if (frame.LayoutRevision == layout.LayoutRevision)
                        heldRevision = frame.Revision;
                }

                var update = surface.UpdateInto(memory.Span, stride, layout.Width, layout.Height, heldRevision, at);
                if (update.LayoutRevision != layout.LayoutRevision && attempt == 0)
                {
                    frame?.LayoutRevision = 0; // what the buffer holds is unknown: draw it whole next time
                    continue;
                }

                changed = update.Revision != heldRevision;
                if (frame is not null)
                {
                    frame.Revision = update.Revision;
                    frame.LayoutRevision = update.LayoutRevision;
                }

                // The buffer has a transparent margin of one CD+G pixel around the picture, which can reach past the
                // canvas (5x at 1080 high is 1090 high): show the picture without it. The canvas is the size it was
                // drawn for (the scale's size, not the presentation canvas), which the renderers fit to the screen.
                var m = layout.Margin;
                return new SubtitleRasterResult(
                    memory[((m * stride) + (m * 4))..],
                    layout.Width - (2 * m),
                    layout.Height - (2 * m),
                    stride,
                    layout.BitmapX + m,
                    layout.BitmapY + m,
                    size.Width,
                    size.Height);
            }
        }
        catch (ObjectDisposedException)
        {
            return null; // another song was loaded meanwhile
        }
        catch (Fa.AudioException ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, Category, $"FatzAudio could not draw the scaled CD+G: {ex.Message}", innerException: ex);
            return null;
        }
    }

    /// <summary>
    /// A scaled surface on <paramref name="carrier"/> with the current options, or null when it cannot be made. Caller
    /// holds _renderLock.
    /// </summary>
    Fa.CdgSurface? CreateScaledSurface(Fa.PushAudioStream carrier, string label)
    {
        Fa.CdgSurface? surface = null;
        try
        {
            // Not ApplyLook: a surface FatzAudio would not scale must not be shown as the scaled picture.
            surface = carrier.CreateCdgSurface(new Fa.CdgSurfaceOptions { PixelFormat = Fa.CdgPixelFormat.Bgra32 });
            surface.Presentation = FatzAudioMappings.ToCdgPresentation(_scaling);
            surface.Effects = FatzAudioMappings.ToCdgEffects(_scaling);
            return surface;
        }
        catch (Exception ex) when (ex is Fa.AudioException or ArgumentException)
        {
            surface?.Dispose();
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, Category, $"Scaled CD+G is not available for '{label}' ({ex.Message}); the classic picture is shown.", innerException: ex);
            return null;
        }
    }

    // What the surface itself is told; the scale is ours (its presentation size).
    static CdgScalingOptions ToLook(CdgScalingOptions options) => options with { Scale = CdgScale.None };

    static void ApplyLook(Fa.CdgSurface surface, CdgScalingOptions options)
    {
        try
        {
            surface.Presentation = FatzAudioMappings.ToCdgPresentation(options);
            surface.Effects = FatzAudioMappings.ToCdgEffects(options);
        }
        catch (ObjectDisposedException)
        {
            // The song is closing.
        }
        catch (Exception ex) when (ex is Fa.AudioException or ArgumentException)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, Category, $"FatzAudio did not take the scaled CD+G options ({ex.Message}); the picture keeps its look.", innerException: ex);
        }
    }

    /// <summary>
    /// Scaling went on or off: the snapshot carries the whole picture again, or only the wash; a pooled buffer may have
    /// held anything meanwhile. Caller holds _renderLock.
    /// </summary>
    void SwitchPictureLocked()
    {
        _scaledFrames = [];
        _lastSnapshot = null;
        _dirtyRects.Clear();
        _dirtyRects.Add(CdgDirtyRect.FullFrame);
        _dirtyBaseRevision = -1;
        _revision++;
    }

    /// <summary>A snapshot with the wash and no pixels, for the scaled picture. Caller holds _renderLock.</summary>
    CdgRenderSnapshot CreateWashOnlySnapshotLocked()
    {
        if (_lastSnapshot is { ColorIndices.IsEmpty: true } previous
            && previous.Revision == _revision
            && previous.BackgroundWashArgb == _backgroundWashArgb
            && previous.BackgroundColorIndex == _backgroundIndex)
        {
            return previous;
        }

        _dirtyRects.Clear();
        _lastSnapshot = new CdgRenderSnapshot(_revision, _paletteRevision, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<int>.Empty, _backgroundWashArgb, _backgroundIndex, []);
        return _lastSnapshot;
    }
}
