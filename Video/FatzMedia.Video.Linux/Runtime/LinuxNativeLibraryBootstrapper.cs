using System.Runtime.InteropServices;
using FatzMedia.Video.Core.Files;

namespace FatzMedia.Video.Linux.Runtime;

/// <summary>Native operations used by the bootstrapper; replaced in tests.</summary>
interface ILinuxNativeOperations
{
    /// <summary>Loads a shared library by absolute path (dlopen).</summary>
    bool TryLoad(string path, out IntPtr handle);

    /// <summary>Points FFmpeg.AutoGen at a directory and verifies that the libraries answer.</summary>
    bool TryActivateFfmpeg(string directory);
}

/// <summary>Outcome of one native-library initialization.</summary>
sealed record LinuxNativeLoadResult(
    bool IsFfmpegLoaded,
    string? FfmpegDirectory,
    IReadOnlyList<string> Messages,
    NativeDependencyReport Report)
{
    /// <summary>FFmpeg loaded, so a player can start.</summary>
    public bool IsInitialized => IsFfmpegLoaded;

    public string Message => IsInitialized
        ? $"Native libraries loaded. FFmpeg: {FfmpegDirectory}."
        : "Failed to load required native libraries. " + string.Join(" ", Messages);
}

/// <summary>
/// Loads the host-supplied FFmpeg shared libraries on Linux: preloaded in dependency order, then activated
/// through FFmpeg.AutoGen's root path. The audio engine (FatzAudio) loads its own library. A failed attempt can
/// be retried with other paths; a success is kept for the process (native libraries cannot be unloaded safely
/// while in use), so FFmpeg is never activated twice.
/// </summary>
sealed class LinuxNativeLibraryBootstrapper(ILinuxNativeOperations operations, string baseDirectory, string runtimeIdentifier)
{
    readonly Lock _gate = new();
    LinuxNativeLoadResult? _success;

    public static LinuxNativeLibraryBootstrapper Default { get; } = new(
        new LinuxNativeOperations(),
        AppContext.BaseDirectory,
        LinuxNativeLibraryLocator.CurrentRuntimeIdentifier);

    public LinuxNativeLoadResult? LastResult { get; private set; }

    public LinuxNativeLoadResult EnsureInitialized(IReadOnlyList<string> hostPaths)
    {
        lock (_gate)
        {
            if (_success is not null)
            {
                RaiseResult(_success);
                return _success;
            }

            List<string> messages = [];
            var rejected = new List<string>();
            var searchPaths = LinuxNativeLibraryLocator.BuildSearchPaths(hostPaths, baseDirectory, runtimeIdentifier, rejected);
            foreach (var path in rejected)
                messages.Add($"Native library path does not exist: {path}.");

            var ffmpegLoaded = LoadFfmpeg(searchPaths, messages, out var ffmpegDirectory);
            if (searchPaths.Count == 0)
                messages.Add("No native library search path exists.");
            else if (!ffmpegLoaded)
                messages.Add($"Search path(s): {string.Join("; ", searchPaths)}.");

            var report = BuildReport(searchPaths, ffmpegLoaded, ffmpegDirectory);
            foreach (var check in report.Libraries)
            {
                if (check.Hint is not null)
                    messages.Add(check.Hint);
            }

            var result = new LinuxNativeLoadResult(ffmpegLoaded, ffmpegDirectory, messages, report);
            LastResult = result;
            if (result.IsInitialized)
                _success = result;

            RaiseResult(result);
            return result;
        }
    }

    // Raised for every player, also when it reuses a result: its diagnostics belong to it.
    static void RaiseResult(LinuxNativeLoadResult result) =>
        DiagnosticRouter.Raise(
            result.IsInitialized ? MediaDiagnosticLevel.Info : MediaDiagnosticLevel.Error,
            "LinuxBootstrap",
            result.Message);
    bool LoadFfmpeg(IReadOnlyList<string> searchPaths, List<string> messages, out string? ffmpegDirectory)
    {
        ffmpegDirectory = searchPaths.FirstOrDefault(path =>
            LinuxNativeLibraryCatalog.RequiredFfmpeg.All(fileName => File.Exists(Path.Combine(path, fileName))));
        if (ffmpegDirectory is null)
        {
            messages.Add($"Missing required FFmpeg libraries in one directory: {string.Join(", ", LinuxNativeLibraryCatalog.RequiredFfmpeg.Order(StringComparer.Ordinal))}.");
            return false;
        }

        foreach (var fileName in LinuxNativeLibraryCatalog.FfmpegInLoadOrder)
        {
            var path = Path.Combine(ffmpegDirectory, fileName);
            var required = LinuxNativeLibraryCatalog.RequiredFfmpeg.Contains(fileName);
            if (!File.Exists(path))
                continue;

            if (!operations.TryLoad(path, out _) && required)
            {
                messages.Add($"Required FFmpeg library was found but did not load: {path}.");
                ffmpegDirectory = null;
                return false;
            }
        }

        if (!operations.TryActivateFfmpeg(ffmpegDirectory))
        {
            messages.Add($"FFmpeg libraries in {ffmpegDirectory} loaded but did not initialize (ABI mismatch with FFmpeg.AutoGen?).");
            ffmpegDirectory = null;
            return false;
        }

        FileTypes.Video.UnionWith(LinuxNativeLibraryCatalog.VideoExtensions);
        FileTypes.RegisterFfmpegAudio();
        return true;
    }

