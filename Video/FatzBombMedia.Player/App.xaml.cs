using System.Windows;

namespace FatzBombMedia.Player;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppContext.SetSwitch("FatzBombMedia.Cdg.EnableDxCdgD3D11Presenter", true);
        AppContext.SetSwitch("FatzBombMedia.Cdg.ShowDxCdgD3D11Presenter", true);

        base.OnStartup(e);
    }
}
