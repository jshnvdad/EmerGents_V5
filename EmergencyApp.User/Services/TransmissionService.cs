using EmergencyApp.User.Models;
using EmergencyApp.User.Reliability;

namespace EmergencyApp.User.Services;

/// What the UI needs to know about one SOS that is still being delivered (kept in memory only).
public class TransmissionInfo
{
    public int Attempts { get; set; }
    public DateTime? NextRetryUtc { get; set; }
    public string? LastError { get; set; }
    public bool SmsInProgress { get; set; }
    public int SmsFailures { get; set; }
    public bool AutoCallStarted { get; set; }
    public bool AutoCallDismissed { get; set; }
    public bool AutoCallPlaced { get; set; }
    public int? AutoCallSecondsLeft { get; set; }
}

/// Delivers an SOS no matter how bad the network is:
///   1. HTTPS to the dispatch server (the server's reply is the ACK)
///   2. retry with backoff, and immediately again whenever connectivity changes
///   3. a compact SMS to dispatch if the internet cannot deliver it
///   4. a cancellable countdown, then a call to the local emergency number, as the last resort
/// The decisions themselves live in the Reliability/ folder (pure code with unit tests).
public class TransmissionService
{
    private readonly DatabaseService _db;
    private readonly SyncService _sync;
    private readonly SessionService _session;
    private readonly LocationService _location;
    private readonly NetworkMonitor _network;
    private readonly ISmsSender _sms;
    private readonly IPhoneCaller _caller;

    private readonly object _gate = new();
    private readonly HashSet<string> _running = new();
    private readonly Dictionary<string, TransmissionInfo> _info = new();
    private readonly Random _rng = new();
    private DateTime _readinessAt = DateTime.MinValue;

    /// Raised (from any thread) whenever delivery state changes. LiveTrackingService relays it to the pages.
    public event Action<EmergencyRequest>? Changed;

    public bool SmsReady { get; private set; }
    public bool CallPermissionGranted { get; private set; }

    public TransmissionService(DatabaseService db, SyncService sync, SessionService session, LocationService location,
                               NetworkMonitor network, ISmsSender sms, IPhoneCaller caller)
    {
        (_db, _sync, _session, _location, _network, _sms, _caller) = (db, sync, session, location, network, sms, caller);
    }

    public TransmissionInfo GetInfo(string clientId)
    {
        lock (_gate)
        {
            if (!_info.TryGetValue(clientId, out var i)) _info[clientId] = i = new TransmissionInfo();
            return i;
        }
    }

    // ------------------------------------------------------------------ public API

    /// Starts (or resumes) delivery of an SOS that the server has not ACKed yet. Safe to call repeatedly.
    public void Ensure(EmergencyRequest r)
    {
        if (!r.IsActive || r.ServerHasIt || _session.CurrentUser is null) return;
        var id = r.ClientId;
        lock (_gate) { if (!_running.Add(id)) return; }
        _ = Task.Run(() => RunAsync(id));
    }

    /// The user pressed "Cancel auto-call".
    public void DismissAutoCall(string clientId)
    {
        GetInfo(clientId).AutoCallDismissed = true;
        RaiseById(clientId);
    }

    /// Re-checks whether the SMS fallback and direct calling are usable (permission, number, hardware).
    public async Task RefreshReadinessAsync()
    {
        var configured = AppSettings.SmsFallbackEnabled
                         && AppSettings.DispatchSmsNumber.Trim().Length >= 3
                         && _sms.IsSupported;
        SmsReady = configured && await _sms.HasPermissionAsync();
        CallPermissionGranted = await _caller.HasPermissionAsync();
        _readinessAt = DateTime.UtcNow;
    }

