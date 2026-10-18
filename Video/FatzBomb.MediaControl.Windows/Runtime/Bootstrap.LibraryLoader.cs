using FatzBomb.MediaControl.Core.Files;
using FFmpeg.AutoGen;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MBass = ManagedBass.Bass;

namespace FatzBomb.MediaControl;

internal static partial class Bootstrap
{
    public static bool LoadBass()
    {
        if (IsBassLoaded) return true;

        // A retry after a failed initialization replaces this component's earlier results.
        _ = _nativeChecks.RemoveAll(static check => check.Component == "BASS");

        (string dllFile, string[] extensions, bool isRequired)[] libraryFiles =
        [
            ("bass.dll", [".aif", ".aiff", ".mp3", ".mp3pro", ".ogg", ".wav", ".mod", ".it", ".s3m", ".xm", ".mo3", ".mtm", ".umx"], true),
                ("bass_fx.dll", [], true),
                ("bass_aac.dll", [".aac", ".m4a", ".m4b"], false),
                ("bass_ac3.dll", [".ac3"], false),
                ("bassalac.dll", [".aac", ".m4a"], false),
                ("basscd.dll", [".cda"], false),
                ("bassflac.dll", [".flac"], false),
                ("bassmidi.dll", [".kar", ".mid", ".midi", ".rmi"], false),
                ("bassopus.dll", [".opus"], false),
                ("basswma.dll", [".wma"], false)
        ];

        bool loadSuccess;
        string successMessage;
        var foundRequired = 0;
        var foundOptional = 0;
        var totalRequired = libraryFiles.Count(l => l.isRequired);
        var totalOptional = libraryFiles.Count(l => !l.isRequired);
        List<string> missingRequired = [];
        List<string> missingOptional = [];
        List<string> failedRequired = [];
        List<string> failedOptional = [];

        Dictionary<string, string> found = DirectoryScanner.LocateFiles(searchPaths, libraryFiles.Select(f => f.dllFile), true, CompatibilityLevel.Compatible);

        foreach ((var dllFile, var extensions, var isRequired) in libraryFiles)
            if (found.TryGetValue(dllFile, out var loadPath))
            {
                if (File.Exists(loadPath))
                {
                    var loaded = isRequired
                        ? LoadLibrary(loadPath) != IntPtr.Zero
                        : MBass.PluginLoad(loadPath) != 0;

                    AddNativeCheck("BASS", dllFile, isRequired, loaded ? NativeLibraryStatus.Loaded : NativeLibraryStatus.FailedToLoad, loadPath);
                    if (loaded)
                    {
                        FileTypes.Audio.UnionWith(extensions);
                        DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "BASS", $"{dllFile} loaded from {loadPath}");
                        if (isRequired)
                            foundRequired++;
                        else
                            foundOptional++;
                    }
                    else
                        if (isRequired)
                        {
                            failedRequired.Add(dllFile);
                            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "BASS", $"{dllFile} loading failed from {loadPath} (required)");
                        }
                        else
                        {
                            failedOptional.Add(dllFile);
                            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "BASS", $"{dllFile} loading failed from {loadPath} (optional)");
                        }
                }
            }
            else
            {
                AddNativeCheck("BASS", dllFile, isRequired, NativeLibraryStatus.Missing);
                if (isRequired)
                {
                    missingRequired.Add(dllFile);
                    DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "BASS", $"{dllFile} not found (required)");
                }
                else
                {
                    missingOptional.Add(dllFile);
                    DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "BASS", $"{dllFile} not found (optional)");
                }

            }

        loadSuccess = foundRequired >= totalRequired;
        if (missingRequired.Count > 0)
            AddNativeLoadMessage($"Missing required BASS DLLs: {string.Join(", ", missingRequired)}.");
        if (failedRequired.Count > 0)
            AddNativeLoadMessage($"Required BASS DLLs were found but did not load: {string.Join(", ", failedRequired)}.");
        if (missingOptional.Count > 0)
            AddNativeLoadMessage($"Optional BASS plugins not found: {string.Join(", ", missingOptional)}.");
        if (failedOptional.Count > 0)
            AddNativeLoadMessage($"Optional BASS plugins were found but did not load: {string.Join(", ", failedOptional)}.");

        successMessage = loadSuccess ? "✅ Bass Loaded!" : "❌ Bass Failed!!!";
        successMessage += $"  [Required: {foundRequired}/{totalRequired}]  [Optional: {foundOptional}/{totalOptional}]";
        DiagnosticRouter.Raise(
            loadSuccess ? MediaDiagnosticLevel.Info : MediaDiagnosticLevel.Error,
            "Bootstrap",
            loadSuccess ? "BASS loaded successfully" : $"BASS failed: {successMessage}");

        return loadSuccess;
    }



    public static bool LoadFfmpeg()
    {
        if (IsFfmpegLoaded) return true;

        // A retry after a failed initialization replaces this component's earlier results.
        _ = _nativeChecks.RemoveAll(static check => check.Component == "FFmpeg");

        string[] supportedFileTypes = [".3g2", ".3gp", ".asf", ".avi", ".f4v", ".flv", ".m2ts", ".m2v", ".m4v", ".mkv", ".mov", ".mp4", ".mpeg",
                                          ".mpg", ".mts", ".mxf", ".ogm", ".ogv", ".qt", ".rm", ".rmvb", ".ts", ".vob", ".webm", ".wmv"];

        string[] libraryFiles =
        [
            $"avcodec-{ffmpeg.LIBAVCODEC_VERSION_MAJOR}.dll",
                $"avdevice-{ffmpeg.LIBAVDEVICE_VERSION_MAJOR}.dll",
                $"avfilter-{ffmpeg.LIBAVFILTER_VERSION_MAJOR}.dll",
                $"avformat-{ffmpeg.LIBAVFORMAT_VERSION_MAJOR}.dll",
                $"avutil-{ffmpeg.LIBAVUTIL_VERSION_MAJOR}.dll",
                $"swresample-{ffmpeg.LIBSWRESAMPLE_VERSION_MAJOR}.dll",
                $"swscale-{ffmpeg.LIBSWSCALE_VERSION_MAJOR}.dll"
        ];

        bool ok;
        var foundPath = DirectoryScanner.LocateFilesDirectory(searchPaths, libraryFiles, CompatibilityLevel.Compatible) ?? string.Empty;
        List<string> missingRequired = [];

        if (!string.IsNullOrEmpty(foundPath))
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "FFmpeg", $"Path found: {foundPath}");
            var tempPath = ffmpeg.RootPath;
            ffmpeg.RootPath = foundPath;
            string? loadError = null;
            try
            {
                unsafe { ok = ffmpeg.av_guess_format("avi", null, null) != null; }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or NotSupportedException)
            {
                // FFmpeg.AutoGen throws, rather than returning null, when a DLL in the set cannot
                // load (mixed builds, wrong architecture, or a missing dependency).
                ok = false;
                loadError = ex.Message;
            }

            foreach (var dllFile in libraryFiles)
                AddNativeCheck("FFmpeg", dllFile, isRequired: true, ok ? NativeLibraryStatus.Loaded : NativeLibraryStatus.FailedToLoad, Path.Combine(foundPath, dllFile));

            if (ok)
            {
                FileTypes.Video.UnionWith(supportedFileTypes);
                FileTypes.RegisterFfmpegAudio();
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "FFmpeg", "All FFmpeg libraries loaded successfully");
                return true;
            }
            else
            {
                AddNativeLoadMessage(loadError is null
                    ? "FFmpeg DLLs were found but did not load successfully."
                    : $"FFmpeg DLLs were found but did not load successfully: {loadError}");
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "FFmpeg", "FFmpeg libraries found but failed to initialize");
                ffmpeg.RootPath = tempPath;
                return false;
            }
        }
        else
        {
            // No folder holds the full set: report which files exist anywhere and which do not.
            Dictionary<string, string> partial = DirectoryScanner.LocateFiles(searchPaths, libraryFiles, true, CompatibilityLevel.Compatible);
            foreach (var dllFile in libraryFiles)
            {
                if (partial.TryGetValue(dllFile, out var partialPath))
                {
                    AddNativeCheck("FFmpeg", dllFile, isRequired: true, NativeLibraryStatus.NotLoaded, partialPath);
                    continue;
                }

                AddNativeCheck("FFmpeg", dllFile, isRequired: true, NativeLibraryStatus.Missing);
                missingRequired.Add(dllFile);
            }

            var problem = missingRequired.Count == 0
                ? "FFmpeg DLLs are split across folders; all seven must be in one folder."
                : $"Missing required FFmpeg DLLs: {string.Join(", ", missingRequired)}.";
            AddNativeLoadMessage(problem);
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "FFmpeg", problem);
            Debug.WriteLine($"❌ FFmpeg failed to load!   [Files found: {libraryFiles.Length - missingRequired.Count}/{libraryFiles.Length}]   [Successful Load: 0/{libraryFiles.Length}]");
            return false;
        }
    }

    [LibraryImport("kernel32.dll",
    SetLastError = true,
    EntryPoint = "LoadLibraryW",
    StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr LoadLibrary(string lpLibFileName);
}
