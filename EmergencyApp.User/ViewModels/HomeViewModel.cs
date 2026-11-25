using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmergencyApp.User.Models;
using EmergencyApp.User.Services;

namespace EmergencyApp.User.ViewModels;

public partial class HomeViewModel : ObservableObject
{
    private const int HoldSeconds = 3;   // how long the SOS button must be held

    private readonly DatabaseService _db;
    private readonly SessionService _session;
    private readonly LocationService _location;
    private readonly LiveTrackingService _tracking;
    private readonly SosService _sos;
    private readonly TriggerService _triggers;
    private readonly NetworkMonitor _network;
    private readonly PowerService _power;
    private readonly TransmissionService _transmission;

    private CancellationTokenSource? _holdCts;
    private CancellationTokenSource? _gpsCts;
    private string? _activeClientId;

    public List<EmergencyTypeOption> Types => EmergencyTypeOption.All;
    public List<PeopleOption> PeopleOptions => PeopleOption.All;

    [ObservableProperty] private EmergencyTypeOption? selectedType;
    [ObservableProperty] private PeopleOption? selectedPeople;
    [ObservableProperty] private string greeting = "";
    [ObservableProperty] private double holdProgress;
    [ObservableProperty] private double holdScale = 1;
    [ObservableProperty] private string holdHint = "";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string gpsTitle = "📍 Locating…";
    [ObservableProperty] private string gpsCoords = "";
    [ObservableProperty] private string gpsDetail = "Allow location access to share your position.";
    [ObservableProperty] private bool hasActive;
    [ObservableProperty] private string activeText = "";
    [ObservableProperty] private string channelText = "";
    [ObservableProperty] private Color channelColor = Colors.Gray;
    [ObservableProperty] private bool hasBatteryWarning;
    [ObservableProperty] private string batteryWarning = "";

    partial void OnSelectedTypeChanged(EmergencyTypeOption? value) => _sos.Type = value;
    partial void OnSelectedPeopleChanged(PeopleOption? value) => _sos.People = value?.Value ?? 1;

    public HomeViewModel(DatabaseService db, SessionService session, LocationService location,
                         LiveTrackingService tracking, SosService sos, TriggerService triggers,
                         NetworkMonitor network, PowerService power, TransmissionService transmission)
    {
        (_db, _session, _location, _tracking, _sos, _triggers) = (db, session, location, tracking, sos, triggers);
        (_network, _power, _transmission) = (network, power, transmission);
        HoldHint = IdleHint();
    }

    private static string IdleHint() => AppSettings.TapMode
        ? $"Tap to start a {AppSettings.CountdownSeconds}-second SOS countdown"
        : $"Hold for {HoldSeconds} seconds to send SOS";

    public async Task OnAppearingAsync()
    {
        Greeting = $"Hello, {_session.CurrentUser?.FullName.Split(' ')[0]}";
        SelectedType = _sos.Type;
        SelectedPeople = PeopleOption.All.FirstOrDefault(p => p.Value == _sos.People) ?? PeopleOption.All[0];
        HoldHint = IdleHint();

        _triggers.Start();            // shake / volume triggers (if enabled in Profile)
        _tracking.EnsureRunning();

        _network.Changed -= OnEnvironmentChanged; _network.Changed += OnEnvironmentChanged;
        _power.Changed -= OnEnvironmentChanged; _power.Changed += OnEnvironmentChanged;
        _transmission.Changed -= OnDeliveryChanged; _transmission.Changed += OnDeliveryChanged;
        _power.Refresh();
        await _transmission.RefreshReadinessAsync();
        UpdateEnvironment();

        await LoadActiveAsync();
        StartGps();
    }

    public void OnDisappearing()
    {
        _network.Changed -= OnEnvironmentChanged;
        _power.Changed -= OnEnvironmentChanged;
        _transmission.Changed -= OnDeliveryChanged;
        _gpsCts?.Cancel(); _gpsCts = null;
        EndHold();
    }

    private void OnEnvironmentChanged() => MainThread.BeginInvokeOnMainThread(UpdateEnvironment);

    private void OnDeliveryChanged(EmergencyRequest r)
    {
        if (r.ClientId == _activeClientId || _activeClientId is null)
            MainThread.BeginInvokeOnMainThread(async () => await LoadActiveAsync());
    }