    /// One line for the "how will my alert be sent right now?" chip. Level: 0 = good, 1 = fallback, 2 = problem.
    public (string Text, int Level) ChannelSummary()
    {
        if (_network.State == NetworkState.Online)
        {
            var via = string.IsNullOrEmpty(_network.Transport) ? "" : $" ({_network.Transport})";
            return ($"● Online{via}: alerts are sent over the internet", 0);
        }
        var what = _network.State == NetworkState.Limited ? "Limited connection" : "No internet";
        if (SmsReady) return ($"● {what}: your alert will be sent by SMS", 1);
        return AppSettings.AutoCallEnabled
            ? ($"● {what}: SMS fallback is not set up. The app will offer to call {AppSettings.EmergencyNumber}", 2)
            : ($"● {what}: SMS fallback is not set up. Use the call button", 2);
    }

    /// Plain-language status of an SOS that has not been confirmed by the server yet.
    public string DescribeDelivery(EmergencyRequest r)
    {
        var i = GetInfo(r.ClientId);
        if (r.ServerHasIt)
            return r.Delivery == DeliveryStep.Acknowledged
                ? "A dispatcher has acknowledged your alert."
                : "The server has your alert. Waiting for a dispatcher to acknowledge it.";

        if (i.SmsInProgress) return "Sending an SMS to dispatch…";
        var smsLine = r.SmsSentAtUtc is not null
            ? "Your alert was sent to dispatch by SMS. Still trying the internet so the server can confirm. "
            : "";

        if (_network.State == NetworkState.Offline)
            return smsLine + (r.SmsSentAtUtc is null
                ? "No internet. Your alert is saved and will be sent automatically as soon as you are connected."
                    + (SmsReady ? " If it takes long, it will go out by SMS." : "")
                : "Waiting for a connection.");

        if (i.NextRetryUtc is DateTime next)
        {
            var secs = Math.Max(0, (int)Math.Ceiling((next - DateTime.UtcNow).TotalSeconds));
            return smsLine + $"Could not reach the server (attempt {i.Attempts}). Retrying in {secs} s…";
        }
        return smsLine + (i.Attempts > 0 ? "Sending over the internet…" : "Saved on your phone. Sending…");
    }

    // ------------------------------------------------------------------ delivery loop

