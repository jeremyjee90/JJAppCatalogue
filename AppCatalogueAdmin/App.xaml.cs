using System.Windows;
using System.Threading.Tasks;

namespace AppCatalogueAdmin;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var splash = new SplashWindow();
        splash.Show();

        await Task.Delay(TimeSpan.FromSeconds(2));

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        splash.Close();
        mainWindow.Show();
    }
}
