namespace EmergencyApp.User.Reliability;

public enum BatteryMode { Normal, Low, Critical }

/// Low-battery mode: slow down live updates when the battery is low, but never stop the alert.
public static class BatteryPolicy
{
    public const double LowLevel = 0.20;
    public const double CriticalLevel = 0.10;
    public const double RecoverLevel = 0.25;   // hysteresis: avoids flipping back and forth around 20 %

    /// <param name="level">0..1, or a negative number when unknown.</param>
    public static BatteryMode Evaluate(double level, bool charging, bool energySaver, bool enabled, BatteryMode current)
    {
        if (!enabled || charging) return BatteryMode.Normal;

        if (level < 0 || level > 1)
            return energySaver ? BatteryMode.Low : BatteryMode.Normal;   // level unknown

        if (level <= CriticalLevel) return BatteryMode.Critical;
        if (level <= LowLevel || energySaver) return BatteryMode.Low;

        // Between 20 % and 25 %: stay in Low until the battery has clearly recovered.
        if (current != BatteryMode.Normal && level <= RecoverLevel) return BatteryMode.Low;
        return BatteryMode.Normal;
    }

    /// How often the live location is sent while an emergency is ongoing.
    public static TimeSpan TrackingInterval(BatteryMode mode) => mode switch
    {
        BatteryMode.Critical => TimeSpan.FromSeconds(45),
        BatteryMode.Low => TimeSpan.FromSeconds(20),
        _ => TimeSpan.FromSeconds(5)
    };
}
