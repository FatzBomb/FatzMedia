namespace FatzMedia.Video.Core.Audio;

/// <summary>Karaoke song metadata (title, artist, key, tempo, ...) shown on the lyric intro card.</summary>
public sealed record KaraokeMetadata(
    string Title,
    string Artist,
    string Writer,
    string Lyricist,
    string Year,
    string Copyright,
    string KeySignature,
    string Tempo,
    string TimeSignature,
    TimeSpan Duration,
    bool IsFallback,
    IReadOnlyList<string> AdditionalInfo);
