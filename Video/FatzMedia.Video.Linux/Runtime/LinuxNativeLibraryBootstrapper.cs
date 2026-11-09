using System.Reflection;
using System.Runtime.InteropServices;
using FatzMedia.Video.Core.Files;

namespace FatzMedia.Video.Linux.Runtime;

/// <summary>Native operations used by the bootstrapper; replaced in tests.</summary>
interface ILinuxNativeOperations
{
    /// <summary>Loads a shared library by absolute path (dlopen).</summary>
    bool TryLoad(string path, out IntPtr handle);

    /// <summary>Registers a BASS add-on through <c>BASS_PluginLoad</c>.</summary>
    bool TryLoadBassPlugin(string path);

    /// <summary>Points FFmpeg.AutoGen at a directory and verifies that the libraries answer.</summary>
    bool TryActivateFfmpeg(string directory);

    /// <summary>Routes ManagedBass <c>DllImport</c> names to the loaded libraries.</summary>
    void InstallBassResolver(Func<string, string?> resolvePath);
}

/// <summary>Outcome of one native-library initialization.</summary>
sealed record LinuxNativeLoadResult(
    bool IsBassLoaded,
    bool IsFfmpegLoaded,
    string? BassDirectory,
    string? FfmpegDirectory,
    IReadOnlyList<string> Messages,
    NativeDependencyReport Report)
{
    /// <summary>FFmpeg loaded, so a player can start. BASS is optional (ENG-033).</summary>
    public bool IsInitialized => IsFfmpegLoaded;

    /// <summary>BASS loaded, so the player has audio.</summary>
    public bool IsAudioAvailable => IsBassLoaded;

    public string Message => !IsInitialized
        ? "Failed to load required native libraries. " + string.Join(" ", Messages)
        : IsBassLoaded
            ? $"Native libraries loaded. BASS: {BassDirectory}; FFmpeg: {FfmpegDirectory}."
            : $"Native libraries loaded without BASS: audio is unavailable. FFmpeg: {FfmpegDirectory}. " + string.Join(" ", Messages);
}

/// <summary>
/// Loads host-supplied BASS and FFmpeg shared libraries on Linux. BASS core libraries are
/// dlopen'ed by absolute path and exposed to ManagedBass through a <c>DllImport</c> resolver;
/// add-ons go through <c>BASS_PluginLoad</c>; FFmpeg libraries are preloaded in dependency order
/// and then activated through FFmpeg.AutoGen's root path. FFmpeg is required; BASS is optional
/// (ENG-033): without it the player starts with no audio. A failed attempt, or one without BASS,
/// can be retried with other paths (the same paths reuse the result without BASS, ENG-039 P5-8:
/// BASS added to them later is found after a restart); what loaded is kept for the process
/// (native libraries cannot be unloaded safely while in use), so FFmpeg is never activated twice.
/// </summary>
sealed class LinuxNativeLibraryBootstrapper(ILinuxNativeOperations operations, string baseDirectory, string runtimeIdentifier)
{
    readonly Lock _gate = new();
    readonly Dictionary<string, string> _loadedBassByFileName = new(StringComparer.Ordinal);
    LinuxNativeLoadResult? _success;
    // The search paths of _success when it loaded without BASS: a later player with the same paths
    // takes that result instead of scanning them for BASS again (ENG-039 P5-8).
    IReadOnlyList<string>? _successSearchPaths;
    string? _loadedFfmpegDirectory;

    public static LinuxNativeLibraryBootstrapper Default { get; } = new(
        new LinuxNativeOperations(),
        AppContext.BaseDirectory,
        LinuxNativeLibraryLocator.CurrentRuntimeIdentifier);

    public LinuxNativeLoadResult? LastResult { get; private set; }

