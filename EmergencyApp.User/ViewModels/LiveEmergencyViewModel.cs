using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmergencyApp.User.Models;
using EmergencyApp.User.Reliability;
using EmergencyApp.User.Services;

namespace EmergencyApp.User.ViewModels;

public partial class LiveEmergencyViewModel : ObservableObject, IQueryAttributable
{
    private readonly DatabaseService _db;
    private readonly ApiService _api;
    private readonly SessionService _session;
    private readonly LiveTrackingService _tracking;
    private readonly TransmissionService _transmission;
    private readonly NetworkMonitor _network;
    private readonly PowerService _power;
    private readonly IPhoneCaller _caller;

    private string _clientId = "";
    private EmergencyRequest? _request;
    private bool _attached, _wasActiveOnOpen, _closing;
    private CancellationTokenSource? _closeCts;

    [ObservableProperty] private string typeLabel = "";
    [ObservableProperty] private string headline = "";
    [ObservableProperty] private string subtitle = "";
    [ObservableProperty] private string statusText = "";
    [ObservableProperty] private Color statusColor = Colors.Gray;
    [ObservableProperty] private Color statusBg = Colors.LightGray;
    [ObservableProperty] private bool showClosing;
    [ObservableProperty] private string closingText = "";

    // Delivery: Sent -> Received -> Acknowledged by dispatcher
    [ObservableProperty] private string sentIcon = "⚪";
    [ObservableProperty] private string sentLabel = "Queued on your phone";
    [ObservableProperty] private string receivedIcon = "⚪";
    [ObservableProperty] private string ackIcon = "⚪";
    [ObservableProperty] private string receivedTime = "—";
    [ObservableProperty] private string deliveryNote = "";
    [ObservableProperty] private bool hasDeliveryNote;

    // Fallback card: shown while the server has not confirmed the alert and something went wrong
    [ObservableProperty] private bool showFallback;
    [ObservableProperty] private string fallbackTitle = "";
    [ObservableProperty] private string fallbackText = "";
    [ObservableProperty] private bool autoCallActive;
    [ObservableProperty] private string callButtonText = "";

    // Network chip + low-battery banner
    [ObservableProperty] private string channelText = "";
    [ObservableProperty] private Color channelColor = Colors.Gray;
    [ObservableProperty] private bool hasBatteryWarning;
    [ObservableProperty] private string batteryWarning = "";
    [ObservableProperty] private string updateNote = "Live info updates every 5 seconds while the app is open.";

    [ObservableProperty] private string sentTime = "—";
    [ObservableProperty] private string ackTime = "—";
    [ObservableProperty] private string dispatchedTime = "—";
    [ObservableProperty] private string arrivedTime = "—";
    [ObservableProperty] private string finishedTime = "—";

    [ObservableProperty] private bool hasResponder;
    [ObservableProperty] private bool hasResponderPhone;
    [ObservableProperty] private bool hasResponderLocation;
    [ObservableProperty] private string responderName = "";
    [ObservableProperty] private string responderUnit = "";
    [ObservableProperty] private string responderAddress = "";
    [ObservableProperty] private string distanceText = "";
    [ObservableProperty] private string etaText = "";

    [ObservableProperty] private string myCoords = "Location unavailable";
    [ObservableProperty] private string myDetail = "";
    [ObservableProperty] private bool canCancel;

    public LiveEmergencyViewModel(DatabaseService db, ApiService api, SessionService session, LiveTrackingService tracking,
                                  TransmissionService transmission, NetworkMonitor network, PowerService power, IPhoneCaller caller)
    {
        (_db, _api, _session, _tracking) = (db, api, session, tracking);
        (_transmission, _network, _power, _caller) = (transmission, network, power, caller);
    }

