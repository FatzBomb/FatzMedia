namespace FatzBomb.MediaControl.Pipeline;

/// <summary>
/// The pipeline side of <see cref="VideoSeekPump"/> (Stage 015A slice 2): the host callbacks
/// and one-line delegates that keep the existing call sites unchanged.
/// </summary>
internal sealed partial class VideoPipeline : IVideoSeekPumpHost
{
    private readonly Lock _seekPumpCreationGate = new();
    private VideoSeekPump? _seekPump;

    // Read on every presented frame and in seek decode loops: a plain volatile read once created.
    private VideoSeekPump SeekPump => Volatile.Read(ref _seekPump) ?? CreateSeekPump();

    private VideoSeekPump CreateSeekPump()
    {
        lock (_seekPumpCreationGate)
        {
            if (_seekPump is null)
                Volatile.Write(ref _seekPump, new VideoSeekPump(this));

            return _seekPump!;
        }
    }

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