    public LinuxNativeLoadResult EnsureInitialized(IReadOnlyList<string> hostPaths)
    {
        lock (_gate)
        {
            if (_success is { IsBassLoaded: true })
                return _success;

            List<string> messages = [];
            var rejected = new List<string>();
            var searchPaths = LinuxNativeLibraryLocator.BuildSearchPaths(hostPaths, baseDirectory, runtimeIdentifier, rejected);
            if (_success is { } withoutBass && _successSearchPaths is { } scanned && scanned.SequenceEqual(searchPaths, StringComparer.Ordinal))
            {
                LastResult = withoutBass;
                RaiseResult(withoutBass);
                return withoutBass;
            }

            foreach (var path in rejected)
                messages.Add($"Native library path does not exist: {path}.");

            var bassLoaded = LoadBass(searchPaths, messages, out var bassDirectory);
            var ffmpegLoaded = LoadFfmpeg(searchPaths, messages, out var ffmpegDirectory);
            if (searchPaths.Count == 0)
                messages.Add("No native library search path exists.");
            else if (!bassLoaded || !ffmpegLoaded)
                messages.Add($"Search path(s): {string.Join("; ", searchPaths)}.");

            var report = BuildReport(searchPaths, bassLoaded, ffmpegLoaded, ffmpegDirectory);
            foreach (var check in report.Libraries)
            {
                if (check.Hint is not null)
                    messages.Add(check.Hint);
            }

            var result = new LinuxNativeLoadResult(bassLoaded, ffmpegLoaded, bassDirectory, ffmpegDirectory, messages, report);
            LastResult = result;
            if (result.IsInitialized)
            {
                _success = result;
                _successSearchPaths = searchPaths;
            }

            RaiseResult(result);
            return result;
        }
    }

    // Raised for every player, also when it reuses a result: its diagnostics belong to it.
    static void RaiseResult(LinuxNativeLoadResult result) =>
        DiagnosticRouter.Raise(
            !result.IsInitialized ? MediaDiagnosticLevel.Error
                : result.IsAudioAvailable ? MediaDiagnosticLevel.Info
                : MediaDiagnosticLevel.Warning,
            "LinuxBootstrap",
            result.Message);

    bool LoadBass(IReadOnlyList<string> searchPaths, List<string> messages, out string? bassDirectory)
    {
        bassDirectory = null;
        var found = LinuxNativeLibraryLocator.Locate(
            searchPaths,
            LinuxNativeLibraryCatalog.Bass.Select(static library => library.FileName));

        var coreOk = true;
        foreach (var library in LinuxNativeLibraryCatalog.Bass.Where(static library => library.IsCore))
        {
            if (_loadedBassByFileName.ContainsKey(library.FileName))
                continue;

            if (!found.TryGetValue(library.FileName, out var path))
            {
                messages.Add($"Missing BASS library (audio unavailable): {library.FileName}.");
                coreOk = false;
                continue;
            }

            if (!operations.TryLoad(path, out _))
            {
                messages.Add($"BASS library was found but did not load (audio unavailable): {path}.");
                coreOk = false;
                continue;
            }

            _loadedBassByFileName[library.FileName] = path;
        }

        // Without the full core no BASS function may be called (no resolver, no plugins) and no
        // BASS format is offered.
        if (!coreOk)
            return false;

        foreach (var library in LinuxNativeLibraryCatalog.Bass.Where(static library => library.IsCore))
            FileTypes.Audio.UnionWith(library.Extensions);

        operations.InstallBassResolver(ResolveBassPath);
        bassDirectory = Path.GetDirectoryName(_loadedBassByFileName["libbass.so"]);

        foreach (var library in LinuxNativeLibraryCatalog.Bass.Where(static library => !library.IsCore))
        {
            if (_loadedBassByFileName.ContainsKey(library.FileName))
                continue;

            if (!found.TryGetValue(library.FileName, out var path))
                continue;

            // bassmidi is also called directly by ManagedBass.Midi, so it is dlopen'ed for the
            // resolver as well as registered as a plugin.
            if (library.DllImportName == "bassmidi" && !operations.TryLoad(path, out _))
            {
                messages.Add($"Optional BASS library did not load: {path}.");
                continue;
            }

            if (!operations.TryLoadBassPlugin(path))
            {
                messages.Add($"Optional BASS plugin did not load: {path}.");
                continue;
            }

            _loadedBassByFileName[library.FileName] = path;
            FileTypes.Audio.UnionWith(library.Extensions);
        }

        return true;
    }

    bool LoadFfmpeg(IReadOnlyList<string> searchPaths, List<string> messages, out string? ffmpegDirectory)
    {
        // A retry for BASS keeps the FFmpeg that is already active.
        if (_loadedFfmpegDirectory is not null)
        {
            ffmpegDirectory = _loadedFfmpegDirectory;
            return true;
        }

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
        _loadedFfmpegDirectory = ffmpegDirectory;
        return true;
    }

