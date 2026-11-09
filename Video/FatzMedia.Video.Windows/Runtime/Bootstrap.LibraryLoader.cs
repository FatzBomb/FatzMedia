using System.Diagnostics;
using System.Runtime.InteropServices;
using FatzMedia.Video.Core.Files;
using FFmpeg.AutoGen;
using MBass = ManagedBass.Bass;

namespace FatzMedia.Video;

static partial class Bootstrap
{
    public static bool LoadBass()
    {
        if (IsBassLoaded) return true;

        // A retry after a failed initialization replaces this component's earlier results.
        _ = _nativeChecks.RemoveAll(static check => check.Component == "BASS");

        (string dllFile, string[] extensions, bool isCore)[] libraryFiles =
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

        // BASS is optional (ENG-033): without bass.dll and bass_fx.dll the player starts with no
        // audio. Plugins load through BASS_PluginLoad, so they are attempted only once both core
        // DLLs loaded: no BASS function is called without them.
        var foundCore = 0;
        var foundOptional = 0;
        var totalCore = libraryFiles.Count(l => l.isCore);
        var totalOptional = libraryFiles.Count(l => !l.isCore);
        List<string> missingCore = [];
        List<string> missingOptional = [];
        List<string> failedCore = [];
        List<string> failedOptional = [];
        List<string> coreExtensions = [];

        var found = DirectoryScanner.LocateFiles(_searchPaths, libraryFiles.Select(f => f.dllFile), true, CompatibilityLevel.Compatible);

        foreach ((var dllFile, var extensions, _) in libraryFiles.Where(static l => l.isCore))
        {
            if (!found.TryGetValue(dllFile, out var loadPath) || !File.Exists(loadPath))
            {
                AddNativeCheck("BASS", dllFile, isRequired: false, NativeLibraryStatus.Missing);
                missingCore.Add(dllFile);
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "BASS", $"{dllFile} not found (audio unavailable)");
                continue;
            }

            var loaded = LoadLibrary(loadPath) != IntPtr.Zero;
            AddNativeCheck("BASS", dllFile, isRequired: false, loaded ? NativeLibraryStatus.Loaded : NativeLibraryStatus.FailedToLoad, loadPath);
            if (loaded)
            {
                coreExtensions.AddRange(extensions);
                foundCore++;
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "BASS", $"{dllFile} loaded from {loadPath}");
            }
            else
            {
                failedCore.Add(dllFile);
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "BASS", $"{dllFile} loading failed from {loadPath} (audio unavailable)");
            }
        }

        var loadSuccess = foundCore >= totalCore;
        if (loadSuccess)
            FileTypes.Audio.UnionWith(coreExtensions);

        foreach ((var dllFile, var extensions, _) in libraryFiles.Where(static l => !l.isCore))
        {
            if (!found.TryGetValue(dllFile, out var loadPath) || !File.Exists(loadPath))
            {
                AddNativeCheck("BASS", dllFile, isRequired: false, NativeLibraryStatus.Missing);
                missingOptional.Add(dllFile);
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "BASS", $"{dllFile} not found (optional)");
                continue;
            }

            if (!loadSuccess)
            {
                AddNativeCheck("BASS", dllFile, isRequired: false, NativeLibraryStatus.NotLoaded, loadPath);
                continue;
            }

            var loaded = MBass.PluginLoad(loadPath) != 0;
            AddNativeCheck("BASS", dllFile, isRequired: false, loaded ? NativeLibraryStatus.Loaded : NativeLibraryStatus.FailedToLoad, loadPath);
            if (loaded)
            {
                FileTypes.Audio.UnionWith(extensions);
                foundOptional++;
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "BASS", $"{dllFile} loaded from {loadPath}");
            }
            else
            {
                failedOptional.Add(dllFile);
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "BASS", $"{dllFile} loading failed from {loadPath} (optional)");
            }
        }

        if (missingCore.Count > 0)
            AddNativeLoadMessage($"Missing BASS DLLs (audio unavailable): {string.Join(", ", missingCore)}.");
        if (failedCore.Count > 0)
            AddNativeLoadMessage($"BASS DLLs were found but did not load (audio unavailable): {string.Join(", ", failedCore)}.");
        if (missingOptional.Count > 0)
            AddNativeLoadMessage($"Optional BASS plugins not found: {string.Join(", ", missingOptional)}.");
        if (failedOptional.Count > 0)
            AddNativeLoadMessage($"Optional BASS plugins were found but did not load: {string.Join(", ", failedOptional)}.");

        var summary = $"[Core: {foundCore}/{totalCore}]  [Optional: {foundOptional}/{totalOptional}]";
        DiagnosticRouter.Raise(
            loadSuccess ? MediaDiagnosticLevel.Info : MediaDiagnosticLevel.Warning,
            "Bootstrap",
            loadSuccess ? "BASS loaded successfully" : $"BASS not loaded; audio is unavailable. {summary}");

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
        var foundPath = DirectoryScanner.LocateFilesDirectory(_searchPaths, libraryFiles, CompatibilityLevel.Compatible) ?? string.Empty;
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
            var partial = DirectoryScanner.LocateFiles(_searchPaths, libraryFiles, true, CompatibilityLevel.Compatible);
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
