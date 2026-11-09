using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;

namespace FatzMedia.Video.Sample.Player.Shared.ViewModels;

/// <summary>
/// Log panel: every engine diagnostic (info, warning, error), newest first. Each entry is also
/// written to <see cref="Trace"/>, which the host's debug log file (<see cref="LogFilePath"/>)
/// keeps for the whole run.
/// </summary>
public partial class PlayerViewModel
{
    const int MaxLogEntries = 1000;

    public ObservableCollection<DiagnosticLogEntry> DiagnosticLog { get; } = [];

    /// <summary>Gets the host's debug log file, or an empty string when it has none.</summary>
    public string LogFilePath => _host.LogFilePath ?? string.Empty;

    // Any thread: the debug log file gets the line even while the UI thread is blocked (ENG-039).
    void TraceLogEntry(DiagnosticLogEntry entry) =>
        Trace.WriteLine(Label is { Length: > 0 } label ? $"[Diagnostic] {label} {entry.Line}" : $"[Diagnostic] {entry.Line}");

    // UI thread: the log panel. The entry was traced already.
    void AppendToLog(DiagnosticLogEntry entry)
    {
        DiagnosticLog.Insert(0, entry);
        while (DiagnosticLog.Count > MaxLogEntries)
            DiagnosticLog.RemoveAt(DiagnosticLog.Count - 1);
    }

    /// <summary>Gets the whole log, oldest first, one <see cref="DiagnosticLogEntry.Line"/> per line.</summary>
    public string GetLogText() => string.Join(Environment.NewLine, DiagnosticLog.Reverse().Select(entry => entry.Line));

    [RelayCommand]
    async Task CopyLogAsync()
    {
        if (_host.Clipboard is not { } clipboard)
        {
            StatusText = "No clipboard is available.";
            return;
        }

        var text = GetLogText();
        var count = DiagnosticLog.Count;
        try
        {
            await clipboard.SetTextAsync(text);
            StatusText = $"Copied {count} log lines to the clipboard.";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not copy the log: {ex.Message}";
        }
    }

    [RelayCommand]
    void ClearLog() => DiagnosticLog.Clear();

    [RelayCommand]
    void OpenLogFile()
    {
        if (LogFilePath.Length == 0 || !File.Exists(LogFilePath))
        {
            StatusText = LogFilePath.Length == 0 ? "This player keeps no log file." : $"No log file at {LogFilePath}.";
            return;
        }

        try
        {
            _ = Process.Start(new ProcessStartInfo(LogFilePath) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            StatusText = $"Could not open {LogFilePath}: {ex.Message}";
        }
    }
}