    static NativeDependencyReport BuildReport(IReadOnlyList<string> searchPaths, bool ffmpegLoaded, string? ffmpegDirectory)
    {
        List<NativeLibraryCheck> checks = [];
        var requiredFfmpeg = LinuxNativeLibraryCatalog.FfmpegInLoadOrder.Where(LinuxNativeLibraryCatalog.RequiredFfmpeg.Contains);
        if (ffmpegLoaded && ffmpegDirectory is not null)
        {
            checks.AddRange(requiredFfmpeg.Select(fileName =>
                new NativeLibraryCheck("FFmpeg", fileName, IsRequired: true, NativeLibraryStatus.Loaded, Path.Combine(ffmpegDirectory, fileName))));
            return new NativeDependencyReport(checks, searchPaths);
        }

        // Listing the search paths is only needed to explain a missing file.
        var presentNames = new Lazy<string[]>(() => [.. ListFileNames(searchPaths)]);
        // A folder with the full set that still failed is a load failure; otherwise report
        // which files exist somewhere and which do not.
        var completeDirectory = searchPaths.FirstOrDefault(path =>
            LinuxNativeLibraryCatalog.RequiredFfmpeg.All(fileName => File.Exists(Path.Combine(path, fileName))));
        var foundFfmpeg = LinuxNativeLibraryLocator.Locate(searchPaths, LinuxNativeLibraryCatalog.RequiredFfmpeg);
        foreach (var fileName in requiredFfmpeg)
        {
            if (completeDirectory is not null)
                checks.Add(new NativeLibraryCheck("FFmpeg", fileName, IsRequired: true, NativeLibraryStatus.FailedToLoad, Path.Combine(completeDirectory, fileName)));
            else if (foundFfmpeg.TryGetValue(fileName, out var foundPath))
                checks.Add(new NativeLibraryCheck("FFmpeg", fileName, IsRequired: true, NativeLibraryStatus.NotLoaded, foundPath));
            else
                checks.Add(Missing("FFmpeg", fileName, isRequired: true, presentNames));
        }

        return new NativeDependencyReport(checks, searchPaths);
    }
    static NativeLibraryCheck Missing(string component, string fileName, bool isRequired, Lazy<string[]> presentNames) =>
        new(component, fileName, isRequired, NativeLibraryStatus.Missing, Hint: NativeDependencyReport.FindVersionHint(fileName, presentNames.Value));

    static IEnumerable<string> ListFileNames(IReadOnlyList<string> searchPaths)
    {
        foreach (var path in searchPaths)
        {
            string[] files;
            try
            {
                files = [.. Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Select(Path.GetFileName).OfType<string>()];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
                yield return file;
        }
    }
}

/// <summary>Real native operations.</summary>
sealed class LinuxNativeOperations : ILinuxNativeOperations
{
    public bool TryLoad(string path, out IntPtr handle) => NativeLibrary.TryLoad(path, out handle);

    public unsafe bool TryActivateFfmpeg(string directory)
    {
        var previous = FFmpeg.AutoGen.ffmpeg.RootPath;
        try
        {
            FFmpeg.AutoGen.ffmpeg.RootPath = directory;
            if (FFmpeg.AutoGen.ffmpeg.av_guess_format("avi", null, null) != null)
                return true;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or NotSupportedException or InvalidOperationException)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "LinuxBootstrap", $"FFmpeg activation failed: {exception.Message}", innerException: exception);
        }

        FFmpeg.AutoGen.ffmpeg.RootPath = previous;
        return false;
    }
}
