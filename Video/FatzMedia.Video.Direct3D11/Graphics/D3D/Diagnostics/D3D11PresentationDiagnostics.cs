using System.Diagnostics;
using FatzMedia.Video.Core.Configuration;

namespace FatzMedia.Video.Core.Graphics.D3D.Diagnostics;

/// <summary>
/// Aggregates upload and native-present performance diagnostics.  The renderer
/// should decide when work happens; this class owns only the counters/logging.
/// </summary>
sealed class D3D11PresentationDiagnostics(MediaPlayerConfiguration configuration)
{
    const int UploadFrameInterval = 120;
    const int NativePresentFrameInterval = 120;
    const int NativeResizeFrameInterval = 30;
    int _uploadFrames;
    double _uploadMs;
    double _uploadMaxMs;

    int _nativePresentFrames;
    double _nativePresentMs;
    double _nativePresentMaxMs;

    int _nativeResizeFrames;
    double _nativeResizeMs;
    double _nativeResizeMaxMs;
    int _lastNativeResizeWidth;
    int _lastNativeResizeHeight;

    public void RecordUpload(double uploadMs, int sourceWidth, int sourceHeight, int surfaceWidth, int surfaceHeight)
    {
        _uploadFrames++;
        _uploadMs += uploadMs;
        _uploadMaxMs = Math.Max(_uploadMaxMs, uploadMs);

        if (_uploadFrames < UploadFrameInterval)
        {
            return;
        }

        if (configuration.EnablePerformanceLogging)
        {
            Debug.WriteLine(
                $"[VorticeD3D11:Perf] frames={_uploadFrames}, avgUploadFlush={_uploadMs / _uploadFrames:F2}ms, " +
                $"maxUploadFlush={_uploadMaxMs:F2}ms, source={sourceWidth}x{sourceHeight}, surface={surfaceWidth}x{surfaceHeight}");
        }

        _uploadFrames = 0;
        _uploadMs = 0;
        _uploadMaxMs = 0;
    }

    public void RecordNativePresent(double presentMs, int syncInterval)
    {
        _nativePresentFrames++;
        _nativePresentMs += presentMs;
        _nativePresentMaxMs = Math.Max(_nativePresentMaxMs, presentMs);

        if (_nativePresentFrames < NativePresentFrameInterval)
        {
            return;
        }

        if (configuration.EnablePerformanceLogging)
        {
            var mode = syncInterval == 0 ? "Immediate" : "VSync";
            Debug.WriteLine(
                $"[VorticeD3D11:PresentPerf] frames={_nativePresentFrames}, mode={mode}, " +
                $"avgPresent={_nativePresentMs / _nativePresentFrames:F2}ms, " +
                $"maxPresent={_nativePresentMaxMs:F2}ms");
        }

        _nativePresentFrames = 0;
        _nativePresentMs = 0;
        _nativePresentMaxMs = 0;
    }

    public void RecordNativeResize(double resizeMs, int width, int height)
    {
        _nativeResizeFrames++;
        _nativeResizeMs += resizeMs;
        _nativeResizeMaxMs = Math.Max(_nativeResizeMaxMs, resizeMs);
        _lastNativeResizeWidth = width;
        _lastNativeResizeHeight = height;

        if (_nativeResizeFrames < NativeResizeFrameInterval)
        {
            return;
        }

        if (configuration.EnablePerformanceLogging)
        {
            Debug.WriteLine(
                $"[VorticeD3D11:ResizePerf] resizes={_nativeResizeFrames}, " +
                $"avgResizeBuffers={_nativeResizeMs / _nativeResizeFrames:F2}ms, " +
                $"maxResizeBuffers={_nativeResizeMaxMs:F2}ms, last={_lastNativeResizeWidth}x{_lastNativeResizeHeight}");
        }

        _nativeResizeFrames = 0;
        _nativeResizeMs = 0;
        _nativeResizeMaxMs = 0;
    }

    public static double ElapsedMilliseconds(long startTimestamp) => (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;
}
