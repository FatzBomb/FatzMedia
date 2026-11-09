using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace FatzMedia.Video.Sample.Player.Avalonia;

/// <summary>Colours a log entry's level: warnings amber, errors red, the rest muted (as in the WPF player).</summary>
public sealed class DiagnosticLevelBrushConverter : IValueConverter
{
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#9AA7B4"));
    private static readonly IBrush Warning = new SolidColorBrush(Color.Parse("#FFC857"));
    private static readonly IBrush Error = new SolidColorBrush(Color.Parse("#FF6B6B"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        MediaDiagnosticLevel.Error => Error,
        MediaDiagnosticLevel.Warning => Warning,
        _ => Muted
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
