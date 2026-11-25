using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmergencyDesk.Models;
using EmergencyDesk.Reporting;
using EmergencyDesk.Services;

namespace EmergencyDesk.ViewModels;

/// <summary>Reporting &amp; analytics: KPIs, trends, operator performance, audit log, exports, scheduled e-mail.</summary>
public partial class MainViewModel
{
    // =====================================================================
    // Audit helper
    // =====================================================================
    private void Audit(string action, string targetType = "", object? targetId = null, string details = "",
                       bool ok = true, string? op = null, string? role = null)
    {
        var text = ok ? details : "FAILED: " + details;
        if (!App.Audit.Write(action, targetType, targetId?.ToString() ?? "", text, op, role))
            StatusMessage = "Warning: the audit log could not be written (" + App.Audit.LastError + ").";
    }

    private int? _lastViewedIncidentId;
    private DateTime _lastViewedAtUtc;

    private void LogIncidentView(Incident? inc)
    {
        if (inc is null) return;
        // Refreshes re-select the same incident every few seconds; that is not a new "view".
        if (inc.Id == _lastViewedIncidentId && DateTime.UtcNow - _lastViewedAtUtc < TimeSpan.FromMinutes(10)) return;
        _lastViewedIncidentId = inc.Id;
        _lastViewedAtUtc = DateTime.UtcNow;
        Audit(AuditActions.ViewIncident, "Incident", inc.Id, "Opened incident details");
    }

    // =====================================================================
    // Page switching
    // =====================================================================
    partial void OnCurrentPageChanged(string value)
    {
        switch (value)
        {
            case "Users": Audit(AuditActions.ViewUsers, "Page", "Users", "Opened the app users list"); break;
            case "Operators": Audit(AuditActions.ViewOperators, "Page", "Operators", "Opened operator performance"); UpdateReports(); break;
            case "Analytics": UpdateReports(); break;
            case "Audit": Audit(AuditActions.ViewAuditLog, "Page", "Audit", "Opened the audit log"); LoadAudit(); break;
        }
    }

    // =====================================================================
    // Selected incident: timeline + "viewed" audit entry
    // =====================================================================
    public ObservableCollection<TimelineRow> SelectedTimeline { get; } = new();

    partial void OnSelectedIncidentChanged(Incident? value)
    {
        LogIncidentView(value);
        SelectedTimeline.Clear();
        if (value is null) return;

        var day = Incident.AsUtc(value.ReportedAt).ToLocalTime().Date;
        foreach (var item in IncidentReportBuilder.BuildTimeline(value, _store.Rules, _store))
            SelectedTimeline.Add(new TimelineRow(item, day));
    }

    // =====================================================================
    // KPIs (dashboard) and analytics page
    // =====================================================================
    [ObservableProperty] private int _kpiAlertsToday;
    [ObservableProperty] private int _kpiOpen;
    [ObservableProperty] private string _kpiAvgResponse = "—";
    [ObservableProperty] private string _kpiAvgDispatchText = "";
    [ObservableProperty] private string _kpiFalseAlarmRate = "—";
    [ObservableProperty] private string _kpiFalseAlarmDetail = "";

    [ObservableProperty] private string _analyticsRange = "7";
    partial void OnAnalyticsRangeChanged(string value) => UpdateReports();

    [ObservableProperty] private string _rangeSummaryText = "";
    [ObservableProperty] private string _rangeAvgAck = "—";
    [ObservableProperty] private string _rangeAvgDispatch = "—";
    [ObservableProperty] private string _rangeFalseAlarm = "—";
    [ObservableProperty] private int _rangeCount;

    public ObservableCollection<BarItem> TypeBars { get; } = new();
    public ObservableCollection<BarItem> OutcomeBars { get; } = new();
    public ObservableCollection<BarItem> TimeBars { get; } = new();
    public ObservableCollection<BarItem> WeekdayBars { get; } = new();
    public ObservableCollection<BarItem> LocationBars { get; } = new();
    public ObservableCollection<DayBar> DailyBars { get; } = new();
    public ObservableCollection<OperatorStat> OperatorStats { get; } = new();

