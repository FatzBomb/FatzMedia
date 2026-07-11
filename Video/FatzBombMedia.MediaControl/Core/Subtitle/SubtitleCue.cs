namespace FatzBombMedia.MediaControl.Core.Subtitle;

public sealed record SubtitleCue(int Sequence, TimeSpan Start, TimeSpan End, string Text);
