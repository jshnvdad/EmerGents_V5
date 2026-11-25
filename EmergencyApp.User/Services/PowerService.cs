using EmergencyApp.User.Reliability;

namespace EmergencyApp.User.Services;

/// Low-battery mode: slows down live updates when the battery is low or Power Saver is on.
/// It never stops the alert itself, and the SOS upload / SMS fallback are not affected.
public class PowerService
{
    public BatteryMode Mode { get; private set; } = BatteryMode.Normal;
    public int Percent { get; private set; } = -1;
    public bool Charging { get; private set; }
    public bool EnergySaver { get; private set; }

    public event Action? Changed;

    public TimeSpan TrackingInterval => BatteryPolicy.TrackingInterval(Mode);
    public GeolocationAccuracy LocationAccuracy => Mode == BatteryMode.Normal ? GeolocationAccuracy.Medium : GeolocationAccuracy.Low;
    public bool IsLow => Mode != BatteryMode.Normal;

    public string Warning => Mode switch
    {
        BatteryMode.Critical =>
            $"🔋 Battery critically low{PercentText}. Live updates slowed to every {(int)TrackingInterval.TotalSeconds} s. Charge your phone if you can. Your alert stays active.",
        BatteryMode.Low =>
            $"🔋 {(EnergySaver && Percent > 20 ? "Power saver is on" : $"Battery low{PercentText}")}. Live updates slowed to every {(int)TrackingInterval.TotalSeconds} s to save power. Your alert stays active.",
        _ => ""
    };

    private string PercentText => Percent >= 0 ? $" ({Percent}%)" : "";

    public PowerService()
    {
        Refresh();
        try
        {
            Battery.Default.BatteryInfoChanged += (_, _) => Refresh();
            Battery.Default.EnergySaverStatusChanged += (_, _) => Refresh();
        }
        catch { /* battery info not available */ }
    }

    /// Re-reads the battery. Also call it after the user changes the low-battery setting.
    public void Refresh()
    {
        double level = -1; var charging = false; var saver = false;
        try
        {
            level = Battery.Default.ChargeLevel;
            charging = Battery.Default.State is BatteryState.Charging or BatteryState.Full;
            saver = Battery.Default.EnergySaverStatus == EnergySaverStatus.On;
        }
        catch { }

        var mode = BatteryPolicy.Evaluate(level, charging, saver, AppSettings.LowBatteryModeEnabled, Mode);
        var percent = level >= 0 && level <= 1 ? (int)Math.Round(level * 100) : -1;

        var changed = mode != Mode || percent != Percent || charging != Charging || saver != EnergySaver;
        Mode = mode; Percent = percent; Charging = charging; EnergySaver = saver;
        if (changed) Changed?.Invoke();
    }
}
