using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Cdg;
using FatzMedia.Video.Pipeline;

namespace FatzMedia.Video.Portable.Tests;

/// <summary>
/// ENG-013: CDG seeks run on the shared <see cref="SeekPump"/>. Each seek target renders once
/// (the pump's settle must not draw the same frame again), and the latest target wins.
/// </summary>
public sealed class CdgPipelineSeekTests : IDisposable
{
    readonly string _path = Path.Combine(Path.GetTempPath(), $"cdg-seek-{Guid.NewGuid():N}.cdg");

    public CdgPipelineSeekTests() => File.WriteAllBytes(_path, []);

    [Fact]
    public async Task Seek_RendersTheTargetOnceWithARebuild()
    {
        var engine = new RecordingCdgEngine();
        var pipeline = new CdgPipeline(engine, new PlaybackClock(), new MediaState());
        pipeline.OpenStandalone(_path);
        try
        {
            Assert.True(pipeline.Seek(42));
            await engine.WaitForRenderAsync(42);
            await Task.Delay(250); // the pump interval plus the settle

            Assert.Equal(["0 incremental", "42 rebuild"], engine.Renders);
        }
        finally
        {
            pipeline.Dispose();
        }
    }

    [Fact]
    public async Task Seek_ClampsToTheDuration()
    {
        var engine = new RecordingCdgEngine();
        var pipeline = new CdgPipeline(engine, new PlaybackClock(), new MediaState());
        pipeline.OpenStandalone(_path);
        try
        {
            Assert.True(pipeline.Seek(500));
            await engine.WaitForRenderAsync(engine.TotalDurationSeconds);
        }
        finally
        {
            pipeline.Dispose();
        }
    }

    [Fact]
    public void Dispose_ClosesTheCdg_AndCanRunTwice()
    {
        var pipeline = new CdgPipeline(new RecordingCdgEngine(), new PlaybackClock(), new MediaState());
        pipeline.OpenStandalone(_path);
        Assert.True(pipeline.Seek(3));

        pipeline.Dispose();
        pipeline.Dispose();

        Assert.False(pipeline.IsOpen);
    }

    [Fact]
    public void Seek_BeforeOpen_IsRefused() =>
        Assert.False(new CdgPipeline(new RecordingCdgEngine(), new PlaybackClock(), new MediaState()).Seek(5));

    public void Dispose() => File.Delete(_path);

    sealed class RecordingCdgEngine : ICdgEngine
    {
        readonly Lock _gate = new();
        readonly List<string> _renders = [];
        readonly List<(double Seconds, TaskCompletionSource Done)> _waiters = [];

        public IReadOnlyList<string> Renders
        {
            get
            {
                lock (_gate)
                    return [.. _renders];
            }
        }

        public Task WaitForRenderAsync(double seconds)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                if (_renders.Contains($"{seconds:0.###} rebuild"))
                    return Task.CompletedTask;

                _waiters.Add((seconds, done));
            }

            return done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public int FrameWidth => CdgConstants.FrameWidth;
        public int FrameHeight => CdgConstants.FrameHeight;
        public uint BackgroundWashArgb => 0;
        public double TotalDurationSeconds => 180;
        public byte BackgroundOpacity { get; set; }
        public uint? BackgroundOverrideArgb { get; set; }

        public void RenderAtSeconds(double seconds) => RenderAtSeconds(seconds, forceRebuild: false);

        public void RenderAtSeconds(double seconds, bool forceRebuild)
        {
            lock (_gate)
            {
                _renders.Add($"{seconds:0.###} {(forceRebuild ? "rebuild" : "incremental")}");
                if (!forceRebuild)
                    return;

                foreach (var waiter in _waiters.Where(w => Math.Abs(w.Seconds - seconds) < 0.001).ToList())
                {
                    _ = _waiters.Remove(waiter);
                    waiter.Done.SetResult();
                }
            }
        }

        public void Load(string filePath) { }
        public void Load(IArchiveEntryInfo archiveEntry) { }
        public void Play() { }
        public void Pause() { }
        public void Stop() { }
        public void Reset() { }
        public void Dispose() { }

#pragma warning disable CS0067 // Events are part of the interface; the pipeline does not raise them here.
        public event EventHandler? FrameInvalidated;
        public event EventHandler? BackgroundWashChanged;
        public event EventHandler<CdgEngineState>? StateChanged;
        public event EventHandler? Opened;
        public event EventHandler? Closed;
        public event EventHandler? PlaybackStarted;
#pragma warning restore CS0067
    }
}
