using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmergencyApp.User.Models;
using EmergencyApp.User.Reliability;
using EmergencyApp.User.Services;

namespace EmergencyApp.User.ViewModels;

public partial class ProfileViewModel : ObservableObject
{
    private readonly DatabaseService _db;
    private readonly SessionService _session;

    public ObservableCollection<EmergencyContact> Contacts { get; } = new();

    [ObservableProperty] private string fullName = "";
    [ObservableProperty] private string phone = "";
    [ObservableProperty] private string email = "";
    [ObservableProperty] private string bloodType = "";
    [ObservableProperty] private string allergies = "";
    [ObservableProperty] private string conditions = "";
    [ObservableProperty] private string serverUrl = ApiService.BaseUrl;
    [ObservableProperty] private string emergencyNumber = AppSettings.EmergencyNumber;

    private readonly TriggerService _triggers;
    private readonly TransmissionService _transmission;
    private readonly PowerService _power;
    private readonly ISmsSender _sms;
    private readonly IPhoneCaller _caller;

    // Transmission & reliability
    [ObservableProperty] private bool smsFallbackEnabled = AppSettings.SmsFallbackEnabled;
    [ObservableProperty] private string dispatchSmsNumber = AppSettings.DispatchSmsNumber;
    [ObservableProperty] private bool autoCallEnabled = AppSettings.AutoCallEnabled;
    [ObservableProperty] private bool lowBatteryEnabled = AppSettings.LowBatteryModeEnabled;
    [ObservableProperty] private string reliabilityStatus = "";
    [ObservableProperty] private bool showHttpWarning;

    [ObservableProperty] private bool tapMode = AppSettings.TapMode;
    [ObservableProperty] private bool shakeEnabled = AppSettings.ShakeEnabled;
    [ObservableProperty] private bool volumeEnabled = AppSettings.VolumeEnabled;

    partial void OnTapModeChanged(bool value) => AppSettings.TapMode = value;
    partial void OnShakeEnabledChanged(bool value) { AppSettings.ShakeEnabled = value; _triggers.ApplySettings(); }
    partial void OnVolumeEnabledChanged(bool value) => AppSettings.VolumeEnabled = value;

    partial void OnSmsFallbackEnabledChanged(bool value)
    {
        AppSettings.SmsFallbackEnabled = value;
        _ = value ? RequestSmsPermissionAsync() : RefreshReliabilityAsync();
    }
    partial void OnDispatchSmsNumberChanged(string value)
    {
        AppSettings.DispatchSmsNumber = value?.Trim() ?? "";
        _ = RefreshReliabilityAsync();
    }
    partial void OnAutoCallEnabledChanged(bool value)
    {
        AppSettings.AutoCallEnabled = value;
        _ = value ? RequestCallPermissionAsync() : RefreshReliabilityAsync();
    }
    partial void OnLowBatteryEnabledChanged(bool value)
    {
        AppSettings.LowBatteryModeEnabled = value;
        _power.Refresh();
    }
    partial void OnServerUrlChanged(string value) => ShowHttpWarning = UrlSecurity.IsPlainHttpOnPublicHost(value);

    public ProfileViewModel(DatabaseService db, SessionService session, TriggerService triggers,
                            TransmissionService transmission, PowerService power, ISmsSender sms, IPhoneCaller caller)
    {
        (_db, _session, _triggers) = (db, session, triggers);
        (_transmission, _power, _sms, _caller) = (transmission, power, sms, caller);
    }

    public async Task LoadAsync()
    {
        var u = _session.CurrentUser;
        if (u is null) return;
        FullName = u.FullName; Phone = u.Phone; Email = u.Email;
        BloodType = u.BloodType; Allergies = u.Allergies; Conditions = u.Conditions;
        ServerUrl = ApiService.BaseUrl;

        Contacts.Clear();
        foreach (var c in await _db.GetContactsAsync(u.Id)) Contacts.Add(c);

        await RefreshReliabilityAsync();
    }

