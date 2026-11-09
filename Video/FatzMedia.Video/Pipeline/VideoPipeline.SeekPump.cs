namespace FatzMedia.Video.Pipeline;

/// <summary>
/// The pipeline side of <see cref="SeekPump"/> (Stage 015A slice 2): the host callbacks
/// and one-line delegates that keep the existing call sites unchanged.
/// </summary>
sealed partial class VideoPipeline : ISeekPumpHost
{
    readonly Lock _seekPumpCreationGate = new();
    SeekPump? _seekPump;

    // Read on every presented frame and in seek decode loops: a plain volatile read once created.
    SeekPump SeekPump => Volatile.Read(ref _seekPump) ?? CreateSeekPump();

    SeekPump CreateSeekPump()
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

    void StartSeekWorker() => SeekPump.Start();

    void StopSeekWorker() => SeekPump.Stop();

    void SetActiveSeekTarget(double target) => SeekPump.SetActiveTarget(target);

    void ClearActiveSeekTarget(double target) => SeekPump.ClearActiveTarget(target);

    void SetMinimumCommittedVideoPresentationTarget(double target) => SeekPump.SetMinimumCommittedTarget(target);

    double GetMinimumCommittedVideoPresentationTarget() => SeekPump.MinimumCommittedTarget;

    bool HasSupersedingSeekRequest() => SeekPump.HasSupersedingRequest;

    bool IsSeekSequenceActive() => SeekPump.IsSequenceActive;

    void EndSeekSequenceIfSettled() => SeekPump.EndSequenceIfSettled();
}
