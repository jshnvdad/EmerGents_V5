using EmergencyDesk.Server.Data;
using EmergencyDesk.Server.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EmergencyDesk.Server.Controllers;

/// <summary>Endpoints used by the WPF desk app (Admin + Dispatcher).</summary>
[ApiController]
[Route("api/incidents")]
[Authorize(Roles = "Admin,Dispatcher")]
public class IncidentsController : ControllerBase
{
    private readonly AppDbContext _db;
    public IncidentsController(AppDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] DateTimeOffset? fromUtc,
        [FromQuery] DateTimeOffset? toUtc,
        [FromQuery] int? skip,
        [FromQuery] int? take)
    {
        if (fromUtc.HasValue && toUtc.HasValue && fromUtc.Value > toUtc.Value)
            return BadRequest("fromUtc must be earlier than or equal to toUtc.");
        if (skip is < 0)
            return BadRequest("skip must be zero or greater.");
        if (take is < 1 or > 5000)
            return BadRequest("take must be between 1 and 5000.");

        IQueryable<Incident> query = _db.Incidents
            .AsNoTracking()
            .Include(i => i.User).Include(i => i.AssignedResponder)
            .AsQueryable();

        if (fromUtc.HasValue)
            query = query.Where(i => i.ReportedAt >= fromUtc.Value.UtcDateTime);
        if (toUtc.HasValue)
            query = query.Where(i => i.ReportedAt <= toUtc.Value.UtcDateTime);

        query = query.OrderByDescending(i => i.ReportedAt).ThenByDescending(i => i.Id);
        if (skip.HasValue)
            query = query.Skip(skip.Value);
        if (take.HasValue)
            query = query.Take(take.Value);

        var list = await query.ToListAsync();

        return Ok(list.Select(i => new DeskIncidentDto(
            i.Id, i.Type, i.Status, i.Description,
            i.User?.FullName ?? "", i.User?.Phone ?? "",
            i.Latitude ?? 0, i.Longitude ?? 0, i.Address,
            DateTime.SpecifyKind(i.ReportedAt, DateTimeKind.Utc),
            i.AssignedResponderId, i.AssignedResponder?.Name, i.PeopleCount, i.Source,
            i.AccuracyMeters, Utc(i.LocationUpdatedAt), Utc(i.AcknowledgedAt), i.AcknowledgedBy,
            Utc(i.DispatchedAt), Utc(i.ArrivedAt), Utc(i.ResolvedAt), i.DeviceId)));
    }

    [HttpPut("{id:int}/assign")]
    public async Task<IActionResult> Assign(int id, AssignRequest r)
    {
        var incident = await _db.Incidents.FindAsync(id);
        var responder = await _db.Responders.FindAsync(r.ResponderId);
        if (incident is null || responder is null) return NotFound();

        // Free the previously assigned responder, if any.
        if (incident.AssignedResponderId is int oldId && oldId != responder.Id)
        {
            var old = await _db.Responders.FindAsync(oldId);
            if (old is not null && old.Status == ResponderStatus.Busy) old.Status = ResponderStatus.Available;
        }

        incident.AssignedResponderId = responder.Id;
        incident.AcknowledgedAt ??= DateTime.UtcNow;
        incident.AcknowledgedBy ??= User.Identity?.Name;
        if (incident.Status == IncidentStatus.Received)
        {
            incident.Status = IncidentStatus.Dispatched;
            incident.DispatchedAt ??= DateTime.UtcNow;
        }
        responder.Status = ResponderStatus.Busy;
        await _db.SaveChangesAsync();
        return Ok();
    }

    [HttpPut("{id:int}/status")]
    public async Task<IActionResult> SetStatus(int id, StatusRequest r)
    {
        var incident = await _db.Incidents.Include(i => i.AssignedResponder).FirstOrDefaultAsync(i => i.Id == id);
        if (incident is null) return NotFound();

        var now = DateTime.UtcNow;
        incident.Status = r.Status;
        if (r.Status != IncidentStatus.Received)
        {
            incident.AcknowledgedAt ??= now;
            incident.AcknowledgedBy ??= User.Identity?.Name;
        }
        if (r.Status is IncidentStatus.Dispatched or IncidentStatus.EnRoute or IncidentStatus.OnScene)
            incident.DispatchedAt ??= now;
        if (r.Status == IncidentStatus.OnScene) incident.ArrivedAt ??= now;
        if (r.Status is IncidentStatus.Resolved or IncidentStatus.Cancelled) incident.ResolvedAt ??= now;
        if (r.Status is IncidentStatus.Resolved or IncidentStatus.Cancelled
            && incident.AssignedResponder is { Status: ResponderStatus.Busy } responder)
            responder.Status = ResponderStatus.Available;

        await _db.SaveChangesAsync();
        return Ok();
    }

    [HttpPut("{id:int}/acknowledge")]
    public async Task<IActionResult> Acknowledge(int id, AcknowledgeRequest r)
    {
        var incident = await _db.Incidents.FindAsync(id);
        if (incident is null) return NotFound();
        if (incident.Status is IncidentStatus.Resolved or IncidentStatus.Cancelled)
            return Conflict("Closed incidents cannot be acknowledged.");
        incident.AcknowledgedAt ??= DateTime.UtcNow;
        if (string.IsNullOrWhiteSpace(incident.AcknowledgedBy))
            incident.AcknowledgedBy = string.IsNullOrWhiteSpace(r.AcknowledgedBy)
                ? User.Identity?.Name : r.AcknowledgedBy.Trim();
        await _db.SaveChangesAsync();
        return Ok();
    }

    private static DateTime? Utc(DateTime? value) => value is null
        ? null : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);
}

