using System.ComponentModel;
using System.Windows;
using FatzBomb.MediaControl.Player.Hosting;
using FatzBomb.MediaControl.Player.Shared.Hosting;
using FatzBomb.MediaControl.Player.Shared.ViewModels;
using FatzBomb.MediaControl.Wpf;

namespace FatzBomb.MediaControl.Player.ViewModels;

/// <summary>
/// One WPF player's view model (one per <see cref="PlayerView"/>, <c>--players=1..4</c>): the shared
/// <see cref="PlayerViewModel"/> plus what only the WPF <c>MediaSurface</c> has, the presentation
/// mode (native HWND overlay or WPF composited) and surface auto-hide.
/// </summary>
public sealed class MainWindowViewModel : PlayerViewModel
{
    private static readonly WpfPresentationMode[] SoftwarePresentationModes = [WpfPresentationMode.WpfComposited];
    private static readonly WpfPresentationMode[] AllPresentationModes =
    [
        WpfPresentationMode.NativeHwndOverlay,
        WpfPresentationMode.WpfComposited
    ];

    // Stage 036A: what startup expects until App.RendererSelection resolves (OnRendererResolved).
    private bool _usesSoftwareRenderer = App.ExpectsSoftwareRenderer;

    /// <param name="owner">The window that owns this player's dialogs.</param>
    /// <param name="index">The player's number (1-based); only player 1 gets the startup media.</param>
    public MainWindowViewModel(Window owner, int index)
        : base(
            App.StartupOptions.ForPlayer(index),
            new PlayerHost(
                new WpfPlayerDispatcher(),
                new WpfPlayerEngineFactory(),
                new WpfPlayerClipboard(),
                new WpfPlayerFilePicker(owner),
                App.LogFilePath))
    {
        Label = $"P{index}";
    }

    public IReadOnlyList<WpfPresentationMode> PresentationModes =>
        _usesSoftwareRenderer ? SoftwarePresentationModes : AllPresentationModes;

    public bool CanChangePresentationMode => !_usesSoftwareRenderer && !IsBusy && Player?.IsOpen != true;

    public WpfPresentationMode PresentationMode
    {
        get;
        set
        {
            if (Player?.IsOpen == true)
                throw new InvalidOperationException("PresentationMode can only be changed before media is loaded.");

            _ = SetProperty(ref field, value);
        }
    } = App.ExpectsSoftwareRenderer || App.StartupOptions.PreferFrameSinkSurface
        ? WpfPresentationMode.WpfComposited
        : WpfPresentationMode.NativeHwndOverlay;

    public bool AutoHideSurfaces
    {
        get;
        set => _ = SetProperty(ref field, value);
    } = App.AutoHideSurfaces;

    /// <summary>
    /// Stage 036A: the auto chain lands on the software renderer only when Direct3D11 has no
    /// device at all; then the surface switches to WPF-composited before any media is loaded.
    /// </summary>
    protected override void OnRendererResolved(PlayerRendererInfo renderer)
    {
        if (renderer.RequiresFrameSinkSurface == _usesSoftwareRenderer)
            return;

        _usesSoftwareRenderer = renderer.RequiresFrameSinkSurface;
        // The mode first, so the combo box never holds a value missing from its new list.
        if (_usesSoftwareRenderer)
            PresentationMode = WpfPresentationMode.WpfComposited;
        OnPropertyChanged(nameof(PresentationModes));
        OnPropertyChanged(nameof(CanChangePresentationMode));
    }

    protected override string DescribeHostStartup() => $"Presentation: {PresentationMode} | AutoHide={AutoHideSurfaces}";

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Player) or nameof(IsBusy))
            OnPropertyChanged(nameof(CanChangePresentationMode));
    }
}
