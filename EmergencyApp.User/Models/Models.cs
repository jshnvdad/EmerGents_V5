using System.Globalization;
using EmergencyApp.User.Reliability;
using SQLite;

namespace EmergencyApp.User.Models;

public static class RequestStatus
{
    public const string Pending = "Pending";       // saved on the phone, not yet on the server
    public const string Received = "Received";     // server has it
    public const string Dispatched = "Dispatched"; // a unit is on the way
    public const string Arrived = "Arrived";       // unit is on scene
    public const string Resolved = "Resolved";
    public const string Cancelled = "Cancelled";

    public static bool IsActive(string s) => s is not (Resolved or Cancelled);

    /// Maps whatever words your server uses to the statuses above. Adjust to match your server.
    public static string Normalize(string? s)
    {
        var k = (s ?? "").Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "");
        return k switch
        {
            "new" or "pending" or "open" or "received" or "acknowledged" => Received,
            "dispatched" or "assigned" or "enroute" or "ontheway" => Dispatched,
            "arrived" or "onscene" => Arrived,
            "resolved" or "completed" or "closed" or "done" or "finished" => Resolved,
            "cancelled" or "canceled" => Cancelled,
            _ => string.IsNullOrEmpty(s) ? Received : s!
        };
    }
}

public class LocalUser
{
    [PrimaryKey] public int Id { get; set; }
    public string FullName { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public string BloodType { get; set; } = "";
    public string Allergies { get; set; } = "";
    public string Conditions { get; set; } = "";
}

public class EmergencyContact
{
    [PrimaryKey, AutoIncrement] public int LocalId { get; set; }
    public int UserId { get; set; }
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
}

public class EmergencyRequest
{
    [PrimaryKey, AutoIncrement] public int LocalId { get; set; }
    [Indexed] public string ClientId { get; set; } = Guid.NewGuid().ToString();
    public int UserId { get; set; }
    public int? ServerId { get; set; }
    public string Type { get; set; } = "";
    public string Description { get; set; } = "";
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string Status { get; set; } = RequestStatus.Pending;
    public bool IsSynced { get; set; }
    public int PeopleCount { get; set; } = 1;     // 4 means "4 or more"
    public string? Source { get; set; }           // Hold, Tap, Shake, Volume button
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? DeviceId { get; set; }          // this phone's install ID, sent with the alert

    // Delivery tracking (Transmission & Reliability)
    public DateTime? SentAtUtc { get; set; }       // first time the alert left the phone (internet or SMS)
    public string? SentChannel { get; set; }       // "Internet" or "SMS" (whichever got out first)
    public DateTime? SmsSentAtUtc { get; set; }    // fallback SMS handed to the carrier
    public DateTime? AcknowledgedAtUtc { get; set; } // a dispatcher acknowledged it (from the server)

    // Live info (filled in by the server while the emergency is ongoing, kept after it finishes)
    public string? ResponderName { get; set; }
    public string? ResponderUnit { get; set; }
    public string? ResponderPhone { get; set; }
    public string? ResponderAddress { get; set; }
    public double? ResponderLatitude { get; set; }
    public double? ResponderLongitude { get; set; }
    public double? DistanceKm { get; set; }
    public int? EtaMinutes { get; set; }
    public DateTime? ReceivedAtUtc { get; set; }
    public DateTime? DispatchedAtUtc { get; set; }
    public DateTime? ArrivedAtUtc { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }

    // UI helpers (not stored)
    [Ignore] public string PeopleText => PeopleCount <= 1 ? "1 person" : PeopleCount >= 4 ? "4+ people" : $"{PeopleCount} people";
    [Ignore] public string Title => $"{Type} · {PeopleText}";
    [Ignore] public bool IsActive => RequestStatus.IsActive(Status);
    [Ignore] public bool ServerHasIt => IsSynced && ServerId is not null;
    [Ignore] public DeliveryStep Delivery => DeliveryCalculator.Step(ServerHasIt, SentAtUtc, AcknowledgedAtUtc, Status);
    [Ignore] public string DeliveryText => Delivery switch
    {
        DeliveryStep.Acknowledged => "Acknowledged by dispatcher",
        DeliveryStep.Received => "Received by server",
        DeliveryStep.Sent => "Sent by SMS",
        _ => "Queued on this phone"
    };
    [Ignore] public string CreatedLocal => CreatedAtUtc.ToLocalTime().ToString("dd MMM yyyy, HH:mm");
    [Ignore] public string Icon => EmergencyTypeOption.All.FirstOrDefault(t => t.Name == Type)?.Icon ?? "🚨";
    [Ignore] public string DistanceText => DistanceKm is double d ? GeoMath.FormatDistance(d) + " away" : "";
    [Ignore] public string ResponderText =>
        !string.IsNullOrEmpty(ResponderName) ? $"Responder: {ResponderName}"
        : IsActive ? "Waiting for a responder" : "";
    [Ignore] public string LiveHint => IsActive
        ? (string.IsNullOrEmpty(DistanceText) ? "Tap to view live info ›" : $"{DistanceText} · Tap to view live info ›")
        : "Tap to view details ›";
    [Ignore] public Color StatusColor => Status switch
    {
        RequestStatus.Pending => Colors.Orange,
        RequestStatus.Received => Colors.DodgerBlue,
        RequestStatus.Dispatched => Colors.Purple,
        RequestStatus.Arrived => Colors.Teal,
        RequestStatus.Resolved => Colors.SeaGreen,
        _ => Colors.Gray
    };
}

public class EmergencyTypeOption
{
    public string Name { get; init; } = "";
    public string Icon { get; init; } = "";

