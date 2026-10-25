using FatzBomb.MediaControl.Core.Files;
using FatzBomb.MediaControl.Linux.Runtime;

namespace FatzBomb.MediaControl.Linux.Tests;

public sealed class LinuxNativeLibraryCatalogTests
{
    [Theory]
    [InlineData("bass", "libbass.so")]
    [InlineData("bass.dll", "libbass.so")]
    [InlineData("libbass.so", "libbass.so")]
    [InlineData("bass_fx", "libbass_fx.so")]
    [InlineData("BASSMIDI", "libbassmidi.so")]
    [InlineData("bassflac", "libbassflac.so")]
    public void MapDllImportName_MapsBassNames(string name, string expected) =>
        Assert.Equal(expected, LinuxNativeLibraryCatalog.MapDllImportName(name));

    [Theory]
    [InlineData("kernel32")]
    [InlineData("avcodec")]
    [InlineData("")]
    public void MapDllImportName_IgnoresOtherLibraries(string name) =>
        Assert.Null(LinuxNativeLibraryCatalog.MapDllImportName(name));

    [Fact]
    public void Bass_CoreIsBassAndFx()
    {
        Assert.Equal(["libbass.so", "libbass_fx.so"], LinuxNativeLibraryCatalog.Bass.Where(static l => l.IsCore).Select(static l => l.FileName));
        Assert.DoesNotContain(LinuxNativeLibraryCatalog.Bass, static l => l.FileName.Contains("wma", StringComparison.Ordinal));
    }

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
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fbm-linux-" + Guid.NewGuid().ToString("N"));

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

        IReadOnlyList<string> paths = LinuxNativeLibraryLocator.BuildSearchPaths([host, "native"], _root, "linux-x64");

        Assert.Equal([host, native, Path.GetFullPath(_root), rid], paths);
    }

    [Fact]
    public void BuildSearchPaths_ReportsMissingHostPaths()
    {
        var rejected = new List<string>();

        IReadOnlyList<string> paths = LinuxNativeLibraryLocator.BuildSearchPaths([Path.Combine(_root, "missing")], _root, "linux-x64", rejected);

        Assert.Single(rejected);
        Assert.Equal([Path.GetFullPath(_root)], paths);
    }

    [Fact]
    public void Locate_FirstPathWins()
    {
        var first = Directory.CreateDirectory(Path.Combine(_root, "a")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(_root, "b")).FullName;
        File.WriteAllText(Path.Combine(first, "libbass.so"), "");
        File.WriteAllText(Path.Combine(second, "libbass.so"), "");
        File.WriteAllText(Path.Combine(second, "libbass_fx.so"), "");

        Dictionary<string, string> found = LinuxNativeLibraryLocator.Locate([first, second], ["libbass.so", "libbass_fx.so", "libbassflac.so"]);

        Assert.Equal(Path.Combine(first, "libbass.so"), found["libbass.so"]);
        Assert.Equal(Path.Combine(second, "libbass_fx.so"), found["libbass_fx.so"]);
        Assert.False(found.ContainsKey("libbassflac.so"));
    }

    [Fact]
    public void Locate_IsCaseSensitiveOnCaseSensitiveFileSystems()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            return; // default Windows/macOS file systems are case-insensitive

        File.WriteAllText(Path.Combine(_root, "LIBBASS.SO"), "");

        Assert.Empty(LinuxNativeLibraryLocator.Locate([_root], ["libbass.so"]));
    }

    [Fact]
    public void CurrentRuntimeIdentifier_IsLinuxArch() =>
        Assert.Matches("^linux-[a-z0-9]+$", LinuxNativeLibraryLocator.CurrentRuntimeIdentifier);
}

