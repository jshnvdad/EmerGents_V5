using System.Globalization;
using EmergencyDesk.Models;

namespace EmergencyDesk.Services;

// ---------------------------------------------------------------------------------------------
// Small view-model-friendly result types (plain classes so XAML can bind to them)
// ---------------------------------------------------------------------------------------------

/// <summary>One horizontal bar in a "by type / by time / by location" list.</summary>
public class BarItem
{
    public string Label { get; init; } = "";
    public int Count { get; init; }
    /// <summary>Bar length 0-100, relative to the longest bar of the same list.</summary>
    public double Percent { get; init; }
    public string CountText { get; init; } = "";
}

/// <summary>One column of the "incidents per day" chart.</summary>
public class DayBar
{
    public string Label { get; init; } = "";
    public string Tooltip { get; init; } = "";
    public int Count { get; init; }
    public double BarHeight { get; init; }
    public double BarWidth { get; init; }
}

public class OperatorStat
{
    public string Name { get; init; } = "";
    public int AckCount { get; init; }
    public double? AvgAckSeconds { get; init; }
    public double? SlowestAckSeconds { get; init; }
    public double? AckMetRate { get; init; }
    public int DispatchCount { get; init; }
    public double? AvgDispatchSeconds { get; init; }
    public double? SlowestDispatchSeconds { get; init; }
    public double? DispatchMetRate { get; init; }

    public string AvgAckText => AnalyticsEngine.Dur(AvgAckSeconds);
    public string SlowestAckText => AnalyticsEngine.Dur(SlowestAckSeconds);
    public string AckMetText => AnalyticsEngine.Pct(AckMetRate);
    public string AvgDispatchText => AnalyticsEngine.Dur(AvgDispatchSeconds);
    public string SlowestDispatchText => AnalyticsEngine.Dur(SlowestDispatchSeconds);
    public string DispatchMetText => AnalyticsEngine.Pct(DispatchMetRate);
}

/// <summary>Headline numbers for the dashboard and the analytics page.</summary>
public class KpiSet
{
    public int AlertsToday { get; init; }
    public int OpenIncidents { get; init; }
    /// <summary>Incidents reported inside the chosen window (merged duplicates not counted).</summary>
    public int Incidents { get; init; }
    public int FalseAlarms { get; init; }
    public double? AvgAckSeconds { get; init; }
    public double? AvgDispatchSeconds { get; init; }
    public double? FalseAlarmRate { get; init; }

    public string AvgAckText => AnalyticsEngine.Dur(AvgAckSeconds);
    public string AvgDispatchText => AnalyticsEngine.Dur(AvgDispatchSeconds);
    public string FalseAlarmText => AnalyticsEngine.Pct(FalseAlarmRate, 1);
}

/// <summary>
/// All the numbers behind the dashboard KPIs, trend charts, operator table and summary reports.
/// Pure functions over a list of incidents, so they are easy to test.
///
/// Definitions used everywhere:
///   * Incident            = a report that was not merged into another one (merged duplicates would count one event twice).
///   * Response time       = report received -> acknowledged by an operator.
///   * Dispatch time       = report received -> first moved out of "Received" (responder dispatched).
///   * False alarm         = incident closed as "Cancelled" (and not a merged duplicate).
/// </summary>
public static class AnalyticsEngine
{
    // ------------------------------------------------------------------ formatting
    public static string Dur(double? seconds) => seconds is { } s ? IncidentEngine.Fmt(s) : "—";