    private async Task RunAsync(string clientId)
    {
        var info = GetInfo(clientId);
        var failures = 0;
        try
        {
            while (true)
            {
                var r = await _db.FindByClientIdAsync(clientId);
                if (r is null || !r.IsActive || _session.CurrentUser is null) break;
                if (r.ServerHasIt) { Raise(r); break; }

                // 1. Primary channel: HTTPS to the dispatch server. The reply is the ACK.
                var offline = _network.State == NetworkState.Offline;
                if (!offline)
                {
                    info.Attempts++;
                    info.NextRetryUtc = null;
                    Raise(r);

                    var outcome = await _sync.TryPushAsync(r);
                    if (outcome.Ok)
                    {
                        info.LastError = null;
                        info.NextRetryUtc = null;
                        info.AutoCallSecondsLeft = null;
                        Raise(await _db.FindByClientIdAsync(clientId) ?? r);
                        break;
                    }

                    failures++;
                    info.LastError = outcome.Status is 401 or 403
                        ? "Your session expired. Log in again to send your alert over the internet."
                        : outcome.Error ?? "Could not reach the server.";
                }

                // 2. Wait. Backoff while the server is failing; just wait for a connection when offline.
                //    Fallbacks are re-checked every second, and a connectivity change ends the wait early.
                var delay = offline ? TimeSpan.FromSeconds(30) : BackoffPolicy.Delay(Math.Max(failures, 1), _rng.NextDouble());
                info.NextRetryUtc = DateTime.UtcNow + delay;
                Raise(r);

                var seen = _network.Version;
                var until = DateTime.UtcNow + delay;
                while (DateTime.UtcNow < until && _network.Version == seen)
                {
                    await EvaluateFallbacksAsync(clientId, info);
                    await Task.Delay(1000);
                }
                if (_network.Version != seen) failures = 0;   // connectivity changed: start fresh
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        finally
        {
            info.NextRetryUtc = null;
            lock (_gate) _running.Remove(clientId);
        }
    }

    private async Task EvaluateFallbacksAsync(string clientId, TransmissionInfo info)
    {
        var r = await _db.FindByClientIdAsync(clientId);
        if (r is null || !r.IsActive || r.ServerHasIt) return;

        if (DateTime.UtcNow - _readinessAt > TimeSpan.FromSeconds(5)) await RefreshReadinessAsync();

        var action = FallbackPlanner.Next(new FallbackInput(
            ElapsedSeconds: (DateTime.UtcNow - r.CreatedAtUtc).TotalSeconds,
            InternetUsable: _network.State == NetworkState.Online,
            Delivered: false,
            SmsReady: SmsReady,
            SmsSent: r.SmsSentAtUtc is not null,
            SmsFailures: info.SmsFailures,
            AutoCallEnabled: AppSettings.AutoCallEnabled && !info.AutoCallDismissed,
            AutoCallStarted: info.AutoCallStarted));

        if (action == FallbackAction.SendSms) await SendSmsAsync(r, info);
        else if (action == FallbackAction.StartAutoCall) StartAutoCall(clientId, info);
    }

    // ------------------------------------------------------------------ SMS fallback

    private async Task SendSmsAsync(EmergencyRequest r, TransmissionInfo info)
    {
        if (info.SmsInProgress) return;
        info.SmsInProgress = true;
        Raise(r);
        try
        {
            var user = _session.CurrentUser;
            var lat = r.Latitude ?? _location.Last?.Latitude;
            var lng = r.Longitude ?? _location.Last?.Longitude;
            var text = SmsFormatter.Build(r.Type, r.PeopleCount, user?.FullName ?? "", user?.Phone ?? "",
                                          lat, lng, r.ClientId, r.CreatedAtUtc);

            var ok = await _sms.SendAsync(AppSettings.DispatchSmsNumber.Trim(), text);

            var fresh = await _db.FindByClientIdAsync(r.ClientId) ?? r;
            if (ok)
            {
                fresh.SmsSentAtUtc = DateTime.UtcNow;
                fresh.SentAtUtc ??= fresh.SmsSentAtUtc;
                fresh.SentChannel ??= "SMS";
                await _db.SaveRequestAsync(fresh);
            }
            else info.SmsFailures++;
            Raise(fresh);
        }
        catch (Exception ex) { info.SmsFailures++; System.Diagnostics.Debug.WriteLine(ex); }
        finally { info.SmsInProgress = false; }
    }

    // ------------------------------------------------------------------ auto-call (last resort)

    private void StartAutoCall(string clientId, TransmissionInfo info)
    {
        if (info.AutoCallStarted) return;
        info.AutoCallStarted = true;

        _ = Task.Run(async () =>
        {
            try
            {
                for (var s = AppSettings.AutoCallCountdownSeconds; s > 0; s--)
                {
                    if (!await StillNeedsCallAsync(clientId, info)) { Stop(clientId, info); return; }
                    info.AutoCallSecondsLeft = s;
                    RaiseById(clientId);
                    try { Vibration.Default.Vibrate(150); } catch { }
                    await Task.Delay(1000);
                }

                if (!await StillNeedsCallAsync(clientId, info)) { Stop(clientId, info); return; }
                info.AutoCallSecondsLeft = null;
                info.AutoCallPlaced = await _caller.CallAsync(AppSettings.EmergencyNumber);
                RaiseById(clientId);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); Stop(clientId, info); }
        });
    }

    /// The call is only placed while the alert is still undelivered and the user has not cancelled it.
    private async Task<bool> StillNeedsCallAsync(string clientId, TransmissionInfo info)
    {
        if (info.AutoCallDismissed) return false;
        var r = await _db.FindByClientIdAsync(clientId);
        return r is not null && r.IsActive && !r.ServerHasIt && r.SmsSentAtUtc is null;
    }

    private void Stop(string clientId, TransmissionInfo info)
    {
        info.AutoCallSecondsLeft = null;
        RaiseById(clientId);
    }

    // ------------------------------------------------------------------ events

    private void Raise(EmergencyRequest r) { try { Changed?.Invoke(r); } catch { } }

    private void RaiseById(string clientId)
    {
        _ = Task.Run(async () =>
        {
            var r = await _db.FindByClientIdAsync(clientId);
            if (r is not null) Raise(r);
        });
    }
}
