using FatzMedia.Video.Core.Graphics.Composition;
using FatzMedia.Video.Core.Graphics.D3D.Video;

namespace FatzMedia.Video.Core.Graphics.D3D.Presentation;

/// <summary>
/// Owns mutable presentation settings that are shared by native HWND, hardware-frame,
/// and CPU-frame presentation paths.  Keeping this state outside the renderer makes
/// VorticeD3D11Renderer a thinner composition root while preserving the existing public
/// methods used by higher-level host code.
/// </summary>
internal sealed class D3D11PresentationState
{
    private int _nativeLiveResizeInProgress;

    public MediaCompositionStretchMode NativePresentationStretch { get; private set; } = MediaCompositionStretchMode.UniformFit;

    public bool IsNativeLiveResizeInProgress => Volatile.Read(ref _nativeLiveResizeInProgress) != 0;

    public void SetNativeLiveResizeInProgress(bool isResizing) => Volatile.Write(ref _nativeLiveResizeInProgress, isResizing ? 1 : 0);

    public void SetNativePresentationStretch(MediaCompositionStretchMode stretch, D3D11VideoProcessorPresenter videoProcessorPresenter)
    {
        if (NativePresentationStretch == stretch)
        {
            return;
        }

        NativePresentationStretch = stretch;
        videoProcessorPresenter.Reset();
    }
}
