using System.ComponentModel;
using System.Text.Json.Serialization;

namespace EmergencyDesk.Models;

public enum IncidentStatus { Received, Dispatched, EnRoute, OnScene, Resolved, Cancelled }
public enum IncidentType { Medical, Fire, Police, Rescue, Accident, Other, General }
public enum ResponderStatus { Available, Busy, OffDuty }

/// <summary>Inbox lifecycle derived on the desk: New -> Acknowledged -> InProgress -> Closed.</summary>
public enum QueueState { New, Acknowledged, InProgress, Closed }
public enum PriorityLevel { Low, Medium, High, Critical }
public enum SlaState { None, Ok, Warning, Breached, Met, Late }

public class LoginRequest
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

public class LoginResponse
{
    public string Token { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>"Admin" or "Dispatcher"</summary>
    public string Role { get; set; } = "";
}

public class Incident : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    // ---------- fields sent by the server ----------
    public int Id { get; set; }
    public IncidentType Type { get; set; }
    public int PeopleCount { get; set; } = 1; // 4 means "4 or more"
    public string? Source { get; set; }
    public IncidentStatus Status { get; set; }
    public string Description { get; set; } = "";
    public string ReporterName { get; set; } = "";
    public string ReporterPhone { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string? Address { get; set; }
    public DateTime ReportedAt { get; set; }
    public int? AssignedResponderId { get; set; }
    public string? AssignedResponderName { get; set; }

    // Optional: filled by the server once it supports acknowledge / dispatch timestamps.
    // Until then the desk fills them from its own local record.
    public DateTime? AcknowledgedAt { get; set; }
    public string? AcknowledgedBy { get; set; }
    public DateTime? DispatchedAt { get; set; }
    public string? DispatchedBy { get; set; }

    // ---------- helpers ----------
    [JsonIgnore] public string Coordinates => $"{Latitude:F5}, {Longitude:F5}";
    [JsonIgnore] public string PeopleText => PeopleCount switch
    {
        <= 1 => "1 person",
        >= 4 => "4+ people",
        _ => $"{PeopleCount} people"
    };
    [JsonIgnore] public string ReportedLocal => ReportedAt.ToLocalTime().ToString("g");
    [JsonIgnore] public bool IsClosed => Status is IncidentStatus.Resolved or IncidentStatus.Cancelled;
    [JsonIgnore] public int QueueRank => IsClosed ? 1 : 0;

    /// <summary>The API sends UTC; a value without a zone is treated as UTC.</summary>
    public static DateTime AsUtc(DateTime d) =>
        d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : DateTime.SpecifyKind(d, DateTimeKind.Utc);

    // ---------- desk-side state (filled by IncidentEngine, never sent to the server) ----------
    [JsonIgnore] public QueueState QueueState { get; set; }
    [JsonIgnore] public int PriorityScore { get; set; }
    [JsonIgnore] public PriorityLevel PriorityLevel { get; set; }
    [JsonIgnore] public string PriorityReason { get; set; } = "";
    [JsonIgnore] public bool IsEscalated { get; set; }
    [JsonIgnore] public bool IsStale { get; set; }
    [JsonIgnore] public int? DuplicateOfId { get; set; }
    [JsonIgnore] public int DuplicateCount { get; set; }
    [JsonIgnore] public int? MergedIntoId { get; set; }
    [JsonIgnore] public string AgeText { get; set; } = "";
    [JsonIgnore] public string SlaAckText { get; set; } = "—";
    [JsonIgnore] public SlaState SlaAckState { get; set; }
    [JsonIgnore] public string SlaDispatchText { get; set; } = "—";
    [JsonIgnore] public SlaState SlaDispatchState { get; set; }

    [JsonIgnore] public bool IsDuplicate => DuplicateOfId is not null;
    /// <summary>Closed on this desk as a duplicate of another report; left out of statistics so one event counts once.</summary>
    [JsonIgnore] public bool IsMergedDuplicate => MergedIntoId is not null;
    [JsonIgnore] public bool CanMerge => !IsClosed && (DuplicateOfId is not null || DuplicateCount > 0);
    [JsonIgnore] public bool HasDuplicateInfo => MergedIntoId is not null || CanMerge;

    [JsonIgnore] public string PriorityLabel => IsClosed ? "—" : $"{PriorityLevel} {PriorityScore}";

    [JsonIgnore] public string StateText => IsEscalated ? "Escalated" : QueueState switch
    {
        QueueState.New => "New",
        QueueState.Acknowledged => "Acknowledged",
        QueueState.InProgress => "In progress",
        _ => Status.ToString()
    };

    [JsonIgnore] public string FlagsText
    {
        get
        {
            var f = new List<string>();
            if (MergedIntoId is { } m) f.Add($"merged #{m}");
            else if (DuplicateOfId is { } d && !IsClosed) f.Add($"dup of #{d}");
            else if (DuplicateCount > 0 && !IsClosed) f.Add($"+{DuplicateCount} dup");
            return string.Join(" · ", f);
        }
    }

    [JsonIgnore] public string DuplicateText =>
        MergedIntoId is { } m ? $"Merged into incident #{m}."
        : DuplicateOfId is { } d ? $"Possible duplicate of incident #{d} (same reporter or location, close in time)."
        : DuplicateCount > 0 ? $"{DuplicateCount} other report(s) look like the same incident."
        : "";

    [JsonIgnore] public string AckText =>
        AcknowledgedAt is { } a
            ? $"Acknowledged{(string.IsNullOrWhiteSpace(AcknowledgedBy) ? "" : " by " + AcknowledgedBy)} at {AsUtc(a).ToLocalTime():T}"
            : "";

    /// <summary>Refreshes every binding on this incident (used after the engine recomputes desk-side fields).</summary>
    public void NotifyClientChanged() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

public class Responder
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "";      // e.g. "Ambulance 3", "Engine 12"
    public IncidentType Specialty { get; set; }
    public ResponderStatus Status { get; set; }
    public string Phone { get; set; } = "";

    // Desk-side only (not sent to the server)
    [JsonIgnore] public string Region { get; set; } = "";
    [JsonIgnore] public int ActiveJobs { get; set; }
}

public class AppUser
{
    public int Id { get; set; }
    public string FullName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public bool IsActive { get; set; }
    public DateTime RegisteredAt { get; set; }

    [JsonIgnore] public string RegisteredLocal => RegisteredAt.ToLocalTime().ToString("d");
}

public class AssignRequest { public int ResponderId { get; set; } }
public class StatusRequest { public IncidentStatus Status { get; set; } }
public class ActiveRequest { public bool IsActive { get; set; } }
public class AcknowledgeRequest { public string AcknowledgedBy { get; set; } = ""; }
