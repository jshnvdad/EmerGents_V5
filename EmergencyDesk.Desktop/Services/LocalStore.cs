using System.IO;
using System.Text.Json;

namespace EmergencyDesk.Services;

/// <summary>Operator-configurable rules (alerts, escalation, SLA, auto-assignment, duplicates).</summary>
public class AppRules
{
    // Alerts
    public bool SoundEnabled { get; set; } = true;
    public int ReAlertSeconds { get; set; } = 30;       // popup comes back this often until acknowledged
    public int StaleAfterHours { get; set; } = 24;      // older unhandled reports no longer pop up / escalate

    // Escalation + SLA targets
    public int EscalateAfterSeconds { get; set; } = 120;
    public int AckSlaSeconds { get; set; } = 60;
    public int DispatchSlaSeconds { get; set; } = 300;

    // Auto-assignment
    public bool AutoAssignEnabled { get; set; }
    public bool MatchSpecialty { get; set; } = true;
    public bool RequireSpecialtyMatch { get; set; }
    public bool MatchRegion { get; set; }
    public bool BalanceWorkload { get; set; } = true;

    // Duplicate detection
    public int DuplicateWindowMinutes { get; set; } = 10;
    public int DuplicateRadiusMeters { get; set; } = 150;

    public AppRules Clone() => JsonSerializer.Deserialize<AppRules>(JsonSerializer.Serialize(this))!;

    public void Normalize()
    {
        ReAlertSeconds = Math.Clamp(ReAlertSeconds, 5, 3600);
        StaleAfterHours = Math.Clamp(StaleAfterHours, 1, 720);
        EscalateAfterSeconds = Math.Clamp(EscalateAfterSeconds, 10, 86400);
        AckSlaSeconds = Math.Clamp(AckSlaSeconds, 5, 86400);
        DispatchSlaSeconds = Math.Clamp(DispatchSlaSeconds, 10, 86400);
        DuplicateWindowMinutes = Math.Clamp(DuplicateWindowMinutes, 1, 240);
        DuplicateRadiusMeters = Math.Clamp(DuplicateRadiusMeters, 10, 5000);
    }
}

/// <summary>One step in an incident's history as this desk knows it (shown in the incident report timeline).</summary>
public class TimelineEvent
{
    public DateTime TimeUtc { get; set; }
    public string Text { get; set; } = "";
    public string By { get; set; } = "";
    /// <summary>"Operator" = done on a desk; "Observed" = noticed while polling the server (time is approximate).</summary>
    public string Source { get; set; } = "Observed";
}

/// <summary>What this desk knows about an incident beyond what the server sends.</summary>
public class IncidentLocalRecord
{
    public DateTime FirstSeenUtc { get; set; }
    public bool SeenAsReceived { get; set; }
    public DateTime? AckedAtUtc { get; set; }
    public string? AckedBy { get; set; }
    public DateTime? DispatchedAtUtc { get; set; }
    public string? DispatchedBy { get; set; }
    public int? MergedInto { get; set; }

    // History tracking (incident reports / timeline)
    public string? LastStatus { get; set; }
    public int? LastResponderId { get; set; }
    public List<TimelineEvent> Events { get; set; } = new();
}

