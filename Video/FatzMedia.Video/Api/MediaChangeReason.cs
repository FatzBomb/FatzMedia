#pragma warning disable IDE0130
namespace FatzMedia.Video;

public enum MediaChangeReason
{
    User = 0,
    LoadReset = 1,

    /// <summary><see cref="MediaControlPlayer.Reset"/> rebuilt the player, and the value is back to its default.</summary>
    PlayerReset = 2
}