    // Shell passes ?clientId=... here
    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("clientId", out var v)) _clientId = v?.ToString() ?? "";
        if (_attached && _request is null) _ = LoadAsync();
    }

    public async Task AttachAsync()
    {
        if (_attached) return;
        _attached = true;
        _tracking.Updated += OnUpdated;
        _network.Changed += OnEnvironmentChanged;
        _power.Changed += OnEnvironmentChanged;
        _power.Refresh();
        await _transmission.RefreshReadinessAsync();
        await LoadAsync();
    }

    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _tracking.Updated -= OnUpdated;
        _network.Changed -= OnEnvironmentChanged;
        _power.Changed -= OnEnvironmentChanged;
        _closeCts?.Cancel();
        _closing = false;
    }

    private async Task LoadAsync()
    {
        if (string.IsNullOrEmpty(_clientId)) return;
        _request = await _db.FindByClientIdAsync(_clientId);
        if (_request is null) return;
        _wasActiveOnOpen = _request.IsActive;
        Render();
        if (_wasActiveOnOpen) _tracking.EnsureRunning();
    }

    private void OnUpdated(EmergencyRequest r)
    {
        if (r.ClientId != _clientId) return;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            _request = r;
            Render();
            if (_wasActiveOnOpen && !r.IsActive) await CloseAfterDelayAsync();
        });
    }

    // Connectivity or battery changed: refresh the chip, the banner and the fallback card.
    private void OnEnvironmentChanged() => MainThread.BeginInvokeOnMainThread(Render);

    /// Finished: show the final state for 5 seconds, then close the page (it stays in History).
    private async Task CloseAfterDelayAsync()
    {
        if (_closing) return;
        _closing = true;
        _closeCts = new CancellationTokenSource();
        try
        {
            for (var i = 5; i > 0; i--)
            {
                ClosingText = $"Closing in {i}…";
                ShowClosing = true;
                await Task.Delay(1000, _closeCts.Token);
            }
            ShowClosing = false;
            await Shell.Current.GoToAsync("..");
        }
        catch (OperationCanceledException) { }
    }

    private static string T(DateTime? utc) => utc is null ? "—" : utc.Value.ToLocalTime().ToString("HH:mm");

    private void Render()
    {
        var r = _request;
        if (r is null) return;
        var user = _tracking.LastLocation;

        TypeLabel = $"SOS · {r.Type.ToUpperInvariant()} · {r.PeopleText.ToUpperInvariant()}";
        (Headline, Subtitle) = r.Status switch
        {
            RequestStatus.Pending => ("Sending your SOS…", _transmission.DescribeDelivery(r)),
            RequestStatus.Received => ("SOS received.",
                r.Delivery == DeliveryStep.Acknowledged
                    ? "A dispatcher has acknowledged your alert. Stay in a safe place if possible."
                    : "The response center has your alert and location. Waiting for a dispatcher to acknowledge it."),
            RequestStatus.Dispatched => ("Help is on the way.",
                "A responder has been dispatched to your location."),
            RequestStatus.Arrived => ("Responder has arrived.",
                "Help is at your location."),
            RequestStatus.Resolved => ("Emergency resolved.", "This emergency has been marked as finished."),
            RequestStatus.Cancelled => ("SOS cancelled.", "This request was cancelled."),
            _ => (r.Status, "")
        };
        StatusText = r.Status;
        StatusColor = r.StatusColor;
        StatusBg = r.StatusColor.WithAlpha(0.14f);
        CanCancel = r.IsActive;

        RenderDelivery(r);
        DispatchedTime = T(r.DispatchedAtUtc);
        ArrivedTime = T(r.ArrivedAtUtc);
        FinishedTime = T(r.ResolvedAtUtc);

        // Responder
        HasResponderLocation = r.ResponderLatitude is not null && r.ResponderLongitude is not null;
        HasResponderPhone = !string.IsNullOrWhiteSpace(r.ResponderPhone);
        HasResponder = !string.IsNullOrEmpty(r.ResponderName) || HasResponderLocation;
        ResponderName = r.ResponderName ?? "Responder";
        ResponderUnit = r.ResponderUnit ?? "";
        ResponderAddress = string.IsNullOrEmpty(r.ResponderAddress) ? "" : $"📍 {r.ResponderAddress}";

        double? km = r.DistanceKm;
        if (km is null && HasResponderLocation)
        {
            var (lat, lng) = user is not null ? (user.Latitude, user.Longitude) : (r.Latitude ?? 0, r.Longitude ?? 0);
            if (user is not null || r.Latitude is not null)
                km = GeoMath.DistanceKm(lat, lng, r.ResponderLatitude!.Value, r.ResponderLongitude!.Value);
        }
        DistanceText = r.Status == RequestStatus.Arrived ? "Arrived"
                     : km is double d ? $"{GeoMath.FormatDistance(d)} away" : "Distance unavailable";
        EtaText = r.IsActive && r.EtaMinutes is int m && r.Status != RequestStatus.Arrived ? $"ETA about {m} min" : "";

        // Your live location
        double? ulat = user?.Latitude ?? r.Latitude, ulng = user?.Longitude ?? r.Longitude;
        MyCoords = ulat is double a && ulng is double b ? GeoMath.FormatCoords(a, b) : "Location unavailable";
        MyDetail = user is not null
            ? (user.Accuracy is double acc ? $"Accuracy ±{acc:0} m · " : "") + $"updated {DateTime.Now:HH:mm:ss}"
            : r.IsActive ? "Waiting for GPS…" : "Location when the SOS was sent";
    }

    /// Delivery card (Sent -> Received -> Acknowledged), network chip, battery banner and fallback card.
    private void RenderDelivery(EmergencyRequest r)
    {
        var step = r.Delivery;
        var info = _transmission.GetInfo(r.ClientId);

        SentIcon = step >= DeliveryStep.Sent ? "✅" : "⚪";
        ReceivedIcon = step >= DeliveryStep.Received ? "✅" : "⚪";
        AckIcon = step >= DeliveryStep.Acknowledged ? "✅" : "⚪";

        SentLabel = step == DeliveryStep.Queued ? "Queued on your phone"
                  : r.SentChannel == "SMS" ? "Sent by SMS" : "Sent over the internet";
        SentTime = T(r.SentAtUtc ?? (step >= DeliveryStep.Sent ? (DateTime?)r.CreatedAtUtc : null));
        ReceivedTime = T(step >= DeliveryStep.Received ? r.ReceivedAtUtc : null);
        AckTime = T(step >= DeliveryStep.Acknowledged ? (r.AcknowledgedAtUtc ?? r.DispatchedAtUtc ?? r.ReceivedAtUtc) : null);

        // Extra detail while the server has not confirmed it (the main sentence is already in the status card)
        DeliveryNote = r.IsActive && step < DeliveryStep.Received && _network.State != NetworkState.Offline
                       && info.LastError is { Length: > 0 } err ? err : "";
        HasDeliveryNote = DeliveryNote.Length > 0;

        // Network chip + low-battery banner
        var (text, level) = _transmission.ChannelSummary();
        ChannelText = text;
        ChannelColor = level switch { 0 => Colors.SeaGreen, 1 => Colors.DarkOrange, _ => Colors.Firebrick };
        HasBatteryWarning = _power.IsLow && r.IsActive;
        BatteryWarning = _power.Warning;
        UpdateNote = _power.IsLow
            ? $"Battery saver: live info updates every {(int)_power.TrackingInterval.TotalSeconds} seconds."
            : "Live info updates every 5 seconds while the app is open.";

        // Fallback card (one-tap call / auto-call countdown)
        var number = AppSettings.EmergencyNumber;
        AutoCallActive = info.AutoCallSecondsLeft is not null;
        ShowFallback = r.IsActive && step < DeliveryStep.Received &&
                       (_network.State == NetworkState.Offline || info.LastError is not null ||
                        r.SmsSentAtUtc is not null || AutoCallActive || info.AutoCallPlaced);
        CallButtonText = $"📞 Call {number} now";
        if (AutoCallActive)
        {
            FallbackTitle = $"Calling {number} in {info.AutoCallSecondsLeft} s";
            FallbackText = "Your alert could not be sent, so the app will call the emergency number for you. Tap Cancel to stop.";
        }
        else if (info.AutoCallPlaced)
        {
            FallbackTitle = "Call started";
            FallbackText = "The app started a call to the emergency number because your alert could not be sent.";
        }
        else if (r.SmsSentAtUtc is not null)
        {
            FallbackTitle = "Sent to dispatch by SMS";
            FallbackText = "Your location and details went out by SMS. The app keeps trying the internet. You can also call.";
        }
        else
        {
            FallbackTitle = "Alert not confirmed yet";
            FallbackText = "If it is urgent, call the emergency number now.";
        }
    }

    [RelayCommand]
    private void CancelAutoCall() => _transmission.DismissAutoCall(_clientId);

    [RelayCommand]
    private async Task CallEmergencyNowAsync()
    {
        _transmission.DismissAutoCall(_clientId);   // the user took over: no second auto-call
        if (!await _caller.CallAsync(AppSettings.EmergencyNumber))
            await Shell.Current.DisplayAlertAsync("Cannot call", "The phone app could not be opened.", "OK");
    }

    [RelayCommand]
    private async Task CancelRequestAsync()
    {
        var r = _request;
        if (r is null || !r.IsActive) return;
        if (!await Shell.Current.DisplayAlert("Cancel SOS",
                "Request cancellation? Only do this if you are safe and no longer need help.",
                "Request cancellation", "Keep SOS")) return;

        if (r.ServerId is null || !r.IsSynced)
        {   // never reached the server: cancel locally
            r.Status = RequestStatus.Cancelled; r.IsSynced = true; r.ResolvedAtUtc = DateTime.UtcNow;
        }
        else
        {
            var res = await _api.CancelAsync(r.ServerId.Value, _session.Token);
            if (!res.Success)
            {
                await Shell.Current.DisplayAlert("Could not cancel", res.Error ?? "Try again.", "OK");
                return;
            }
            if (res.Data is not null) r.Apply(res.Data); else { r.Status = RequestStatus.Cancelled; r.ResolvedAtUtc = DateTime.UtcNow; }
        }
        await _db.SaveRequestAsync(r);
        Render();
        if (_wasActiveOnOpen && !r.IsActive) await CloseAfterDelayAsync();
    }

    [RelayCommand]
    private void CallResponder()
    {
        if (_request?.ResponderPhone is { Length: > 0 } p) try { PhoneDialer.Default.Open(p); } catch { }
    }

    [RelayCommand]
    private async Task OpenMyLocationAsync()
    {
        var u = _tracking.LastLocation;
        double? lat = u?.Latitude ?? _request?.Latitude, lng = u?.Longitude ?? _request?.Longitude;
        if (lat is double a && lng is double b) try { await Launcher.Default.OpenAsync(GeoMath.MapsUrl(a, b)); } catch { }
    }

    [RelayCommand]
    private async Task OpenResponderLocationAsync()
    {
        if (_request?.ResponderLatitude is double a && _request.ResponderLongitude is double b)
            try { await Launcher.Default.OpenAsync(GeoMath.MapsUrl(a, b)); } catch { }
    }
}
