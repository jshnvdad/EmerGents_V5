namespace EmergencyApp.User.Services;

/// Extra ways to start an SOS (shake, volume button x3). Both open the countdown screen,
/// so an accidental trigger can be cancelled. They work while the app is open.
public class TriggerService
{
    private readonly SessionService _session;
    private readonly DatabaseService _db;
    private readonly List<DateTime> _volumePresses = new();
    private bool _started, _shakeOn, _firing;

    public bool CountdownOpen { get; set; }

    public TriggerService(SessionService session, DatabaseService db) => (_session, _db) = (session, db);

    public void Start()
    {
        if (!_started)
        {
            _started = true;
            if (Accelerometer.Default.IsSupported)
                Accelerometer.Default.ShakeDetected += (_, _) => _ = FireAsync("Shake");
        }
        ApplySettings();
    }

    public void ApplySettings()
    {
        if (!Accelerometer.Default.IsSupported) return;
        try
        {
            if (AppSettings.ShakeEnabled && !_shakeOn) { Accelerometer.Default.Start(SensorSpeed.Game); _shakeOn = true; }
            else if (!AppSettings.ShakeEnabled && _shakeOn) { Accelerometer.Default.Stop(); _shakeOn = false; }
        }
        catch { /* sensor busy / unavailable */ }
    }

    /// Called from MainActivity on every volume key press. Returns true when the press was used.
    public bool OnVolumeKey(int repeatCount)
    {
        if (!AppSettings.VolumeEnabled || !_session.IsLoggedIn || repeatCount > 0) return false;

        var now = DateTime.UtcNow;
        _volumePresses.RemoveAll(t => now - t > TimeSpan.FromSeconds(2));
        _volumePresses.Add(now);
        if (_volumePresses.Count < 3) return false;

        _volumePresses.Clear();
        _ = FireAsync("Volume button");
        return true;
    }

    private async Task FireAsync(string source)
    {
        if (_firing || CountdownOpen || _session.CurrentUser is null) return;
        _firing = true;
        try
        {
            // Don't start another SOS while one is already ongoing.
            if ((await _db.GetRequestsAsync(_session.CurrentUser.Id)).Any(r => r.IsActive)) return;
            await MainThread.InvokeOnMainThreadAsync(() =>
                Shell.Current.GoToAsync($"countdown?source={Uri.EscapeDataString(source)}"));
        }
        finally { await Task.Delay(2000); _firing = false; }
    }
}
