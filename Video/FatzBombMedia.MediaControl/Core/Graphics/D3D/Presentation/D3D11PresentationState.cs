using System.Threading;
using System.Windows.Media;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Video;

namespace FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation;

/// <summary>
/// Owns mutable presentation settings that are shared by native HWND, hardware-frame,
/// and CPU-frame presentation paths.  Keeping this state outside the renderer makes
/// VorticeD3D11Renderer a thinner composition root while preserving the existing public
/// methods used by higher-level host code.
/// </summary>
internal sealed class D3D11PresentationState
{
    private int _nativeLiveResizeInProgress;
    private Stretch _nativePresentationStretch = Stretch.Uniform;

    public Stretch NativePresentationStretch => _nativePresentationStretch;

    public bool IsNativeLiveResizeInProgress => Volatile.Read(ref _nativeLiveResizeInProgress) != 0;

    public void SetNativeLiveResizeInProgress(bool isResizing)
    {
        Volatile.Write(ref _nativeLiveResizeInProgress, isResizing ? 1 : 0);
    }

    public void SetNativePresentationStretch(Stretch stretch, D3D11VideoProcessorPresenter videoProcessorPresenter)
    {
        if (_nativePresentationStretch == stretch)
        {
            return;
        }

        _nativePresentationStretch = stretch;
        videoProcessorPresenter.Reset();
    }
}