    NativeDependencyReport BuildReport(IReadOnlyList<string> searchPaths, bool bassLoaded, bool ffmpegLoaded, string? ffmpegDirectory)
    {
        List<NativeLibraryCheck> checks = [];
        // Listing the search paths is only needed to explain a missing file.
        var presentNames = new Lazy<string[]>(() => [.. ListFileNames(searchPaths)]);
        var foundBass = LinuxNativeLibraryLocator.Locate(searchPaths, LinuxNativeLibraryCatalog.Bass.Select(static library => library.FileName));
        // BASS is never required (ENG-033): without its core the report says audio is unavailable.
        foreach (var library in LinuxNativeLibraryCatalog.Bass)
        {
            if (_loadedBassByFileName.TryGetValue(library.FileName, out var loadedPath))
                checks.Add(new NativeLibraryCheck("BASS", library.FileName, IsRequired: false, NativeLibraryStatus.Loaded, loadedPath));
            else if (foundBass.TryGetValue(library.FileName, out var foundPath))
            {
                // Plugins are not attempted when a core library failed.
                var status = library.IsCore || bassLoaded ? NativeLibraryStatus.FailedToLoad : NativeLibraryStatus.NotLoaded;
                checks.Add(new NativeLibraryCheck("BASS", library.FileName, IsRequired: false, status, foundPath));
            }
            else
                checks.Add(Missing("BASS", library.FileName, isRequired: false, presentNames));
        }

        var requiredFfmpeg = LinuxNativeLibraryCatalog.FfmpegInLoadOrder.Where(LinuxNativeLibraryCatalog.RequiredFfmpeg.Contains);
        if (ffmpegLoaded && ffmpegDirectory is not null)
        {
            checks.AddRange(requiredFfmpeg.Select(fileName =>
                new NativeLibraryCheck("FFmpeg", fileName, IsRequired: true, NativeLibraryStatus.Loaded, Path.Combine(ffmpegDirectory, fileName))));
            return new NativeDependencyReport(checks, searchPaths) { IsAudioAvailable = bassLoaded };
        }

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

        return new NativeDependencyReport(checks, searchPaths) { IsAudioAvailable = bassLoaded };
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

    string? ResolveBassPath(string libraryName)
    {
        var fileName = LinuxNativeLibraryCatalog.MapDllImportName(libraryName);
        lock (_gate)
        {
            return fileName is not null && _loadedBassByFileName.TryGetValue(fileName, out var path) ? path : null;
        }
    }
}

/// <summary>Real native operations.</summary>
sealed class LinuxNativeOperations : ILinuxNativeOperations
{
    static readonly Lock ResolverGate = new();
    static readonly HashSet<Assembly> ResolvedAssemblies = [];
    static Func<string, string?>? _resolvePath;

    public bool TryLoad(string path, out IntPtr handle) => NativeLibrary.TryLoad(path, out handle);

    public bool TryLoadBassPlugin(string path) => ManagedBass.Bass.PluginLoad(path) != 0;

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

    public void InstallBassResolver(Func<string, string?> resolvePath)
    {
        lock (ResolverGate)
        {
            _resolvePath = resolvePath;
            foreach (var assembly in new[] { typeof(ManagedBass.Bass).Assembly, typeof(ManagedBass.Fx.BassFx).Assembly, typeof(ManagedBass.Midi.BassMidi).Assembly })
            {
                if (!ResolvedAssemblies.Add(assembly))
                    continue;

                try
                {
                    NativeLibrary.SetDllImportResolver(assembly, Resolve);
                }
                catch (InvalidOperationException exception)
                {
                    // The host installed its own resolver for this assembly; it owns resolution.
                    DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "LinuxBootstrap", $"A DllImport resolver already exists for {assembly.GetName().Name}; using the host's resolver.", innerException: exception);
                }
            }
        }
    }

    static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        Func<string, string?>? resolvePath;
        lock (ResolverGate)
        {
            resolvePath = _resolvePath;
        }

        var path = resolvePath?.Invoke(libraryName);
        return path is not null && NativeLibrary.TryLoad(path, out var handle) ? handle : IntPtr.Zero;
    }
}
