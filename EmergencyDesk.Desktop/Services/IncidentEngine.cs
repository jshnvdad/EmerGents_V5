using System.Text.RegularExpressions;
using EmergencyDesk.Models;

namespace EmergencyDesk.Services;

/// <summary>
/// All the dispatch-desk logic that is not UI: queue state, priority scoring, duplicate detection,
/// SLA countdowns, escalation and auto-assignment. Pure logic so it can be unit-tested.
/// </summary>
public class IncidentEngine
{
    private readonly LocalStore _store;
    public IncidentEngine(LocalStore store) => _store = store;

    private AppRules Rules => _store.Rules;

    /// <summary>Server clock minus local clock (from the HTTP Date header) so SLA timers match the server.</summary>
    public TimeSpan ClockOffset { get; set; }
    public DateTime Now => DateTime.UtcNow + ClockOffset;

    // =====================================================================
    // Full recompute (after each refresh / rule change / local action)
    // =====================================================================
    public void Process(IList<Incident> incidents, IList<Responder> responders)
    {
        var now = Now;
        foreach (var i in incidents) ApplyLocalRecord(i, now);
        DetectDuplicates(incidents);
        foreach (var i in incidents)
        {
            UpdateTimers(i, now);   // sets QueueState / stale / SLA / escalation
            Score(i, now);
            Observe(i, now);        // keeps the per-incident timeline
        }

        foreach (var r in responders)
        {
            r.Region = _store.GetRegion(r.Id);
            r.ActiveJobs = incidents.Count(i => !i.IsClosed && i.AssignedResponderId == r.Id);
        }

        _store.SaveIfDirty();
        foreach (var i in incidents) i.NotifyClientChanged();
    }

    /// <summary>Cheap once-a-second update of countdowns and escalation. Returns true if a queue state or escalation flag flipped.</summary>
    public bool Tick(IEnumerable<Incident> incidents)
    {
        var now = Now;
        var changed = false;
        foreach (var i in incidents)
        {
            if (i.IsClosed) continue;
            var before = (i.IsEscalated, i.QueueState, i.IsStale);
            UpdateTimers(i, now);
            if (before != (i.IsEscalated, i.QueueState, i.IsStale)) changed = true;
            i.NotifyClientChanged();
        }
        return changed;
    }

    // =====================================================================
    // Local record (ack / dispatch time / merge) <-> incident
    // =====================================================================
    private void ApplyLocalRecord(Incident i, DateTime now)
    {
        var rec = i.Status == IncidentStatus.Received ? _store.GetOrCreate(i.Id, now) : _store.TryGet(i.Id);
        if (rec is null) return;

        if (i.Status == IncidentStatus.Received)
        {
            if (!rec.SeenAsReceived) { rec.SeenAsReceived = true; _store.MarkDirty(); }
        }
        else if (i.Status != IncidentStatus.Cancelled && rec.SeenAsReceived && rec.DispatchedAtUtc is null)
        {
            // We watched this incident leave "Received": that moment is the dispatch time.
            rec.DispatchedAtUtc = now;
            _store.MarkDirty();
        }

        if (i.AcknowledgedAt is null && rec.AckedAtUtc is { } a)
        {
            i.AcknowledgedAt = a;
            i.AcknowledgedBy ??= rec.AckedBy;
        }
        if (i.DispatchedAt is null && rec.DispatchedAtUtc is { } d) i.DispatchedAt = d;
        if (string.IsNullOrWhiteSpace(i.DispatchedBy) && !string.IsNullOrWhiteSpace(rec.DispatchedBy)) i.DispatchedBy = rec.DispatchedBy;
        i.MergedIntoId = rec.MergedInto;
    }

