namespace FatzBombMedia.Player.ViewModels;

public sealed record DiagnosticLogEntry(DateTime Timestamp, MediaControl.MediaDiagnosticLevel Level, string Category, string Message);
