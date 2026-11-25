using System.Windows;
using EmergencyDesk.Services;
using EmergencyDesk.Views;

namespace EmergencyDesk;

public partial class App : Application
{
    public static AppSettings Settings { get; } = AppSettings.Load();
    public static SessionService Session { get; } = new();
    public static ApiClient Api { get; } = new(Settings, Session);
    public static LocalStore Store { get; } = LocalStore.Load();
    public static AuditLog Audit { get; } = new(Store.DataDirectory, () => (Session.DisplayName, Session.Role));
    public static ISecretProtector Secrets { get; } = new DpapiSecretProtector();
    public static IncidentEngine Engine { get; } = new(Store);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        ShowLogin();
    }

    public static void ShowLogin()
    {
        Session.Clear();
        var login = new LoginWindow();
        if (login.ShowDialog() == true)
        {
            var main = new MainWindow();
            Current.MainWindow = main;
            main.Show();
        }
        else
        {
            Current.Shutdown();
        }
    }
}