    // =====================================================================
    // Timeline: remember status / responder changes made by anyone else (responders, other desks)
    // =====================================================================
    /// <summary>
    /// Compares the incident with what this desk saw last time and logs the difference. Changes made by an operator on
    /// this desk are logged immediately by LocalStore.Record*, which also updates LastStatus / LastResponderId, so they
    /// are not logged twice. What is logged here is only *noticed* at polling time, so its time is approximate.
    /// </summary>
    private void Observe(Incident i, DateTime now)
    {
        var rec = _store.TryGet(i.Id);
        if (rec is null)
        {
            if (i.IsClosed) return;              // old closed reports: nothing worth tracking
            rec = _store.GetOrCreate(i.Id, now);
        }

        var status = i.Status.ToString();
        if (rec.LastStatus is null)
        {
            // First time this desk sees the incident.
            rec.LastStatus = status;
            rec.LastResponderId = i.AssignedResponderId;
            if (i.Status != IncidentStatus.Received)
                _store.AddEvent(rec, now, $"First seen by this desk already in status {status}", "", "Observed");
            _store.MarkDirty();
            return;
        }

        if (rec.LastStatus != status)
        {
            _store.AddEvent(rec, now, $"Status changed: {rec.LastStatus} -> {status}", "", "Observed");
            rec.LastStatus = status;
        }

        if (i.AssignedResponderId != rec.LastResponderId)
        {
            if (i.AssignedResponderId is not null)
                _store.AddEvent(rec, now, $"Responder assigned: {i.AssignedResponderName ?? "#" + i.AssignedResponderId}", "", "Observed");
            rec.LastResponderId = i.AssignedResponderId;
            _store.MarkDirty();
        }
    }

    // =====================================================================
    // Queue state, SLA timers, escalation
    // =====================================================================
    public static QueueState GetState(Incident i)
    {
        if (i.IsClosed) return QueueState.Closed;
        if (i.Status != IncidentStatus.Received) return QueueState.InProgress;
        return i.AcknowledgedAt is not null || i.AssignedResponderId is not null
            ? QueueState.Acknowledged : QueueState.New;
    }

    public void UpdateTimers(Incident i, DateTime now)
    {
        var reported = Incident.AsUtc(i.ReportedAt);
        var age = Math.Max(0, (now - reported).TotalSeconds);

        i.QueueState = GetState(i);
        i.IsStale = !i.IsClosed && age > Rules.StaleAfterHours * 3600.0;
        i.AgeText = i.IsClosed ? "" : Fmt(age);
        i.IsEscalated = i.QueueState == QueueState.New && !i.IsStale && age >= Rules.EscalateAfterSeconds;

        // Time-to-acknowledge
        (i.SlaAckText, i.SlaAckState) = i.IsClosed && i.AcknowledgedAt is null
            ? ("—", SlaState.None)
            : Sla(age, i.AcknowledgedAt, i.QueueState == QueueState.New, Rules.AckSlaSeconds, reported, i.IsStale);

        // Time-to-dispatch
        (i.SlaDispatchText, i.SlaDispatchState) = i.IsClosed && i.DispatchedAt is null
            ? ("—", SlaState.None)
            : Sla(age, i.DispatchedAt, i.Status == IncidentStatus.Received, Rules.DispatchSlaSeconds, reported, i.IsStale);
    }

    private static (string, SlaState) Sla(double age, DateTime? doneAt, bool running, int target, DateTime reportedUtc, bool stale)
    {
        if (doneAt is { } done)
        {
            var took = Math.Max(0, (Incident.AsUtc(done) - reportedUtc).TotalSeconds);
            return took <= target ? ($"Met {Fmt(took)}", SlaState.Met) : ($"Late {Fmt(took)}", SlaState.Late);
        }
        if (!running) return ("—", SlaState.None);
        if (stale) return ("Stale", SlaState.None);

        var remaining = target - age;
        if (remaining < 0) return ($"OVERDUE {Fmt(-remaining)}", SlaState.Breached);
        return ($"{Fmt(remaining)} left", remaining < target * 0.25 ? SlaState.Warning : SlaState.Ok);
    }

    public static string Fmt(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    // =====================================================================
    // Priority scoring (0-100). Transparent: the reasons are shown to the operator.
    // =====================================================================
    private static Regex Build(params string[] words) =>
        new($@"\b(?:{string.Join("|", words.Select(Regex.Escape))})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex CriticalRx = Build(
        "unconscious", "not breathing", "no pulse", "cardiac", "heart attack", "seizure", "severe bleeding", "stroke",
        "choking", "drowning", "trapped", "explosion", "gun", "weapon", "shooting", "stabbing", "hostage",
        "walang malay", "hindi humihinga", "nalulunod", "naipit");

    private static readonly Regex VulnerableRx = Build(
        "child", "children", "kid", "kids", "baby", "infant", "toddler", "elderly", "pregnant", "disabled",
        "wheelchair", "senior", "diabetic", "asthma", "bata", "sanggol", "matanda", "buntis");

    private static readonly Regex SeriousRx = Build(
        "bleeding", "blood", "fracture", "broken", "accident", "collision", "crash", "smoke", "burn", "burns",
        "flood", "fire", "sunog", "dugo", "aksidente");

    private static List<string> Matches(Regex rx, string text) =>
        rx.Matches(text).Select(m => m.Value.ToLowerInvariant()).Distinct().ToList();

    private static bool HasCoordinates(Incident i) => !(Math.Abs(i.Latitude) < 1e-9 && Math.Abs(i.Longitude) < 1e-9);

    public void Score(Incident i, DateTime now)
    {
        if (i.IsClosed)
        {
            i.PriorityScore = 0; i.PriorityLevel = PriorityLevel.Low; i.PriorityReason = "";
            return;
        }

        var score = i.Type switch
        {
            IncidentType.Medical => 40,
            IncidentType.Fire => 40,
            IncidentType.Rescue => 35,
            _ => 30
        };
        var reasons = new List<string> { i.Type.ToString() };
        var text = i.Description ?? "";

        var crit = Matches(CriticalRx, text);
        if (crit.Count > 0) { score += 35; reasons.Add("critical: " + string.Join(", ", crit.Take(2))); }

        var vuln = Matches(VulnerableRx, text);
        if (vuln.Count > 0) { score += 15; reasons.Add("vulnerable person: " + string.Join(", ", vuln.Take(2))); }

        var serious = Matches(SeriousRx, text);
        if (serious.Count > 0) { score += 10; reasons.Add("serious: " + string.Join(", ", serious.Take(2))); }

        if (i.DuplicateCount > 0)
        {
            score += Math.Min(15, 5 * i.DuplicateCount);
            reasons.Add($"{i.DuplicateCount + 1} reports");
        }

        if (string.IsNullOrWhiteSpace(i.Address) && !HasCoordinates(i))
        {
            score += 5; reasons.Add("location unclear");
        }

        if (i.Status == IncidentStatus.Received)
        {
            var mins = Math.Max(0, (now - Incident.AsUtc(i.ReportedAt)).TotalMinutes);
            var bonus = (int)Math.Min(15, mins);   // aging: nothing waits forever behind newer reports
            score += bonus;
            if (bonus >= 3) reasons.Add($"waiting {(int)mins} min");
        }

        score = Math.Clamp(score, 0, 100);
        i.PriorityScore = score;
        i.PriorityLevel = score >= 75 ? PriorityLevel.Critical
                        : score >= 55 ? PriorityLevel.High
                        : score >= 40 ? PriorityLevel.Medium : PriorityLevel.Low;
        i.PriorityReason = string.Join(" · ", reasons);
    }

    // =====================================================================
    // Duplicate detection: same reporter phone, or same type within N metres, inside the time window
    // =====================================================================
    public void DetectDuplicates(IList<Incident> incidents)
    {
        foreach (var i in incidents) { i.DuplicateOfId = null; i.DuplicateCount = 0; }

        var open = incidents.Where(i => !i.IsClosed).OrderBy(i => Incident.AsUtc(i.ReportedAt)).ThenBy(i => i.Id).ToList();
        var window = Rules.DuplicateWindowMinutes;
        var radius = Rules.DuplicateRadiusMeters;

        for (var b = 0; b < open.Count; b++)
        {
            for (var a = 0; a < b; a++)
            {
                var primary = open[a];
                if (primary.DuplicateOfId is not null) continue;   // only compare against group leaders
                var minutes = Math.Abs((Incident.AsUtc(open[b].ReportedAt) - Incident.AsUtc(primary.ReportedAt)).TotalMinutes);
                if (minutes > window) continue;
                if (!LooksSame(primary, open[b], radius)) continue;

                open[b].DuplicateOfId = primary.Id;
                primary.DuplicateCount++;
                break;
            }
        }
    }

    private static bool LooksSame(Incident a, Incident b, int radiusMeters)
    {
        var pa = PhoneKey(a.ReporterPhone);
        if (pa.Length > 0 && pa == PhoneKey(b.ReporterPhone)) return true;

        return a.Type == b.Type && HasCoordinates(a) && HasCoordinates(b) &&
               Meters(a.Latitude, a.Longitude, b.Latitude, b.Longitude) <= radiusMeters;
    }

    private static string PhoneKey(string? phone)
    {
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length < 7) return "";
        return digits.Length > 9 ? digits[^9..] : digits;   // ignore +63 / 0 prefix differences
    }

    public static double Meters(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371000;
        static double Rad(double d) => d * Math.PI / 180;
        var dLat = Rad(lat2 - lat1);
        var dLon = Rad(lon2 - lon1);
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    // =====================================================================
    // Auto-assignment: type -> region -> workload
    // =====================================================================
    public List<(Incident Incident, Responder Responder)> PlanAutoAssign(
        IList<Incident> incidents, IList<Responder> responders, ISet<int> alreadyAttempted)
    {
        var plan = new List<(Incident, Responder)>();
        if (!Rules.AutoAssignEnabled) return plan;

        var taken = new HashSet<int>();
        var waiting = incidents
            .Where(i => i.Status == IncidentStatus.Received && i.AssignedResponderId is null &&
                        !i.IsDuplicate && !i.IsStale && !alreadyAttempted.Contains(i.Id))
            .OrderByDescending(i => i.PriorityScore)
            .ThenBy(i => Incident.AsUtc(i.ReportedAt));

        foreach (var inc in waiting)
        {
            var pick = PickResponder(inc, responders, taken);
            if (pick is null) continue;
            taken.Add(pick.Id);          // one unit, one job per pass
            plan.Add((inc, pick));
        }
        return plan;
    }

    public Responder? PickResponder(Incident inc, IEnumerable<Responder> responders, ISet<int> taken)
    {
        var c = responders.Where(r => r.Status == ResponderStatus.Available && !taken.Contains(r.Id)).ToList();

        if (Rules.MatchSpecialty)
        {
            var m = c.Where(r => r.Specialty == inc.Type).ToList();
            if (m.Count > 0) c = m;
            else if (Rules.RequireSpecialtyMatch) return null;
        }

        if (Rules.MatchRegion)
        {
            var m = c.Where(r => RegionMatches(r.Region, inc.Address)).ToList();
            if (m.Count > 0) c = m;
        }

        return (Rules.BalanceWorkload ? c.OrderBy(r => r.ActiveJobs).ThenBy(r => r.Id) : c.OrderBy(r => r.Id))
               .FirstOrDefault();
    }

    private static bool RegionMatches(string? regions, string? address)
    {
        if (string.IsNullOrWhiteSpace(regions) || string.IsNullOrWhiteSpace(address)) return false;
        return regions.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                      .Any(r => address.Contains(r, StringComparison.OrdinalIgnoreCase));
    }
}
