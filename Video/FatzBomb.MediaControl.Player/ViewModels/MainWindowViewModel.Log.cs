using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace FatzBomb.MediaControl.Player.ViewModels;

/// <summary>
/// Log panel: every engine diagnostic (info, warning, error), newest first. Each entry is also
/// written to the debug log file (<see cref="App.LogFilePath"/>), which keeps the whole run.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private const int MaxLogEntries = 1000;

    public ObservableCollection<DiagnosticLogEntry> DiagnosticLog { get; } = [];

    public string LogFilePath => App.LogFilePath;

    private void AppendToLog(DiagnosticLogEntry entry)
    {
        Trace.WriteLine($"[Diagnostic] {entry.Line}");
        DiagnosticLog.Insert(0, entry);
        while (DiagnosticLog.Count > MaxLogEntries)
            DiagnosticLog.RemoveAt(DiagnosticLog.Count - 1);
    }

    [RelayCommand]
    private void CopyLog()
    {
        var text = string.Join(Environment.NewLine, DiagnosticLog.Reverse().Select(entry => entry.Line));
        try
        {
            Clipboard.SetText(text);
            StatusText = $"Copied {DiagnosticLog.Count} log lines to the clipboard.";
        }
        catch (COMException ex)
        {
            StatusText = $"Could not copy the log: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ClearLog() => DiagnosticLog.Clear();

    [RelayCommand]
    private void OpenLogFile()
    {
        if (!File.Exists(LogFilePath))
        {
            StatusText = $"No log file at {LogFilePath}.";
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
