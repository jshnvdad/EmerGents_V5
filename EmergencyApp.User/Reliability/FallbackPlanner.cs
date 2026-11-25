namespace EmergencyApp.User.Reliability;

public enum FallbackAction { None, SendSms, StartAutoCall }

/// Everything the planner needs to decide. Plain values only, so it can be unit tested.
public record FallbackInput(
    double ElapsedSeconds,    // seconds since the SOS was created
    bool InternetUsable,      // the OS reports a working internet connection
    bool Delivered,           // the server has confirmed receipt (ACK)
    bool SmsReady,            // SMS fallback is on, number set, permission granted, device can send
    bool SmsSent,             // an SMS has already been handed to the carrier
    int SmsFailures,          // SMS attempts that failed
    bool AutoCallEnabled,     // user setting (and not dismissed for this SOS)
    bool AutoCallStarted);    // the call countdown already started for this SOS

/// Decides when the SOS should leave the internet channel:
///   1. internet (HTTPS)  ->  2. SMS fallback  ->  3. call the local emergency number.
public static class FallbackPlanner
{
    public const int SmsDelayOnlineSeconds = 25;    // internet looks fine but there is no ACK yet: give it time
    public const int SmsDelayOfflineSeconds = 10;   // no internet: do not wait long
    public const int SmsRetrySpacingSeconds = 10;
    public const int MaxSmsFailures = 3;
    public const int AutoCallDelaySeconds = 45;
    public const int AutoCallMaxAgeSeconds = 600;   // never auto-dial for an SOS that is more than 10 minutes old

    public static FallbackAction Next(FallbackInput i)
    {
        if (i.Delivered) return FallbackAction.None;

        if (i.SmsReady && !i.SmsSent && i.SmsFailures < MaxSmsFailures)
        {
            var due = (i.InternetUsable ? SmsDelayOnlineSeconds : SmsDelayOfflineSeconds)
                      + i.SmsFailures * SmsRetrySpacingSeconds;
            if (i.ElapsedSeconds >= due) return FallbackAction.SendSms;
        }

        // Last resort: nothing has gotten out (SMS is unavailable or keeps failing).
        var smsGaveUp = !i.SmsReady || i.SmsFailures >= MaxSmsFailures;
        if (i.AutoCallEnabled && !i.AutoCallStarted && !i.SmsSent && smsGaveUp
            && i.ElapsedSeconds >= AutoCallDelaySeconds && i.ElapsedSeconds <= AutoCallMaxAgeSeconds)
            return FallbackAction.StartAutoCall;

        return FallbackAction.None;
    }
}
