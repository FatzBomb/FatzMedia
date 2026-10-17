namespace FatzBomb.MediaControl.Player.ViewModels;

public sealed record DiagnosticLogEntry(DateTime Timestamp, MediaControl.MediaDiagnosticLevel Level, string Category, string Message)
{
    public string TimeText => Timestamp.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>One line as written to the debug log file and copied to the clipboard.</summary>
    public string Line => $"{TimeText} [{Level}] {Category}: {Message}";
}
