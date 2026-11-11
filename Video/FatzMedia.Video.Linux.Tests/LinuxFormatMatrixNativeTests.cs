using FatzMedia.Video.Core.Abstractions;
using Xunit.Abstractions;

namespace FatzMedia.Video.Linux.Tests;

/// <summary>
/// Opt-in format matrix on the FatzAudio engine (ENG-043, 033A slice 2, FatzAudio's format answer): the
/// file set of <c>scripts/smoke-backlog.ps1</c> test 1, made by <c>scripts/setup-linux-native.sh</c> in
/// <c>/opt/fatzbomb-native/media/formats</c>. Every file must load and play on FatzAudio
/// (null output); which path it took (FatzAudio native, FFmpeg conversion, video file played as audio) is printed,
/// not asserted. Needs <c>FATZBOMB_LINUX_FFMPEG_DIR</c> (see
/// <see cref="LinuxNativeTestPlayer"/>); skips without them.
/// </summary>
[Collection("File type registry")]
public sealed class LinuxFormatMatrixNativeTests(ITestOutputHelper output)
{
    static readonly string Formats = Path.Combine(LinuxNativeTestPlayer.MediaDir, "formats");

    static readonly string[] Files =
    [
        "tone.mp3", "tone.mp2", "tone.ac3", "tone-aac.m4a", "tone-alac.m4a", "tone-alac.caf", "tone.m4r",
        "tone.flac", "tone.wav", "tone.aiff", "tone.ogg", "tone.opus", "tone.wma", "tone.wv", "tone.tta",
        "tone.mka", "tone.au", "tone.dts", "audio-only.mp4", "audio-only.ts", "cover-only.mp4",
        "divx-mp3.avi", "mpeg2-mp2.mpg", "video-aac.mp4",
    ];

    public static TheoryData<string> Matrix
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var file in Files)
                data.Add(file);

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void File_LoadsAndPlays(string file)
    {
        var path = Path.Combine(Formats, file);
        if (!LinuxNativeTestPlayer.TryGetFfmpegFolder(out var ffmpeg) || !File.Exists(path))
            return;

        using var session = LinuxNativeTestPlayer.Create([ffmpeg]);
        var loaded = session.Player.Load(path);
        var playing = loaded && session.PlayUntil(0.5, TimeSpan.FromSeconds(10));
        var route = Route(session);
        output.WriteLine($"| {file} | loaded {loaded} | playing {playing} | {route} |");
        foreach (var line in session.Diagnostics.Where(static l => l.StartsWith("Warning", StringComparison.Ordinal) || l.StartsWith("Error", StringComparison.Ordinal)))
            output.WriteLine("    " + line);
        Assert.True(loaded, $"{file} did not load: {string.Join(" | ", session.Errors)}");
        Assert.True(playing, $"{file} did not play: position {session.Player.Position}, {string.Join(" | ", session.Errors)}");
    }

    [Fact]
    public void LongFile_ConvertedToATempFile_IsRemovedOnClose()
    {
        var path = Path.Combine(Formats, "long-10min.wv");
        if (!LinuxNativeTestPlayer.TryGetFfmpegFolder(out var ffmpeg) || !File.Exists(path))
            return;

        var tempFolder = Path.Combine(Path.GetTempPath(), "FatzMediaVideo");
        var before = WavFiles(tempFolder);
        using (var session = LinuxNativeTestPlayer.Create([ffmpeg], attachSurface: false))
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

    static string Route(LinuxNativeTestPlayer session)
    {
        if (session.HasDiagnostic("has no video stream") || session.HasDiagnostic("opening it as audio"))
            return "video container without picture: FFmpeg soundtrack path";
        if (session.HasDiagnostic("with FFmpeg in"))
            return "FFmpeg conversion, played by FatzAudio";
        if (session.HasDiagnostic("FatzAudioEngine: Audio opened"))
            return "FatzAudio decodes it";
        return session.Player.Content.HasFlag(MediaContent.Video) ? "video pipeline (FFmpeg picture and soundtrack)" : "no FatzAudio open reported";
    }

    static string[] WavFiles(string folder) =>
        Directory.Exists(folder) ? Directory.GetFiles(folder, "*.wav") : [];
}
