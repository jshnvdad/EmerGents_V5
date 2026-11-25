using System.Net;
using System.Net.Mail;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmergencyDesk.Services;

public enum ReportFrequency { Daily, Weekly }

/// <summary>Encrypts a secret (the SMTP password) so it is not stored as plain text. Windows: see DpapiSecretProtector.</summary>
public interface ISecretProtector
{
    string Protect(string plain);
    string Unprotect(string protectedText);
}

/// <summary>Settings for the automatic daily/weekly summary e-mail. Stored in reports.json next to rules.json.</summary>
public class ReportSchedule
{
    public bool Enabled { get; set; }
    public ReportFrequency Frequency { get; set; } = ReportFrequency.Daily;
    public DayOfWeek WeeklyDay { get; set; } = DayOfWeek.Monday;
    public int SendHour { get; set; } = 7;
    public int SendMinute { get; set; }
    public string Recipients { get; set; } = "";
    public bool AttachPdf { get; set; } = true;

    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseSsl { get; set; } = true;
    public string SmtpUser { get; set; } = "";
    public string FromAddress { get; set; } = "";
    /// <summary>Encrypted with ISecretProtector (Windows DPAPI, this user only). Never the plain password.</summary>
    public string SmtpPasswordProtected { get; set; } = "";

    /// <summary>Desk-local time of the last successful scheduled send.</summary>
    public DateTime? LastSentLocal { get; set; }
    public string LastResult { get; set; } = "";

    /// <summary>How far back a scheduled report looks.</summary>
    [JsonIgnore] public TimeSpan Window => Frequency == ReportFrequency.Weekly ? TimeSpan.FromDays(7) : TimeSpan.FromHours(24);

    public ReportSchedule Clone() => JsonSerializer.Deserialize<ReportSchedule>(JsonSerializer.Serialize(this))!;

    public void Normalize()
    {
        SendHour = Math.Clamp(SendHour, 0, 23);
        SendMinute = Math.Clamp(SendMinute, 0, 59);
        SmtpPort = Math.Clamp(SmtpPort, 1, 65535);
        Recipients = (Recipients ?? "").Trim();
        SmtpHost = (SmtpHost ?? "").Trim();
        SmtpUser = (SmtpUser ?? "").Trim();
        FromAddress = (FromAddress ?? "").Trim();
        SmtpPasswordProtected ??= "";
        LastResult ??= "";
    }

    public static List<string> SplitRecipients(string? text) =>
        (text ?? "").Split(new[] { ',', ';', ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Returns "" when the settings are good enough to send mail, otherwise a message for the operator.</summary>
    public string ValidateForSending()
    {
        if (SmtpHost.Length == 0) return "Enter the SMTP server (for example smtp.gmail.com).";
        if (!MailAddress.TryCreate(FromAddress, out _)) return "Enter a valid 'From' e-mail address.";
        var to = SplitRecipients(Recipients);
        if (to.Count == 0) return "Enter at least one recipient e-mail address.";
        var bad = to.FirstOrDefault(r => !MailAddress.TryCreate(r, out _));
        return bad is null ? "" : $"'{bad}' is not a valid e-mail address.";
    }

    /// <summary>The most recent moment this schedule should have fired, at or before <paramref name="nowLocal"/>.</summary>
    public DateTime LatestScheduledTime(DateTime nowLocal)
    {
        var timeOfDay = new TimeSpan(SendHour, SendMinute, 0);
        if (Frequency == ReportFrequency.Daily)
        {
            var today = nowLocal.Date + timeOfDay;
            return today <= nowLocal ? today : today.AddDays(-1);
        }

        var daysBack = ((int)nowLocal.DayOfWeek - (int)WeeklyDay + 7) % 7;
        var candidate = nowLocal.Date.AddDays(-daysBack) + timeOfDay;
        return candidate <= nowLocal ? candidate : candidate.AddDays(-7);
    }

    /// <summary>The next time the schedule will fire after <paramref name="nowLocal"/>.</summary>
    public DateTime NextScheduledTime(DateTime nowLocal) =>
        LatestScheduledTime(nowLocal).Add(Frequency == ReportFrequency.Weekly ? TimeSpan.FromDays(7) : TimeSpan.FromDays(1));

    /// <summary>True when a scheduled time has passed since the last successful send (also catches up after downtime).</summary>
    public bool IsDue(DateTime nowLocal)
    {
        if (!Enabled || SplitRecipients(Recipients).Count == 0) return false;
        return LastSentLocal is null || LastSentLocal.Value < LatestScheduledTime(nowLocal);
    }
}

/// <summary>Sends the report over SMTP. SmtpClient supports STARTTLS (port 587); it does not support implicit SSL on port 465.</summary>
public static class ReportMailer
{
    public static async Task SendAsync(ReportSchedule s, string password, string subject, string body,
                                       byte[]? attachment, string attachmentName)
    {
        using var msg = new MailMessage { From = new MailAddress(s.FromAddress), Subject = subject, Body = body, IsBodyHtml = false };
        foreach (var r in ReportSchedule.SplitRecipients(s.Recipients)) msg.To.Add(r);

        using var data = attachment is null ? null : new MemoryStream(attachment);
        if (data is not null) msg.Attachments.Add(new Attachment(data, attachmentName, "application/pdf"));

        using var client = new SmtpClient(s.SmtpHost, s.SmtpPort)
        {
            EnableSsl = s.SmtpUseSsl,
            Timeout = 25000,
            DeliveryMethod = SmtpDeliveryMethod.Network
        };
        if (!string.IsNullOrWhiteSpace(s.SmtpUser))
            client.Credentials = new NetworkCredential(s.SmtpUser, password);
        else
            client.UseDefaultCredentials = false;

        await client.SendMailAsync(msg);
    }
}
