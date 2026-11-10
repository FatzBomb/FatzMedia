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
/// frame at 1080p without effects, 15-25 ms with them, a fraction of that for a typical change.
/// </summary>
sealed partial class FatzAudioCdgEngine : IKaraokeOverlaySource
{
    readonly CdgScalingOptions _scaling;
    // Guarded by _renderLock (the reference); the surface itself is drawn on the presentation thread only.
    Fa.CdgSurface? _scaledSurface;
    TimeSpan _lastTime;
    // Which surface revision each pooled buffer holds, so a buffer that comes back gets only the change.
    ConditionalWeakTable<byte[], HeldFrame> _scaledFrames = [];

    sealed class HeldFrame
    {
        public ulong Revision;
        public ulong LayoutRevision;
    }

    bool IKaraokeOverlaySource.DrawsOverlay
    {
        get
        {
            lock (_renderLock)
                return _scaledSurface is not null && !_disposed;
        }
    }

    SubtitleRasterResult? IKaraokeOverlaySource.DrawOverlay(TimeSpan time, int canvasWidth, int canvasHeight, Func<int, Memory<byte>> rentPixels, out bool changed)
    {
        changed = false;
        Fa.CdgSurface surface;
        TimeSpan at;
        ConditionalWeakTable<byte[], HeldFrame> held;
        lock (_renderLock)
        {
            if (_disposed || _scaledSurface is null)
                return null;

            surface = _scaledSurface;
            at = _lastTime;
            held = _scaledFrames;
        }

        try
        {
            var size = new Fa.PixelSize(canvasWidth, canvasHeight);
            if (surface.PresentationSize != size)
                surface.PresentationSize = size;

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
            changed = update.Revision != heldRevision;
            if (frame is not null)
            {
                frame.Revision = update.Revision;
                frame.LayoutRevision = update.LayoutRevision;
            }

            // The buffer has a transparent margin of one CD+G pixel around the picture, which can reach past the
            // canvas (5x at 1080 high is 1090 high): show the picture without it.
            var m = layout.Margin;
            return new SubtitleRasterResult(
                memory[((m * stride) + (m * 4))..],
                layout.Width - (2 * m),
                layout.Height - (2 * m),
                stride,
                layout.BitmapX + m,
                layout.BitmapY + m,
                canvasWidth,
                canvasHeight);
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

    /// <summary>The scaled surface on <paramref name="carrier"/>, or null when scaling is off or it cannot be made.</summary>
    Fa.CdgSurface? CreateScaledSurface(Fa.PushAudioStream carrier, string label)
    {
        if (!_scaling.Enabled)
            return null;

        try
        {
            var surface = carrier.CreateCdgSurface(new Fa.CdgSurfaceOptions { PixelFormat = Fa.CdgPixelFormat.Bgra32 });
            surface.Presentation = FatzAudioMappings.ToCdgPresentation(_scaling);
            surface.Effects = _scaling.Effects ? FatzAudioMappings.CdgEffects : null;
            return surface;
        }
        catch (Exception ex) when (ex is Fa.AudioException or ArgumentException)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, Category, $"Scaled CD+G is not available for '{label}' ({ex.Message}); the classic picture is shown.", innerException: ex);
            return null;
        }
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
