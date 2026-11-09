namespace FatzMedia.Video.Sample.Player.Shared.Hosting;

/// <summary>The host's clipboard (Copy log).</summary>
public interface IPlayerClipboard
{
    /// <summary>Puts <paramref name="text"/> on the clipboard; called on the UI thread.</summary>
    Task SetTextAsync(string text);
}

/// <summary>The host's file picker (Open, Choose next).</summary>
public interface IPlayerFilePicker
{
    /// <summary>
    /// Asks for one local file; called on the UI thread. <paramref name="filter"/> is the engine's
    /// "Name|*.a;*.b|Name2|*.c" filter (<see cref="MediaControlPlayer.OpenFileDialogFilter"/>).
    /// </summary>
    /// <returns>The chosen path, or null when the user cancelled.</returns>
    Task<string?> PickFileAsync(string title, string filter);
}
