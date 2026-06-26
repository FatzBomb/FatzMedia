using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FatzBombMedia.MediaControl.Controllers;

namespace FatzBombMedia.MediaControl.Wpf;

/// <summary>
/// Connects a host-owned WPF Image surface to a MediaRenderer.
/// </summary>
public static partial class RenderSurface
{
    public static IDisposable AttachSurface(this MediaRenderer renderer, Image surface)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(surface);

        return new ImageSurfaceAttachment(renderer, surface, backgroundHost: null);
    }

    public static IDisposable AttachSurface(this MediaRenderer renderer, Image surface, Panel backgroundHost)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(backgroundHost);

        return new ImageSurfaceAttachment(renderer, surface, backgroundHost);
    }

    private sealed class ImageSurfaceAttachment : IDisposable
    {
        private readonly MediaRenderer _renderer;
        private readonly Image _surface;
        private readonly Panel? _backgroundHost;
        private readonly Image? _backgroundImage;
        private readonly Brush? _fallbackBackground;
        private readonly object _attachmentToken = new();
        private bool _disposed;
        private Window? _hostWindow;
        private double? _lastAppliedRefreshHz;

        private static readonly DependencyProperty ActiveImageAttachmentTokenProperty = DependencyProperty.RegisterAttached(
            "ActiveImageAttachmentToken",
            typeof(object),
            typeof(ImageSurfaceAttachment),
            new PropertyMetadata(null));

        private static readonly DependencyProperty ActiveBackgroundAttachmentTokenProperty = DependencyProperty.RegisterAttached(
            "ActiveBackgroundAttachmentToken",
            typeof(object),
            typeof(ImageSurfaceAttachment),
            new PropertyMetadata(null));

        public ImageSurfaceAttachment(MediaRenderer renderer, Image surface, Panel? backgroundHost)
        {
            _renderer = renderer;
            _surface = surface;
            _backgroundHost = backgroundHost;
            _fallbackBackground = backgroundHost?.Background;
            _backgroundImage = CreateBackgroundImage(backgroundHost);

            _surface.SetValue(ActiveImageAttachmentTokenProperty, _attachmentToken);
            _backgroundHost?.SetValue(ActiveBackgroundAttachmentTokenProperty, _attachmentToken);

            _renderer.ImageSourceChanged += OnImageSourceChanged;
            _renderer.BackgroundChanged += OnBackgroundChanged;
            _renderer.FramePresented += OnFramePresented;
            _surface.Loaded += OnSurfaceLoaded;
            UpdateSurface();
            UpdateBackground();
        }

        private void OnSurfaceLoaded(object sender, RoutedEventArgs e)
        {
            _hostWindow = Window.GetWindow(_surface);
            if (_hostWindow != null)
            {
                _hostWindow.LocationChanged += OnHostWindowMoved;
                _hostWindow.StateChanged += OnHostWindowMoved;
            }

            RefreshDisplayRate();
        }

        private void OnHostWindowMoved(object? sender, EventArgs e)
        {
            RefreshDisplayRate();
        }

        /// <summary>
        /// Detects the refresh rate of the monitor currently displaying this surface's window
        /// and supplies it to the renderer, so video presentation timing matches the real
        /// display rather than a DWM-wide guess. Relevant on multi-monitor setups with
        /// mismatched refresh rates (e.g. an ultrawide at 59.94Hz alongside 60Hz displays).
        /// </summary>
        private void RefreshDisplayRate()
        {
            if (_disposed) return;

            var refreshHz = MonitorRefreshRate.GetForVisual(_surface);
            if (refreshHz == _lastAppliedRefreshHz)
            {
                return;
            }

            _lastAppliedRefreshHz = refreshHz;
            _renderer.SetDisplayRefreshRate(refreshHz);
        }

        private void OnFramePresented(object? sender, EventArgs e)
        {
            if (_disposed) return;
            // Must run synchronously on the surface's own dispatcher tick, not via BeginInvoke,
            // so the invalidation lands before WPF decides whether to schedule another render pass.
            if (_surface.Dispatcher.CheckAccess())
            {
                InvalidateVisuals();
            }
            else
            {
                _surface.Dispatcher.BeginInvoke(new Action(InvalidateVisuals), DispatcherPriority.Render);
            }
        }

        private void InvalidateVisuals()
        {
            _surface.InvalidateVisual();
            _backgroundImage?.InvalidateVisual();
            _backgroundHost?.InvalidateVisual();
        }

        private void OnImageSourceChanged(object? sender, EventArgs e)
        {
            if (_disposed) return;
            _surface.Dispatcher.BeginInvoke(new Action(UpdateSurface), DispatcherPriority.Render);
        }

        private void OnBackgroundChanged(object? sender, EventArgs e)
        {
            if (_disposed || _backgroundHost == null) return;
            _backgroundHost.Dispatcher.BeginInvoke(new Action(UpdateBackground), DispatcherPriority.Render);
        }

        private void UpdateSurface()
        {
            if (_disposed || !IsCurrentImageAttachment()) return;
            _surface.Source = _renderer.ImageSource;
        }

        private void UpdateBackground()
        {
            if (_disposed || _backgroundHost == null || !IsCurrentBackgroundAttachment()) return;

            _backgroundHost.Background = new SolidColorBrush(_renderer.BackgroundColor);

            if (_backgroundImage == null)
            {
                return;
            }

            var backgroundSource = _renderer.EffectiveBackgroundSource;
            if (backgroundSource == null)
            {
                _backgroundImage.Source = null;
                _backgroundImage.Visibility = System.Windows.Visibility.Collapsed;
                return;
            }

            _backgroundImage.Source = backgroundSource;
            _backgroundImage.Stretch = _renderer.EffectiveBackgroundStretch;
            _backgroundImage.Visibility = System.Windows.Visibility.Visible;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _renderer.ImageSourceChanged -= OnImageSourceChanged;
            _renderer.BackgroundChanged -= OnBackgroundChanged;
            _renderer.FramePresented -= OnFramePresented;
            _surface.Loaded -= OnSurfaceLoaded;
            if (_hostWindow != null)
            {
                _hostWindow.LocationChanged -= OnHostWindowMoved;
                _hostWindow.StateChanged -= OnHostWindowMoved;
                _hostWindow = null;
            }
            _disposed = true;
            _surface.Dispatcher.BeginInvoke(new Action(ClearSurfaceIfCurrent), DispatcherPriority.Render);
            _backgroundHost?.Dispatcher.BeginInvoke(new Action(RestoreBackgroundHostIfCurrent), DispatcherPriority.Render);
        }

        private bool IsCurrentImageAttachment()
        {
            return ReferenceEquals(_surface.GetValue(ActiveImageAttachmentTokenProperty), _attachmentToken);
        }

        private bool IsCurrentBackgroundAttachment()
        {
            return _backgroundHost != null
                && ReferenceEquals(_backgroundHost.GetValue(ActiveBackgroundAttachmentTokenProperty), _attachmentToken);
        }

        private void ClearSurfaceIfCurrent()
        {
            if (!IsCurrentImageAttachment())
            {
                return;
            }

            _surface.Source = null;
            _surface.ClearValue(ActiveImageAttachmentTokenProperty);
        }

        private void RestoreBackgroundHostIfCurrent()
        {
            if (!IsCurrentBackgroundAttachment())
            {
                return;
            }

            RestoreBackgroundHost();
            _backgroundHost?.ClearValue(ActiveBackgroundAttachmentTokenProperty);
        }

        private static Image? CreateBackgroundImage(Panel? backgroundHost)
        {
            if (backgroundHost == null)
            {
                return null;
            }

            var image = new Image
            {
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                VerticalAlignment = System.Windows.VerticalAlignment.Stretch,
                IsHitTestVisible = false,
                Visibility = System.Windows.Visibility.Collapsed
            };

            backgroundHost.Children.Insert(0, image);
            return image;
        }

        private void RestoreBackgroundHost()
        {
            if (_backgroundHost == null)
            {
                return;
            }

            if (_backgroundImage != null)
            {
                _backgroundHost.Children.Remove(_backgroundImage);
            }

            _backgroundHost.Background = _fallbackBackground;
        }
    }

    /// <summary>
    /// Detects the refresh rate of the monitor currently displaying a given visual, via
    /// MonitorFromWindow + EnumDisplaySettings. This reflects the specific monitor a window
    /// is on, unlike DWM's global composition timing, which does not reliably distinguish
    /// between monitors on a multi-monitor, mixed-refresh-rate setup.
    /// </summary>
    internal static partial class MonitorRefreshRate
    {
        private const int MONITOR_DEFAULTTONEAREST = 2;
        private const int ENUM_CURRENT_SETTINGS = -1;

        /// <summary>
        /// Returns the refresh rate (Hz) of the monitor showing the window that hosts the given
        /// visual, or null if it cannot be determined (no presentation source yet, no HWND,
        /// or the OS reports an unknown/default rate of 0 or 1).
        /// </summary>
        public static double? GetForVisual(Visual visual)
        {
            try
            {
                if (PresentationSource.FromVisual(visual) is not HwndSource source || source.Handle == IntPtr.Zero)
                    return null;

                var monitor = MonitorFromWindow(source.Handle, MONITOR_DEFAULTTONEAREST);
                if (monitor == IntPtr.Zero)
                    return null;

                var monitorInfo = new MONITORINFOEX
                {
                    cbSize = Marshal.SizeOf<MONITORINFOEX>()
                };
                if (!GetMonitorInfo(monitor, ref monitorInfo))
                    return null;

                var devMode = new DEVMODE
                {
                    dmSize = (short)Marshal.SizeOf<DEVMODE>()
                };
                if (!EnumDisplaySettings(monitorInfo.szDevice, ENUM_CURRENT_SETTINGS, ref devMode))
                    return null;

                // 0 or 1 mean "use hardware default" per the Win32 docs; not a usable rate.
                if (devMode.dmDisplayFrequency <= 1)
                    return null;

                return devMode.dmDisplayFrequency;
            }
            catch
            {
                return null;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public int dmDisplayOrientation;
            public int dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;
            public int dmICMMethod;
            public int dmICMIntent;
            public int dmMediaType;
            public int dmDitherType;
            public int dmReserved1;
            public int dmReserved2;
            public int dmPanningWidth;
            public int dmPanningHeight;
        }

        [LibraryImport("user32.dll")]
        private static partial IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

        #pragma warning disable SYSLIB1054 // LibraryImport does not support these Win32 structs cleanly without unsafe fixed buffers.

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

        #pragma warning restore SYSLIB1054

    }
}
