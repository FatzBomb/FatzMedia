using FatzMedia.Video.Core.Files;
using FatzMedia.Video.Linux.Runtime;
using Xunit.Abstractions;

namespace FatzMedia.Video.Linux.Tests;

public sealed class LinuxNativeLibraryCatalogTests
{
    [Fact]
    public void Ffmpeg_LoadOrderPutsDependenciesFirst()
    {
        List<string> order = [.. LinuxNativeLibraryCatalog.FfmpegInLoadOrder];
        int IndexOf(string prefix) => order.FindIndex(name => name.StartsWith(prefix, StringComparison.Ordinal));

        Assert.Equal(0, IndexOf("libavutil.so."));
        Assert.True(IndexOf("libswresample.so.") < IndexOf("libavcodec.so."));
        Assert.True(IndexOf("libavcodec.so.") < IndexOf("libavformat.so."));
        Assert.True(IndexOf("libavformat.so.") < IndexOf("libavdevice.so."));
        Assert.Subset(order.ToHashSet(), LinuxNativeLibraryCatalog.RequiredFfmpeg.ToHashSet());
    }

    [Fact]
    public void Ffmpeg_NamesCarrySonameMajor() =>
        Assert.All(LinuxNativeLibraryCatalog.FfmpegInLoadOrder, static name => Assert.Matches(@"^lib[a-z]+\.so\.\d+$", name));
}

public sealed class LinuxNativeLibraryLocatorTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "fbm-linux-" + Guid.NewGuid().ToString("N"));

    public LinuxNativeLibraryLocatorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void BuildSearchPaths_HostPathsFirstThenExistingDefaults()
    {
        var host = Directory.CreateDirectory(Path.Combine(_root, "host")).FullName;
        var native = Directory.CreateDirectory(Path.Combine(_root, "native")).FullName;
        var rid = Directory.CreateDirectory(Path.Combine(_root, "runtimes", "linux-x64", "native")).FullName;

        var paths = LinuxNativeLibraryLocator.BuildSearchPaths([host, "native"], _root, "linux-x64");

        Assert.Equal([host, native, Path.GetFullPath(_root), rid], paths);
    }

    [Fact]
    public void BuildSearchPaths_ReportsMissingHostPaths()
    {
        var rejected = new List<string>();

        var paths = LinuxNativeLibraryLocator.BuildSearchPaths([Path.Combine(_root, "missing")], _root, "linux-x64", rejected);

        Assert.Single(rejected);
        Assert.Equal([Path.GetFullPath(_root)], paths);
    }

    [Fact]
    public void Locate_FirstPathWins()
    {
        var first = Directory.CreateDirectory(Path.Combine(_root, "a")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(_root, "b")).FullName;
        File.WriteAllText(Path.Combine(first, "libone.so"), "");
        File.WriteAllText(Path.Combine(second, "libone.so"), "");
        File.WriteAllText(Path.Combine(second, "libtwo.so"), "");

        var found = LinuxNativeLibraryLocator.Locate([first, second], ["libone.so", "libtwo.so", "libthree.so"]);

        Assert.Equal(Path.Combine(first, "libone.so"), found["libone.so"]);
        Assert.Equal(Path.Combine(second, "libtwo.so"), found["libtwo.so"]);
        Assert.False(found.ContainsKey("libthree.so"));
    }

    [Fact]
    public void Locate_IsCaseSensitiveOnCaseSensitiveFileSystems()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            return; // default Windows/macOS file systems are case-insensitive

        File.WriteAllText(Path.Combine(_root, "LIBONE.SO"), "");

        Assert.Empty(LinuxNativeLibraryLocator.Locate([_root], ["libone.so"]));
    }

    [Fact]
    public void CurrentRuntimeIdentifier_IsLinuxArch() =>
        Assert.Matches("^linux-[a-z0-9]+$", LinuxNativeLibraryLocator.CurrentRuntimeIdentifier);
}