    public static string Pct(double? rate, int decimals = 0) =>
        rate is { } r ? (r * 100).ToString(decimals == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + "%" : "—";

    // ------------------------------------------------------------------ selection
    public static IEnumerable<Incident> Real(IEnumerable<Incident> all) => all.Where(i => !i.IsMergedDuplicate);

    /// <summary>Start of the range as UTC. range: "Today", "7", "30", "90" (days, counting today) or anything else = all time.</summary>
    public static DateTime RangeStartUtc(string range, DateTime nowUtc)
    {
        var today = nowUtc.ToLocalTime().Date;
        return range switch
        {
            "Today" => today.ToUniversalTime(),
            "7" => today.AddDays(-6).ToUniversalTime(),
            "30" => today.AddDays(-29).ToUniversalTime(),
            "90" => today.AddDays(-89).ToUniversalTime(),
            _ => DateTime.MinValue
        };
    }

    public static string RangeLabel(string range) => range switch
    {
        "Today" => "today",
        "7" => "last 7 days",
        "30" => "last 30 days",
        "90" => "last 90 days",
        _ => "all recorded incidents"
    };

    public static List<Incident> InRange(IEnumerable<Incident> all, DateTime fromUtc, DateTime toUtc) =>
        Real(all).Where(i =>
        {
            var t = Incident.AsUtc(i.ReportedAt);
            return t >= fromUtc && t < toUtc;
        }).ToList();

    private static double Secs(DateTime reported, DateTime done) =>
        Math.Max(0, (Incident.AsUtc(done) - Incident.AsUtc(reported)).TotalSeconds);

    /// <summary>True if nobody acknowledged the incident within the escalation limit.</summary>
    public static bool WasEscalated(Incident i, AppRules rules)
    {
        var escalateAt = Incident.AsUtc(i.ReportedAt).AddSeconds(rules.EscalateAfterSeconds);
        return i.AcknowledgedAt is { } a ? Incident.AsUtc(a) > escalateAt : i.IsEscalated;
    }

    // ------------------------------------------------------------------ KPIs
    public static KpiSet Kpis(IEnumerable<Incident> all, DateTime fromUtc, DateTime nowUtc)
    {
        var real = Real(all).ToList();
        var todayLocal = nowUtc.ToLocalTime().Date;
        var inWindow = real.Where(i => Incident.AsUtc(i.ReportedAt) >= fromUtc).ToList();

        var ack = inWindow.Where(i => i.AcknowledgedAt is not null).Select(i => Secs(i.ReportedAt, i.AcknowledgedAt!.Value)).ToList();
        var disp = inWindow.Where(i => i.DispatchedAt is not null).Select(i => Secs(i.ReportedAt, i.DispatchedAt!.Value)).ToList();
        var cancelled = inWindow.Count(i => i.Status == IncidentStatus.Cancelled);

        return new KpiSet
        {
            AlertsToday = real.Count(i => Incident.AsUtc(i.ReportedAt).ToLocalTime().Date == todayLocal),
            OpenIncidents = real.Count(i => !i.IsClosed),
            Incidents = inWindow.Count,
            FalseAlarms = cancelled,
            AvgAckSeconds = ack.Count > 0 ? ack.Average() : null,
            AvgDispatchSeconds = disp.Count > 0 ? disp.Average() : null,
            FalseAlarmRate = inWindow.Count > 0 ? (double)cancelled / inWindow.Count : null
        };
    }

    // ------------------------------------------------------------------ bars
    public static List<BarItem> MakeBars(IEnumerable<(string Label, int Count)> items, int total)
    {
        var list = items.ToList();
        var max = list.Count == 0 ? 0 : list.Max(x => x.Count);
        return list.Select(x => new BarItem
        {
            Label = x.Label,
            Count = x.Count,
            Percent = max == 0 ? 0 : x.Count * 100.0 / max,
            CountText = total == 0 ? x.Count.ToString(CultureInfo.InvariantCulture)
                                   : $"{x.Count}  ({(x.Count * 100.0 / total).ToString("0", CultureInfo.InvariantCulture)}%)"
        }).ToList();
    }

    public static List<BarItem> ByType(IReadOnlyList<Incident> list) =>
        MakeBars(Enum.GetValues<IncidentType>()
                     .Select(t => (t.ToString(), list.Count(i => i.Type == t)))
                     .Where(x => x.Item2 > 0)
                     .OrderByDescending(x => x.Item2), list.Count);

    public static string OutcomeOf(Incident i) => i.Status switch
    {
        IncidentStatus.Resolved => "Resolved",
        IncidentStatus.Cancelled => "False alarm / cancelled",
        IncidentStatus.Received => "Waiting (not dispatched)",
        _ => "Active (dispatched / en route / on scene)"
    };

    public static List<BarItem> ByOutcome(IReadOnlyList<Incident> list)
    {
        var order = new[] { "Resolved", "False alarm / cancelled", "Active (dispatched / en route / on scene)", "Waiting (not dispatched)" };
        return MakeBars(order.Select(o => (o, list.Count(i => OutcomeOf(i) == o))), list.Count);
    }

    public static List<BarItem> ByTimeBand(IReadOnlyList<Incident> list)
    {
        var counts = new int[8];
        foreach (var i in list) counts[Incident.AsUtc(i.ReportedAt).ToLocalTime().Hour / 3]++;
        return MakeBars(Enumerable.Range(0, 8).Select(b => ($"{b * 3:00}:00 - {b * 3 + 2:00}:59", counts[b])), list.Count);
    }

    public static List<BarItem> ByWeekday(IReadOnlyList<Incident> list)
    {
        var order = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
                            DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday };
        return MakeBars(order.Select(d => (d.ToString(), list.Count(i => Incident.AsUtc(i.ReportedAt).ToLocalTime().DayOfWeek == d))), list.Count);
    }

