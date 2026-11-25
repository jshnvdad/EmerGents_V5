using EmergencyApp.User.Models;

namespace EmergencyApp.User.Services;

/// Holds what the user picked (type, number of people) and creates the SOS request.
/// Shared by the hold button and the tap / shake / volume countdown.
public class SosService
{
    private readonly DatabaseService _db;
    private readonly SessionService _session;
    private readonly LocationService _location;
    private readonly LiveTrackingService _tracking;
    private readonly TransmissionService _transmission;

    public EmergencyTypeOption? Type { get; set; }
    public int People { get; set; } = 1;

    public SosService(DatabaseService db, SessionService session, LocationService location,
                      LiveTrackingService tracking, TransmissionService transmission)
        => (_db, _session, _location, _tracking, _transmission) = (db, session, location, tracking, transmission);

    public void Reset() { Type = null; People = 1; }

    /// Saves the request locally, starts delivering/tracking it, and returns its ClientId.
    public async Task<string?> SendAsync(string source)
    {
        var user = _session.CurrentUser;
        if (user is null) return null;

        // Use a recent location right away; only wait for a fresh fix if we have nothing recent.
        var loc = _location.Last;
        if (loc is null || DateTimeOffset.UtcNow - loc.Timestamp > TimeSpan.FromMinutes(2))
            loc = await _location.GetCurrentAsync() ?? _location.Last;

        var req = new EmergencyRequest
        {
            UserId = user.Id,
            Type = Type?.Name ?? "General",
            PeopleCount = People,
            Source = source,
            Latitude = loc?.Latitude,
            Longitude = loc?.Longitude,
            DeviceId = AppSettings.DeviceId,
        };
        await _db.SaveRequestAsync(req);   // saved first, so it survives being offline
        Reset();
        _transmission.Ensure(req);         // start delivery right now (internet, then retry / SMS / call fallbacks)
        _tracking.EnsureRunning();         // live location + status updates
        return req.ClientId;
    }
}
