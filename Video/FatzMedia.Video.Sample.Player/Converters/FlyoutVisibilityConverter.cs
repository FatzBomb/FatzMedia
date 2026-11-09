using System.Globalization;
using System.Windows;
using System.Windows.Data;
using FatzMedia.Video.Sample.Player.Shared.ViewModels;

namespace FatzMedia.Video.Sample.Player.Converters;

public sealed class FlyoutVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is PlayerFlyout activeFlyout && parameter is string flyoutName)
        {
            return activeFlyout == PlayerViewModel.ParseFlyout(flyoutName) ? Visibility.Visible : Visibility.Collapsed;
        }

        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
