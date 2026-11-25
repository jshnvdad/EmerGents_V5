namespace EmergencyApp.User.Services;

public static class AppSettings
{
    public const int CountdownSeconds = 5;
    public const int AutoCallCountdownSeconds = 10;   // "Calling 911 in 10 s... [Cancel]"

    public static bool TapMode
    {
        get => Preferences.Get("sos_tap_mode", false);
        set => Preferences.Set("sos_tap_mode", value);
    }
    public static bool ShakeEnabled
    {
        get => Preferences.Get("trg_shake", false);
        set => Preferences.Set("trg_shake", value);
    }
    public static bool VolumeEnabled
    {
        get => Preferences.Get("trg_volume", false);
        set => Preferences.Set("trg_volume", value);
    }

    // ---- Transmission & reliability ----

    /// Local emergency number (911 / 112 / 999 ...). Used by the call button and the auto-call fallback.
    public static string EmergencyNumber
    {
        get => Preferences.Get("emergency_number", "911");
        set => Preferences.Set("emergency_number", value);
    }

    /// Send a compact SMS to dispatch when the internet cannot deliver the alert.
    public static bool SmsFallbackEnabled
    {
        get => Preferences.Get("sms_fallback", true);
        set => Preferences.Set("sms_fallback", value);
    }

    /// Phone number of the dispatch center's SMS line (a mobile number or an SMS gateway).
    public static string DispatchSmsNumber
    {
        get => Preferences.Get("dispatch_sms_number", "");
        set => Preferences.Set("dispatch_sms_number", value ?? "");
    }

    /// If nothing could be sent, start a cancellable countdown and then call EmergencyNumber.
    public static bool AutoCallEnabled
    {
        get => Preferences.Get("auto_call", true);
        set => Preferences.Set("auto_call", value);
    }

    /// Slow down live location updates when the battery is low (the alert itself stays active).
    public static bool LowBatteryModeEnabled
    {
        get => Preferences.Get("low_battery_mode", true);
        set => Preferences.Set("low_battery_mode", value);
    }

    /// Random ID created once per install. Sent with every alert so dispatch can recognise the phone.
    public static string DeviceId
    {
        get
        {
            var id = Preferences.Get("device_id", "");
            if (string.IsNullOrEmpty(id))
            {
                id = Guid.NewGuid().ToString("N");
                Preferences.Set("device_id", id);
            }
            return id;
        }
    }
}