    /// "How will my alert be sent right now?" chip + low-battery warning.
    private void UpdateEnvironment()
    {
        var (text, level) = _transmission.ChannelSummary();
        ChannelText = text;
        ChannelColor = level switch { 0 => Colors.SeaGreen, 1 => Colors.DarkOrange, _ => Colors.Firebrick };
        HasBatteryWarning = _power.IsLow;
        BatteryWarning = _power.Warning;
    }

    private async Task LoadActiveAsync()
    {
        if (_session.CurrentUser is null) return;
        var a = (await _db.GetRequestsAsync(_session.CurrentUser.Id)).FirstOrDefault(r => r.IsActive);
        _activeClientId = a?.ClientId;
        HasActive = a is not null;
        ActiveText = a is null ? "" : $"{a.Icon} {a.Type} emergency · {(a.ServerHasIt ? a.Status : a.DeliveryText)}";
    }

    private async void StartGps()
    {
        if (_gpsCts is not null) return;
        var cts = _gpsCts = new CancellationTokenSource();
        var first = true;
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var loc = first ? await _location.GetCurrentAsync() : await _location.GetQuietAsync();
                first = false;
                if (loc is not null)
                {
                    GpsTitle = "📍 GPS location captured";
                    GpsCoords = GeoMath.FormatCoords(loc.Latitude, loc.Longitude);
                    GpsDetail = (loc.Accuracy is double a ? $"Accuracy ±{a:0} m · " : "") + $"updated {DateTime.Now:HH:mm}";
                }
                else if (_location.Last is null)
                {
                    GpsTitle = "📍 Location unavailable";
                    GpsDetail = "Turn on GPS and allow location access.";
                }
                await Task.Delay(TimeSpan.FromSeconds(10), cts.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// If an SOS is already ongoing, ask before starting another. Returns false if the user chose to view the current one.
    private async Task<bool> ConfirmNewSosAsync()
    {
        if (!HasActive) return true;
        if (await Shell.Current.DisplayAlert("Emergency in progress",
                "You already have an active SOS. Send another one?", "Send another", "View current"))
            return true;
        await OpenActiveAsync();
        return false;
    }

    // ---- SOS button: hold for 3 s, or tap -> countdown (Profile > SOS triggers) ----
    public async void BeginHold()
    {
        if (IsBusy || _holdCts is not null || _session.CurrentUser is null) return;

        if (AppSettings.TapMode)
        {
            if (await ConfirmNewSosAsync()) await Shell.Current.GoToAsync("countdown?source=Tap");
            return;
        }

        var cts = _holdCts = new CancellationTokenSource();
        var start = DateTime.UtcNow;
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var p = (DateTime.UtcNow - start).TotalSeconds / HoldSeconds;
                HoldProgress = Math.Min(p, 1);
                HoldScale = 1 + 0.08 * HoldProgress;
                HoldHint = p < 1 ? $"Keep holding… {Math.Ceiling(HoldSeconds - p * HoldSeconds)}" : "Sending…";

                if (p >= 1)
                {
                    try { HapticFeedback.Default.Perform(HapticFeedbackType.LongPress); } catch { }
                    await SendHeldSosAsync();
                    return;
                }
                await Task.Delay(50, cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _holdCts = null;
            HoldProgress = 0; HoldScale = 1;
            HoldHint = IdleHint();
        }
    }

    public void EndHold() => _holdCts?.Cancel();

    private async Task SendHeldSosAsync()
    {
        IsBusy = true;
        try
        {
            if (!await ConfirmNewSosAsync()) return;
            var id = await _sos.SendAsync("Hold");
            if (id is not null) await Shell.Current.GoToAsync($"live?clientId={id}");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task OpenActiveAsync()
    {
        if (_activeClientId is not null) await Shell.Current.GoToAsync($"live?clientId={_activeClientId}");
    }

    [RelayCommand]
    private async Task OpenMapsAsync()
    {
        var l = _location.Last;
        if (l is null) return;
        try { await Launcher.Default.OpenAsync(GeoMath.MapsUrl(l.Latitude, l.Longitude)); } catch { }
    }

    [RelayCommand]
    private async Task CallEmergencyAsync()
    {
        var number = AppSettings.EmergencyNumber;
        try { PhoneDialer.Default.Open(number); }
        catch { await Shell.Current.DisplayAlert("Error", "Cannot open the phone dialer.", "OK"); }
    }
}
