using FatzMedia.Video.Sample.Player.Shared;

namespace FatzMedia.Video.Sample.Player.Avalonia;

/// <summary>
/// The command line of this run. Every flag is shared with the WPF and WinForms players
/// (<see cref="PlayerStartupOptions"/>: media file or http(s) URL, <c>--url=</c>, <c>--mode=</c>,
/// <c>--native=</c>, <c>--renderer=</c>, <c>--software</c>, <c>--surface=</c>, <c>--decoding=</c>,
/// <c>--players=</c>, <c>--second-screen=</c>, ...); since Stage 046A slice 3 this player has none
/// of its own.
/// </summary>
static class StartupOptions
{
    public static PlayerStartupOptions Current { get; set; } = new();
}
