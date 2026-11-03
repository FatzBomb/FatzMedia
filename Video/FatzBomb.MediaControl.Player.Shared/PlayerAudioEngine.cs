namespace FatzBomb.MediaControl.Player.Shared;

/// <summary>
/// The audio engine a test player is asked for (<c>--audio=</c>, 037A slice 2). Neutral: each host
/// turns <see cref="FatzAudio"/> into the FatzAudio package's engine factory, since this project
/// references only Core.
/// </summary>
public enum PlayerAudioEngine
{
    /// <summary>The built-in BASS engine, the default (<c>--audio=bass</c>).</summary>
    Bass,

    /// <summary>The opt-in FatzAudio engine for audio files and archives (<c>--audio=fatz</c>); video audio stays on BASS.</summary>
    FatzAudio,
}

/// <summary>Parses <c>--audio=</c> values.</summary>
public static class PlayerAudioEngineRules
{
    /// <summary>The accepted <c>--audio=</c> names, for warnings.</summary>
    public const string Usage = "bass or fatz";

    /// <summary>Parses a value (case-insensitive, trimmed): <c>bass</c>, or <c>fatz</c>/<c>fatzaudio</c>; null when not a name.</summary>
    public static PlayerAudioEngine? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "bass" => PlayerAudioEngine.Bass,
        "fatz" or "fatzaudio" => PlayerAudioEngine.FatzAudio,
        _ => null,
    };
}
