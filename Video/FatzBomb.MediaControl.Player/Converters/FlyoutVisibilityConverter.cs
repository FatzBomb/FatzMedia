using System.Globalization;
using System.Windows;
using System.Windows.Data;
using FatzBomb.MediaControl.Player.ViewModels;

namespace FatzBomb.MediaControl.Player.Converters;

public sealed class FlyoutVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is PlayerFlyout activeFlyout && parameter is string flyoutName)
        {
            var target = flyoutName switch
            {
                "Sync" => PlayerFlyout.Sync,
                "Video" => PlayerFlyout.Video,
                "Audio" => PlayerFlyout.Audio,
                "AV" => PlayerFlyout.AV,
                _ => PlayerFlyout.None
            };

            return activeFlyout == target ? Visibility.Visible : Visibility.Collapsed;
        }

        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
