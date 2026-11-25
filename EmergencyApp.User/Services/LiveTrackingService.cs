using EmergencyApp.User.Models;
using EmergencyApp.User.Reliability;

namespace EmergencyApp.User.Services;

/// While the app is open and an emergency is ongoing: sends the user's live location and pulls the latest
/// status / responder info. Pages just listen to <see cref="Updated"/>.
///  - interval is 5 s normally, slower in low-battery mode (PowerService)
///  - no server calls while the phone is offline; it wakes up the moment connectivity returns
///  - failed updates back off (2 s, 4 s, 8 s ... up to 60 s) instead of hammering a weak network
///  - getting the SOS itself to the server (retries, SMS, call) is handled by TransmissionService
public class LiveTrackingService
{
    private readonly DatabaseService _db;
    private readonly ApiService _api;
    private readonly SessionService _session;
    private readonly SyncService _sync;
    private readonly LocationService _location;
    private readonly TransmissionService _transmission;
    private readonly NetworkMonitor _network;
    private readonly PowerService _power;
    private CancellationTokenSource? _cts;

    public Location? LastLocation { get; private set; }
    public event Action<EmergencyRequest>? Updated;

    public LiveTrackingService(DatabaseService db, ApiService api, SessionService session, SyncService sync,
                               LocationService location, TransmissionService transmission,
                               NetworkMonitor network, PowerService power)
    {
        (_db, _api, _session, _sync, _location, _transmission, _network, _power) =
            (db, api, session, sync, location, transmission, network, power);

        // Delivery progress (queued, retrying, SMS sent, ACK...) reaches the pages through the same event.
        _transmission.Changed += r => Updated?.Invoke(r);
    }

    public void EnsureRunning()
    {
        if (_cts is not null || _session.CurrentUser is null) return;
        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = RunAsync(cts);
    }

    public void Stop() { _cts?.Cancel(); _cts = null; }

    private async Task RunAsync(CancellationTokenSource cts)
    {
        var ct = cts.Token;
        var failures = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var user = _session.CurrentUser;
                if (user is null) break;

                var active = (await _db.GetRequestsAsync(user.Id)).Where(r => r.IsActive).ToList();
                if (active.Count == 0) break;   // nothing ongoing: stop until the next SOS

                var loc = await _location.GetQuietAsync(_power.LocationAccuracy);
                if (loc is not null) LastLocation = loc;

                var online = _network.State != NetworkState.Offline;
                var anyFailed = false;
                foreach (var r in active)
                    if (!await TickAsync(r.ClientId, loc, online)) anyFailed = true;

                failures = anyFailed ? failures + 1 : 0;
                var wait = _power.TrackingInterval;
                if (failures > 0) wait = TimeSpan.FromSeconds(Math.Max(wait.TotalSeconds, BackoffPolicy.Delay(failures).TotalSeconds));
                await WaitAsync(wait, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        finally { if (ReferenceEquals(_cts, cts)) _cts = null; }
    }

    /// Waits, but wakes up early when connectivity changes so updates resume immediately.
    private async Task WaitAsync(TimeSpan wait, CancellationToken ct)
    {
        var seen = _network.Version;
        var until = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < until && _network.Version == seen)
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
    }

    /// Returns false when the server could not be reached (so the loop backs off).
    private async Task<bool> TickAsync(string clientId, Location? loc, bool online)
    {
        var r = await _db.FindByClientIdAsync(clientId);
        if (r is null || !r.IsActive) return true;

        if (!r.ServerHasIt)
        {
            // Not on the server yet: TransmissionService owns the retries, SMS fallback and call fallback.
            _transmission.Ensure(r);
            Updated?.Invoke(r);
            return true;
        }

        if (!online) { Updated?.Invoke(r); return false; }   // wait for connectivity instead of failing requests

        var res = await _api.TrackAsync(r.ServerId!.Value,
            new TrackRequest(loc?.Latitude, loc?.Longitude, loc?.Accuracy), _session.Token);

        if (res.Success && res.Data is not null)
        {
            r.Apply(res.Data);
            await _db.SaveRequestAsync(r);
        }
        else if (!res.Offline)
        {
            // /track not available (or the emergency just ended): fall back to the plain status list.
            await _sync.SyncAllAsync();
            r = await _db.FindByClientIdAsync(clientId) ?? r;
        }
        Updated?.Invoke(r);
        return res.Success || res.Status is >= 400 and < 500;   // 4xx is an answer, not a network failure
    }
}
