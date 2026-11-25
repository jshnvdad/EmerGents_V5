namespace EmergencyApp.User.Services;

/// Sends the fallback SMS. Implemented for Android in Platforms/Android/AndroidReliability.cs.
public interface ISmsSender
{
    /// The device has telephony hardware (a phone, not a Wi-Fi-only tablet).
    bool IsSupported { get; }
    Task<bool> HasPermissionAsync();
    /// Shows the system permission prompt. Call it from Profile, never in the middle of an emergency.
    Task<bool> RequestPermissionAsync();
    /// True when the carrier accepted the message.
    Task<bool> SendAsync(string number, string message, CancellationToken ct = default);
}

/// Places a phone call. Dials directly when the Phone permission is granted, otherwise opens the dialer.
public interface IPhoneCaller
{
    Task<bool> HasPermissionAsync();
    Task<bool> RequestPermissionAsync();
    Task<bool> CallAsync(string number);
}