[ApiController]
[Route("api/responders")]
[Authorize(Roles = "Admin,Dispatcher")]
public class RespondersController : ControllerBase
{
    private readonly AppDbContext _db;
    public RespondersController(AppDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> GetAll() =>
        Ok(await _db.Responders.OrderBy(r => r.Name)
            .Select(r => new ResponderDto(r.Id, r.Name, r.Unit, r.Specialty, r.Status, r.Phone,
                r.Address, r.Latitude, r.Longitude))
            .ToListAsync());

    [HttpPost]
    public async Task<IActionResult> Create(ResponderDto d)
    {
        if (string.IsNullOrWhiteSpace(d.Name) || string.IsNullOrWhiteSpace(d.Unit))
            return BadRequest("Name and unit are required.");
        var r = new Responder { Name = d.Name, Unit = d.Unit, Specialty = d.Specialty, Status = d.Status,
            Phone = d.Phone, Address = d.Address, Latitude = d.Latitude, Longitude = d.Longitude };
        _db.Responders.Add(r);
        await _db.SaveChangesAsync();
        return Ok(new ResponderDto(r.Id, r.Name, r.Unit, r.Specialty, r.Status, r.Phone,
            r.Address, r.Latitude, r.Longitude));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, ResponderDto d)
    {
        var r = await _db.Responders.FindAsync(id);
        if (r is null) return NotFound();
        r.Name = d.Name; r.Unit = d.Unit; r.Specialty = d.Specialty; r.Status = d.Status; r.Phone = d.Phone;
        r.Address = d.Address; r.Latitude = d.Latitude; r.Longitude = d.Longitude;
        await _db.SaveChangesAsync();
        return Ok();
    }
}

[ApiController]
[Route("api/users")]
[Authorize(Roles = "Admin")]
public class UsersController : ControllerBase
{
    private readonly AppDbContext _db;
    public UsersController(AppDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> GetAll() =>
        Ok((await _db.AppUsers.OrderBy(u => u.FullName).ToListAsync())
            .Select(u => new DeskUserDto(u.Id, u.FullName, u.Email, u.Phone, u.IsActive,
                DateTime.SpecifyKind(u.RegisteredAt, DateTimeKind.Utc))));

    [HttpPut("{id:int}/active")]
    public async Task<IActionResult> SetActive(int id, ActiveRequest r)
    {
        var u = await _db.AppUsers.FindAsync(id);
        if (u is null) return NotFound();
        u.IsActive = r.IsActive;
        await _db.SaveChangesAsync();
        return Ok();
    }
}
