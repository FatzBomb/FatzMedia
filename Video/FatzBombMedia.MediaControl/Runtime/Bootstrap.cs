//#pragma warning disable IDE0130
using System.Diagnostics;

namespace FatzBombMedia.MediaControl;

internal static partial class Bootstrap
{
    private static int _initOnce;
    private static volatile bool _isInitialized;

    internal static bool IsBassLoaded { get; private set; } = false;
    internal static bool IsFfmpegLoaded { get; private set; } = false;
    public static bool IsInitialized => _isInitialized;
    public static string Message { get; set; } = string.Empty;
    public static IReadOnlyList<string> NativeLoadMessages => [.. _nativeLoadMessages];
    internal static string? RootPath { get; private set; }
    public static string? FfmpegPath => FFmpeg.AutoGen.ffmpeg.RootPath;
    internal static string? BassPath { get; private set; }
    public static bool EnsureInitialized(string? userLibraryPath = null) =>
        EnsureInitialized(ToNativeLibraryPathArray(userLibraryPath));

    public static bool EnsureInitialized(IReadOnlyList<string> nativeLibraryPaths) => Initialize(nativeLibraryPaths);
    private static List<string> searchPaths = [];
    private static readonly List<string> _nativeLoadMessages = [];

    private static bool Initialize(IReadOnlyList<string> nativeLibraryPaths)
    {
        if (_isInitialized) return true;

        if (Interlocked.CompareExchange(ref _initOnce, 1, 0) == 0)
        {
            try
            {
                _nativeLoadMessages.Clear();
                List<string> candidatePaths = [];
                foreach (var nativeLibraryPath in nativeLibraryPaths)
                {
                    var resolvedPath = GetUserPath(nativeLibraryPath);
                    if (resolvedPath != null && !candidatePaths.Contains(resolvedPath, StringComparer.OrdinalIgnoreCase))
                        candidatePaths.Add(resolvedPath);
                }

                RootPath = candidatePaths.FirstOrDefault();

                if (candidatePaths.Count == 0)
                {
                    var appNativePath = GetUserPath("native");
                    if (appNativePath != null)
                        candidatePaths.Add(appNativePath);
                }

                searchPaths = candidatePaths;


                IsBassLoaded = LoadBass();
                IsFfmpegLoaded = LoadFfmpeg();

                _isInitialized = IsBassLoaded && IsFfmpegLoaded;
                if (!_isInitialized)
                {
                    Message = BuildFailureMessage();
                    _ = Interlocked.Exchange(ref _initOnce, 0);
                }
            }
            catch (Exception ex)
            {
                Message = $"[Bootstrap] ❌ Exception during initialization: {ex.Message}";
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "Bootstrap", $"Exception during initialization: {ex.Message}", innerException: ex);
                _isInitialized = false;
                _ = Interlocked.Exchange(ref _initOnce, 0);
                return IsInitialized;
            }
            return IsInitialized;
        }

        return IsInitialized;
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
