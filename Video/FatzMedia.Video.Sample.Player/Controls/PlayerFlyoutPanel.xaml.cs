using System.Windows;
using System.Windows.Controls;

namespace FatzMedia.Video.Sample.Player.Controls;

public partial class PlayerFlyoutPanel : UserControl
{
    public static readonly DependencyProperty IsSecondaryOutputOpenProperty =
        DependencyProperty.Register(
            nameof(IsSecondaryOutputOpen),
            typeof(bool),
            typeof(PlayerFlyoutPanel),
            new PropertyMetadata(false, OnIsSecondaryOutputOpenChanged));

    public event EventHandler? SecondaryWindowToggleRequested;

    public PlayerFlyoutPanel()
    {
        InitializeComponent();
    }

    public bool IsSecondaryOutputOpen
    {
        get => (bool)GetValue(IsSecondaryOutputOpenProperty);
        set => SetValue(IsSecondaryOutputOpenProperty, value);
    }

    static void OnIsSecondaryOutputOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PlayerFlyoutPanel panel && panel.SecondaryWindowCheckBox != null)
            panel.SecondaryWindowCheckBox.IsChecked = (bool)e.NewValue;
    }

    void SecondaryWindowCheckBox_Click(object sender, RoutedEventArgs e)
    {
        SecondaryWindowToggleRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }
}
