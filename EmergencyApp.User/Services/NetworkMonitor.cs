namespace EmergencyApp.User.Services;

public enum NetworkState { Offline, Limited, Online }

/// Network state monitor: watches connectivity and tells the rest of the app when it changes,
/// so the delivery engine can retry the moment a connection returns and switch channels (internet / SMS).
public class NetworkMonitor
{
    private int _version;

    public NetworkState State { get; private set; } = NetworkState.Online;
    public string Transport { get; private set; } = "";

    /// Goes up on every change. Loops compare it to wake up early instead of waiting out a delay.
    public int Version => Volatile.Read(ref _version);

    public event Action? Changed;

    public NetworkMonitor()
    {
        Refresh();
        try { Connectivity.Current.ConnectivityChanged += OnConnectivityChanged; } catch { }
    }

    private void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
    {
        Refresh();
        Interlocked.Increment(ref _version);
        Changed?.Invoke();
    }

    public void Refresh()
    {
        try
        {
            var access = Connectivity.Current.NetworkAccess;
            State = access switch
            {
                NetworkAccess.None => NetworkState.Offline,
                NetworkAccess.Local => NetworkState.Limited,
                NetworkAccess.ConstrainedInternet => NetworkState.Limited,
                _ => NetworkState.Online        // Internet, or Unknown (let the request itself decide)
            };

            var profiles = Connectivity.Current.ConnectionProfiles;
            Transport = profiles.Contains(ConnectionProfile.WiFi) ? "Wi-Fi"
                      : profiles.Contains(ConnectionProfile.Cellular) ? "Mobile data"
                      : profiles.Contains(ConnectionProfile.Ethernet) ? "Ethernet" : "";
        }
        catch { State = NetworkState.Online; Transport = ""; }
    }
}
