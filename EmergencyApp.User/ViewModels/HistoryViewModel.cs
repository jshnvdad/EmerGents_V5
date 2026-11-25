using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmergencyApp.User.Models;
using EmergencyApp.User.Services;

namespace EmergencyApp.User.ViewModels;

public class RequestGroup : List<EmergencyRequest>
{
    public string Name { get; }
    public RequestGroup(string name, IEnumerable<EmergencyRequest> items) : base(items) => Name = name;
}

public partial class HistoryViewModel : ObservableObject
{
    private readonly DatabaseService _db;
    private readonly SessionService _session;
    private readonly SyncService _sync;
    private readonly LiveTrackingService _tracking;
    private string _signature = "";

    public ObservableCollection<RequestGroup> Groups { get; } = new();
    [ObservableProperty] private bool isRefreshing;

    public HistoryViewModel(DatabaseService db, SessionService session, SyncService sync, LiveTrackingService tracking)
        => (_db, _session, _sync, _tracking) = (db, session, sync, tracking);

    public void Attach() { _tracking.Updated -= OnUpdated; _tracking.Updated += OnUpdated; }
    public void Detach() => _tracking.Updated -= OnUpdated;

    private void OnUpdated(EmergencyRequest r) =>
        MainThread.BeginInvokeOnMainThread(async () => await ReloadAsync());

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_session.CurrentUser is null) { IsRefreshing = false; return; }
        IsRefreshing = true;
        try
        {
            await ReloadAsync();
            await _sync.SyncAllAsync();
            await ReloadAsync();
            _tracking.EnsureRunning();
        }
        finally { IsRefreshing = false; }
    }

    private async Task ReloadAsync()
    {
        if (_session.CurrentUser is null) return;
        var list = await _db.GetRequestsAsync(_session.CurrentUser.Id);

        // Only rebuild the list when something visible changed (avoids flicker every 5 s).
        var sig = string.Join("|", list.Select(r => $"{r.ClientId}:{r.Status}:{r.DistanceKm}:{r.ResponderName}"));
        if (sig == _signature) return;
        _signature = sig;

        Groups.Clear();
        var ongoing = list.Where(r => r.IsActive).ToList();
        var finished = list.Where(r => !r.IsActive).ToList();
        if (ongoing.Count > 0) Groups.Add(new RequestGroup("Ongoing", ongoing));
        if (finished.Count > 0) Groups.Add(new RequestGroup("Finished", finished));
    }

    [RelayCommand]
    private Task OpenAsync(EmergencyRequest r) => Shell.Current.GoToAsync($"live?clientId={r.ClientId}");
}
