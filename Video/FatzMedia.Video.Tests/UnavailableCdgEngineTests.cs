using FatzMedia.Video.Pipeline;

namespace FatzMedia.Video.Tests;

/// <summary>Without the audio engine (FatzAudio draws CD+G) the CD+G engine shows nothing and warns once.</summary>
public sealed class UnavailableCdgEngineTests
{
    [Fact]
    public void Load_WarnsOnce_AndShowsNothing()
    {
        List<MediaDiagnosticInfo> diagnostics = [];
        void Capture(MediaDiagnosticInfo info) => diagnostics.Add(info);
        DiagnosticRouter.DiagnosticOccurred += Capture;
        try
        {
            using var engine = new UnavailableCdgEngine();

            engine.Load("song.cdg");
            engine.Load("another.cdg");
            engine.RenderAtSeconds(1.5, forceRebuild: true);
            engine.Play();
            engine.Stop();

            var warning = Assert.Single(diagnostics, static info => info.Category == "CDG");
            Assert.Equal(MediaDiagnosticLevel.Warning, warning.Level);
            Assert.Contains("FatzAudio", warning.Message, StringComparison.Ordinal);
            Assert.Equal(0, engine.TotalDurationSeconds);
        }
        finally
        {
            DiagnosticRouter.DiagnosticOccurred -= Capture;
        }
    }
}
