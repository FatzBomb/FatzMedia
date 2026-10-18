//#pragma warning disable IDE0130
using System.Diagnostics;

namespace FatzBomb.MediaControl;

internal static partial class Bootstrap
{
    // Serializes loading: a concurrent caller waits for the load in progress and gets its result
    // instead of failing with an empty message. A failed load is retried by the next caller.
    private static readonly Lock InitGate = new();
    private static volatile bool _isInitialized;

    internal static bool IsBassLoaded { get; private set; } = false;
    internal static bool IsFfmpegLoaded { get; private set; } = false;
    public static bool IsInitialized => _isInitialized;
    public static string Message { get; set; } = string.Empty;
    public static IReadOnlyList<string> NativeLoadMessages => [.. _nativeLoadMessages];
    public static NativeDependencyReport NativeDependencies { get; private set; } = NativeDependencyReport.Empty;
    internal static string? RootPath { get; private set; }
    public static string? FfmpegPath => FFmpeg.AutoGen.ffmpeg.RootPath;
    internal static string? BassPath { get; private set; }
    public static bool EnsureInitialized(string? userLibraryPath = null) =>
        EnsureInitialized(ToNativeLibraryPathArray(userLibraryPath));

    public static bool EnsureInitialized(IReadOnlyList<string> nativeLibraryPaths) => Initialize(nativeLibraryPaths);
    private static List<string> searchPaths = [];
    private static readonly List<string> _nativeLoadMessages = [];
    private static readonly List<NativeLibraryCheck> _nativeChecks = [];

    private static bool Initialize(IReadOnlyList<string> nativeLibraryPaths)
    {
        if (_isInitialized) return true;

        lock (InitGate)
        {
            if (_isInitialized) return true;

            try
            {
                _nativeLoadMessages.Clear();
                _nativeChecks.Clear();
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

                RootPath = candidatePaths.FirstOrDefault();
                searchPaths = candidatePaths;


                IsBassLoaded = LoadBass();
                IsFfmpegLoaded = LoadFfmpeg();

                _isInitialized = IsBassLoaded && IsFfmpegLoaded;
                if (!_isInitialized)
                    Message = BuildFailureMessage();
            }
            catch (Exception ex)
            {
                Message = $"[Bootstrap] ❌ Exception during initialization: {ex.Message}";
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "Bootstrap", $"Exception during initialization: {ex.Message}", innerException: ex);
                _isInitialized = false;
                return IsInitialized;
            }
            finally
            {
                // Recorded even when a load step throws, so the report never goes stale.
                NativeDependencies = new NativeDependencyReport([.. _nativeChecks], [.. searchPaths]);
            }

            return IsInitialized;
        }
    }

    private static string BuildFailureMessage()
    {
        var searchPathText = searchPaths.Count == 0
            ? "none"
            : string.Join("; ", searchPaths);

        var details = NativeLoadMessages.Count == 0
            ? string.Empty
            : $" {string.Join(" ", NativeLoadMessages)}";

        return $"Failed to load required native libraries. Search path(s): {searchPathText}.{details}";
    }

    private static void AddNativeCheck(string component, string fileName, bool isRequired, NativeLibraryStatus status, string? path = null)
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

    private static IEnumerable<string> ListSearchPathFileNames()
    {
        foreach (var path in searchPaths)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(path, "*.dll", SearchOption.AllDirectories).Select(Path.GetFileName).OfType<string>().ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
                yield return file;
        }
    }

    private static void AddNativeLoadMessage(string message)
    {
        _nativeLoadMessages.Add(message);
        Debug.WriteLine(message);
    }

    private static string? GetUserPath(string? path)
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

    private static string[] ToNativeLibraryPathArray(string? nativeLibraryPath) => string.IsNullOrWhiteSpace(nativeLibraryPath)
            ? []
            : [nativeLibraryPath];
}
