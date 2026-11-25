using Android.App;
using Android.Content.PM;
using Android.Views;
using EmergencyApp.User.Services;
using Microsoft.Extensions.DependencyInjection;

namespace EmergencyApp.User;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode |
                           ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    public override bool OnKeyDown(Keycode keyCode, KeyEvent? e)
    {
        if (keyCode == Keycode.VolumeDown || keyCode == Keycode.VolumeUp)
        {
            var triggers = IPlatformApplication.Current?.Services.GetService<TriggerService>();
            if (triggers?.OnVolumeKey(e?.RepeatCount ?? 0) == true) return true;
        }
        return base.OnKeyDown(keyCode, e);
    }
}
