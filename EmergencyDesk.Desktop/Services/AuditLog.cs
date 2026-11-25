using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmergencyDesk.Services;

/// <summary>Names of the actions written to the audit log (kept as strings so old entries stay readable).</summary>
public static class AuditActions
{
    public const string Login = "Login";
    public const string LoginFailed = "LoginFailed";
    public const string Logout = "Logout";
    public const string SessionExpired = "SessionExpired";

    public const string ViewIncident = "ViewIncident";
    public const string AcknowledgeIncident = "AcknowledgeIncident";
    public const string AssignResponder = "AssignResponder";
    public const string AutoAssign = "AutoAssign";
    public const string UpdateStatus = "UpdateStatus";
    public const string MergeDuplicates = "MergeDuplicates";

    public const string SaveResponder = "SaveResponder";
    public const string ChangeRules = "ChangeRules";
    public const string ToggleUser = "ToggleUser";

    public const string ViewUsers = "ViewUsers";
    public const string ViewOperators = "ViewOperators";
    public const string ViewAuditLog = "ViewAuditLog";
    public const string VerifyAuditLog = "VerifyAuditLog";
    public const string ExportAuditLog = "ExportAuditLog";

    public const string ExportReport = "ExportReport";
    public const string ChangeReportSettings = "ChangeReportSettings";
    public const string SendReport = "SendReport";
}

/// <summary>One line of the audit log. Each entry carries the hash of the entry before it (a hash chain).</summary>
public class AuditEntry
{
    public long Seq { get; set; }
    public DateTime TimeUtc { get; set; }
    public string Operator { get; set; } = "";
    public string Role { get; set; } = "";
    public string Action { get; set; } = "";
    public string TargetType { get; set; } = "";
    public string TargetId { get; set; } = "";
    public string Details { get; set; } = "";
    public string PrevHash { get; set; } = "";
    public string Hash { get; set; } = "";

    [JsonIgnore] public string TimeLocal => TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    [JsonIgnore] public string Target =>
        TargetType.Length == 0 ? "" : TargetId.Length == 0 ? TargetType : $"{TargetType} {TargetId}";
}

public record AuditVerifyResult(bool Ok, long Entries, string Message);

