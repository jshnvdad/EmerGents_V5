using EmergencyDesk.Server.Data;

namespace EmergencyDesk.Server.Dtos;

// ---------- Mobile app (matches EmergencyApp.User/Models/Models.cs) ----------
public record MobileLoginRequest(string Email, string Password);
public record MobileRegisterRequest(string FullName, string Phone, string Email, string Password);
public record MobileAuthResponse(int UserId, string FullName, string Phone, string Email, string Token);
public record CreateEmergencyDto(string ClientId, int UserId, string Type, string Description,
                                 double? Latitude, double? Longitude, DateTime CreatedAtUtc,
                                 int PeopleCount = 1, string? Source = null, string? DeviceId = null);
public record TrackRequest(double? Latitude, double? Longitude, double? AccuracyMeters);
public record MobileEmergencyDto(
    int ServerId, string ClientId, string Status, string? ResponderName,
    string? ResponderUnit = null, string? ResponderPhone = null, string? ResponderAddress = null,
    double? ResponderLatitude = null, double? ResponderLongitude = null, double? DistanceKm = null,
    int? EtaMinutes = null, DateTime? ReceivedAtUtc = null, DateTime? DispatchedAtUtc = null,
    DateTime? ArrivedAtUtc = null, DateTime? ResolvedAtUtc = null, DateTime? AcknowledgedAtUtc = null);

// ---------- Desktop app (matches EmergencyDesk/Models/Models.cs) ----------
public record StaffLoginRequest(string Username, string Password);
public record StaffLoginResponse(string Token, string DisplayName, string Role);

public record DeskIncidentDto(
    int Id, IncidentType Type, IncidentStatus Status, string Description,
    string ReporterName, string ReporterPhone, double Latitude, double Longitude,
    string? Address, DateTime ReportedAt, int? AssignedResponderId, string? AssignedResponderName,
    int PeopleCount = 1, string? Source = null, double? AccuracyMeters = null,
    DateTime? LocationUpdatedAt = null, DateTime? AcknowledgedAt = null,
    string? AcknowledgedBy = null, DateTime? DispatchedAt = null,
    DateTime? ArrivedAt = null, DateTime? ResolvedAt = null, string? DeviceId = null);

public record ResponderDto(int Id, string Name, string Unit, IncidentType Specialty,
                           ResponderStatus Status, string Phone, string? Address = null,
                           double? Latitude = null, double? Longitude = null);

public record DeskUserDto(int Id, string FullName, string Email, string Phone,
                          bool IsActive, DateTime RegisteredAt);

public record AssignRequest(int ResponderId);
public record StatusRequest(IncidentStatus Status);
public record ActiveRequest(bool IsActive);
public record AcknowledgeRequest(string AcknowledgedBy);
