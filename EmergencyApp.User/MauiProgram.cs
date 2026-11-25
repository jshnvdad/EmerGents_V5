using EmergencyApp.User.Services;
using EmergencyApp.User.ViewModels;
using EmergencyApp.User.Views;
using Microsoft.Extensions.Logging;

namespace EmergencyApp.User;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        // Services
        builder.Services.AddSingleton<DatabaseService>();
        builder.Services.AddSingleton<ApiService>();
        builder.Services.AddSingleton<SessionService>();
        builder.Services.AddSingleton<LocationService>();
        builder.Services.AddSingleton<SyncService>();
        builder.Services.AddSingleton<NetworkMonitor>();        // connectivity changes -> retry / switch channel
        builder.Services.AddSingleton<PowerService>();          // low-battery mode
#if ANDROID
        builder.Services.AddSingleton<ISmsSender, AndroidSmsSender>();     // SMS fallback
        builder.Services.AddSingleton<IPhoneCaller, AndroidPhoneCaller>(); // auto-call fallback
#endif
        builder.Services.AddSingleton<TransmissionService>();   // HTTPS -> retry/backoff -> SMS -> call
        builder.Services.AddSingleton<LiveTrackingService>();
        builder.Services.AddSingleton<SosService>();
        builder.Services.AddSingleton<TriggerService>();

        // ViewModels + Pages
        builder.Services.AddTransient<LoginViewModel>();
        builder.Services.AddTransient<RegisterViewModel>();
        builder.Services.AddTransient<HomeViewModel>();
        builder.Services.AddTransient<HistoryViewModel>();
        builder.Services.AddTransient<ProfileViewModel>();
        builder.Services.AddTransient<LiveEmergencyViewModel>();
        builder.Services.AddTransient<CountdownViewModel>();

        builder.Services.AddTransient<LoginPage>();
        builder.Services.AddTransient<RegisterPage>();
        builder.Services.AddTransient<HomePage>();
        builder.Services.AddTransient<HistoryPage>();
        builder.Services.AddTransient<ProfilePage>();
        builder.Services.AddTransient<LiveEmergencyPage>();
        builder.Services.AddTransient<CountdownPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif
        return builder.Build();
    }
}
