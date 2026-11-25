using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmergencyDesk.Models;
using EmergencyDesk.Services;
using EmergencyDesk.Views;

namespace EmergencyDesk.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly DispatcherTimer _timer;   // server polling
    private readonly DispatcherTimer _tick;    // 1 s: SLA countdowns, escalation, re-alerts
    private readonly IncidentEngine _engine = App.Engine;
    private readonly LocalStore _store = App.Store;

    private int _maxSeenIncidentId;
    private bool _firstLoad = true;
    private bool _refreshing;
    private bool _followUpRefresh;

    // alert popup state
    private AlertWindow? _alertWindow;
    private bool _closingAlert;
    private readonly Dictionary<int, DateTime> _snoozedUntil = new();
    private readonly HashSet<int> _escalationAlerted = new();
    private readonly HashSet<int> _autoAssignAttempted = new();

    public MainViewModel()
    {
        Incidents = new ObservableCollection<Incident>();
        Responders = new ObservableCollection<Responder>();
        Users = new ObservableCollection<AppUser>();

        // Dashboard "latest reports": newest first
        IncidentsView = new CollectionViewSource { Source = Incidents }.View;
        IncidentsView.SortDescriptions.Add(new SortDescription(nameof(Incident.ReportedAt), ListSortDirection.Descending));

        // Incident queue / inbox: open first, escalated first, then priority, then longest-waiting
        QueueView = new CollectionViewSource { Source = Incidents }.View;
        QueueView.Filter = o => o is Incident i && MatchesQueueFilter(i);
        QueueView.SortDescriptions.Add(new SortDescription(nameof(Incident.QueueRank), ListSortDirection.Ascending));
        QueueView.SortDescriptions.Add(new SortDescription(nameof(Incident.IsEscalated), ListSortDirection.Descending));
        QueueView.SortDescriptions.Add(new SortDescription(nameof(Incident.PriorityScore), ListSortDirection.Descending));
        QueueView.SortDescriptions.Add(new SortDescription(nameof(Incident.ReportedAt), ListSortDirection.Ascending));

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(2, App.Settings.RefreshSeconds)) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => OnTick();
        _tick.Start();

        _ = RefreshAsync();
    }

    // ---- session ----
    public string DisplayName => App.Session.DisplayName;
    public string Role => App.Session.Role;
    public bool IsAdmin => App.Session.IsAdmin;
    public bool SoundEnabled => _store.Rules.SoundEnabled;

    // ---- data ----
    public ObservableCollection<Incident> Incidents { get; }
    public ObservableCollection<Responder> Responders { get; }
    public ObservableCollection<AppUser> Users { get; }
    public ICollectionView IncidentsView { get; }
    public ICollectionView QueueView { get; }

    public IncidentStatus[] IncidentStatuses { get; } = Enum.GetValues<IncidentStatus>();
    public IncidentType[] IncidentTypes { get; } = Enum.GetValues<IncidentType>();
    public ResponderStatus[] ResponderStatuses { get; } = Enum.GetValues<ResponderStatus>();

    // ---- navigation ----
    [ObservableProperty] private string _currentPage = "Dashboard";
    [RelayCommand] private void Navigate(string page) => CurrentPage = page;

    // ---- status ----
    [ObservableProperty] private string _statusMessage = "Connecting...";
    [ObservableProperty] private bool _isOnline;
    [ObservableProperty] private DateTime? _lastUpdated;

    // ---- dashboard counters ----
    [ObservableProperty] private int _pendingCount;
    [ObservableProperty] private int _activeCount;
    [ObservableProperty] private int _resolvedCount;
    [ObservableProperty] private int _availableResponders;
    [ObservableProperty] private int _totalUsers;

    // ---- queue chips ----
    [ObservableProperty] private string _queueFilter = "Open";
    partial void OnQueueFilterChanged(string value) => QueueView.Refresh();

    [ObservableProperty] private int _openCount;
    [ObservableProperty] private int _newCount;
    [ObservableProperty] private int _ackedCount;
    [ObservableProperty] private int _inProgressCount;
    [ObservableProperty] private int _closedCount;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasEscalations), nameof(EscalationBannerText))]
    private int _escalatedCount;

    public bool HasEscalations => EscalatedCount > 0;
    public string EscalationBannerText =>
        $"{EscalatedCount} incident(s) unacknowledged for over {_store.Rules.EscalateAfterSeconds}s — escalated to supervisor.";

    private bool MatchesQueueFilter(Incident i) => QueueFilter switch
    {
        "Open" => !i.IsClosed,
        "New" => i.QueueState == QueueState.New,
        "Acknowledged" => i.QueueState == QueueState.Acknowledged,
        "InProgress" => i.QueueState == QueueState.InProgress,
        "Escalated" => i.IsEscalated,
        "Closed" => i.IsClosed,
        _ => true
    };

    [RelayCommand]
    private void ShowEscalated()
    {
        CurrentPage = "Incidents";
        QueueFilter = "Escalated";
    }

    private void UpdateCounts()
    {
        OpenCount = Incidents.Count(i => !i.IsClosed);
        NewCount = Incidents.Count(i => i.QueueState == QueueState.New);
        AckedCount = Incidents.Count(i => i.QueueState == QueueState.Acknowledged);
        InProgressCount = Incidents.Count(i => i.QueueState == QueueState.InProgress);
        ClosedCount = Incidents.Count(i => i.IsClosed);
        EscalatedCount = Incidents.Count(i => i.IsEscalated);
    }

    // ---- incidents ----
    [ObservableProperty] private Incident? _selectedIncident;
    [ObservableProperty] private Responder? _assignChoice;
    [ObservableProperty] private IncidentStatus _newStatusChoice = IncidentStatus.Dispatched;

    private string Operator => string.IsNullOrWhiteSpace(DisplayName) ? "Operator" : DisplayName;

    [RelayCommand]
    private async Task AssignAsync()
    {
        if (SelectedIncident is null || AssignChoice is null) return;
        var id = SelectedIncident.Id;
        var choice = AssignChoice;   // the list is rebuilt on every refresh, so keep our own reference
        var wasReceived = SelectedIncident.Status == IncidentStatus.Received;
        var ok = await App.Api.AssignResponderAsync(id, choice.Id);
        if (ok)
        {
            ClaimLocally(id);   // assigning a responder means an operator has taken it
            var now = _engine.Now;
            _store.RecordAssign(id, choice.Id, $"{choice.Name} ({choice.Unit})", Operator, now);
            if (wasReceived) _store.RecordDispatchBy(id, Operator, now);
        }
        Audit(AuditActions.AssignResponder, "Incident", id, $"Assign {choice.Name} ({choice.Unit})", ok);
        StatusMessage = ok ? $"Assigned {choice.Name} to incident #{id}." : "Assignment failed.";
        await RefreshAsync();
        SelectedIncident = Incidents.FirstOrDefault(i => i.Id == id);
    }

    [RelayCommand]
    private async Task UpdateStatusAsync()
    {
        if (SelectedIncident is null) return;
        var id = SelectedIncident.Id;
        var oldStatus = SelectedIncident.Status;
        var ok = await App.Api.UpdateStatusAsync(id, NewStatusChoice);
        if (ok)
        {
            ClaimLocally(id);
            var now = _engine.Now;
            _store.RecordStatusSet(id, NewStatusChoice.ToString(), oldStatus.ToString(), Operator, now);
            if (oldStatus == IncidentStatus.Received && NewStatusChoice is not (IncidentStatus.Received or IncidentStatus.Cancelled))
                _store.RecordDispatchBy(id, Operator, now);
        }
        Audit(AuditActions.UpdateStatus, "Incident", id, $"Status {oldStatus} -> {NewStatusChoice}", ok);
        StatusMessage = ok ? $"Incident #{id} set to {NewStatusChoice}." : "Status update failed.";
        await RefreshAsync();
        SelectedIncident = Incidents.FirstOrDefault(i => i.Id == id);
    }

    private void ClaimLocally(int id)
    {
        var inc = Incidents.FirstOrDefault(i => i.Id == id);
        if (inc is { AcknowledgedAt: null }) _store.RecordAck(id, Operator, _engine.Now);
    }

    // ---- acknowledge ----
    [RelayCommand] private Task AcknowledgeAlertAsync() => AcknowledgeAsync(CurrentAlert);
    [RelayCommand] private Task AcknowledgeSelectedAsync() => AcknowledgeAsync(SelectedIncident);

    private async Task AcknowledgeAsync(Incident? inc)
    {
        if (inc is null || inc.QueueState != QueueState.New) return;
        var id = inc.Id;
        var by = Operator;

        var synced = false;
        try { synced = await App.Api.AcknowledgeAsync(id, by); } catch { /* offline: still claim locally */ }

        _store.RecordAck(id, by, _engine.Now);
        Audit(AuditActions.AcknowledgeIncident, "Incident", id, synced ? "Acknowledged (server updated)" : "Acknowledged on this desk only");
        _snoozedUntil.Remove(id);
        StatusMessage = synced
            ? $"Incident #{id} acknowledged by {by}."
            : $"Incident #{id} acknowledged on this desk (server has no acknowledge endpoint yet, so other desks still see it as new).";
        RecomputeNow();
    }

    // ---- duplicates ----
    [RelayCommand]
    private async Task MergeDuplicatesAsync()
    {
        var sel = SelectedIncident;
        if (sel is null || !sel.CanMerge) return;

        var primaryId = sel.DuplicateOfId ?? sel.Id;
        var dupes = sel.DuplicateOfId is not null
            ? new List<Incident> { sel }
            : Incidents.Where(i => i.DuplicateOfId == primaryId && !i.IsClosed).ToList();
        if (dupes.Count == 0) return;

        var ask = MessageBox.Show(
            $"Merge {dupes.Count} duplicate report(s) into incident #{primaryId}?\n\n" +
            "The duplicates will be closed as Cancelled and linked to #" + primaryId + ".",
            "Merge duplicates", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ask != MessageBoxResult.Yes) return;

        var merged = 0;
        foreach (var d in dupes)
        {
            if (await App.Api.UpdateStatusAsync(d.Id, IncidentStatus.Cancelled))
            {
                _store.RecordMerge(d.Id, primaryId, _engine.Now, Operator);
                merged++;
            }
        }
        Audit(AuditActions.MergeDuplicates, "Incident", primaryId,
              $"Merged {merged} of {dupes.Count} duplicate(s) ({string.Join(", ", dupes.Select(x => "#" + x.Id))}) into #{primaryId}", merged == dupes.Count);
        StatusMessage = merged == dupes.Count
            ? $"Merged {merged} duplicate(s) into incident #{primaryId}."
            : $"Merged {merged} of {dupes.Count} duplicate(s); some updates failed.";
        await RefreshAsync();
        SelectedIncident = Incidents.FirstOrDefault(i => i.Id == primaryId);
    }

    // ---- real-time alert popup ----
    [ObservableProperty] private Incident? _currentAlert;
    [ObservableProperty] private int _alertQueueCount;
    [ObservableProperty] private bool _hasMoreAlerts;

    private void Snooze(int id) =>
        _snoozedUntil[id] = _engine.Now.AddSeconds(Math.Max(5, _store.Rules.ReAlertSeconds));

    [RelayCommand]
    private void SnoozeAlert()
    {
        if (CurrentAlert is null) return;
        Snooze(CurrentAlert.Id);
        UpdateAlerts();
    }

    [RelayCommand]
    private void OpenAlert()
    {
        if (CurrentAlert is null) return;
        var id = CurrentAlert.Id;
        Snooze(id);
        QueueFilter = "Open";
        CurrentPage = "Incidents";
        SelectedIncident = Incidents.FirstOrDefault(i => i.Id == id);
        if (Application.Current.MainWindow is { } w)
        {
            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
            w.Activate();
        }
        UpdateAlerts();
    }

    /// <summary>Decides which unacknowledged incidents should be shouting at the operator right now.</summary>
    private void UpdateAlerts()
    {
        var now = _engine.Now;
        var alerting = Incidents.Where(i => i.QueueState == QueueState.New && !i.IsStale).ToList();
        var ids = alerting.Select(i => i.Id).ToHashSet();

        foreach (var k in _snoozedUntil.Keys.Where(k => !ids.Contains(k)).ToList()) _snoozedUntil.Remove(k);

        // An escalation overrides "remind me later": it must be seen.
        foreach (var esc in alerting.Where(x => x.IsEscalated))
            if (_escalationAlerted.Add(esc.Id)) _snoozedUntil.Remove(esc.Id);

        var visible = alerting
            .Where(i => !_snoozedUntil.TryGetValue(i.Id, out var until) || until <= now)
            .OrderByDescending(i => i.IsEscalated)
            .ThenByDescending(i => i.PriorityScore)
            .ThenBy(i => i.ReportedAt)
            .ToList();

        AlertQueueCount = visible.Count;
        HasMoreAlerts = visible.Count > 1;
        CurrentAlert = visible.FirstOrDefault();

        if (visible.Count == 0) { CloseAlertWindow(); return; }
        if (_alertWindow is not null) return;

        _alertWindow = new AlertWindow(this);
        _alertWindow.Closed += OnAlertWindowClosed;
        _alertWindow.Show();
        _alertWindow.Activate();
    }

    private void OnAlertWindowClosed(object? sender, EventArgs e)
    {
        // Closed by the operator (Alt+F4 etc.) = "remind me later" for everything currently showing.
        if (!_closingAlert)
            foreach (var n in Incidents.Where(x => x.QueueState == QueueState.New)) Snooze(n.Id);
        _closingAlert = false;
        _alertWindow = null;
    }

    private void CloseAlertWindow()
    {
        if (_alertWindow is null) return;
        _closingAlert = true;
        _alertWindow.Close();
    }

    // ---- 1-second tick ----
    private void OnTick()
    {
        CheckScheduledReport();
        if (Incidents.Count == 0) return;
        if (_engine.Tick(Incidents)) QueueView.Refresh();   // an escalation flipped: re-sort / re-filter
        UpdateCounts();
        UpdateAlerts();
    }

    /// <summary>Recompute desk-side state on the data already loaded (after a local action or rule change).</summary>
    private void RecomputeNow()
    {
        _engine.Process(Incidents.ToList(), Responders.ToList());
        QueueView.Refresh();
        UpdateCounts();
        UpdateReports();
        UpdateAlerts();
    }

    // ---- rules & SLA (editable by Admin) ----
    [ObservableProperty] private AppRules _editRules = App.Store.Rules.Clone();

    [RelayCommand]
    private void SaveRules()
    {
        if (!IsAdmin) { StatusMessage = "Only an admin can change rules."; return; }
        var changes = ObjectDiff.Describe(_store.Rules, EditRules);
        _store.ApplyRules(EditRules);
        Audit(AuditActions.ChangeRules, "Settings", "Rules", changes.Length > 0 ? changes : "Saved (no changes)");
        EditRules = _store.Rules.Clone();
        OnPropertyChanged(nameof(SoundEnabled));
        OnPropertyChanged(nameof(EscalationBannerText));
        StatusMessage = "Rules saved.";
        RecomputeNow();
    }

    // ---- responders ----
    [ObservableProperty] private Responder? _selectedResponder;
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editUnit = "";
    [ObservableProperty] private string _editPhone = "";
    [ObservableProperty] private string _editRegion = "";
    [ObservableProperty] private IncidentType _editSpecialty = IncidentType.Medical;
    [ObservableProperty] private ResponderStatus _editStatus = ResponderStatus.Available;

    partial void OnSelectedResponderChanged(Responder? value)
    {
        if (value is null) return;
        EditName = value.Name; EditUnit = value.Unit; EditPhone = value.Phone; EditRegion = value.Region;
        EditSpecialty = value.Specialty; EditStatus = value.Status;
    }

    [RelayCommand]
    private void NewResponder()
    {
        SelectedResponder = null;
        EditName = EditUnit = EditPhone = EditRegion = "";
        EditSpecialty = IncidentType.Medical;
        EditStatus = ResponderStatus.Available;
    }

    [RelayCommand]
    private async Task SaveResponderAsync()
    {
        if (string.IsNullOrWhiteSpace(EditName) || string.IsNullOrWhiteSpace(EditUnit))
        {
            StatusMessage = "Responder name and unit are required.";
            return;
        }
        var previous = SelectedResponder;
        var r = new Responder
        {
            Id = SelectedResponder?.Id ?? 0,
            Name = EditName.Trim(), Unit = EditUnit.Trim(), Phone = EditPhone.Trim(),
            Specialty = EditSpecialty, Status = EditStatus
        };
        var region = EditRegion.Trim();
        var ok = await App.Api.SaveResponderAsync(r);
        StatusMessage = ok ? "Responder saved." : "Saving responder failed.";

        if (ok && r.Id != 0) _store.SetRegion(r.Id, region);
        await RefreshAsync();

        string ResponderDetails()
        {
            if (previous is null) return $"Created {r.Name} ({r.Unit}), {r.Specialty}, {r.Status}, region '{region}'";
            var ch = new List<string>();
            if (previous.Name != r.Name) ch.Add($"Name: {previous.Name} -> {r.Name}");
            if (previous.Unit != r.Unit) ch.Add($"Unit: {previous.Unit} -> {r.Unit}");
            if (previous.Phone != r.Phone) ch.Add($"Phone: {previous.Phone} -> {r.Phone}");
            if (previous.Specialty != r.Specialty) ch.Add($"Specialty: {previous.Specialty} -> {r.Specialty}");
            if (previous.Status != r.Status) ch.Add($"Status: {previous.Status} -> {r.Status}");
            if (previous.Region != region) ch.Add($"Region: '{previous.Region}' -> '{region}'");
            return $"{previous.Name}: " + (ch.Count > 0 ? string.Join("; ", ch) : "saved (no changes)");
        }
        var savedId = r.Id != 0 ? r.Id : Responders.LastOrDefault(x => x.Name == r.Name && x.Unit == r.Unit)?.Id;
        Audit(AuditActions.SaveResponder, "Responder", savedId, ResponderDetails(), ok);

        if (ok && r.Id == 0 && region.Length > 0)
        {
            // New responder: the server just gave it an id, find it and attach the coverage region.
            var created = Responders.LastOrDefault(x => x.Name == r.Name && x.Unit == r.Unit);
            if (created is not null)
            {
                _store.SetRegion(created.Id, region);
                await RefreshAsync();
            }
        }
    }

    // ---- users (admin only) ----
    [ObservableProperty] private AppUser? _selectedUser;

    [RelayCommand]
    private async Task ToggleUserAsync()
    {
        if (!IsAdmin || SelectedUser is null) return;
        var target = !SelectedUser.IsActive;
        var name = SelectedUser.FullName;
        var userId = SelectedUser.Id;
        var ok = await App.Api.SetUserActiveAsync(userId, target);
        Audit(AuditActions.ToggleUser, "User", userId, $"{name} {(target ? "activated" : "deactivated")}", ok);
        StatusMessage = ok ? $"{name} {(target ? "activated" : "deactivated")}." : "User update failed.";
        await RefreshAsync();
    }

    // ---- refresh / logout ----
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var incidents = await App.Api.GetIncidentsAsync();
            var responders = await App.Api.GetRespondersAsync();
            var users = IsAdmin ? await App.Api.GetUsersAsync() : new List<AppUser>();

            _engine.ClockOffset = App.Api.ServerClockOffset;
            _engine.Process(incidents, responders);
            await RunAutoAssignAsync(incidents, responders);

            var selIncident = SelectedIncident?.Id;
            var selResponder = SelectedResponder?.Id;
            var selUser = SelectedUser?.Id;

            Replace(Incidents, incidents);
            Replace(Responders, responders);
            Replace(Users, users);

            if (selIncident is not null) SelectedIncident = Incidents.FirstOrDefault(i => i.Id == selIncident);
            if (selResponder is not null) SelectedResponder = Responders.FirstOrDefault(r => r.Id == selResponder);
            if (selUser is not null) SelectedUser = Users.FirstOrDefault(u => u.Id == selUser);

            PendingCount = incidents.Count(i => i.Status == IncidentStatus.Received);
            ActiveCount = incidents.Count(i => i.Status is IncidentStatus.Dispatched or IncidentStatus.EnRoute or IncidentStatus.OnScene);
            ResolvedCount = incidents.Count(i => i.Status == IncidentStatus.Resolved);
            AvailableResponders = responders.Count(r => r.Status == ResponderStatus.Available);
            TotalUsers = users.Count;
            UpdateCounts();
            UpdateReports();

            var newest = incidents.Count == 0 ? 0 : incidents.Max(i => i.Id);
            if (!_firstLoad && newest > _maxSeenIncidentId)
                StatusMessage = "New emergency report received!";   // the popup + sound come from UpdateAlerts
            _maxSeenIncidentId = Math.Max(_maxSeenIncidentId, newest);
            _firstLoad = false;

            UpdateAlerts();

            IsOnline = true;
            LastUpdated = DateTime.Now;
            if (StatusMessage is "Connecting..." or "Server unreachable.") StatusMessage = "Connected.";
        }
        catch (UnauthorizedAccessException)
        {
            _timer.Stop();
            Audit(AuditActions.SessionExpired, "Session", "", "Server rejected the token; signed out");
            MessageBox.Show("Your session has expired. Please sign in again.", "Session expired");
            Logout();
        }
        catch (Exception)
        {
            IsOnline = false;
            StatusMessage = "Server unreachable.";
        }
        finally
        {
            _refreshing = false;
        }

        if (_followUpRefresh)
        {
            _followUpRefresh = false;
            _ = RefreshAsync();
        }
    }

    /// <summary>Applies the auto-assignment rules to unassigned incoming reports (only if enabled on this desk).</summary>
    private async Task RunAutoAssignAsync(List<Incident> incidents, List<Responder> responders)
    {
        var plan = _engine.PlanAutoAssign(incidents, responders, _autoAssignAttempted);
        if (plan.Count == 0) return;

        var assigned = false;
        foreach (var (inc, resp) in plan)
        {
            _autoAssignAttempted.Add(inc.Id);
            var ok = false;
            try { ok = await App.Api.AssignResponderAsync(inc.Id, resp.Id); } catch { }

            if (ok)
            {
                _store.RecordAck(inc.Id, "Auto-assign", _engine.Now);
                _store.RecordAssign(inc.Id, resp.Id, $"{resp.Name} ({resp.Unit})", "Auto-assign", _engine.Now);
                _store.RecordDispatchBy(inc.Id, "Auto-assign", _engine.Now);
                Audit(AuditActions.AutoAssign, "Incident", inc.Id, $"Auto-assigned {resp.Name} ({resp.Unit}) on the desk of {Operator}",
                      op: "Auto-assign", role: "System");
                StatusMessage = $"Auto-assigned {resp.Name} ({resp.Unit}) to incident #{inc.Id}.";
                assigned = true;
            }
            else
            {
                StatusMessage = $"Auto-assign failed for incident #{inc.Id}. Please assign manually.";
            }
        }

        if (assigned)
        {
            _engine.Process(incidents, responders);   // pick up the new acknowledgements before showing alerts
            _followUpRefresh = true;                   // and re-read server state shortly
        }
    }

    [RelayCommand]
    private void Logout()
    {
        Audit(AuditActions.Logout, "Session", "", "Signed out");
        Shutdown();
        var old = Application.Current.MainWindow;
        App.ShowLogin();
        old?.Close();
    }

    /// <summary>Stops polling/timers and closes the alert popup (sign-out or window closed).</summary>
    public void Shutdown()
    {
        _timer.Stop();
        _tick.Stop();
        CloseAlertWindow();
    }

    private static void Replace<T>(ObservableCollection<T> target, List<T> source)
    {
        target.Clear();
        foreach (var item in source) target.Add(item);
    }
}
