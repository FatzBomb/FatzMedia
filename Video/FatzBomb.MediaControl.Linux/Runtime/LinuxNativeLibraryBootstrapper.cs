using FatzBomb.MediaControl.Core.Files;
using System.Reflection;
using System.Runtime.InteropServices;

namespace FatzBomb.MediaControl.Linux.Runtime;

/// <summary>Native operations used by the bootstrapper; replaced in tests.</summary>
internal interface ILinuxNativeOperations
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
internal sealed record LinuxNativeLoadResult(
    bool IsBassLoaded,
    bool IsFfmpegLoaded,
    string? BassDirectory,
    string? FfmpegDirectory,
    IReadOnlyList<string> Messages)
{
    public bool IsInitialized => IsBassLoaded && IsFfmpegLoaded;

    public string Message => IsInitialized
        ? $"Native libraries loaded. BASS: {BassDirectory}; FFmpeg: {FfmpegDirectory}."
        : "Failed to load required native libraries. " + string.Join(" ", Messages);
}

/// <summary>
/// Loads host-supplied BASS and FFmpeg shared libraries on Linux. BASS core libraries are
/// dlopen'ed by absolute path and exposed to ManagedBass through a <c>DllImport</c> resolver;
/// add-ons go through <c>BASS_PluginLoad</c>; FFmpeg libraries are preloaded in dependency order
/// and then activated through FFmpeg.AutoGen's root path. A failed attempt can be retried with
/// other paths; a successful one is kept for the process (native libraries cannot be unloaded
/// safely while in use).
/// </summary>
internal sealed class LinuxNativeLibraryBootstrapper(ILinuxNativeOperations operations, string baseDirectory, string runtimeIdentifier)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _loadedBassByFileName = new(StringComparer.Ordinal);
    private LinuxNativeLoadResult? _success;

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
                return _success;

            List<string> messages = [];
            var rejected = new List<string>();
            IReadOnlyList<string> searchPaths = LinuxNativeLibraryLocator.BuildSearchPaths(hostPaths, baseDirectory, runtimeIdentifier, rejected);
            foreach (var path in rejected)
                messages.Add($"Native library path does not exist: {path}.");

            var bassLoaded = LoadBass(searchPaths, messages, out var bassDirectory);
            var ffmpegLoaded = LoadFfmpeg(searchPaths, messages, out var ffmpegDirectory);
            if (searchPaths.Count == 0)
                messages.Add("No native library search path exists.");
            else if (!bassLoaded || !ffmpegLoaded)
                messages.Add($"Search path(s): {string.Join("; ", searchPaths)}.");

            var result = new LinuxNativeLoadResult(bassLoaded, ffmpegLoaded, bassDirectory, ffmpegDirectory, messages);
            LastResult = result;
            if (result.IsInitialized)
                _success = result;

            DiagnosticRouter.Raise(
                result.IsInitialized ? MediaDiagnosticLevel.Info : MediaDiagnosticLevel.Error,
                "LinuxBootstrap",
                result.Message);
            return result;
        }
    }

    private bool LoadBass(IReadOnlyList<string> searchPaths, List<string> messages, out string? bassDirectory)
    {
        bassDirectory = null;
        Dictionary<string, string> found = LinuxNativeLibraryLocator.Locate(
            searchPaths,
            LinuxNativeLibraryCatalog.Bass.Select(static library => library.FileName));

        var requiredOk = true;
        foreach (BassLibrary library in LinuxNativeLibraryCatalog.Bass.Where(static library => library.IsRequired))
        {
            if (_loadedBassByFileName.ContainsKey(library.FileName))
                continue;

            if (!found.TryGetValue(library.FileName, out var path))
            {
                messages.Add($"Missing required BASS library: {library.FileName}.");
                requiredOk = false;
                continue;
            }

            if (!operations.TryLoad(path, out _))
            {
                messages.Add($"Required BASS library was found but did not load: {path}.");
                requiredOk = false;
                continue;
            }

            _loadedBassByFileName[library.FileName] = path;
            FileTypes.Audio.UnionWith(library.Extensions);
        }

        if (!requiredOk)
            return false;

        operations.InstallBassResolver(ResolveBassPath);
        bassDirectory = Path.GetDirectoryName(_loadedBassByFileName["libbass.so"]);

        foreach (BassLibrary library in LinuxNativeLibraryCatalog.Bass.Where(static library => !library.IsRequired))
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

    private bool LoadFfmpeg(IReadOnlyList<string> searchPaths, List<string> messages, out string? ffmpegDirectory)
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

    private string? ResolveBassPath(string libraryName)
    {
        var fileName = LinuxNativeLibraryCatalog.MapDllImportName(libraryName);
        lock (_gate)
        {
            return fileName is not null && _loadedBassByFileName.TryGetValue(fileName, out var path) ? path : null;
        }
    }
}

/// <summary>Real native operations.</summary>
internal sealed class LinuxNativeOperations : ILinuxNativeOperations
{
    private static readonly Lock ResolverGate = new();
    private static readonly HashSet<Assembly> ResolvedAssemblies = [];
    private static Func<string, string?>? _resolvePath;

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
            foreach (Assembly assembly in new[] { typeof(ManagedBass.Bass).Assembly, typeof(ManagedBass.Fx.BassFx).Assembly, typeof(ManagedBass.Midi.BassMidi).Assembly })
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

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
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
