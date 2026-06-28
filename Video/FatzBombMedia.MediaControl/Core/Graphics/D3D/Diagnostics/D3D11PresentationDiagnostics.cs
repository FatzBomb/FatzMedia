using System.Diagnostics;
using FatzBombMedia.MediaControl.Core.Configuration;

namespace FatzBombMedia.MediaControl.Core.Graphics.D3D.Diagnostics;

/// <summary>
/// Aggregates upload and native-present performance diagnostics.  The renderer
/// should decide when work happens; this class owns only the counters/logging.
/// </summary>
internal sealed class D3D11PresentationDiagnostics(MediaPlayerConfiguration configuration)
{
    private const int UploadFrameInterval = 120;
    private const int NativePresentFrameInterval = 120;
    private int _uploadFrames;
    private double _uploadMs;
    private double _uploadMaxMs;

    private int _nativePresentFrames;
    private double _nativePresentMs;
    private double _nativePresentMaxMs;

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

    public static double ElapsedMilliseconds(long startTimestamp) => (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;
}
