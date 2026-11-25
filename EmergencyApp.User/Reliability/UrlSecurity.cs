namespace EmergencyApp.User.Reliability;

public static class UrlSecurity
{
    /// True when the URL is plain http:// to a host that is not on a private/local network.
    /// (http:// to 10.0.2.2 / localhost / a LAN address is fine while developing.)
    public static bool IsPlainHttpOnPublicHost(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttp) return false;

        var h = u.Host.ToLowerInvariant();
        if (h is "localhost" || h.EndsWith(".local")) return false;
        if (!System.Net.IPAddress.TryParse(h, out var ip)) return true;   // a real domain name over http
        var b = ip.GetAddressBytes();
        if (b.Length != 4) return !System.Net.IPAddress.IsLoopback(ip);
        return !(b[0] == 10 || b[0] == 127 || (b[0] == 192 && b[1] == 168) ||
                 (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 169 && b[1] == 254));
    }
}
