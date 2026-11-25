using System.Globalization;
using EmergencyDesk.Models;
using EmergencyDesk.Services;

namespace EmergencyDesk.Reporting;

/// <summary>Builds the period summary (used by the Analytics export and the scheduled e-mail).</summary>
public static class SummaryReportBuilder
{
    private const int MaxListedIncidents = 150;

    private static string Local(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static string PeriodText(DateTime fromUtc, DateTime toUtc) =>
        fromUtc == DateTime.MinValue ? $"All recorded incidents, up to {Local(toUtc)}" : $"{Local(fromUtc)} to {Local(toUtc)}";

    public static ReportDoc Build(IEnumerable<Incident> all, IEnumerable<Responder> responders, AppRules rules,
                                  DateTime fromUtc, DateTime toUtc, string title, string generatedBy, bool includeOperators)
    {
        var list = AnalyticsEngine.InRange(all, fromUtc, toUtc);
        var regions = AnalyticsEngine.KnownRegions(responders);
        var kpi = AnalyticsEngine.Kpis(list, fromUtc, toUtc);

        var doc = new ReportDoc
        {
            Title = title,
            Subtitle = $"{PeriodText(fromUtc, toUtc)}  ·  generated {Local(DateTime.UtcNow)} by {generatedBy}  ·  merged duplicate reports are not counted"
        };

        var resolved = list.Count(i => i.Status == IncidentStatus.Resolved);
        var open = list.Count(i => !i.IsClosed);
        var escalated = list.Count(i => AnalyticsEngine.WasEscalated(i, rules));
        var ackWithin = list.Where(i => i.AcknowledgedAt is not null)
                            .Select(i => (Incident.AsUtc(i.AcknowledgedAt!.Value) - Incident.AsUtc(i.ReportedAt)).TotalSeconds).ToList();
        var dispWithin = list.Where(i => i.DispatchedAt is not null)
                             .Select(i => (Incident.AsUtc(i.DispatchedAt!.Value) - Incident.AsUtc(i.ReportedAt)).TotalSeconds).ToList();

        doc.Heading("Key figures").KeyValues(
            ("Incidents reported", list.Count.ToString(CultureInfo.InvariantCulture)),
            ("Resolved", resolved.ToString(CultureInfo.InvariantCulture)),
            ("False alarms (cancelled)", $"{kpi.FalseAlarms} ({kpi.FalseAlarmText})"),
            ("Still open", open.ToString(CultureInfo.InvariantCulture)),
            ("Average response time (to acknowledge)", kpi.AvgAckText),
            ("Average time to dispatch", kpi.AvgDispatchText),
            ("Acknowledged within target", ackWithin.Count == 0 ? "—" : AnalyticsEngine.Pct((double)ackWithin.Count(s => s <= rules.AckSlaSeconds) / ackWithin.Count) + $" (target {IncidentEngine.Fmt(rules.AckSlaSeconds)})"),
            ("Dispatched within target", dispWithin.Count == 0 ? "—" : AnalyticsEngine.Pct((double)dispWithin.Count(s => s <= rules.DispatchSlaSeconds) / dispWithin.Count) + $" (target {IncidentEngine.Fmt(rules.DispatchSlaSeconds)})"),
            ("Escalated (not acknowledged in time)", escalated.ToString(CultureInfo.InvariantCulture)));

        static string[] Row(BarItem b) => new[] { b.Label, b.Count.ToString(CultureInfo.InvariantCulture), b.CountText };
        var two = new[] { 60.0, 15, 25 };
        var cols = new[] { "", "Incidents", "Share" };

        doc.Heading("By type").Table(new[] { "Type", cols[1], cols[2] }, two, AnalyticsEngine.ByType(list).Select(Row));
        doc.Heading("By outcome").Table(new[] { "Outcome", cols[1], cols[2] }, two, AnalyticsEngine.ByOutcome(list).Select(Row));
        doc.Heading("By time of day").Table(new[] { "Time of day", cols[1], cols[2] }, two, AnalyticsEngine.ByTimeBand(list).Select(Row));
        doc.Heading("By day of week").Table(new[] { "Day", cols[1], cols[2] }, two, AnalyticsEngine.ByWeekday(list).Select(Row));
        doc.Heading("Top locations").Table(new[] { "Area", cols[1], cols[2] }, two, AnalyticsEngine.ByLocation(list, regions).Select(Row));

        if (includeOperators)
        {
            var ops = AnalyticsEngine.Operators(list, rules);
            doc.Heading("Operator performance").Table(
                new[] { "Operator", "Acked", "Avg ack", "Slowest", "Ack in target", "Dispatched", "Avg dispatch", "Disp. in target" },
                new[] { 22.0, 8, 10, 10, 12, 11, 12, 13 },
                ops.Select(o => new[] { o.Name, o.AckCount.ToString(CultureInfo.InvariantCulture), o.AvgAckText, o.SlowestAckText,
                                        o.AckMetText, o.DispatchCount.ToString(CultureInfo.InvariantCulture), o.AvgDispatchText, o.DispatchMetText }));
        }

        var rows = list.OrderBy(i => Incident.AsUtc(i.ReportedAt)).Take(MaxListedIncidents).Select(i => new[]
        {
            "#" + i.Id,
            Local(Incident.AsUtc(i.ReportedAt)),
            i.Type.ToString(),
            i.Status.ToString(),
            i.AcknowledgedAt is { } a ? IncidentEngine.Fmt(Math.Max(0, (Incident.AsUtc(a) - Incident.AsUtc(i.ReportedAt)).TotalSeconds)) : "—",
            i.DispatchedAt is { } d ? IncidentEngine.Fmt(Math.Max(0, (Incident.AsUtc(d) - Incident.AsUtc(i.ReportedAt)).TotalSeconds)) : "—",
            i.AssignedResponderName ?? "—"
        });
        doc.Heading("Incident list").Table(
            new[] { "#", "Reported", "Type", "Status", "Ack", "Dispatch", "Responder" },
            new[] { 7.0, 19, 11, 12, 9, 9, 33 }, rows);
        if (list.Count > MaxListedIncidents)
            doc.Paragraph($"Only the first {MaxListedIncidents} of {list.Count} incidents are listed. Export a shorter period to see the rest.", muted: true);

        return doc;
    }
}
