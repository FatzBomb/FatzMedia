using ManagedBass.Midi;

namespace FatzBombMedia.MediaControl.Core.Audio;

/// <summary>
/// Manages the default MIDI soundfont for BASS MIDI synthesis. BASS MIDI only supports SF2/SFZ;
/// DLS files (including Windows gm.dls) are converted to SF2 and cached.
/// </summary>
internal static class BassMidiSoundfontManager
{
    private static string? _currentSoundfontPath;

    /// <summary>
    /// Initializes the default MIDI soundfont. If <paramref name="userPath"/> is set, that path
    /// is used regardless of any auto-detected system soundfont. Otherwise, the manager
    /// auto-detects a suitable soundfont (Windows gm.dls converted to SF2, then common .sf2 files,
    /// then the bundled TimGM6mb.sf2 fallback).
    /// </summary>
    public static void Initialize(string? userPath)
    {
        var path = ResolveSoundfontPath(userPath);
        if (path == null)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "BASSMIDI", "No soundfont found; MIDI playback will be silent.");
            return;
        }

        if (_currentSoundfontPath == path)
            return;

        try
        {
            BassMidi.DefaultFont = path;
            _currentSoundfontPath = path;
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "BASSMIDI", $"Default soundfont set: {path}");
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "BASSMIDI", $"Soundfont init failed: {ex.Message}");
        }
    }

    private static string? ResolveSoundfontPath(string? userPath)
    {
        if (!string.IsNullOrWhiteSpace(userPath) && File.Exists(userPath))
            return ResolveSoundfontFile(userPath);

        if (!string.IsNullOrWhiteSpace(userPath))
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "BASSMIDI", $"Configured soundfont not found: {userPath}");

        return AutoDetectSoundfont();
    }

    private static string? ResolveSoundfontFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".sf2" or ".sfz" ? path : ext == ".dls" ? DlsToSf2Converter.Convert(path) : path;
    }

    private static string? AutoDetectSoundfont()
    {
        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        var gmDls = Path.Combine(systemRoot, "System32", "drivers", "gm.dls");
        if (File.Exists(gmDls))
            return DlsToSf2Converter.Convert(gmDls);

        var candidates = new[]
        {
            Path.Combine(systemRoot, "System32", "drivers", "sgm.sb"),
            Path.Combine(AppContext.BaseDirectory, "soundfont.sf2"),
            Path.Combine(AppContext.BaseDirectory, "native", "soundfont.sf2"),
            Path.Combine(AppContext.BaseDirectory, "native", "TimGM6mb.sf2")
        };

        foreach (var candidate in candidates)
            if (File.Exists(candidate))
                return candidate;

        return null;
    }
}
