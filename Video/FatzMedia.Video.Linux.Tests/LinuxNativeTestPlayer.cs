using System.Collections.Concurrent;
using FatzMedia.Video.Audio;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Linux.Runtime;
using FatzMedia.Video.Software;

namespace FatzMedia.Video.Linux.Tests;

/// <summary>
/// A real player for the opt-in Linux native tests with FFmpeg and FatzAudio (026B, 033A, 043A, 039A):
/// FFmpeg only from the given folders, the software renderer, FatzAudio on its null output device (no sound
/// card or sound server needed), an optional counting frame sink, and the errors and diagnostics it raised.
/// Set <c>FATZBOMB_LINUX_FFMPEG_DIR</c> (<c>scripts/setup-linux-native.sh</c> prints it); FatzAudio's native
/// library comes from Audio/ (<see cref="FatzAudioNativeGroup.CanRun"/>).
/// </summary>
sealed class LinuxNativeTestPlayer : IDisposable
{
    /// <summary>The media <c>scripts/setup-linux-native.sh</c> makes.</summary>
    public const string MediaDir = "/opt/fatzbomb-native/media";

    readonly string _appBase;
    readonly MediaPresentationFrameSinkHandle? _handle;
    readonly ConcurrentQueue<string> _errors = new();
    readonly ConcurrentQueue<string> _diagnostics = new();

    LinuxNativeTestPlayer(string appBase, PlayerCore player, NativeTestFrameSink sink, MediaPresentationFrameSinkHandle? handle)
    {
        _appBase = appBase;
        Player = player;
        Sink = sink;
        _handle = handle;
        player.MediaError += (_, args) => _errors.Enqueue(args.Message);
        player.MediaDiagnostic += (_, args) => _diagnostics.Enqueue($"{args.Level} {args.Category}: {args.Message}");
    }

    public PlayerCore Player { get; }

    public NativeTestFrameSink Sink { get; }

    public IReadOnlyList<string> Errors => [.. _errors];

    public IReadOnlyList<string> Diagnostics => [.. _diagnostics];

    /// <summary>Gets the FFmpeg folder from the environment; false when the tests should skip.</summary>
    public static bool TryGetFfmpegFolder(out string ffmpeg)
    {
        ffmpeg = Environment.GetEnvironmentVariable("FATZBOMB_LINUX_FFMPEG_DIR") ?? string.Empty;
        return OperatingSystem.IsLinux() && FatzAudioNativeGroup.CanRun && Directory.Exists(ffmpeg);
    }

    /// <param name="nativePaths">The native folders, searched in order (an empty application folder otherwise).</param>
    /// <param name="configuration">The configuration; the audio engine is FatzAudio's null device unless it sets a factory, and the loudness cache is turned off unless it names a folder.</param>
    /// <param name="attachSurface">Attaches a 640x360 counting frame sink as the primary surface.</param>
    public static LinuxNativeTestPlayer Create(IReadOnlyList<string> nativePaths, MediaPlayerConfiguration? configuration = null, bool attachSurface = true)
    {
        var appBase = Directory.CreateTempSubdirectory("fbm-native-").FullName;
        var bootstrapper = new LinuxNativeLibraryBootstrapper(new LinuxNativeOperations(), appBase, LinuxNativeLibraryLocator.CurrentRuntimeIdentifier);
        var factory = new LinuxPlatformFactory(new SoftwarePresentationCoordinatorFactory(), LinuxPlatformOptions.Default, bootstrapper);
        var config = configuration ?? new MediaPlayerConfiguration(VideoDecodingMethod.Software);
        if (config.Normalization.CacheDirectory is null)
            config = config with { Normalization = config.Normalization with { CacheDirectory = string.Empty } };
        if (config.AudioEngineFactory is null)
            config = config with { AudioEngineFactory = new FatzAudioEngineFactory(nullOutput: true) };

        var player = new PlayerCore(factory, config, nativePaths);
        var sink = new NativeTestFrameSink();
        MediaPresentationFrameSinkHandle? handle = null;
        if (attachSurface && config.Mode != MediaPlayerMode.Audio)
        {
            handle = new MediaPresentationFrameSinkHandle(sink);
            Assert.True(player.AttachPresentationSurface(new MediaPresentationSurfaceDescriptor(
                MediaPresentationSurfaceDescriptor.PrimarySurfaceId,
                MediaPresentationSurfaceRole.Primary,
                handle.CreateSurfaceDescriptor(),
                640,
                360)));
        }

        return new LinuxNativeTestPlayer(appBase, player, sink, handle);
    }

    /// <summary>Whether any diagnostic so far contains <paramref name="text"/>.</summary>
    public bool HasDiagnostic(string text) => _diagnostics.Any(line => line.Contains(text, StringComparison.OrdinalIgnoreCase));

    /// <summary>Waits up to 5 s for a diagnostic containing <paramref name="text"/> (host events arrive asynchronously).</summary>
    public bool WaitForDiagnostic(string text) => SpinWait.SpinUntil(() => HasDiagnostic(text), TimeSpan.FromSeconds(5));

    /// <summary>Plays and waits until the position passes <paramref name="seconds"/>.</summary>
    public bool PlayUntil(double seconds, TimeSpan timeout) =>
        Player.Play() && SpinWait.SpinUntil(() => Player.Position.TotalSeconds > seconds, timeout);

    public void Dispose()
    {
        if (_handle is not null)
            _ = Player.DetachPresentationSurface(MediaPresentationSurfaceDescriptor.PrimarySurfaceId);
        Player.Dispose();
        _handle?.Dispose();
        try { Directory.Delete(_appBase, recursive: true); } catch (IOException) { }
    }
}

/// <summary>Counts presented frames and checks the last one is not blank (a middle row with more than 8 values).</summary>
sealed class NativeTestFrameSink : IMediaPresentationFrameSink
{
    int _frames;
    volatile bool _lastFrameHasContent;

    public int Frames => Volatile.Read(ref _frames);

    public bool LastFrameHasContent => _lastFrameHasContent;

    /// <summary>The blue value of the last presented frame's centre pixel.</summary>
    public int LastCentreBlue => Volatile.Read(ref _lastCentreBlue);

    int _lastCentreBlue;

    public void PresentFrame(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride)
    {
        var row = bgraPixels.Slice(height / 2 * stride, width * 4);
        var distinct = new HashSet<byte>();
        foreach (var value in row)
        {
            if (distinct.Add(value) && distinct.Count > 8)
                break;
        }

        _lastFrameHasContent = distinct.Count > 8;
        Volatile.Write(ref _lastCentreBlue, row[width / 2 * 4]);
        _ = Interlocked.Increment(ref _frames);
    }
}
