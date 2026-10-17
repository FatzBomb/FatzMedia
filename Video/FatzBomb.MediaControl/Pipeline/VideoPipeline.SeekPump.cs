namespace FatzBomb.MediaControl.Pipeline;

/// <summary>
/// The pipeline side of <see cref="SeekPump"/> (Stage 015A slice 2): the host callbacks
/// and one-line delegates that keep the existing call sites unchanged.
/// </summary>
internal sealed partial class VideoPipeline : ISeekPumpHost
{
    private readonly Lock _seekPumpCreationGate = new();
    private SeekPump? _seekPump;

    // Read on every presented frame and in seek decode loops: a plain volatile read once created.
    private SeekPump SeekPump => Volatile.Read(ref _seekPump) ?? CreateSeekPump();

    private SeekPump CreateSeekPump()
    {
        lock (_seekPumpCreationGate)
        {
            if (_seekPump is null)
                Volatile.Write(ref _seekPump, new SeekPump(this, "VideoSeek", "VideoPipeline"));

            return _seekPump!;
        }
    }

    double ISeekPumpHost.NormalizeSeekTarget(double seconds) => NormalizeSeekTarget(seconds);

    bool ISeekPumpHost.RunSeek(double target, bool asPreview, bool allowPreviewFallback) =>
        SeekCore(target, asPreview, allowPreviewFallback);

    double ISeekPumpHost.CurrentFrameTime => _currentFrame?.PresentationTime ?? double.NaN;

    void ISeekPumpHost.WaitForPumpInterval() => SeekPump.WaitInterval(_seekPumpTiming.IntervalMilliseconds);

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
