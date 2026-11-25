namespace EmergencyApp.User.Services;

public class LocationService
{
    public Location? Last { get; private set; }

    /// Asks for permission if needed. Returns null if denied or no fix. Call from the UI thread.
    public async Task<Location?> GetCurrentAsync()
    {
        var l = await CurrentCoreAsync();
        if (l is not null) Last = l;
        return l;
    }

    private async Task<Location?> CurrentCoreAsync()
    {
        var status = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
        if (status != PermissionStatus.Granted)
            status = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
        if (status != PermissionStatus.Granted) return null;

        try
        {
            var fresh = await Geolocation.Default.GetLocationAsync(
                new GeolocationRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(10)));
            return fresh ?? await Geolocation.Default.GetLastKnownLocationAsync();
        }
        catch
        {
            try { return await Geolocation.Default.GetLastKnownLocationAsync(); } catch { return null; }
        }
    }

    /// Never shows a permission prompt. Used for background updates.
    public async Task<Location?> GetQuietAsync(GeolocationAccuracy accuracy = GeolocationAccuracy.Medium)
    {
        var l = await QuietCoreAsync(accuracy);
        if (l is not null) Last = l;
        return l;
    }

    private Task<Location?> QuietCoreAsync(GeolocationAccuracy accuracy) => MainThread.InvokeOnMainThreadAsync<Location?>(async () =>
    {
        try
        {
            var status = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
            if (status != PermissionStatus.Granted) return null;
            var loc = await Geolocation.Default.GetLocationAsync(
                new GeolocationRequest(accuracy, TimeSpan.FromSeconds(6)));
            return loc ?? await Geolocation.Default.GetLastKnownLocationAsync();
        }
        catch { return null; }
    });
}
