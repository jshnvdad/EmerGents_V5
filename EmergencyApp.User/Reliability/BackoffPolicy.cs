namespace EmergencyApp.User.Reliability;

/// Exponential backoff with jitter, used when a send fails (Transmission & Reliability: "Retry with backoff").
/// 2 s, 4 s, 8 s, 15 s, 30 s, then 60 s between attempts (each +/- 20 %), so a weak network is not hammered
/// and the phone does not burn battery or data on retries.
public static class BackoffPolicy
{
    private static readonly int[] Seconds = { 2, 4, 8, 15, 30, 60 };

    /// <param name="failures">How many attempts have failed so far (1 = the first failure).</param>
    /// <param name="jitter">A random number from 0 to 1 (pass a fixed value in tests).</param>
    public static TimeSpan Delay(int failures, double jitter = 0.5)
    {
        var i = Math.Clamp(failures, 1, Seconds.Length) - 1;
        var factor = 0.8 + 0.4 * Math.Clamp(jitter, 0, 1);
        return TimeSpan.FromSeconds(Seconds[i] * factor);
    }
}