[Collection("File type registry")]
public sealed class LinuxNativeLibraryBootstrapperTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fbm-linux-boot-" + Guid.NewGuid().ToString("N"));

    public LinuxNativeLibraryBootstrapperTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        _ = FileTypes.Audio.Remove(".flac");
    }

    [Fact]
    public void FullSet_LoadsBassThenPluginsThenFfmpegInOrder()
    {
        var dir = CreateLibraries(["libbass.so", "libbass_fx.so", "libbassflac.so", "libbassmidi.so", .. LinuxNativeLibraryCatalog.FfmpegInLoadOrder]);
        var operations = new FakeOperations();
        var bootstrapper = new LinuxNativeLibraryBootstrapper(operations, _root, "linux-x64");

        LinuxNativeLoadResult result = bootstrapper.EnsureInitialized([dir]);

        Assert.True(result.IsInitialized, result.Message);
        Assert.Equal(dir, result.BassDirectory);
        Assert.Equal(dir, result.FfmpegDirectory);
        Assert.Equal(["libbass.so", "libbass_fx.so", "libbassmidi.so", .. LinuxNativeLibraryCatalog.FfmpegInLoadOrder], operations.Loaded.Select(Path.GetFileName));
        Assert.Equal(["libbassflac.so", "libbassmidi.so"], operations.Plugins.Select(Path.GetFileName));
        Assert.Equal(dir, operations.ActivatedFfmpegDirectory);
        Assert.Contains(".flac", FileTypes.Audio);
        Assert.Equal(Path.Combine(dir, "libbass_fx.so"), operations.Resolve!("bass_fx"));
        Assert.Equal(Path.Combine(dir, "libbassmidi.so"), operations.Resolve!("bassmidi"));
        Assert.Null(operations.Resolve!("bassopus"));
    }

    [Fact]
    public void MissingBassFx_StartsWithoutAudioAndSkipsResolverAndPlugins()
    {
        var dir = CreateLibraries(["libbass.so", "libbassflac.so", .. LinuxNativeLibraryCatalog.FfmpegInLoadOrder]);
        var operations = new FakeOperations();

        LinuxNativeLoadResult result = new LinuxNativeLibraryBootstrapper(operations, _root, "linux-x64").EnsureInitialized([dir]);

        Assert.True(result.IsInitialized, result.Message);
        Assert.False(result.IsBassLoaded);
        Assert.False(result.IsAudioAvailable);
        Assert.True(result.IsFfmpegLoaded);
        Assert.StartsWith("Native libraries loaded without BASS: audio is unavailable.", result.Message, StringComparison.Ordinal);
        Assert.Contains("libbass_fx.so", result.Message, StringComparison.Ordinal);
        Assert.Null(operations.Resolve);
        Assert.Empty(operations.Plugins);
        Assert.DoesNotContain(".flac", FileTypes.Audio);
    }

    [Fact]
    public void NoBass_ReportIsTruthfulAndRequiresOnlyFfmpeg()
    {
        var dir = CreateLibraries([.. LinuxNativeLibraryCatalog.FfmpegInLoadOrder]);

        NativeDependencyReport report = new LinuxNativeLibraryBootstrapper(new FakeOperations(), _root, "linux-x64").EnsureInitialized([dir]).Report;

        Assert.False(report.IsAudioAvailable);
        Assert.True(report.RequiredLoaded);
        Assert.Empty(report.RequiredProblems);
        NativeLibraryCheck bass = Single(report, "libbass.so");
        Assert.False(bass.IsRequired);
        Assert.Equal(NativeLibraryStatus.Missing, bass.Status);
        Assert.Equal(NativeDependencyReport.AudioUnavailableLine, report.Describe()[0]);
    }

    [Fact]
    public void NoBass_RetriesBassLaterWithoutActivatingFfmpegAgain()
    {
        var ffmpegOnly = CreateLibraries([.. LinuxNativeLibraryCatalog.FfmpegInLoadOrder]);
        var operations = new FakeOperations();
        var bootstrapper = new LinuxNativeLibraryBootstrapper(operations, _root, "linux-x64");
        Assert.False(bootstrapper.EnsureInitialized([ffmpegOnly]).IsAudioAvailable);
        var bass = CreateLibraries(["libbass.so", "libbass_fx.so"]);

        LinuxNativeLoadResult result = bootstrapper.EnsureInitialized([bass]);

        Assert.True(result.IsAudioAvailable, result.Message);
        Assert.Equal(ffmpegOnly, result.FfmpegDirectory);
        Assert.Equal(1, operations.FfmpegActivations);
        Assert.True(result.Report.IsAudioAvailable);
        Assert.Same(result, bootstrapper.EnsureInitialized([ffmpegOnly]));
    }

    [Fact]
    public void MissingRequiredFfmpeg_Fails()
    {
        var dir = CreateLibraries(["libbass.so", "libbass_fx.so", LinuxNativeLibraryCatalog.FfmpegInLoadOrder[0]]);

        LinuxNativeLoadResult result = new LinuxNativeLibraryBootstrapper(new FakeOperations(), _root, "linux-x64").EnsureInitialized([dir]);

        Assert.True(result.IsBassLoaded);
        Assert.False(result.IsFfmpegLoaded);
        Assert.Contains("FFmpeg", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FfmpegActivationFailure_ReportsAbiMismatch()
    {
        var dir = CreateLibraries(["libbass.so", "libbass_fx.so", .. LinuxNativeLibraryCatalog.FfmpegInLoadOrder]);
        var operations = new FakeOperations { FfmpegActivates = false };

        LinuxNativeLoadResult result = new LinuxNativeLibraryBootstrapper(operations, _root, "linux-x64").EnsureInitialized([dir]);

        Assert.False(result.IsFfmpegLoaded);
        Assert.Contains("did not initialize", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionalLibrariesMissing_StillSucceeds()
    {
        var dir = CreateLibraries(["libbass.so", "libbass_fx.so", .. LinuxNativeLibraryCatalog.RequiredFfmpeg]);

        Assert.True(new LinuxNativeLibraryBootstrapper(new FakeOperations(), _root, "linux-x64").EnsureInitialized([dir]).IsInitialized);
    }

    [Fact]
    public void Success_IsCachedForTheProcess()
    {
        var dir = CreateLibraries(["libbass.so", "libbass_fx.so", .. LinuxNativeLibraryCatalog.RequiredFfmpeg]);
        var operations = new FakeOperations();
        var bootstrapper = new LinuxNativeLibraryBootstrapper(operations, _root, "linux-x64");
        LinuxNativeLoadResult first = bootstrapper.EnsureInitialized([dir]);
        var loads = operations.Loaded.Count;

        LinuxNativeLoadResult second = bootstrapper.EnsureInitialized([Path.Combine(_root, "elsewhere")]);

        Assert.Same(first, second);
        Assert.Equal(loads, operations.Loaded.Count);
    }

    [Fact]
    public void Failure_CanBeRetriedWithAnotherPath()
    {
        var empty = Directory.CreateDirectory(Path.Combine(_root, "empty")).FullName;
        var bootstrapper = new LinuxNativeLibraryBootstrapper(new FakeOperations(), Path.Combine(_root, "app"), "linux-x64");
        Assert.False(bootstrapper.EnsureInitialized([empty]).IsInitialized);
        var dir = CreateLibraries(["libbass.so", "libbass_fx.so", .. LinuxNativeLibraryCatalog.RequiredFfmpeg]);

        Assert.True(bootstrapper.EnsureInitialized([dir]).IsInitialized);
    }

    [Fact]
    public void LoadFailure_OfCoreBass_LeavesAudioUnavailable()
    {
        var dir = CreateLibraries(["libbass.so", "libbass_fx.so", .. LinuxNativeLibraryCatalog.RequiredFfmpeg]);
        var operations = new FakeOperations { FailLoading = "libbass.so" };

        LinuxNativeLoadResult result = new LinuxNativeLibraryBootstrapper(operations, _root, "linux-x64").EnsureInitialized([dir]);

        Assert.True(result.IsInitialized);
        Assert.False(result.IsBassLoaded);
        Assert.Contains("did not load", result.Message, StringComparison.Ordinal);
        Assert.Equal(NativeLibraryStatus.FailedToLoad, Single(result.Report, "libbass.so").Status);
    }

    [Fact]
    public void Report_ListsEveryLibraryAndFlagsAnOldFfmpegVersion()
    {
        // FFmpeg 8 sonames where this control needs FFmpeg 9 (avcodec 63).
        string[] ffmpeg8 = [.. LinuxNativeLibraryCatalog.FfmpegInLoadOrder.Select(static name => DowngradeMajor(name))];
        var dir = CreateLibraries(["libbass.so", "libbass_fx.so", "libbassflac.so", .. ffmpeg8]);

        LinuxNativeLoadResult result = new LinuxNativeLibraryBootstrapper(new FakeOperations(), _root, "linux-x64").EnsureInitialized([dir]);
        NativeDependencyReport report = result.Report;

        Assert.False(report.RequiredLoaded);
        Assert.Equal(NativeLibraryStatus.Loaded, Single(report, "libbass.so").Status);
        Assert.Equal(NativeLibraryStatus.Loaded, Single(report, "libbassflac.so").Status);
        Assert.Equal(NativeLibraryStatus.Missing, Single(report, "libbassopus.so").Status);
        Assert.Contains(report.OptionalProblems, static check => check.FileName == "libbassopus.so");

        var avcodec = $"libavcodec.so.{FFmpeg.AutoGen.ffmpeg.LIBAVCODEC_VERSION_MAJOR}";
        NativeLibraryCheck codec = Single(report, avcodec);
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
        var dir = CreateLibraries(["libbass.so", "libbass_fx.so", .. LinuxNativeLibraryCatalog.FfmpegInLoadOrder]);

        NativeDependencyReport report = new LinuxNativeLibraryBootstrapper(new FakeOperations(), _root, "linux-x64").EnsureInitialized([dir]).Report;

        Assert.True(report.RequiredLoaded);
        Assert.Empty(report.RequiredProblems);
        Assert.All(report.Libraries.Where(static check => check.Component == "FFmpeg"), static check => Assert.Equal(NativeLibraryStatus.Loaded, check.Status));
    }

    [Fact]
    public void Report_FfmpegSplitAcrossFolders_MarksFoundFilesNotLoaded()
    {
        var first = CreateLibraries(["libbass.so", "libbass_fx.so", LinuxNativeLibraryCatalog.FfmpegInLoadOrder[0]]);
        var second = CreateLibraries([.. LinuxNativeLibraryCatalog.FfmpegInLoadOrder.Skip(1)]);

        NativeDependencyReport report = new LinuxNativeLibraryBootstrapper(new FakeOperations(), _root, "linux-x64").EnsureInitialized([first, second]).Report;

        NativeLibraryCheck avutil = Single(report, LinuxNativeLibraryCatalog.FfmpegInLoadOrder[0]);
        Assert.Equal(NativeLibraryStatus.NotLoaded, avutil.Status);
        Assert.Equal(Path.Combine(first, avutil.FileName), avutil.Path);
    }

    private static NativeLibraryCheck Single(NativeDependencyReport report, string fileName) =>
        Assert.Single(report.Libraries, check => check.FileName == fileName);

    private static string DowngradeMajor(string soname)
    {
        var index = soname.LastIndexOf('.');
        return $"{soname[..(index + 1)]}{int.Parse(soname[(index + 1)..], System.Globalization.CultureInfo.InvariantCulture) - 1}";
    }

    private string CreateLibraries(IEnumerable<string> fileNames)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "libs-" + Guid.NewGuid().ToString("N")[..6])).FullName;
        foreach (var fileName in fileNames)
            File.WriteAllText(Path.Combine(dir, fileName), "");
        return dir;
    }

    private sealed class FakeOperations : ILinuxNativeOperations
    {
        public List<string> Loaded { get; } = [];
        public List<string> Plugins { get; } = [];
        public string? ActivatedFfmpegDirectory { get; private set; }
        public int FfmpegActivations { get; private set; }
        public Func<string, string?>? Resolve { get; private set; }
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

        public bool TryLoadBassPlugin(string path)
        {
            Plugins.Add(path);
            return true;
        }

        public bool TryActivateFfmpeg(string directory)
        {
            ActivatedFfmpegDirectory = directory;
            FfmpegActivations++;
            return FfmpegActivates;
        }

        public void InstallBassResolver(Func<string, string?> resolvePath) => Resolve = resolvePath;
    }
}
