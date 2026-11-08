using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace FatzBomb.MediaControl.Wpf;

internal sealed class MediaOverlayWindow : Window
{
    private readonly ContentPresenter _contentPresenter = new();
    private bool _hitTestVisible;

    public MediaOverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Content = _contentPresenter;
        IsHitTestVisible = false;
        Focusable = false;
    }

    public void SetContent(object? content) => _contentPresenter.Content = content;

    public void SetHitTestVisible(bool value)
    {
        _hitTestVisible = value;
        IsHitTestVisible = value;
        Focusable = value;
        ApplyHitTestStyle();
    }

    public void UpdateBounds(FrameworkElement owner)
    {
        if (!owner.IsLoaded || owner.ActualWidth <= 0 || owner.ActualHeight <= 0)
            return;

        Point topLeft = owner.PointToScreen(new Point(0, 0));
        var source = PresentationSource.FromVisual(owner);
        if (source?.CompositionTarget != null)
            topLeft = source.CompositionTarget.TransformFromDevice.Transform(topLeft);

        Left = topLeft.X;
        Top = topLeft.Y;
        Width = owner.ActualWidth;
        Height = owner.ActualHeight;
        ApplyHitTestStyle();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyHitTestStyle();
    }

    private void ApplyHitTestStyle()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        var exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        var updated = _hitTestVisible
            ? exStyle & ~NativeMethods.WS_EX_TRANSPARENT
            : exStyle | NativeMethods.WS_EX_TRANSPARENT;

        if (updated != exStyle)
            _ = NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, updated);
    }
}