[Collection("File type registry")]
public sealed class LinuxNativeLibraryBootstrapperTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "fbm-linux-boot-" + Guid.NewGuid().ToString("N"));
    readonly ITestOutputHelper _output;

    public LinuxNativeLibraryBootstrapperTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void FullSet_LoadsFfmpegInOrderAndRegistersItsFormats()
    {
        var dir = CreateLibraries([.. LinuxNativeLibraryCatalog.FfmpegInLoadOrder]);
        var operations = new FakeOperations();
        var bootstrapper = new LinuxNativeLibraryBootstrapper(operations, _root, "linux-x64");

        var result = bootstrapper.EnsureInitialized([dir]);

        Assert.True(result.IsInitialized, result.Message);
        Assert.True(result.IsFfmpegLoaded);
        Assert.Equal(dir, result.FfmpegDirectory);
        Assert.Equal(LinuxNativeLibraryCatalog.FfmpegInLoadOrder, operations.Loaded.Select(Path.GetFileName));
        Assert.Equal(dir, operations.ActivatedFfmpegDirectory);
        Assert.Contains(".flac", FileTypes.FfmpegAudio);
        Assert.Contains(".mkv", FileTypes.Video);
    }

    [Fact]
    public void Report_RequiresOnlyFfmpeg()
    {
        var dir = CreateLibraries([.. LinuxNativeLibraryCatalog.FfmpegInLoadOrder]);

        var report = new LinuxNativeLibraryBootstrapper(new FakeOperations(), _root, "linux-x64").EnsureInitialized([dir]).Report;

        Assert.True(report.RequiredLoaded);
        Assert.Empty(report.RequiredProblems);
        Assert.Empty(report.OptionalProblems);
        Assert.Empty(report.Describe());
        Assert.All(report.Libraries, static check => Assert.Equal("FFmpeg", check.Component));
    }

    [Fact]
    public void MissingRequiredFfmpeg_Fails()
    {
        var dir = CreateLibraries([LinuxNativeLibraryCatalog.FfmpegInLoadOrder[0]]);

        var result = new LinuxNativeLibraryBootstrapper(new FakeOperations(), _root, "linux-x64").EnsureInitialized([dir]);

        Assert.False(result.IsInitialized);
        Assert.False(result.IsFfmpegLoaded);
        Assert.Contains("FFmpeg", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FfmpegActivationFailure_ReportsAbiMismatch()
    {
        var dir = CreateLibraries([.. LinuxNativeLibraryCatalog.FfmpegInLoadOrder]);
        var operations = new FakeOperations { FfmpegActivates = false };

        var result = new LinuxNativeLibraryBootstrapper(operations, _root, "linux-x64").EnsureInitialized([dir]);

        Assert.False(result.IsFfmpegLoaded);
        Assert.Contains("did not initialize", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionalLibrariesMissing_StillSucceeds()
    {
        var dir = CreateLibraries([.. LinuxNativeLibraryCatalog.RequiredFfmpeg]);

        Assert.True(new LinuxNativeLibraryBootstrapper(new FakeOperations(), _root, "linux-x64").EnsureInitialized([dir]).IsInitialized);
    }

    [Fact]
    public void Success_IsCachedForTheProcess()
    {
        var dir = CreateLibraries([.. LinuxNativeLibraryCatalog.RequiredFfmpeg]);
        var operations = new FakeOperations();
        var bootstrapper = new LinuxNativeLibraryBootstrapper(operations, _root, "linux-x64");
        var first = bootstrapper.EnsureInitialized([dir]);
        var loads = operations.Loaded.Count;

        var second = bootstrapper.EnsureInitialized([Path.Combine(_root, "elsewhere")]);

        Assert.Same(first, second);
        Assert.Equal(loads, operations.Loaded.Count);
        Assert.Equal(1, operations.FfmpegActivations);
    }

    [Fact]
    public void Failure_CanBeRetriedWithAnotherPath()
    {
        var empty = Directory.CreateDirectory(Path.Combine(_root, "empty")).FullName;
        var bootstrapper = new LinuxNativeLibraryBootstrapper(new FakeOperations(), Path.Combine(_root, "app"), "linux-x64");
        Assert.False(bootstrapper.EnsureInitialized([empty]).IsInitialized);
        var dir = CreateLibraries([.. LinuxNativeLibraryCatalog.RequiredFfmpeg]);

        Assert.True(bootstrapper.EnsureInitialized([dir]).IsInitialized);
    }

    [Fact]
    public void LoadFailure_OfRequiredFfmpeg_FailsAndNamesTheFile()
    {
        var dir = CreateLibraries([.. LinuxNativeLibraryCatalog.RequiredFfmpeg]);
        var failing = LinuxNativeLibraryCatalog.RequiredFfmpeg.Order(StringComparer.Ordinal).First();
        var operations = new FakeOperations { FailLoading = failing };

        var result = new LinuxNativeLibraryBootstrapper(operations, _root, "linux-x64").EnsureInitialized([dir]);

        Assert.False(result.IsInitialized);
        Assert.Contains("did not load", result.Message, StringComparison.Ordinal);
        Assert.Equal(NativeLibraryStatus.FailedToLoad, Single(result.Report, failing).Status);
    }

    [Fact]
    public void Report_ListsEveryLibraryAndFlagsAnOldFfmpegVersion()
    {
        // FFmpeg 8 sonames where this control needs FFmpeg 9 (avcodec 63).
        string[] ffmpeg8 = [.. LinuxNativeLibraryCatalog.FfmpegInLoadOrder.Select(static name => DowngradeMajor(name))];
        var dir = CreateLibraries(ffmpeg8);

        var result = new LinuxNativeLibraryBootstrapper(new FakeOperations(), _root, "linux-x64").EnsureInitialized([dir]);
        var report = result.Report;

        Assert.False(report.RequiredLoaded);
        var avcodec = $"libavcodec.so.{FFmpeg.AutoGen.ffmpeg.LIBAVCODEC_VERSION_MAJOR}";
        var codec = Single(report, avcodec);
        Assert.Equal(NativeLibraryStatus.Missing, codec.Status);
        Assert.True(codec.IsRequired);
        Assert.Contains(DowngradeMajor(avcodec), codec.Hint, StringComparison.Ordinal);
        Assert.Contains(codec.Hint!, result.Message, StringComparison.Ordinal);
        Assert.Contains(report.Describe(), line => line.StartsWith($"FFmpeg {avcodec} (required) is missing.", StringComparison.Ordinal));
        Assert.Equal([dir], report.SearchPaths.Where(path => path == dir));
    }

    [Fact]
    public void Report_FullSet_IsAllLoaded()
    {
        var dir = CreateLibraries([.. LinuxNativeLibraryCatalog.FfmpegInLoadOrder]);

        var report = new LinuxNativeLibraryBootstrapper(new FakeOperations(), _root, "linux-x64").EnsureInitialized([dir]).Report;

        Assert.True(report.RequiredLoaded);
        Assert.Empty(report.RequiredProblems);
        Assert.All(report.Libraries.Where(static check => check.Component == "FFmpeg"), static check => Assert.Equal(NativeLibraryStatus.Loaded, check.Status));
    }

    [Fact]
    public void Report_FfmpegSplitAcrossFolders_MarksFoundFilesNotLoaded()
    {
        var first = CreateLibraries([LinuxNativeLibraryCatalog.FfmpegInLoadOrder[0]]);
        var second = CreateLibraries([.. LinuxNativeLibraryCatalog.FfmpegInLoadOrder.Skip(1)]);

        var report = new LinuxNativeLibraryBootstrapper(new FakeOperations(), _root, "linux-x64").EnsureInitialized([first, second]).Report;

        var avutil = Single(report, LinuxNativeLibraryCatalog.FfmpegInLoadOrder[0]);
        Assert.Equal(NativeLibraryStatus.NotLoaded, avutil.Status);
        Assert.Equal(Path.Combine(first, avutil.FileName), avutil.Path);
    }

    /// <summary>
    /// ENG-039 P5-8: after a success the next players, with the same or other paths, reuse the result
    /// without any scan or load.
    /// </summary>
    [Fact]
    public void AfterSuccess_TheNextPlayers_ReuseTheResult()
    {
        var dir = CreateLibraries([.. LinuxNativeLibraryCatalog.FfmpegInLoadOrder]);
        var operations = new FakeOperations();
        var bootstrapper = new LinuxNativeLibraryBootstrapper(operations, _root, "linux-x64");
        var first = bootstrapper.EnsureInitialized([dir]);
        Assert.True(first.IsInitialized);
        var loads = operations.Loaded.Count;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (var player = 0; player < 200; player++)
            Assert.Same(first, bootstrapper.EnsureInitialized([dir]));

        _output.WriteLine($"P5-8 bootstrap after a success: {stopwatch.Elapsed.TotalMilliseconds / 200:F3} ms per player");
        Assert.Equal(loads, operations.Loaded.Count);
        Assert.Same(first, bootstrapper.LastResult);
    }

    static NativeLibraryCheck Single(NativeDependencyReport report, string fileName) =>
        Assert.Single(report.Libraries, check => check.FileName == fileName);

    static string DowngradeMajor(string soname)
    {
        var index = soname.LastIndexOf('.');
        return $"{soname[..(index + 1)]}{int.Parse(soname[(index + 1)..], System.Globalization.CultureInfo.InvariantCulture) - 1}";
    }

    string CreateLibraries(IEnumerable<string> fileNames)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "libs-" + Guid.NewGuid().ToString("N")[..6])).FullName;
        foreach (var fileName in fileNames)
            File.WriteAllText(Path.Combine(dir, fileName), "");
        return dir;
    }

    sealed class FakeOperations : ILinuxNativeOperations
    {
        public List<string> Loaded { get; } = [];
        public string? ActivatedFfmpegDirectory { get; private set; }
        public int FfmpegActivations { get; private set; }
        public bool FfmpegActivates { get; init; } = true;
        public string? FailLoading { get; init; }

        public bool TryLoad(string path, out IntPtr handle)
        {
            handle = IntPtr.Zero;
            if (Path.GetFileName(path) == FailLoading)
                return false;

            Loaded.Add(path);
            handle = new IntPtr(Loaded.Count);
            return true;
        }

        public bool TryActivateFfmpeg(string directory)
        {
            ActivatedFfmpegDirectory = directory;
            FfmpegActivations++;
            return FfmpegActivates;
        }
    }
}
