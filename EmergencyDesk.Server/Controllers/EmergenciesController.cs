using System.Security.Claims;
using EmergencyDesk.Server.Data;
using EmergencyDesk.Server.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EmergencyDesk.Server.Controllers;

/// <summary>Endpoints used by the mobile app.</summary>
[ApiController]
[Route("api/emergencies")]
[Authorize(Roles = "User")]
public class EmergenciesController : ControllerBase
{
    private readonly AppDbContext _db;
    public EmergenciesController(AppDbContext db) => _db = db;

    private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpPost]
    public async Task<IActionResult> Create(CreateEmergencyDto d)
    {
        var userId = CurrentUserId;   // trust the token, not the body
        var user = await _db.AppUsers.FindAsync(userId);
        if (user is null) return Unauthorized();
        if (!user.IsActive) return StatusCode(403, "This account has been deactivated.");

        // Idempotency: a retried upload returns the existing record.
        var existing = await _db.Incidents.Include(i => i.AssignedResponder)
            .FirstOrDefaultAsync(i => i.ClientId == d.ClientId && i.UserId == userId);
        if (existing is not null) return Ok(ToMobile(existing));

        if (string.IsNullOrWhiteSpace(d.ClientId) || d.ClientId.Length > 64)
            return BadRequest("A clientId of 1 to 64 characters is required.");
        if (d.PeopleCount is < 1 or > 4) return BadRequest("peopleCount must be 1, 2, 3, or 4 (4 means 4 or more).");
        if (!ValidCoordinates(d.Latitude, d.Longitude)) return BadRequest("Latitude or longitude is outside the valid range.");
        if (!Enum.TryParse<IncidentType>(d.Type, ignoreCase: true, out var type))
            return BadRequest("Unknown emergency type.");

        var incident = new Incident
        {
            ClientId = d.ClientId,
            UserId = userId,
            Type = type,
            Description = d.Description ?? "",
            Latitude = d.Latitude,
            Longitude = d.Longitude,
            Address = d.Latitude is null ? "Location unavailable" : null,
            ReportedAt = AsUtc(d.CreatedAtUtc),
            PeopleCount = d.PeopleCount,
            Source = d.Source is { Length: > 40 } ? d.Source[..40] : d.Source,
            DeviceId = d.DeviceId is { Length: > 64 } ? d.DeviceId[..64] : d.DeviceId,
            ReceivedAt = DateTime.UtcNow,      // the ACK: the moment this server got the alert
            LocationUpdatedAt = d.Latitude is null ? null : DateTime.UtcNow,
            Status = IncidentStatus.Received
        };
        _db.Incidents.Add(incident);
        await _db.SaveChangesAsync();
        return Ok(ToMobile(incident));
    }

    [HttpGet("mine")]
    public async Task<IActionResult> Mine()
    {
        var userId = CurrentUserId;
        var list = await _db.Incidents.Include(i => i.AssignedResponder)
            .Where(i => i.UserId == userId)
            .OrderByDescending(i => i.ReportedAt)
            .ToListAsync();
        return Ok(list.Select(ToMobile));
    }

    [HttpPost("{serverId:int}/track")]
    public async Task<IActionResult> Track(int serverId, TrackRequest request)
    {
        var incident = await _db.Incidents.Include(i => i.AssignedResponder)
            .FirstOrDefaultAsync(i => i.Id == serverId && i.UserId == CurrentUserId);
        if (incident is null) return NotFound();
        if (incident.Status is IncidentStatus.Resolved or IncidentStatus.Cancelled)
            return Conflict("This emergency is no longer active.");
        if (!ValidCoordinates(request.Latitude, request.Longitude) || request.Latitude is null || request.Longitude is null)
            return BadRequest("Valid latitude and longitude are required.");
        if (request.AccuracyMeters is < 0 or > 100000) return BadRequest("accuracyMeters is outside the valid range.");

        incident.Latitude = request.Latitude;
        incident.Longitude = request.Longitude;
        incident.AccuracyMeters = request.AccuracyMeters;
        incident.LocationUpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(ToMobile(incident));
    }

    [HttpPost("{serverId:int}/cancel")]
    public async Task<IActionResult> Cancel(int serverId)
    {
        var incident = await _db.Incidents.Include(i => i.AssignedResponder)
            .FirstOrDefaultAsync(i => i.Id == serverId && i.UserId == CurrentUserId);
        if (incident is null) return NotFound();
        if (incident.Status is not (IncidentStatus.Resolved or IncidentStatus.Cancelled))
        {
            incident.Status = IncidentStatus.Cancelled;
            incident.ResolvedAt = DateTime.UtcNow;
            if (incident.AssignedResponder is { Status: ResponderStatus.Busy } responder)
                responder.Status = ResponderStatus.Available;
            await _db.SaveChangesAsync();
        }
        return Ok(ToMobile(incident));
    }

    /// The phone uses Arrived while the desk uses OnScene.
    private static MobileEmergencyDto ToMobile(Incident i)
    {
        var status = i.Status switch
        {
            IncidentStatus.EnRoute => "Dispatched",
            IncidentStatus.OnScene => "Arrived",
            _ => i.Status.ToString()
        };
        return new(i.Id, i.ClientId, status, i.AssignedResponder?.Name,
            i.AssignedResponder?.Unit, i.AssignedResponder?.Phone, i.AssignedResponder?.Address,
            i.AssignedResponder?.Latitude, i.AssignedResponder?.Longitude, null, null,
            Utc(i.ReceivedAt ?? i.ReportedAt), Utc(i.DispatchedAt), Utc(i.ArrivedAt), Utc(i.ResolvedAt),
            Utc(i.AcknowledgedAt));
    }

    private static DateTime? Utc(DateTime? d) => d is null ? null : DateTime.SpecifyKind(d.Value, DateTimeKind.Utc);
    private static DateTime AsUtc(DateTime d) => d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : DateTime.SpecifyKind(d, DateTimeKind.Utc);
    private static bool ValidCoordinates(double? lat, double? lon) =>
        (lat is null && lon is null) || (lat is >= -90 and <= 90 && lon is >= -180 and <= 180);
}