    private async Task RequestSmsPermissionAsync()
    {
        try
        {
            if (!_sms.IsSupported)
                await Shell.Current.DisplayAlertAsync("SMS not available",
                    "This device cannot send SMS messages, so the SMS fallback cannot be used.", "OK");
            else if (!await _sms.RequestPermissionAsync())
                await Shell.Current.DisplayAlertAsync("Permission needed",
                    "Allow the SMS permission so the app can text your location to dispatch when there is no internet.", "OK");
        }
        catch { /* prompt dismissed */ }
        await RefreshReliabilityAsync();
    }

    private async Task RequestCallPermissionAsync()
    {
        try
        {
            if (!await _caller.RequestPermissionAsync())
                await Shell.Current.DisplayAlertAsync("Phone permission not granted",
                    "Without it the app will open the dialer with the number filled in, and you press call.", "OK");
        }
        catch { /* prompt dismissed */ }
        await RefreshReliabilityAsync();
    }

    /// Plain-language summary of which fallbacks will actually work.
    private async Task RefreshReliabilityAsync()
    {
        await _transmission.RefreshReadinessAsync();
        var number = AppSettings.EmergencyNumber;

        string sms =
            !AppSettings.SmsFallbackEnabled ? "SMS fallback: off"
            : string.IsNullOrWhiteSpace(AppSettings.DispatchSmsNumber) ? "SMS fallback: enter the dispatch SMS number"
            : !_sms.IsSupported ? "SMS fallback: this device cannot send SMS"
            : !_transmission.SmsReady ? "SMS fallback: SMS permission needed (switch it off and on to allow)"
            : $"SMS fallback: ready, will text {AppSettings.DispatchSmsNumber}";

        string call =
            !AppSettings.AutoCallEnabled ? "Auto-call: off (the call button is still on the SOS screen)"
            : _transmission.CallPermissionGranted ? $"Auto-call: ready, will dial {number} after a 10-second countdown"
            : $"Auto-call: will open the dialer for {number} (allow Phone permission to dial directly)";

        ReliabilityStatus = sms + "\n" + call;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var u = _session.CurrentUser;
        if (u is null) return;
        u.FullName = FullName.Trim(); u.Phone = Phone.Trim();
        u.BloodType = BloodType.Trim(); u.Allergies = Allergies.Trim(); u.Conditions = Conditions.Trim();
        await _db.SaveUserAsync(u);

        if (!string.IsNullOrWhiteSpace(ServerUrl)) ApiService.BaseUrl = ServerUrl.Trim();
        if (!string.IsNullOrWhiteSpace(EmergencyNumber)) AppSettings.EmergencyNumber = EmergencyNumber.Trim();
        AppSettings.DispatchSmsNumber = DispatchSmsNumber?.Trim() ?? "";
        await RefreshReliabilityAsync();

        await Shell.Current.DisplayAlertAsync("Saved", "Profile saved on this device.", "OK");
    }

    [RelayCommand]
    private async Task AddContactAsync()
    {
        var u = _session.CurrentUser;
        if (u is null) return;
        var name = await Shell.Current.DisplayPromptAsync("Emergency contact", "Name");
        if (string.IsNullOrWhiteSpace(name)) return;
        var phone = await Shell.Current.DisplayPromptAsync("Emergency contact", "Phone number", keyboard: Keyboard.Telephone);
        if (string.IsNullOrWhiteSpace(phone)) return;

        var c = new EmergencyContact { UserId = u.Id, Name = name.Trim(), Phone = phone.Trim() };
        await _db.SaveContactAsync(c);
        Contacts.Add(c);
    }

    [RelayCommand]
    private async Task DeleteContactAsync(EmergencyContact c)
    {
        await _db.DeleteContactAsync(c);
        Contacts.Remove(c);
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        if (!await Shell.Current.DisplayAlertAsync("Log out", "Log out of this account?", "Log out", "Cancel")) return;
        _session.SignOut();
        await Shell.Current.GoToAsync("//login");
    }
}
