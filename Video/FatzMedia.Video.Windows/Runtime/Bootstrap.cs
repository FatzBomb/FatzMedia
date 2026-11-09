using System.Diagnostics;

namespace FatzMedia.Video;

static partial class Bootstrap
{
    // Serializes loading: a concurrent caller waits for the load in progress and gets its result
    // instead of failing with an empty message. A failed load is retried by the next caller.
    // FFmpeg is required; BASS is optional (ENG-033): without it the player starts with no audio,
    // and a later caller with other paths retries BASS while the loaded FFmpeg stays as it is.
    static readonly Lock InitGate = new();
    static volatile bool _isInitialized;
    // The search paths of the last load that found FFmpeg but not BASS: the next player with the
    // same paths keeps that outcome instead of scanning them for BASS again (ENG-039 P5-8). Other
    // paths still retry BASS.
    static List<string>? _withoutBassSearchPaths;

    internal static bool IsBassLoaded { get; private set; } = false;
    internal static bool IsFfmpegLoaded { get; private set; } = false;
    /// <summary>True once FFmpeg and BASS both loaded: nothing is left to load.</summary>
    public static bool IsInitialized => _isInitialized;
    public static string Message { get; set; } = string.Empty;
    public static IReadOnlyList<string> NativeLoadMessages => [.. _nativeLoadMessages];
    public static NativeDependencyReport NativeDependencies { get; private set; } = NativeDependencyReport.Empty;
    public static bool EnsureInitialized(string? userLibraryPath = null) =>
        EnsureInitialized(ToNativeLibraryPathArray(userLibraryPath));

    /// <summary>Loads the native libraries; true when FFmpeg loaded (BASS may still be missing: see <see cref="IsBassLoaded"/>).</summary>
    public static bool EnsureInitialized(IReadOnlyList<string> nativeLibraryPaths) => Initialize(nativeLibraryPaths);
    static List<string> searchPaths = [];
    static readonly List<string> _nativeLoadMessages = [];
    static readonly List<NativeLibraryCheck> _nativeChecks = [];

    static bool Initialize(IReadOnlyList<string> nativeLibraryPaths)
    {
        if (_isInitialized) return true;

        lock (InitGate)
        {
            if (_isInitialized) return true;

            try
            {
                List<string> candidatePaths = [];
                foreach (var nativeLibraryPath in nativeLibraryPaths)
                {
                    var resolvedPath = GetUserPath(nativeLibraryPath);
                    if (resolvedPath != null && !candidatePaths.Contains(resolvedPath, StringComparer.OrdinalIgnoreCase))
                        candidatePaths.Add(resolvedPath);
                }

                foreach (var defaultPath in WindowsNativeLibraryPaths.GetDefaultSearchPaths())
                {
                    if (!candidatePaths.Contains(defaultPath, StringComparer.OrdinalIgnoreCase))
                        candidatePaths.Add(defaultPath);
                }

                if (IsFfmpegLoaded && _withoutBassSearchPaths is { } scanned && scanned.SequenceEqual(candidatePaths, StringComparer.OrdinalIgnoreCase))
                {
                    // This player's diagnostics still say that audio is unavailable.
                    DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "Bootstrap", Message);
                    return true;
                }

                // Each loader replaces its own checks, so a BASS retry keeps the FFmpeg ones.
                _nativeLoadMessages.Clear();
                searchPaths = candidatePaths;


                IsBassLoaded = LoadBass();
                IsFfmpegLoaded = LoadFfmpeg();

                _isInitialized = IsBassLoaded && IsFfmpegLoaded;
                _withoutBassSearchPaths = IsFfmpegLoaded && !IsBassLoaded ? candidatePaths : null;
                Message = !IsFfmpegLoaded
                    ? BuildFailureMessage()
                    : !IsBassLoaded
                        ? $"Native libraries loaded without BASS: audio is unavailable. {string.Join(" ", NativeLoadMessages)}".TrimEnd()
                        : string.Empty;
            }
            catch (Exception ex)
            {
                Message = $"[Bootstrap] ❌ Exception during initialization: {ex.Message}";
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "Bootstrap", $"Exception during initialization: {ex.Message}", innerException: ex);
                _isInitialized = false;
                _withoutBassSearchPaths = null;
                return false;
            }
            finally
            {
                // Recorded even when a load step throws, so the report never goes stale.
                NativeDependencies = new NativeDependencyReport([.. _nativeChecks], [.. searchPaths]) { IsAudioAvailable = IsBassLoaded };
            }

            return IsFfmpegLoaded;
        }
    }

    static string BuildFailureMessage()
    {
        var searchPathText = searchPaths.Count == 0
            ? "none"
            : string.Join("; ", searchPaths);

        var details = NativeLoadMessages.Count == 0
            ? string.Empty
            : $" {string.Join(" ", NativeLoadMessages)}";

        return $"Failed to load required native libraries. Search path(s): {searchPathText}.{details}";
    }

    static void AddNativeCheck(string component, string fileName, bool isRequired, NativeLibraryStatus status, string? path = null)
    {
        string? hint = null;
        if (status == NativeLibraryStatus.Missing)
        {
            hint = NativeDependencyReport.FindVersionHint(fileName, ListSearchPathFileNames());
            if (hint is not null)
                AddNativeLoadMessage(hint);
        }

        _nativeChecks.Add(new NativeLibraryCheck(component, fileName, isRequired, status, path, hint));
    }

    static IEnumerable<string> ListSearchPathFileNames()
    {
        foreach (var path in searchPaths)
        {
            IEnumerable<string> files;
            try
            {
                files = [.. Directory.EnumerateFiles(path, "*.dll", SearchOption.AllDirectories).Select(Path.GetFileName).OfType<string>()];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
                yield return file;
        }
    }

    static void AddNativeLoadMessage(string message)
    {
        _nativeLoadMessages.Add(message);
        Debug.WriteLine(message);
    }

    static string? GetUserPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (Path.IsPathRooted(path))
        {
            if (Directory.Exists(path))
                return Path.GetFullPath(path);

            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "Bootstrap", $"Specified path does not exist: '{path}'");
            return null;
        }

        var candidate = Path.Combine(AppContext.BaseDirectory, path.Trim());

        if (!Directory.Exists(candidate))
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "Bootstrap", $"AppBase relative path does not exist: '{candidate}'");
            return null;
        }

        return Path.GetFullPath(candidate);
    }

    static string[] ToNativeLibraryPathArray(string? nativeLibraryPath) => string.IsNullOrWhiteSpace(nativeLibraryPath)
            ? []
            : [nativeLibraryPath];
}
