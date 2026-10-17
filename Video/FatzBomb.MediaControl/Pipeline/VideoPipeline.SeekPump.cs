namespace FatzBomb.MediaControl.Pipeline;

/// <summary>
/// The pipeline side of <see cref="VideoSeekPump"/> (Stage 015A slice 2): the host callbacks
/// and one-line delegates that keep the existing call sites unchanged.
/// </summary>
internal sealed partial class VideoPipeline : IVideoSeekPumpHost
{
    private VideoSeekPump? _seekPump;

    private VideoSeekPump SeekPump => LazyInitializer.EnsureInitialized(ref _seekPump, () => new VideoSeekPump(this));

    double IVideoSeekPumpHost.NormalizeSeekTarget(double seconds) => NormalizeSeekTarget(seconds);

    bool IVideoSeekPumpHost.RunSeek(double target, bool asPreview, bool allowPreviewFallback) =>
        SeekCore(target, asPreview, allowPreviewFallback);

    double IVideoSeekPumpHost.CurrentFrameTime => _currentFrame?.PresentationTime ?? double.NaN;

    void IVideoSeekPumpHost.WaitForPumpInterval() => SeekPump.WaitInterval(_seekPumpTiming.IntervalMilliseconds);

    private void StartSeekWorker() => SeekPump.Start();

    private void StopSeekWorker() => SeekPump.Stop();

    private void SetActiveSeekTarget(double target) => SeekPump.SetActiveTarget(target);

    private void ClearActiveSeekTarget(double target) => SeekPump.ClearActiveTarget(target);

    private void SetMinimumCommittedVideoPresentationTarget(double target) => SeekPump.SetMinimumCommittedTarget(target);

    private double GetMinimumCommittedVideoPresentationTarget() => SeekPump.MinimumCommittedTarget;

    private bool HasSupersedingSeekRequest() => SeekPump.HasSupersedingRequest;

    private bool IsSeekSequenceActive() => SeekPump.IsSequenceActive;

    private void EndSeekSequenceIfSettled() => SeekPump.EndSequenceIfSettled();
}