    public static readonly List<EmergencyTypeOption> All = new()
    {
        new() { Name = "Medical",  Icon = "🚑" },
        new() { Name = "Fire",     Icon = "🔥" },
        new() { Name = "Police",   Icon = "🚓" },
        new() { Name = "Accident", Icon = "🚗" },
        new() { Name = "Other",    Icon = "❓" },
    };
}

public static class GeoMath
{
    public static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371;
        double Rad(double d) => d * Math.PI / 180;
        var dLat = Rad(lat2 - lat1); var dLon = Rad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Asin(Math.Sqrt(a));
    }

    public static string FormatDistance(double km) =>
        km < 1 ? $"{km * 1000:0} m" : $"{km.ToString("0.0", CultureInfo.InvariantCulture)} km";

    public static string FormatCoords(double lat, double lng) =>
        $"{Math.Abs(lat).ToString("F4", CultureInfo.InvariantCulture)}° {(lat >= 0 ? "N" : "S")}, " +
        $"{Math.Abs(lng).ToString("F4", CultureInfo.InvariantCulture)}° {(lng >= 0 ? "E" : "W")}";

    public static string MapsUrl(double lat, double lng) =>
        $"https://www.google.com/maps?q={lat.ToString(CultureInfo.InvariantCulture)},{lng.ToString(CultureInfo.InvariantCulture)}";
}

public static class RequestExtensions
{
    /// Copies server-provided live info into the local record (never wipes existing values with nulls).
    public static void Apply(this EmergencyRequest r, LiveEmergencyDto d)
    {
        if (d.ServerId != 0) r.ServerId = d.ServerId;
        r.IsSynced = true;
        r.Status = RequestStatus.Normalize(d.Status);
        r.ResponderName = d.ResponderName ?? r.ResponderName;
        r.ResponderUnit = d.ResponderUnit ?? r.ResponderUnit;
        r.ResponderPhone = d.ResponderPhone ?? r.ResponderPhone;
        r.ResponderAddress = d.ResponderAddress ?? r.ResponderAddress;
        r.ResponderLatitude = d.ResponderLatitude ?? r.ResponderLatitude;
        r.ResponderLongitude = d.ResponderLongitude ?? r.ResponderLongitude;
        r.DistanceKm = d.DistanceKm ?? r.DistanceKm;
        r.EtaMinutes = d.EtaMinutes ?? r.EtaMinutes;
        r.ReceivedAtUtc = d.ReceivedAtUtc ?? r.ReceivedAtUtc;
        r.AcknowledgedAtUtc = d.AcknowledgedAtUtc ?? r.AcknowledgedAtUtc;
        r.DispatchedAtUtc = d.DispatchedAtUtc ?? r.DispatchedAtUtc;
        r.ArrivedAtUtc = d.ArrivedAtUtc ?? r.ArrivedAtUtc;
        r.ResolvedAtUtc = d.ResolvedAtUtc ?? r.ResolvedAtUtc;

        // If the server doesn't send timestamps, stamp them when we first see the status.
        var now = DateTime.UtcNow;
        if (r.Status is RequestStatus.Received or RequestStatus.Dispatched or RequestStatus.Arrived or RequestStatus.Resolved)
            r.ReceivedAtUtc ??= now;
        if (r.Status is RequestStatus.Dispatched or RequestStatus.Arrived) r.DispatchedAtUtc ??= now;
        if (r.Status is RequestStatus.Arrived) r.ArrivedAtUtc ??= now;
        if (r.Status is RequestStatus.Resolved or RequestStatus.Cancelled) r.ResolvedAtUtc ??= now;

        // The server has it, so it has certainly left the phone.
        r.SentAtUtc ??= r.ReceivedAtUtc ?? now;
        r.SentChannel ??= "Internet";
    }
}

// ---- API DTOs (keep in sync with the ASP.NET Core server) ----
public record LoginRequest(string Email, string Password);
public record RegisterRequest(string FullName, string Phone, string Email, string Password);
public record AuthResponse(int UserId, string FullName, string Phone, string Email, string Token);
public record CreateEmergencyDto(string ClientId, int UserId, string Type, string Description,
                                 double? Latitude, double? Longitude, DateTime CreatedAtUtc,
                                 int PeopleCount = 1, string? Source = null, string? DeviceId = null);
public record EmergencyDto(int ServerId, string ClientId, string Status, string? ResponderName);
public record TrackRequest(double? Latitude, double? Longitude, double? AccuracyMeters);
public record ApiResult<T>(bool Success, T? Data, string? Error, bool Offline = false, int Status = 0);

/// Live view of an emergency. Every field except ServerId/ClientId/Status is optional.
public class LiveEmergencyDto
{
    public int ServerId { get; set; }
    public string ClientId { get; set; } = "";
    public string Status { get; set; } = "";
    public string? ResponderName { get; set; }
    public string? ResponderUnit { get; set; }
    public string? ResponderPhone { get; set; }
    public string? ResponderAddress { get; set; }
    public double? ResponderLatitude { get; set; }
    public double? ResponderLongitude { get; set; }
    public double? DistanceKm { get; set; }
    public int? EtaMinutes { get; set; }
    public DateTime? ReceivedAtUtc { get; set; }
    public DateTime? AcknowledgedAtUtc { get; set; }
    public DateTime? DispatchedAtUtc { get; set; }
    public DateTime? ArrivedAtUtc { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
}

public class PeopleOption
{
    public int Value { get; init; }
    public string Label { get; init; } = "";

    public static readonly List<PeopleOption> All = new()
    {
        new() { Value = 1, Label = "1" },
        new() { Value = 2, Label = "2" },
        new() { Value = 3, Label = "3" },
        new() { Value = 4, Label = "4+" },
    };
}
