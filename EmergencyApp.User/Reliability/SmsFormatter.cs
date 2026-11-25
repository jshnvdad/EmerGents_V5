using System.Globalization;
using System.Text;

namespace EmergencyApp.User.Reliability;

/// Builds the compact fallback SMS: type + people + who + a maps link. Always ASCII and well under
/// 160 characters, so it travels as ONE plain SMS even on a weak signal.
public static class SmsFormatter
{
    public const int MaxLength = 160;
    private const string Allowed = " +-.,:/?=&@#%()!_*";

    public static string Build(string type, int people, string name, string phone,
                               double? lat, double? lng, string clientId, DateTime createdUtc)
    {
        var inv = CultureInfo.InvariantCulture;
        var t = Truncate(Clean(string.IsNullOrWhiteSpace(type) ? "General" : type), 10).ToUpperInvariant();
        var p = people >= 4 ? "4+" : Math.Max(people, 1).ToString(inv);
        var who = Truncate(Clean(name), 24);
        var ph = Truncate(Clean(phone), 16);
        var loc = lat is double a && lng is double b
            ? $"https://maps.google.com/?q={a.ToString("F5", inv)},{b.ToString("F5", inv)}"
            : "LOCATION UNKNOWN";
        var id = new string(clientId.Where(char.IsAsciiLetterOrDigit).Take(8).ToArray()).ToUpperInvariant();
        var utc = createdUtc.Kind == DateTimeKind.Local ? createdUtc.ToUniversalTime() : createdUtc;

        var sms = $"SOS {t} {p}P - {who} {ph} - {loc} - ref {id} - {utc.ToString("HH:mm", inv)}Z";
        sms = string.Join(' ', sms.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return sms.Length <= MaxLength ? sms : sms[..MaxLength];
    }

    /// Strips accents and anything outside plain ASCII (those would force a 2-part or Unicode SMS).
    public static string Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder();
        foreach (var c in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsAsciiLetterOrDigit(c) || Allowed.Contains(c) ? c : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd();
}
