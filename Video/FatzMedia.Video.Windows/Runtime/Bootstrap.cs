using System.Diagnostics;

namespace FatzMedia.Video;

static partial class Bootstrap
{
    // Serializes loading: a concurrent caller waits for the load in progress and gets its result
    // instead of failing with an empty message. A failed load is retried by the next caller.
    // FFmpeg is the only native library the platform loads; the audio engine (FatzAudio) loads its own.
    static readonly Lock InitGate = new();
    static volatile bool _isInitialized;

    internal static bool IsFfmpegLoaded { get; private set; }
    /// <summary>True once FFmpeg loaded: nothing is left to load.</summary>
    public static bool IsInitialized => _isInitialized;
    public static string Message { get; set; } = string.Empty;
    public static IReadOnlyList<string> NativeLoadMessages => [.. _nativeLoadMessages];
    public static NativeDependencyReport NativeDependencies { get; private set; } = NativeDependencyReport.Empty;
    public static bool EnsureInitialized(string? userLibraryPath = null) =>
        EnsureInitialized(ToNativeLibraryPathArray(userLibraryPath));

    /// <summary>Loads the native libraries; true when FFmpeg loaded.</summary>
    public static bool EnsureInitialized(IReadOnlyList<string> nativeLibraryPaths) => Initialize(nativeLibraryPaths);
    static List<string> _searchPaths = [];
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

                _nativeLoadMessages.Clear();
                _searchPaths = candidatePaths;

                IsFfmpegLoaded = LoadFfmpeg();
                _isInitialized = IsFfmpegLoaded;
                Message = IsFfmpegLoaded ? string.Empty : BuildFailureMessage();
            }
            catch (Exception ex)
            {
                Message = $"[Bootstrap] ❌ Exception during initialization: {ex.Message}";
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "Bootstrap", $"Exception during initialization: {ex.Message}", innerException: ex);
                _isInitialized = false;
                return false;
            }
            finally
            {
                // Recorded even when a load step throws, so the report never goes stale.
                NativeDependencies = new NativeDependencyReport([.. _nativeChecks], [.. _searchPaths]);
            }

            return IsFfmpegLoaded;
        }
    }

    static string BuildFailureMessage()
    {
        var searchPathText = _searchPaths.Count == 0
            ? "none"
            : string.Join("; ", _searchPaths);

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
        foreach (var path in _searchPaths)
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