/// <summary>Persists rules and per-incident desk records under %LocalAppData%\EmergencyDesk.</summary>
public class LocalStore
{
    private class StateFile
    {
        public Dictionary<int, IncidentLocalRecord> Records { get; set; } = new();
        public Dictionary<int, string> ResponderRegions { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    /// <summary>How long per-incident records (timelines, SLA stamps) are kept. Reports need history, so this is long.</summary>
    public const int RecordRetentionDays = 180;
    private const int MaxEventsPerIncident = 100;

    private readonly string _dir;
    private StateFile _state = new();
    private bool _dirty;

    public AppRules Rules { get; private set; } = new();

    /// <summary>Scheduled-report settings (e-mail recipients, SMTP, time of day).</summary>
    public ReportSchedule Schedule { get; private set; } = new();

    /// <summary>Folder where this desk keeps rules.json, state.json, reports.json and audit.jsonl.</summary>
    public string DataDirectory => _dir;

    public LocalStore(string? directory = null)
    {
        _dir = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EmergencyDesk");
    }

    private string RulesPath => Path.Combine(_dir, "rules.json");
    private string StatePath => Path.Combine(_dir, "state.json");
    private string SchedulePath => Path.Combine(_dir, "reports.json");

    public static LocalStore Load(string? directory = null)
    {
        var s = new LocalStore(directory);
        try
        {
            if (File.Exists(s.RulesPath))
                s.Rules = JsonSerializer.Deserialize<AppRules>(File.ReadAllText(s.RulesPath)) ?? new AppRules();
            s.Rules.Normalize();
        }
        catch { s.Rules = new AppRules(); }

        try
        {
            if (File.Exists(s.StatePath))
                s._state = JsonSerializer.Deserialize<StateFile>(File.ReadAllText(s.StatePath)) ?? new StateFile();
        }
        catch { s._state = new StateFile(); }

        try
        {
            if (File.Exists(s.SchedulePath))
                s.Schedule = JsonSerializer.Deserialize<ReportSchedule>(File.ReadAllText(s.SchedulePath)) ?? new ReportSchedule();
            s.Schedule.Normalize();
        }
        catch { s.Schedule = new ReportSchedule(); }

        var cutoff = DateTime.UtcNow.AddDays(-RecordRetentionDays);
        foreach (var k in s._state.Records.Where(kv => kv.Value.FirstSeenUtc < cutoff).Select(kv => kv.Key).ToList())
            s._state.Records.Remove(k);
        return s;
    }

    // ---- rules ----
    public void ApplyRules(AppRules rules)
    {
        rules.Normalize();
        Rules = rules;
        try { Directory.CreateDirectory(_dir); File.WriteAllText(RulesPath, JsonSerializer.Serialize(rules, Opts)); }
        catch { /* non-fatal: rules stay active for this session */ }
    }

    // ---- scheduled reports ----
    public void ApplySchedule(ReportSchedule schedule)
    {
        schedule.Normalize();
        Schedule = schedule;
        SaveSchedule();
    }

    public void SaveSchedule()
    {
        try { Directory.CreateDirectory(_dir); File.WriteAllText(SchedulePath, JsonSerializer.Serialize(Schedule, Opts)); }
        catch { /* non-fatal: settings stay active for this session */ }
    }

    // ---- incident records ----
    public IncidentLocalRecord GetOrCreate(int id, DateTime nowUtc)
    {
        if (!_state.Records.TryGetValue(id, out var r))
        {
            r = new IncidentLocalRecord { FirstSeenUtc = nowUtc };
            _state.Records[id] = r;
            _dirty = true;
        }
        return r;
    }

    public IncidentLocalRecord? TryGet(int id) => _state.Records.TryGetValue(id, out var r) ? r : null;

    public void RecordAck(int id, string by, DateTime nowUtc)
    {
        var r = GetOrCreate(id, nowUtc);
        r.AckedAtUtc ??= nowUtc;
        r.AckedBy ??= by;
        _dirty = true;
        SaveIfDirty();
    }

    public void RecordMerge(int id, int primaryId, DateTime nowUtc, string by = "")
    {
        var dupe = GetOrCreate(id, nowUtc);
        dupe.MergedInto = primaryId;
        dupe.LastStatus = "Cancelled";   // so the status change is not logged a second time when it is observed
        AddEvent(dupe, nowUtc, $"Closed as a duplicate of incident #{primaryId}", by, "Operator");
        AddEvent(GetOrCreate(primaryId, nowUtc), nowUtc, $"Duplicate report #{id} merged into this incident", by, "Operator");
        _dirty = true;
        SaveIfDirty();
    }

    // ---- incident history (timeline) ----
    public void AddEvent(IncidentLocalRecord rec, DateTime whenUtc, string text, string by, string source)
    {
        rec.Events.Add(new TimelineEvent { TimeUtc = whenUtc, Text = text, By = by, Source = source });
        if (rec.Events.Count > MaxEventsPerIncident) rec.Events.RemoveAt(0);
        _dirty = true;
    }

    public void RecordAssign(int id, int responderId, string responderLabel, string by, DateTime nowUtc)
    {
        var r = GetOrCreate(id, nowUtc);
        r.LastResponderId = responderId;
        AddEvent(r, nowUtc, $"Responder assigned: {responderLabel}", by, "Operator");
        SaveIfDirty();
    }

    public void RecordStatusSet(int id, string newStatus, string? oldStatus, string by, DateTime nowUtc)
    {
        var r = GetOrCreate(id, nowUtc);
        r.LastStatus = newStatus;
        AddEvent(r, nowUtc, oldStatus is null ? $"Status set to {newStatus}" : $"Status set to {newStatus} (was {oldStatus})", by, "Operator");
        SaveIfDirty();
    }

    /// <summary>Remembers who moved the incident out of "Received" (used for per-operator dispatch times).</summary>
    public void RecordDispatchBy(int id, string by, DateTime nowUtc)
    {
        var r = GetOrCreate(id, nowUtc);
        r.DispatchedBy ??= by;
        _dirty = true;
        SaveIfDirty();
    }

    // ---- responder regions ----
    public string GetRegion(int responderId) =>
        _state.ResponderRegions.TryGetValue(responderId, out var r) ? r : "";

    public void SetRegion(int responderId, string region)
    {
        region = region.Trim();
        if (region.Length == 0) _state.ResponderRegions.Remove(responderId);
        else _state.ResponderRegions[responderId] = region;
        _dirty = true;
        SaveIfDirty();
    }

    public void MarkDirty() => _dirty = true;

    public void SaveIfDirty()
    {
        if (!_dirty) return;
        _dirty = false;
        try { Directory.CreateDirectory(_dir); File.WriteAllText(StatePath, JsonSerializer.Serialize(_state, Opts)); }
        catch { /* non-fatal */ }
    }
}
