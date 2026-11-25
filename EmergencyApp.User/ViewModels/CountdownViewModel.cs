using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmergencyApp.User.Services;

namespace EmergencyApp.User.ViewModels;

/// 5-second "are you sure?" screen for tap / shake / volume triggers. Cancel = back, no SOS sent.
public partial class CountdownViewModel : ObservableObject, IQueryAttributable
{
    private readonly SosService _sos;
    private readonly TriggerService _triggers;
    private CancellationTokenSource? _cts;
    private string _source = "Trigger";

    [ObservableProperty] private int secondsLeft = AppSettings.CountdownSeconds;
    [ObservableProperty] private string summary = "";
    [ObservableProperty] private bool isSending;

    public CountdownViewModel(SosService sos, TriggerService triggers) => (_sos, _triggers) = (sos, triggers);

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("source", out var v)) _source = v?.ToString() ?? "Trigger";
    }

    public async Task StartAsync()
    {
        if (_cts is not null) return;
        _triggers.CountdownOpen = true;
        DeviceDisplay.Current.KeepScreenOn = true;

        var people = _sos.People >= 4 ? "4+ people" : _sos.People == 1 ? "1 person" : $"{_sos.People} people";
        Summary = $"{_sos.Type?.Name ?? "General emergency"} · {people}";

        var cts = _cts = new CancellationTokenSource();
        try
        {
            for (var i = AppSettings.CountdownSeconds; i > 0; i--)
            {
                SecondsLeft = i;
                try { Vibration.Default.Vibrate(80); } catch { }
                await Task.Delay(1000, cts.Token);
            }
            await SendAsync();
        }
        catch (OperationCanceledException) { }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _triggers.CountdownOpen = false;
        DeviceDisplay.Current.KeepScreenOn = false;
    }

    private async Task SendAsync()
    {
        if (IsSending) return;
        IsSending = true;
        var id = await _sos.SendAsync(_source);
        // "../live" = close this screen, then open the live emergency page
        await Shell.Current.GoToAsync(id is null ? ".." : $"../live?clientId={id}");
    }

    [RelayCommand]
    private async Task SendNowAsync()
    {
        _cts?.Cancel();
        await SendAsync();
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        if (IsSending) return;
        _cts?.Cancel();
        await Shell.Current.GoToAsync("..");
    }
}
