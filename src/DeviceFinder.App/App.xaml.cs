using System.Windows;
using DeviceFinder.App.Services;

namespace DeviceFinder.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // La langue doit être résolue AVANT la création de la fenêtre principale :
        // choix persisté s'il existe, sinon langue de l'interface de l'OS.
        Loc.SetLanguage(Loc.Resolve(AppSettings.LoadOrDefault().Language));
        base.OnStartup(e);
    }
}
