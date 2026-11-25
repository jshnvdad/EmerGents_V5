using EmergencyApp.User.Models;

namespace EmergencyApp.User.Services;

/// Result of one upload attempt. <see cref="Ok"/> means the server ACKed it.
public record PushOutcome(bool Ok, int Status = 0, bool Offline = false, string? Error = null)
{
    public static readonly PushOutcome Success = new(true);
}

public class SyncService
{
    private readonly DatabaseService _db;
    private readonly ApiService _api;
    private readonly SessionService _session;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _pushGate = new(1, 1);

    public SyncService(DatabaseService db, ApiService api, SessionService session)
        => (_db, _api, _session) = (db, api, session);

    /// Uploads one request. Safe to call from several places at once (never double-sends).
    public async Task<bool> PushAsync(EmergencyRequest r) => (await TryPushAsync(r)).Ok;

    /// One upload attempt that also says WHY it failed (offline, timeout, 401, 5xx...), so the
    /// retry logic can react. The server's reply is the ACK: only then is the request marked as received.
    public async Task<PushOutcome> TryPushAsync(EmergencyRequest r)
    {
        await _pushGate.WaitAsync();
        try
        {
            var fresh = await _db.FindByClientIdAsync(r.ClientId) ?? r;
            if (fresh.IsSynced && fresh.ServerId is not null) return PushOutcome.Success;

            var dto = new CreateEmergencyDto(fresh.ClientId, fresh.UserId, fresh.Type, fresh.Description,
                                             fresh.Latitude, fresh.Longitude, fresh.CreatedAtUtc,
                                             fresh.PeopleCount, fresh.Source, fresh.DeviceId);
            var res = await _api.CreateEmergencyAsync(dto, _session.Token);
            if (!res.Success || res.Data is null || res.Data.ServerId == 0)
                return new PushOutcome(false, res.Status, res.Offline, res.Error);

            fresh.Apply(res.Data);                       // ServerId + status + server receive time = the ACK
            fresh.SentAtUtc ??= DateTime.UtcNow;
            fresh.SentChannel ??= "Internet";
            await _db.SaveRequestAsync(fresh);
            return PushOutcome.Success;
        }
        finally { _pushGate.Release(); }
    }

    /// Upload anything still pending, then pull the latest status of all requests.
    public async Task SyncAllAsync()
    {
        if (_session.CurrentUser is null || !await _gate.WaitAsync(0)) return;
        try
        {
            foreach (var r in (await _db.GetUnsyncedAsync(_session.CurrentUser.Id)).Where(x => x.IsActive))
                await PushAsync(r);

            var mine = await _api.GetMyEmergenciesAsync(_session.Token);
            if (!mine.Success || mine.Data is null) return;

            foreach (var dto in mine.Data)
            {
                var local = await _db.FindByClientIdAsync(dto.ClientId);
                if (local is null) continue;
                local.Apply(dto);
                await _db.SaveRequestAsync(local);
            }
        }
        finally { _gate.Release(); }
    }
}