    // ------------------------------------------------------------------ location
    public static List<string> KnownRegions(IEnumerable<Responder> responders) =>
        responders.SelectMany(r => (r.Region ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                  .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static bool HasCoordinates(Incident i) => !(Math.Abs(i.Latitude) < 1e-9 && Math.Abs(i.Longitude) < 1e-9);

    /// <summary>
    /// Groups an incident into an "area". Order: a responder coverage region that appears in the address;
    /// otherwise a piece of the address; otherwise a ~1 km coordinate cell. Adjust this one function if your
    /// addresses are formatted differently.
    /// </summary>
    public static string LocationKey(Incident i, IReadOnlyList<string> regions)
    {
        var addr = (i.Address ?? "").Trim();
        if (addr.Length > 0)
        {
            var hit = regions.Where(r => addr.Contains(r, StringComparison.OrdinalIgnoreCase))
                             .OrderByDescending(r => r.Length).FirstOrDefault();
            if (hit is not null) return hit;

            var parts = addr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 3) return parts[^2];
            if (parts.Length > 0) return parts[^1];
        }
        if (HasCoordinates(i))
            return "Near " + Math.Round(i.Latitude, 2).ToString("0.00", CultureInfo.InvariantCulture) + ", " +
                   Math.Round(i.Longitude, 2).ToString("0.00", CultureInfo.InvariantCulture);
        return "Unknown location";
    }

    public static List<BarItem> ByLocation(IReadOnlyList<Incident> list, IReadOnlyList<string> regions, int top = 10) =>
        MakeBars(list.GroupBy(i => LocationKey(i, regions), StringComparer.OrdinalIgnoreCase)
                     .Select(g => (g.Key, g.Count()))
                     .OrderByDescending(x => x.Item2).ThenBy(x => x.Key)
                     .Take(top), list.Count);

    // ------------------------------------------------------------------ per-day chart
    /// <summary>Incidents per day for the last <paramref name="days"/> days, ending today.</summary>
    public static List<DayBar> Daily(IEnumerable<Incident> all, int days, DateTime nowUtc)
    {
        var today = nowUtc.ToLocalTime().Date;
        var real = Real(all).ToList();
        var counts = Enumerable.Range(0, days).Select(k => today.AddDays(-(days - 1 - k)))
            .Select(d => (Day: d, Count: real.Count(i => Incident.AsUtc(i.ReportedAt).ToLocalTime().Date == d))).ToList();

        var max = counts.Count == 0 ? 0 : counts.Max(c => c.Count);
        var width = days <= 14 ? 30.0 : days <= 31 ? 22.0 : 13.0;
        return counts.Select(c => new DayBar
        {
            Label = c.Day.Day.ToString(CultureInfo.InvariantCulture),
            Tooltip = $"{c.Day:ddd d MMM yyyy}: {c.Count} incident(s)",
            Count = c.Count,
            BarHeight = c.Count == 0 || max == 0 ? 0 : Math.Max(3, c.Count * 110.0 / max),
            BarWidth = width
        }).ToList();
    }

    /// <summary>How many days the per-day chart shows for a range (the chart never shows more than 60).</summary>
    public static int DaysForChart(string range, IEnumerable<Incident> all, DateTime nowUtc) => range switch
    {
        "30" => 30,
        "90" => 60,
        "Today" or "7" => 7,
        _ => Math.Clamp(all.Any()
                ? (int)(nowUtc.ToLocalTime().Date - all.Min(i => Incident.AsUtc(i.ReportedAt)).ToLocalTime().Date).TotalDays + 1
                : 7, 7, 60)
    };

    // ------------------------------------------------------------------ operators
    private class Acc
    {
        public readonly List<double> Ack = new();
        public readonly List<double> Disp = new();
    }

    /// <summary>
    /// Acknowledge / dispatch times per operator. Acknowledgements come from "AcknowledgedBy"; dispatches from "DispatchedBy",
    /// which is only known for dispatches done on a desk running this version (or sent by the server).
    /// </summary>
    public static List<OperatorStat> Operators(IEnumerable<Incident> inRange, AppRules rules)
    {
        var acc = new Dictionary<string, Acc>(StringComparer.OrdinalIgnoreCase);
        Acc Get(string name)
        {
            name = name.Trim();
            if (!acc.TryGetValue(name, out var a)) acc[name] = a = new Acc();
            return a;
        }

        foreach (var i in inRange)
        {
            if (i.AcknowledgedAt is { } a && !string.IsNullOrWhiteSpace(i.AcknowledgedBy))
                Get(i.AcknowledgedBy).Ack.Add(Secs(i.ReportedAt, a));
            if (i.DispatchedAt is { } d && !string.IsNullOrWhiteSpace(i.DispatchedBy))
                Get(i.DispatchedBy).Disp.Add(Secs(i.ReportedAt, d));
        }

        return acc.Select(kv => new OperatorStat
        {
            Name = kv.Key,
            AckCount = kv.Value.Ack.Count,
            AvgAckSeconds = kv.Value.Ack.Count > 0 ? kv.Value.Ack.Average() : null,
            SlowestAckSeconds = kv.Value.Ack.Count > 0 ? kv.Value.Ack.Max() : null,
            AckMetRate = kv.Value.Ack.Count > 0 ? (double)kv.Value.Ack.Count(s => s <= rules.AckSlaSeconds) / kv.Value.Ack.Count : null,
            DispatchCount = kv.Value.Disp.Count,
            AvgDispatchSeconds = kv.Value.Disp.Count > 0 ? kv.Value.Disp.Average() : null,
            SlowestDispatchSeconds = kv.Value.Disp.Count > 0 ? kv.Value.Disp.Max() : null,
            DispatchMetRate = kv.Value.Disp.Count > 0 ? (double)kv.Value.Disp.Count(s => s <= rules.DispatchSlaSeconds) / kv.Value.Disp.Count : null
        }).OrderByDescending(s => s.AckCount + s.DispatchCount).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