    /// <summary>Dashboard KPIs always; the heavier analytics lists only while one of those pages is open.</summary>
    private void UpdateReports()
    {
        var now = _engine.Now;
        var all = Incidents.ToList();

        var week = AnalyticsEngine.Kpis(all, AnalyticsEngine.RangeStartUtc("7", now), now);
        KpiAlertsToday = week.AlertsToday;
        KpiOpen = week.OpenIncidents;
        KpiAvgResponse = week.AvgAckText;
        KpiAvgDispatchText = "Avg dispatch " + week.AvgDispatchText;
        KpiFalseAlarmRate = week.FalseAlarmText;
        KpiFalseAlarmDetail = $"{week.FalseAlarms} of {week.Incidents} cancelled";

        if (CurrentPage is not ("Analytics" or "Operators")) return;

        var from = AnalyticsEngine.RangeStartUtc(AnalyticsRange, now);
        var list = AnalyticsEngine.InRange(all, from, now.AddDays(1));
        var kpi = AnalyticsEngine.Kpis(list, from, now);
        var regions = AnalyticsEngine.KnownRegions(Responders);

        RangeCount = list.Count;
        RangeAvgAck = kpi.AvgAckText;
        RangeAvgDispatch = kpi.AvgDispatchText;
        RangeFalseAlarm = kpi.FalseAlarmText;
        RangeSummaryText = $"{list.Count} incident(s), {AnalyticsEngine.RangeLabel(AnalyticsRange)} (merged duplicates not counted)";

        Replace(TypeBars, AnalyticsEngine.ByType(list));
        Replace(OutcomeBars, AnalyticsEngine.ByOutcome(list));
        Replace(TimeBars, AnalyticsEngine.ByTimeBand(list));
        Replace(WeekdayBars, AnalyticsEngine.ByWeekday(list));
        Replace(LocationBars, AnalyticsEngine.ByLocation(list, regions));
        Replace(DailyBars, AnalyticsEngine.Daily(all, AnalyticsEngine.DaysForChart(AnalyticsRange, AnalyticsEngine.Real(all), now), now));
        if (IsAdmin) Replace(OperatorStats, AnalyticsEngine.Operators(list, _store.Rules));
    }

    // =====================================================================
    // Exports (PDF / CSV)
    // =====================================================================
    [RelayCommand] private void ExportIncidentPdf() => ExportIncident(pdf: true);
    [RelayCommand] private void ExportIncidentCsv() => ExportIncident(pdf: false);
    [RelayCommand] private void ExportSummaryPdf() => ExportSummary(pdf: true);
    [RelayCommand] private void ExportSummaryCsv() => ExportSummary(pdf: false);

    private void ExportIncident(bool pdf)
    {
        var inc = SelectedIncident;
        if (inc is null) return;
        var views = App.Audit.ForTarget("Incident", inc.Id.ToString());
        var doc = IncidentReportBuilder.Build(inc, Incidents.ToList(), _store.Rules, _store, views, Operator, _engine.Now);
        SaveReport(doc, $"incident-{inc.Id}-report", pdf, "Incident", inc.Id, $"Incident #{inc.Id} report");
    }

    private void ExportSummary(bool pdf)
    {
        var now = _engine.Now;
        var from = AnalyticsEngine.RangeStartUtc(AnalyticsRange, now);
        var doc = SummaryReportBuilder.Build(Incidents.ToList(), Responders.ToList(), _store.Rules, from, now,
                                             "Incident summary report", Operator, includeOperators: IsAdmin);
        SaveReport(doc, "incident-summary", pdf, "Report", AnalyticsRange, $"Summary report ({AnalyticsEngine.RangeLabel(AnalyticsRange)})");
    }