/// <summary>
/// Append-only audit trail: who did / viewed what, and when.
///
/// Entries are written one per line (JSON Lines) to %LocalAppData%\EmergencyDesk\audit.jsonl and are never
/// edited or deleted by the app. Every entry stores a SHA-256 hash that covers its own content plus the hash
/// of the previous entry, so changing, removing or re-ordering any old line breaks the chain and
/// <see cref="Verify"/> reports where. This makes tampering *detectable* on this desk; it cannot stop someone
/// with access to the file from deleting the whole file or the newest lines. For a stronger guarantee the
/// entries must also be shipped to the server.
/// </summary>
public class AuditLog
{
    private const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";
    private const int MaxDetails = 600;

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = false };

    private readonly string _path;
    private readonly Func<(string Name, string Role)> _actor;
    private readonly object _lock = new();
    private long _lastSeq;
    private string _lastHash = Genesis;

    /// <summary>Last write/read problem, or null when everything is fine.</summary>
    public string? LastError { get; private set; }
    public string FilePath => _path;
    public long EntryCount => _lastSeq;

    /// <param name="directory">Folder that holds audit.jsonl.</param>
    /// <param name="actor">Returns the signed-in operator (display name, role) at the moment of the call.</param>
    public AuditLog(string directory, Func<(string Name, string Role)> actor)
    {
        _path = Path.Combine(directory, "audit.jsonl");
        _actor = actor;
        LoadTail();
    }

    private void LoadTail()
    {
        try
        {
            if (!File.Exists(_path)) return;
            string? last = null;
            foreach (var line in File.ReadLines(_path))
                if (!string.IsNullOrWhiteSpace(line)) last = line;
            if (last is null) return;

            var e = JsonSerializer.Deserialize<AuditEntry>(last, Opts);
            if (e is null) return;
            _lastSeq = e.Seq;
            _lastHash = e.Hash;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    /// <summary>Writes one entry. Returns false (and sets <see cref="LastError"/>) if it could not be saved.</summary>
    public bool Write(string action, string targetType = "", string targetId = "", string details = "",
                      string? op = null, string? role = null)
    {
        lock (_lock)
        {
            try
            {
                var (name, sessionRole) = _actor();
                var e = new AuditEntry
                {
                    Seq = _lastSeq + 1,
                    TimeUtc = DateTime.UtcNow,
                    Operator = Clean(string.IsNullOrWhiteSpace(op) ? (string.IsNullOrWhiteSpace(name) ? "System" : name) : op, 80),
                    Role = Clean(role ?? sessionRole ?? "", 30),
                    Action = action,
                    TargetType = targetType,
                    TargetId = targetId,
                    Details = Clean(details, MaxDetails),
                    PrevHash = _lastHash
                };
                e.Hash = ComputeHash(e);

                var line = JsonSerializer.Serialize(e, Opts) + "\n";
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                using (var fs = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
                {
                    fs.Seek(0, SeekOrigin.End);
                    // If a crash left a half-written last line, finish it so the new entry starts on its own line.
                    if (fs.Length > 0)
                    {
                        fs.Seek(-1, SeekOrigin.End);
                        if (fs.ReadByte() != '\n') fs.WriteByte((byte)'\n');
                    }
                    var bytes = new UTF8Encoding(false).GetBytes(line);
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(true);
                }

                _lastSeq = e.Seq;
                _lastHash = e.Hash;
                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }
    }

    private static string Clean(string? s, int max)
    {
        s ??= "";
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }

    public static string ComputeHash(AuditEntry e)
    {
        var text = string.Join("\u001f",
            e.Seq.ToString(CultureInfo.InvariantCulture),
            e.TimeUtc.Ticks.ToString(CultureInfo.InvariantCulture),
            e.Operator, e.Role, e.Action, e.TargetType, e.TargetId, e.Details, e.PrevHash);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private static AuditEntry? Parse(string line)
    {
        try { return JsonSerializer.Deserialize<AuditEntry>(line, Opts); }
        catch { return null; }
    }

    /// <summary>The newest <paramref name="limit"/> entries, newest first.</summary>
    public List<AuditEntry> ReadRecent(int limit)
    {
        lock (_lock)
        {
            var all = new List<AuditEntry>();
            try
            {
                if (!File.Exists(_path)) return all;
                foreach (var line in File.ReadLines(_path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var e = Parse(line);
                    if (e is not null) all.Add(e);
                }
            }
            catch (Exception ex) { LastError = ex.Message; }

            all.Reverse();
            return all.Count > limit ? all.GetRange(0, limit) : all;
        }
    }

    /// <summary>Every entry about one target (e.g. "Incident", "12"), oldest first.</summary>
    public List<AuditEntry> ForTarget(string targetType, string targetId)
    {
        var list = ReadRecent(int.MaxValue);
        list.Reverse();
        return list.Where(e => e.TargetType == targetType && e.TargetId == targetId).ToList();
    }

    /// <summary>Walks the whole file and checks numbering, links and hashes.</summary>
    public AuditVerifyResult Verify()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_path)) return new(true, 0, "The audit log is empty.");

                long expected = 1, count = 0;
                var prev = Genesis;
                var lineNo = 0;
                foreach (var line in File.ReadLines(_path))
                {
                    lineNo++;
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var e = Parse(line);
                    if (e is null) return new(false, count, $"Line {lineNo} is not a valid entry.");
                    if (e.Seq != expected)
                        return new(false, count, $"Entry #{expected} is missing or out of order (found #{e.Seq} on line {lineNo}).");
                    if (e.PrevHash != prev)
                        return new(false, count, $"Entry #{e.Seq} does not link to the entry before it.");
                    if (!string.Equals(ComputeHash(e), e.Hash, StringComparison.Ordinal))
                        return new(false, count, $"Entry #{e.Seq} has been modified.");

                    prev = e.Hash;
                    expected++;
                    count++;
                }
                return new(true, count, $"Chain intact: {count} entries checked, none altered.");
            }
            catch (Exception ex)
            {
                return new(false, 0, "Could not read the audit log: " + ex.Message);
            }
        }
    }
}

/// <summary>Turns "before" and "after" copies of a settings object into "Name: old -> new; ..." for the audit log.</summary>
public static class ObjectDiff
{
    public static string Describe<T>(T before, T after, params string[] ignore)
    {
        var parts = new List<string>();
        foreach (var p in typeof(T).GetProperties())
        {
            if (!p.CanRead || p.GetIndexParameters().Length > 0 || ignore.Contains(p.Name)) continue;
            var a = p.GetValue(before);
            var b = p.GetValue(after);
            if (!Equals(a, b)) parts.Add($"{p.Name}: {Show(a)} -> {Show(b)}");
        }
        return string.Join("; ", parts);
    }

    private static string Show(object? v) => v switch
    {
        null => "(none)",
        bool b => b ? "on" : "off",
        string s => s.Length == 0 ? "(empty)" : s,
        _ => v.ToString() ?? ""
    };
}
