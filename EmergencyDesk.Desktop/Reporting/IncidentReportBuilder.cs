using System.Globalization;
using EmergencyDesk.Models;
using EmergencyDesk.Services;

namespace EmergencyDesk.Reporting;

/// <summary>One line of an incident's timeline.</summary>
public record TimelineItem(DateTime TimeUtc, string Text, string By, string Source);

/// <summary>A timeline line prepared for the incident details panel.</summary>
public class TimelineRow
{
    public string TimeText { get; }
    public string Text { get; }

    public TimelineRow(TimelineItem item, DateTime reportedLocalDay)
    {
        var local = item.TimeUtc.ToLocalTime();
        TimeText = local.Date == reportedLocalDay ? local.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                                                  : local.ToString("MMM d HH:mm", CultureInfo.InvariantCulture);
        Text = string.IsNullOrWhiteSpace(item.By) ? item.Text : $"{item.Text} - {item.By}";
    }
}

/// <summary>Builds the "incident report with full timeline" that is exported as PDF or CSV.</summary>
public static class IncidentReportBuilder
{
    /// <summary>
    /// Everything that happened to one incident, oldest first: server timestamps, acknowledgement, escalation, and the
    /// operator actions / observed changes kept in this desk's record.
    /// </summary>
    public static List<TimelineItem> BuildTimeline(Incident i, AppRules rules, LocalStore store)
    {
        var list = new List<TimelineItem>();
        var reported = Incident.AsUtc(i.ReportedAt);

        var who = string.IsNullOrWhiteSpace(i.ReporterName) ? "unknown reporter" : i.ReporterName;
        var phone = string.IsNullOrWhiteSpace(i.ReporterPhone) ? "" : $" ({i.ReporterPhone})";
        list.Add(new(reported, $"Report received: {i.Type}, {i.PeopleText}, from {who}{phone}", "", "Server"));

        if (AnalyticsEngine.WasEscalated(i, rules))
            list.Add(new(reported.AddSeconds(rules.EscalateAfterSeconds),
                         $"Escalated to supervisor: not acknowledged within {rules.EscalateAfterSeconds} s", "System", "Rule"));

        if (i.AcknowledgedAt is { } a)
            list.Add(new(Incident.AsUtc(a), "Acknowledged", i.AcknowledgedBy ?? "", "Operator"));

        var rec = store.TryGet(i.Id);
        if (rec is not null)
            foreach (var e in rec.Events)
                list.Add(new(Incident.AsUtc(e.TimeUtc), e.Text, e.By, e.Source));

        return list.OrderBy(x => x.TimeUtc).ToList();   // OrderBy is stable: same-second events keep their order
    }

    private static string Stamp(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Took(DateTime reported, DateTime? done, int targetSeconds)
    {
        if (done is null) return "Not recorded";
        var secs = Math.Max(0, (Incident.AsUtc(done.Value) - Incident.AsUtc(reported)).TotalSeconds);
        return $"{IncidentEngine.Fmt(secs)} ({(secs <= targetSeconds ? "within" : "over")} the {IncidentEngine.Fmt(targetSeconds)} target)";
    }

    public static ReportDoc Build(Incident i, IReadOnlyCollection<Incident> all, AppRules rules, LocalStore store,
                                  IReadOnlyList<AuditEntry> auditForIncident, string generatedBy, DateTime nowUtc)
    {
        var doc = new ReportDoc
        {
            Title = $"Incident report #{i.Id} - {i.Type}",
            Subtitle = $"Generated {Stamp(nowUtc)} by {generatedBy}. Times are this PC's local time."
        };

        // ---- summary
        var location = string.IsNullOrWhiteSpace(i.Address) ? i.Coordinates : $"{i.Address} ({i.Coordinates})";
        doc.Heading("Summary").KeyValues(
            ("Incident", $"#{i.Id}"),
            ("Type", i.Type.ToString()),
            ("Status", i.Status.ToString()),
            ("Priority", i.IsClosed ? "—" : $"{i.PriorityLabel}{(string.IsNullOrWhiteSpace(i.PriorityReason) ? "" : " (" + i.PriorityReason + ")")}"),
            ("People involved", i.PeopleText),
            ("Source", i.Source ?? "—"),
            ("Reported", Stamp(Incident.AsUtc(i.ReportedAt))),
            ("Reporter", $"{(string.IsNullOrWhiteSpace(i.ReporterName) ? "—" : i.ReporterName)}  {i.ReporterPhone}".Trim()),
            ("Location", location),
            ("Responder", string.IsNullOrWhiteSpace(i.AssignedResponderName) ? "Not assigned" : i.AssignedResponderName!),
            ("Description", i.Description));

        // ---- response times
        doc.Heading("Response times").KeyValues(
            ("Time to acknowledge", Took(i.ReportedAt, i.AcknowledgedAt, rules.AckSlaSeconds)),
            ("Acknowledged by", string.IsNullOrWhiteSpace(i.AcknowledgedBy) ? "—" : i.AcknowledgedBy!),
            ("Time to dispatch", Took(i.ReportedAt, i.DispatchedAt, rules.DispatchSlaSeconds)),
            ("Dispatched by", string.IsNullOrWhiteSpace(i.DispatchedBy) ? "—" : i.DispatchedBy!),
            ("Escalated", AnalyticsEngine.WasEscalated(i, rules) ? "Yes - not acknowledged in time" : "No"));

        // ---- timeline
        var timeline = BuildTimeline(i, rules, store);
        doc.Heading("Timeline").Table(
            new[] { "Time", "Event", "By", "Source" },
            new[] { 17.0, 53, 16, 10 },
            timeline.Select(t => new[] { Stamp(t.TimeUtc), t.Text, t.By, t.Source }));
        doc.Paragraph("Source: Server = timestamp from the server; Operator = action taken on a desk; Rule = automatic rule; " +
                      "Observed = change noticed when the desk refreshed, so its time can be a few seconds late.", muted: true);

        // ---- duplicates / merges
        var merged = all.Where(x => x.MergedIntoId == i.Id).Select(x => "#" + x.Id).ToList();
        if (i.MergedIntoId is { } m)
            doc.Heading("Duplicates").Paragraph($"This report was closed as a duplicate of incident #{m}.");
        else if (merged.Count > 0)
            doc.Heading("Duplicates").Paragraph($"Duplicate reports merged into this incident: {string.Join(", ", merged)}.");

        // ---- who viewed it
        var views = auditForIncident.Where(a => a.Action == AuditActions.ViewIncident)
            .GroupBy(a => a.Operator)
            .Select(g => new[] { g.Key, g.Count().ToString(CultureInfo.InvariantCulture),
                                 Stamp(g.Min(x => x.TimeUtc)), Stamp(g.Max(x => x.TimeUtc)) })
            .ToList();
        doc.Heading("Who viewed this incident");
        if (views.Count == 0) doc.Paragraph("No views have been recorded on this desk.", muted: true);
        else doc.Table(new[] { "Operator", "Views", "First viewed", "Last viewed" }, new[] { 34.0, 10, 28, 28 }, views);

        return doc;
    }
}