    private void SaveReport(ReportDoc doc, string baseName, bool pdf, string targetType, object? targetId, string what)
    {
        var ext = pdf ? ".pdf" : ".csv";
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"{baseName}-{DateTime.Now:yyyyMMdd-HHmm}{ext}",
            DefaultExt = ext,
            AddExtension = true,
            Filter = pdf ? "PDF file (*.pdf)|*.pdf" : "CSV file (*.csv)|*.csv"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            File.WriteAllBytes(dlg.FileName, pdf ? ReportExporter.ToPdf(doc) : ReportExporter.ToCsvBytes(doc));
            Audit(AuditActions.ExportReport, targetType, targetId, $"{what} saved as {Path.GetFileName(dlg.FileName)}");
            StatusMessage = "Saved " + dlg.FileName;
        }
        catch (Exception ex)
        {
            Audit(AuditActions.ExportReport, targetType, targetId, $"{what}: {ex.Message}", ok: false);
            StatusMessage = "Could not save the file (is it open in another program?): " + ex.Message;
        }
    }

    // =====================================================================
    // Audit log page (admin)
    // =====================================================================
    public ObservableCollection<AuditEntry> AuditEntries { get; } = new();

    private ICollectionView? _auditView;
    public ICollectionView AuditView => _auditView ??= CreateAuditView();

    private ICollectionView CreateAuditView()
    {
        var v = new CollectionViewSource { Source = AuditEntries }.View;
        v.Filter = o => o is AuditEntry e && MatchesAuditSearch(e);
        return v;
    }

    private bool MatchesAuditSearch(AuditEntry e)
    {
        var q = AuditSearch.Trim();
        if (q.Length == 0) return true;
        return e.Operator.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               e.Action.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               e.Target.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               e.Details.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    [ObservableProperty] private string _auditSearch = "";
    partial void OnAuditSearchChanged(string value) => AuditView.Refresh();

    [ObservableProperty] private string _auditInfo = "";
    [ObservableProperty] private string _auditVerifyText = "";
    [ObservableProperty] private bool _auditVerifyOk = true;

    private void LoadAudit()
    {
        if (!IsAdmin) return;
        var entries = App.Audit.ReadRecent(2000);
        Replace(AuditEntries, entries);
        AuditInfo = $"Showing the latest {entries.Count} of {App.Audit.EntryCount} entries. File: {App.Audit.FilePath}";
    }

    [RelayCommand] private void RefreshAudit() => LoadAudit();

    [RelayCommand]
    private async Task VerifyAuditAsync()
    {
        if (!IsAdmin) return;
        AuditVerifyText = "Checking...";
        AuditVerifyOk = true;
        var result = await Task.Run(() => App.Audit.Verify());
        AuditVerifyOk = result.Ok;
        AuditVerifyText = (result.Ok ? "OK: " : "PROBLEM: ") + result.Message;
        Audit(AuditActions.VerifyAuditLog, "Page", "Audit", AuditVerifyText);
        LoadAudit();
    }

    [RelayCommand]
    private void ExportAuditCsv()
    {
        if (!IsAdmin) return;
        var doc = new ReportDoc
        {
            Title = "Audit log",
            Subtitle = $"Exported {DateTime.Now:yyyy-MM-dd HH:mm} by {Operator}. {App.Audit.EntryCount} entries in the file."
        };
        var all = App.Audit.ReadRecent(int.MaxValue);
        all.Reverse();   // oldest first
        doc.Table(new[] { "Seq", "Time", "Operator", "Role", "Action", "Target", "Details", "Hash" },
                  new[] { 5.0, 14, 11, 8, 14, 12, 40, 10 },
                  all.Select(e => new[] { e.Seq.ToString(), e.TimeLocal, e.Operator, e.Role, e.Action, e.Target, e.Details, e.Hash }));
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"audit-log-{DateTime.Now:yyyyMMdd-HHmm}.csv", DefaultExt = ".csv", AddExtension = true,
            Filter = "CSV file (*.csv)|*.csv"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllBytes(dlg.FileName, ReportExporter.ToCsvBytes(doc));
            Audit(AuditActions.ExportAuditLog, "Page", "Audit", $"{all.Count} entries saved as {Path.GetFileName(dlg.FileName)}");
            StatusMessage = "Saved " + dlg.FileName;
        }
        catch (Exception ex) { StatusMessage = "Could not save the file: " + ex.Message; }
    }

    // =====================================================================
    // Scheduled reports (admin)
    // =====================================================================
    [ObservableProperty] private ReportSchedule _editSchedule = App.Store.Schedule.Clone();
    [ObservableProperty] private string _editSendTime = $"{App.Store.Schedule.SendHour:00}:{App.Store.Schedule.SendMinute:00}";
    [ObservableProperty] private string _newSmtpPassword = "";
    [ObservableProperty] private string _scheduleStatus = "";
    [ObservableProperty] private string _smtpPasswordHint =
        App.Store.Schedule.SmtpPasswordProtected.Length > 0 ? "A password is saved. Type a new one only to replace it." : "No password saved yet.";

    public ReportFrequency[] ReportFrequencies { get; } = Enum.GetValues<ReportFrequency>();
    public DayOfWeek[] Weekdays { get; } = Enum.GetValues<DayOfWeek>();

    private bool _sendingReport;
    private int _scheduleTicks;
    private DateTime _nextScheduleAttemptUtc = DateTime.MinValue;

    private string ScheduleSummary()
    {
        var s = _store.Schedule;
        var parts = new List<string>();
        parts.Add(s.Enabled ? $"Next report: {s.NextScheduledTime(DateTime.Now):ddd d MMM HH:mm}." : "Scheduled e-mails are off.");
        if (s.LastSentLocal is { } sent) parts.Add($"Last sent: {sent:ddd d MMM HH:mm}.");
        if (s.LastResult.Length > 0) parts.Add(s.LastResult);
        return string.Join(" ", parts);
    }

    [RelayCommand]
    private void SaveSchedule()
    {
        if (!IsAdmin) { StatusMessage = "Only an admin can change report settings."; return; }
        if (!TimeSpan.TryParse(EditSendTime, out var t) || t < TimeSpan.Zero || t >= TimeSpan.FromHours(24))
        {
            ScheduleStatus = "Send time must look like 07:30.";
            return;
        }

        var s = EditSchedule.Clone();
        s.SendHour = t.Hours;
        s.SendMinute = t.Minutes;
        s.Normalize();
        if (s.Enabled)
        {
            var error = s.ValidateForSending();
            if (error.Length > 0) { ScheduleStatus = error; return; }
        }

        var before = _store.Schedule;
        s.SmtpPasswordProtected = NewSmtpPassword.Length > 0 ? App.Secrets.Protect(NewSmtpPassword) : before.SmtpPasswordProtected;
        s.LastSentLocal = before.LastSentLocal;
        s.LastResult = before.LastResult;
        // First time it is switched on: start counting from now, so it does not send straight away for a past time.
        if (s.Enabled && s.LastSentLocal is null) s.LastSentLocal = DateTime.Now;

        var changes = ObjectDiff.Describe(before, s, nameof(ReportSchedule.SmtpPasswordProtected),
                                          nameof(ReportSchedule.LastSentLocal), nameof(ReportSchedule.LastResult));
        if (NewSmtpPassword.Length > 0) changes += (changes.Length > 0 ? "; " : "") + "SMTP password changed";

        _store.ApplySchedule(s);
        EditSchedule = _store.Schedule.Clone();
        NewSmtpPassword = "";
        SmtpPasswordHint = _store.Schedule.SmtpPasswordProtected.Length > 0 ? "A password is saved. Type a new one only to replace it." : "No password saved yet.";
        Audit(AuditActions.ChangeReportSettings, "Settings", "Reports", changes.Length > 0 ? changes : "Saved (no changes)");
        ScheduleStatus = "Saved. " + ScheduleSummary();
        StatusMessage = "Report settings saved.";
    }

    [RelayCommand] private Task SendReportNowAsync() => SendReportAsync(test: false, scheduled: false);
    [RelayCommand] private Task SendTestEmailAsync() => SendReportAsync(test: true, scheduled: false);

    /// <summary>Called every second from OnTick; looks at the clock every 30 seconds.</summary>
    private void CheckScheduledReport()
    {
        if (++_scheduleTicks % 30 != 0) return;
        var s = _store.Schedule;
        if (!s.Enabled || _sendingReport || !IsOnline || LastUpdated is null) return;   // wait until data has loaded
        if (DateTime.UtcNow < _nextScheduleAttemptUtc) return;
        if (!s.IsDue(DateTime.Now)) return;
        _ = SendReportAsync(test: false, scheduled: true);
    }

    private async Task SendReportAsync(bool test, bool scheduled)
    {
        if (_sendingReport) return;
        if (!scheduled && !IsAdmin) { StatusMessage = "Only an admin can send reports."; return; }

        // Manual sends use what is typed on the page (so it can be tested before saving); scheduled ones use the saved settings.
        var cfg = scheduled ? _store.Schedule : EditSchedule;
        var error = cfg.ValidateForSending();
        if (error.Length > 0) { ScheduleStatus = error; return; }

        var password = !scheduled && NewSmtpPassword.Length > 0
            ? NewSmtpPassword
            : App.Secrets.Unprotect(_store.Schedule.SmtpPasswordProtected);

        var actor = scheduled ? "Scheduler" : null;
        var role = scheduled ? "System" : null;
        _sendingReport = true;
        ScheduleStatus = test ? "Sending test e-mail..." : "Sending report...";
        try
        {
            var now = _engine.Now;
            ReportDoc doc;
            if (test)
            {
                doc = new ReportDoc { Title = "Emergency Desk test e-mail", Subtitle = $"Sent {DateTime.Now:yyyy-MM-dd HH:mm} by {Operator}" };
                doc.Paragraph("If you can read this, the e-mail settings work.");
            }
            else
            {
                var title = cfg.Frequency == ReportFrequency.Weekly ? "Weekly incident summary" : "Daily incident summary";
                doc = SummaryReportBuilder.Build(Incidents.ToList(), Responders.ToList(), _store.Rules, now - cfg.Window, now,
                                                 title, scheduled ? "the scheduler" : Operator, includeOperators: true);
            }

            var pdf = !test && cfg.AttachPdf ? ReportExporter.ToPdf(doc) : null;
            var fileName = ReportExporter.SafeFileName($"{doc.Title} {DateTime.Now:yyyy-MM-dd}") + ".pdf";
            await ReportMailer.SendAsync(cfg, password, $"[Emergency Desk] {doc.Title} - {DateTime.Now:yyyy-MM-dd}",
                                         ReportExporter.ToPlainText(doc), pdf, fileName);

            var recipients = string.Join(", ", ReportSchedule.SplitRecipients(cfg.Recipients));
            Audit(AuditActions.SendReport, "Report", test ? "test" : cfg.Frequency.ToString(), $"Sent to {recipients}", op: actor, role: role);
            if (scheduled)
            {
                _store.Schedule.LastSentLocal = DateTime.Now;
                _store.Schedule.LastResult = "";
                _store.SaveSchedule();
            }
            ScheduleStatus = (test ? "Test e-mail sent. " : "Report sent. ") + ScheduleSummary();
            StatusMessage = test ? "Test e-mail sent." : "Report e-mail sent.";
        }
        catch (Exception ex)
        {
            Audit(AuditActions.SendReport, "Report", test ? "test" : cfg.Frequency.ToString(), ex.Message, ok: false, op: actor, role: role);
            if (scheduled)
            {
                _nextScheduleAttemptUtc = DateTime.UtcNow.AddMinutes(15);   // do not hammer the mail server
                _store.Schedule.LastResult = "Last attempt failed: " + ex.Message;
                _store.SaveSchedule();
            }
            ScheduleStatus = "Could not send: " + ex.Message;
            StatusMessage = "Report e-mail failed.";
        }
        finally
        {
            _sendingReport = false;
        }
    }
}
