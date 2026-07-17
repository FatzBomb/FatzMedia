using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Graphics.D3D.Diagnostics;
using FatzBomb.MediaControl.Windows.Video;

namespace FatzBomb.MediaControl;

/// <summary>
/// Supplies Windows DWM timing and Direct3D device-loss checks to Core video playback.
/// </summary>
internal sealed class WindowsVideoPipelinePlatformServices : IVideoPipelinePlatformServices
{
    public double GetPresentationRefreshIntervalSeconds() => DwmCompositionTiming.GetRefreshIntervalSeconds();

    public bool IsHardwareDecoderDeviceLost(IntPtr devicePointer) => D3D11DeviceLostClassifier.IsDeviceLost(devicePointer);
}
