using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Audio;
using FatzMedia.Video.Core.Configuration;
using Xunit.Abstractions;

namespace FatzMedia.Video.Linux.Tests;

/// <summary>
/// Opt-in format matrix on both audio engines (ENG-043, 033A slice 2, FatzAudio's format answer): the
/// file set of <c>scripts/smoke-backlog.ps1</c> test 1, made by <c>scripts/setup-linux-native.sh</c> in
/// <c>/opt/fatzbomb-native/media/formats</c>. Every file must load and play on BASS and on FatzAudio
/// (null output); which path it took (native, FFmpeg conversion, video file played as audio) is printed,
/// not asserted. Needs <c>FATZBOMB_LINUX_BASS_DIR</c> and <c>FATZBOMB_LINUX_FFMPEG_DIR</c> (see
/// <see cref="LinuxNativeTestPlayer"/>); skips without them.
/// </summary>
[Collection("File type registry")]
public sealed class LinuxFormatMatrixNativeTests(ITestOutputHelper output)
{
    private static readonly string Formats = Path.Combine(LinuxNativeTestPlayer.MediaDir, "formats");

    private static readonly string[] Files =
    [
        "tone.mp3", "tone.mp2", "tone.ac3", "tone-aac.m4a", "tone-alac.m4a", "tone-alac.caf", "tone.m4r",
        "tone.flac", "tone.wav", "tone.aiff", "tone.ogg", "tone.opus", "tone.wma", "tone.wv", "tone.tta",
        "tone.mka", "tone.au", "tone.dts", "audio-only.mp4", "audio-only.ts", "cover-only.mp4",
        "divx-mp3.avi", "mpeg2-mp2.mpg", "video-aac.mp4",
    ];

    public static TheoryData<string, string> Matrix
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var engine in new[] { "bass", "fatz" })
            {
                foreach (var file in Files)
                    data.Add(engine, file);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void File_LoadsAndPlays(string engine, string file)
    {
        var path = Path.Combine(Formats, file);
        if (!LinuxNativeTestPlayer.TryGetFolders(out var bass, out var ffmpeg) || !File.Exists(path))
            return;

        using var session = LinuxNativeTestPlayer.Create([bass, ffmpeg], Configuration(engine));
        var loaded = session.Player.Load(path);
        var playing = loaded && session.PlayUntil(0.5, TimeSpan.FromSeconds(10));
        var route = Route(session);
        output.WriteLine($"| {engine} | {file} | loaded {loaded} | playing {playing} | {route} |");
        foreach (var line in session.Diagnostics.Where(static l => l.StartsWith("Warning", StringComparison.Ordinal) || l.StartsWith("Error", StringComparison.Ordinal)))
            output.WriteLine("    " + line);
        Assert.True(loaded, $"{file} on {engine} did not load: {string.Join(" | ", session.Errors)}");
        Assert.True(playing, $"{file} on {engine} did not play: position {session.Player.Position}, {string.Join(" | ", session.Errors)}");
    }

    [Fact]
    public void LongFile_ConvertedToATempFile_IsRemovedOnClose()
    {
        var path = Path.Combine(Formats, "long-10min.wv");
        if (!LinuxNativeTestPlayer.TryGetFolders(out var bass, out var ffmpeg) || !File.Exists(path))
            return;

        var tempFolder = Path.Combine(Path.GetTempPath(), "FatzMediaVideo");
        var before = WavFiles(tempFolder);
        using (var session = LinuxNativeTestPlayer.Create([bass, ffmpeg], attachSurface: false))
        {
            Assert.True(session.Player.Load(path), string.Join(" | ", session.Errors));
            Assert.True(session.PlayUntil(0.5, TimeSpan.FromSeconds(30)), $"position {session.Player.Position}");
            output.WriteLine(string.Join(Environment.NewLine, session.Diagnostics.Where(static l => l.Contains("FFmpeg", StringComparison.Ordinal))));
            Assert.True(session.WaitForDiagnostic("temp file"), "the 10-minute conversion did not go to a temp file");
            Assert.NotEmpty(WavFiles(tempFolder).Except(before));
            session.Player.Close();
        }

        Assert.True(SpinWait.SpinUntil(() => !WavFiles(tempFolder).Except(before).Any(), TimeSpan.FromSeconds(5)),
            "temp WAV left behind: " + string.Join(", ", WavFiles(tempFolder).Except(before)));
    }

    [Fact]
    public void Flac_WithoutBassFlac_PlaysThroughTheFfmpegConversion()
    {
        var path = Path.Combine(Formats, "tone.flac");
        if (!LinuxNativeTestPlayer.TryGetFolders(out var bass, out var ffmpeg) || !File.Exists(path))
            return;

        // BASS plug-ins stay loaded for the process: once another test loaded libbassflac, a player
        // without it still decodes FLAC natively. Run this test on its own to check the fallback
        // (dotnet test --filter Flac_WithoutBassFlac).
        if (File.ReadAllText("/proc/self/maps").Contains("libbassflac", StringComparison.Ordinal))
        {
            output.WriteLine("skipped: libbassflac is already loaded in this test process; run this test on its own");
            return;
        }

        // BASS without its FLAC plug-in (ENG-039 batch 1, 033A slice 2 step 1).
        var noFlac = Directory.CreateTempSubdirectory("fbm-no-bassflac-").FullName;
        try
        {
            foreach (var library in Directory.GetFiles(bass, "*.so").Where(static f => !Path.GetFileName(f).Contains("flac", StringComparison.Ordinal)))
                File.Copy(library, Path.Combine(noFlac, Path.GetFileName(library)));

            using var session = LinuxNativeTestPlayer.Create([noFlac, ffmpeg], attachSurface: false);
            Assert.True(session.Player.NativeDependencies.IsAudioAvailable);
            Assert.True(session.Player.Load(path), string.Join(" | ", session.Errors));
            Assert.True(session.PlayUntil(0.5, TimeSpan.FromSeconds(10)), $"position {session.Player.Position}");
            output.WriteLine(Route(session));
            Assert.True(session.WaitForDiagnostic("with FFmpeg in"), "the FLAC did not go through the FFmpeg conversion");
        }
        finally
        {
            Directory.Delete(noFlac, recursive: true);
        }
    }

    private static MediaPlayerConfiguration Configuration(string engine) =>
        new(VideoDecodingMethod.Software)
        {
            AudioEngineFactory = engine == "fatz" ? new FatzAudioEngineFactory(nullOutput: true) : null,
        };

    private static string Route(LinuxNativeTestPlayer session)
    {
        if (session.HasDiagnostic("has no video stream") || session.HasDiagnostic("opening it as audio"))
            return "video container without picture: FFmpeg soundtrack path";
        if (session.HasDiagnostic("with FFmpeg in"))
            return session.HasDiagnostic("FatzAudioEngine: Audio opened") ? "FFmpeg conversion, played by FatzAudio" : "FFmpeg conversion, played by BASS";
        if (session.HasDiagnostic("FatzAudioEngine: Audio opened"))
            return "FatzAudio decodes it";
        return session.Player.Content.HasFlag(MediaContent.Video) ? "video pipeline (FFmpeg picture and soundtrack)" : "BASS decodes it";
    }

    private static string[] WavFiles(string folder) =>
        Directory.Exists(folder) ? Directory.GetFiles(folder, "*.wav") : [];
}
