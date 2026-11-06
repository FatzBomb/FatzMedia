using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Linux.Runtime;
using FatzBomb.MediaControl.Rendering.Software;
using System.Collections.Concurrent;

namespace FatzBomb.MediaControl.Linux.Tests;

/// <summary>
/// A real player for the opt-in Linux native tests with BASS and FFmpeg (026B, 033A, 043A, 039A):
/// native libraries only from the given folders, the software renderer, an optional counting frame
/// sink, and the errors and diagnostics it raised. Set <c>FATZBOMB_LINUX_BASS_DIR</c> and
/// <c>FATZBOMB_LINUX_FFMPEG_DIR</c> (<c>scripts/setup-linux-native.sh</c> prints both) and run a sound
/// server (PulseAudio or PipeWire; a null sink is enough).
/// </summary>
internal sealed class LinuxNativeTestPlayer : IDisposable
{
    /// <summary>The media <c>scripts/setup-linux-native.sh</c> makes.</summary>
    public const string MediaDir = "/opt/fatzbomb-native/media";

    private readonly string _appBase;
    private readonly MediaPresentationFrameSinkHandle? _handle;
    private readonly ConcurrentQueue<string> _errors = new();
    private readonly ConcurrentQueue<string> _diagnostics = new();

    private LinuxNativeTestPlayer(string appBase, MediaControlPlayer player, NativeTestFrameSink sink, MediaPresentationFrameSinkHandle? handle)
    {
        _appBase = appBase;
        Player = player;
        Sink = sink;
        _handle = handle;
        player.MediaError += (_, args) => _errors.Enqueue(args.Message);
        player.MediaDiagnostic += (_, args) => _diagnostics.Enqueue($"{args.Level} {args.Category}: {args.Message}");
    }

    public MediaControlPlayer Player { get; }

    public NativeTestFrameSink Sink { get; }

    public IReadOnlyList<string> Errors => [.. _errors];

    public IReadOnlyList<string> Diagnostics => [.. _diagnostics];

    /// <summary>Gets the BASS and FFmpeg folders from the environment; false when the tests should skip.</summary>
    public static bool TryGetFolders(out string bass, out string ffmpeg)
    {
        bass = Environment.GetEnvironmentVariable("FATZBOMB_LINUX_BASS_DIR") ?? string.Empty;
        ffmpeg = Environment.GetEnvironmentVariable("FATZBOMB_LINUX_FFMPEG_DIR") ?? string.Empty;
        return OperatingSystem.IsLinux()
            && Directory.Exists(bass) && File.Exists(Path.Combine(bass, "libbass.so"))
            && Directory.Exists(ffmpeg);
    }

    /// <param name="nativePaths">The native folders, searched in order (an empty application folder otherwise).</param>
    /// <param name="configuration">The configuration; the loudness cache is turned off unless it names a folder.</param>
    /// <param name="attachSurface">Attaches a 640x360 counting frame sink as the primary surface.</param>
    public static LinuxNativeTestPlayer Create(IReadOnlyList<string> nativePaths, MediaPlayerConfiguration? configuration = null, bool attachSurface = true)
    {
        var appBase = Directory.CreateTempSubdirectory("fbm-native-").FullName;
        var bootstrapper = new LinuxNativeLibraryBootstrapper(new LinuxNativeOperations(), appBase, LinuxNativeLibraryLocator.CurrentRuntimeIdentifier);
        var factory = new LinuxMediaControlPlatformFactory(new SoftwarePresentationCoordinatorFactory(), LinuxPlatformOptions.Default, bootstrapper);
        MediaPlayerConfiguration config = configuration ?? new MediaPlayerConfiguration(VideoDecodingMethod.Software);
        if (config.Normalization.CacheDirectory is null)
            config = config with { Normalization = config.Normalization with { CacheDirectory = string.Empty } };

        var player = new MediaControlPlayer(factory, config, nativePaths);
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
internal sealed class NativeTestFrameSink : IMediaPresentationFrameSink
{
    private int _frames;
    private volatile bool _lastFrameHasContent;

    public int Frames => Volatile.Read(ref _frames);

    public bool LastFrameHasContent => _lastFrameHasContent;

    /// <summary>The blue value of the last presented frame's centre pixel.</summary>
    public int LastCentreBlue => Volatile.Read(ref _lastCentreBlue);

    private int _lastCentreBlue;

    public void PresentFrame(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride)
    {
        ReadOnlySpan<byte> row = bgraPixels.Slice(height / 2 * stride, width * 4);
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
