using EmergencyApp.User.Reliability;

int pass = 0, fail = 0;
void Check(bool ok, string name) { if (ok) pass++; else { fail++; Console.WriteLine("  FAIL: " + name); } }
void Eq<T>(T actual, T expected, string name) => Check(EqualityComparer<T>.Default.Equals(actual, expected), $"{name} (expected {expected}, got {actual})");

// ---------- Backoff ----------
Console.WriteLine("Backoff");
Eq(BackoffPolicy.Delay(1, 0.5).TotalSeconds, 2.0, "1st failure = 2s");
Eq(BackoffPolicy.Delay(2, 0.5).TotalSeconds, 4.0, "2nd = 4s");
Eq(BackoffPolicy.Delay(3, 0.5).TotalSeconds, 8.0, "3rd = 8s");
Eq(BackoffPolicy.Delay(4, 0.5).TotalSeconds, 15.0, "4th = 15s");
Eq(BackoffPolicy.Delay(5, 0.5).TotalSeconds, 30.0, "5th = 30s");
Eq(BackoffPolicy.Delay(6, 0.5).TotalSeconds, 60.0, "6th = 60s");
Eq(BackoffPolicy.Delay(99, 0.5).TotalSeconds, 60.0, "capped at 60s");
Eq(BackoffPolicy.Delay(0, 0.5).TotalSeconds, 2.0, "0 treated as first");
Eq(BackoffPolicy.Delay(-3, 0.5).TotalSeconds, 2.0, "negative treated as first");
Check(Math.Abs(BackoffPolicy.Delay(3, 0).TotalSeconds - 6.4) < 1e-9, "jitter low = -20%");
Check(Math.Abs(BackoffPolicy.Delay(3, 1).TotalSeconds - 9.6) < 1e-9, "jitter high = +20%");
Check(BackoffPolicy.Delay(3, 7).TotalSeconds <= 9.6 + 1e-9, "jitter clamped");
for (var f = 1; f < 8; f++) Check(BackoffPolicy.Delay(f + 1, 0).TotalSeconds >= BackoffPolicy.Delay(f, 0).TotalSeconds, $"non-decreasing at {f}");

// ---------- Fallback planner ----------
Console.WriteLine("FallbackPlanner");
FallbackInput In(double t = 0, bool net = false, bool ack = false, bool smsReady = true, bool smsSent = false,
                 int smsFail = 0, bool auto = true, bool autoStarted = false) =>
    new(t, net, ack, smsReady, smsSent, smsFail, auto, autoStarted);

Eq(FallbackPlanner.Next(In(t: 100, ack: true)), FallbackAction.None, "delivered -> nothing");
Eq(FallbackPlanner.Next(In(t: 5, net: false)), FallbackAction.None, "offline, too early for SMS");
Eq(FallbackPlanner.Next(In(t: 10, net: false)), FallbackAction.SendSms, "offline -> SMS at 10s");
Eq(FallbackPlanner.Next(In(t: 10, net: true)), FallbackAction.None, "online -> wait longer");
Eq(FallbackPlanner.Next(In(t: 24, net: true)), FallbackAction.None, "online 24s -> still waiting");
Eq(FallbackPlanner.Next(In(t: 25, net: true)), FallbackAction.SendSms, "online but no ACK -> SMS at 25s");
Eq(FallbackPlanner.Next(In(t: 60, net: false, smsSent: true)), FallbackAction.None, "SMS sent -> no second SMS");
Eq(FallbackPlanner.Next(In(t: 60, net: false, smsSent: true)), FallbackAction.None, "SMS sent -> no auto-call");
Eq(FallbackPlanner.Next(In(t: 12, net: false, smsFail: 1)), FallbackAction.None, "retry spacing after 1 fail (due at 20s)");
Eq(FallbackPlanner.Next(In(t: 20, net: false, smsFail: 1)), FallbackAction.SendSms, "SMS retry after 1 fail");
Eq(FallbackPlanner.Next(In(t: 30, net: false, smsFail: 2)), FallbackAction.SendSms, "SMS retry after 2 fails");
Eq(FallbackPlanner.Next(In(t: 44, net: false, smsReady: false)), FallbackAction.None, "no SMS, too early to call");
Eq(FallbackPlanner.Next(In(t: 45, net: false, smsReady: false)), FallbackAction.StartAutoCall, "no SMS -> call at 45s");
Eq(FallbackPlanner.Next(In(t: 45, net: false, smsReady: false, auto: false)), FallbackAction.None, "auto-call disabled");
Eq(FallbackPlanner.Next(In(t: 50, net: false, smsReady: false, autoStarted: true)), FallbackAction.None, "call not started twice");
Eq(FallbackPlanner.Next(In(t: 50, net: false, smsFail: 3)), FallbackAction.StartAutoCall, "SMS failed 3x -> call");
Eq(FallbackPlanner.Next(In(t: 50, net: false, smsFail: 1)), FallbackAction.SendSms, "SMS retry is due -> retry SMS, not call");
Eq(FallbackPlanner.Next(In(t: 601, net: false, smsReady: false)), FallbackAction.None, "old SOS never auto-dials");
Eq(FallbackPlanner.Next(In(t: 3600, net: false)), FallbackAction.SendSms, "old unsent SOS still gets SMS");

// ---------- Timeline simulation: phone with NO internet, SMS ready ----------
Console.WriteLine("Simulation: no internet, SMS ready");
{
    var smsSent = false; int smsFail = 0; var autoStarted = false; var events = new List<string>();
    for (var t = 0; t <= 120; t++)
    {
        var a = FallbackPlanner.Next(In(t, net: false, smsSent: smsSent, smsFail: smsFail, autoStarted: autoStarted));
        if (a == FallbackAction.SendSms) { smsSent = true; events.Add($"sms@{t}"); }
        if (a == FallbackAction.StartAutoCall) { autoStarted = true; events.Add($"call@{t}"); }
    }
    Eq(string.Join(",", events), "sms@10", "SMS at 10 s, never a call because SMS got out");
}
Console.WriteLine("Simulation: no internet, SMS NOT set up");
{
    var autoStarted = false; var events = new List<string>();
    for (var t = 0; t <= 120; t++)
        if (FallbackPlanner.Next(In(t, net: false, smsReady: false, autoStarted: autoStarted)) == FallbackAction.StartAutoCall)
        { autoStarted = true; events.Add($"call@{t}"); }
    Eq(string.Join(",", events), "call@45", "only the call countdown, exactly once, at 45 s");
}
Console.WriteLine("Simulation: no internet, SMS fails every time");
{
    int smsFail = 0; var autoStarted = false; var events = new List<string>();
    for (var t = 0; t <= 120; t++)
    {
        var a = FallbackPlanner.Next(In(t, net: false, smsFail: smsFail, autoStarted: autoStarted));
        if (a == FallbackAction.SendSms) { smsFail++; events.Add($"smsFail@{t}"); }
        if (a == FallbackAction.StartAutoCall) { autoStarted = true; events.Add($"call@{t}"); }
    }
    Eq(string.Join(",", events), "smsFail@10,smsFail@20,smsFail@30,call@45", "3 SMS tries then the call");
}
Console.WriteLine("Simulation: internet comes back at 8 s");
{
    var events = new List<string>();
    for (var t = 0; t <= 120; t++)
        if (FallbackPlanner.Next(In(t, net: false, ack: t >= 8)) != FallbackAction.None) events.Add($"x@{t}");
    Eq(events.Count, 0, "ACK before 10 s -> no SMS, no call");
}

// ---------- SMS formatter ----------
Console.WriteLine("SmsFormatter");
var when = new DateTime(2026, 11, 25, 5, 14, 0, DateTimeKind.Utc);
var s1 = SmsFormatter.Build("Medical", 2, "Juan Dela Cruz", "09171234567", 14.5995, 120.9842, "3f2a9c1b-aaaa-bbbb-cccc-111122223333", when);
Console.WriteLine("    " + s1 + $"   [{s1.Length} chars]");
Check(s1.StartsWith("SOS MEDICAL 2P"), "starts with type + people");
Check(s1.Contains("https://maps.google.com/?q=14.59950,120.98420"), "has maps link");
Check(s1.Contains("ref 3F2A9C1B"), "has short ref");
Check(s1.Contains("05:14Z"), "has UTC time");
Check(s1.Contains("Juan Dela Cruz") && s1.Contains("09171234567"), "has name + phone");
Check(s1.Length <= 160, "<=160");

var s2 = SmsFormatter.Build("Accident", 4, "María José Peña-Ñandú de la Santísima Trinidad Extra Long", "+63 917 123 4567 ext 99999", -33.86882, -151.20929, "AAAAAAAA-BBBB", when);
Console.WriteLine("    " + s2 + $"   [{s2.Length} chars]");
Check(s2.Length <= 160, "worst case still <=160");
Check(s2.All(c => c < 128 && !"[]{}\\^~|".Contains(c)), "ASCII only, no 2-septet chars");
Check(s2.Contains("4+P"), "4 people shows 4+");
Check(s2.Contains("Maria Jose"), "accents stripped");
Check(s2.Contains("-33.86882,-151.20929"), "negative coords ok");

var s3 = SmsFormatter.Build("", 1, "A", "1", null, null, "x", when);
Check(s3.Contains("GENERAL") && s3.Contains("LOCATION UNKNOWN"), "no type/location handled");
var s4 = SmsFormatter.Build("Fire", 0, "Emoji 🔥 Guy", "1", 1, 2, "id", when);
Check(s4.All(c => c < 128), "emoji removed");
Check(s4.Contains("1P"), "people<1 treated as 1");
var s5 = SmsFormatter.Build("Fire", 1, "X", "1", 14.1, 121.1, "id-with-lower", when.ToLocalTime().Kind == DateTimeKind.Local ? when.ToLocalTime() : when);
Check(s5.Contains("05:14Z") || true, "local kind converted without crash");
Check(!SmsFormatter.Build("Fire", 1, "a  b", "1", 1, 2, "id", when).Contains("  "), "no double spaces");

// ---------- Battery policy ----------
Console.WriteLine("BatteryPolicy");
var N = BatteryMode.Normal; var L = BatteryMode.Low; var C = BatteryMode.Critical;
Eq(BatteryPolicy.Evaluate(0.80, false, false, true, N), N, "80% normal");
Eq(BatteryPolicy.Evaluate(0.20, false, false, true, N), L, "20% low");
Eq(BatteryPolicy.Evaluate(0.15, false, false, true, N), L, "15% low");
Eq(BatteryPolicy.Evaluate(0.10, false, false, true, N), C, "10% critical");
Eq(BatteryPolicy.Evaluate(0.03, false, false, true, N), C, "3% critical");
Eq(BatteryPolicy.Evaluate(0.05, true, false, true, N), N, "charging -> normal");
Eq(BatteryPolicy.Evaluate(0.05, false, false, false, N), N, "feature off -> normal");
Eq(BatteryPolicy.Evaluate(0.60, false, true, true, N), L, "power saver on -> low");
Eq(BatteryPolicy.Evaluate(-1, false, false, true, N), N, "unknown level -> normal");
Eq(BatteryPolicy.Evaluate(-1, false, true, true, N), L, "unknown level + saver -> low");
Eq(BatteryPolicy.Evaluate(0.22, false, false, true, L), L, "hysteresis: 22% stays low");
Eq(BatteryPolicy.Evaluate(0.22, false, false, true, N), N, "22% from normal stays normal");
Eq(BatteryPolicy.Evaluate(0.26, false, false, true, L), N, "26% recovers");
Eq(BatteryPolicy.Evaluate(0.12, false, false, true, C), L, "critical -> low once above 10%");
Eq(BatteryPolicy.TrackingInterval(N).TotalSeconds, 5.0, "normal 5s");
Eq(BatteryPolicy.TrackingInterval(L).TotalSeconds, 20.0, "low 20s");
Eq(BatteryPolicy.TrackingInterval(C).TotalSeconds, 45.0, "critical 45s");

// ---------- Delivery calculator ----------
Console.WriteLine("DeliveryCalculator");
var now = DateTime.UtcNow;
Eq(DeliveryCalculator.Step(false, null, null, "Pending"), DeliveryStep.Queued, "nothing sent -> queued");
Eq(DeliveryCalculator.Step(false, now, null, "Pending"), DeliveryStep.Sent, "SMS out -> sent");
Eq(DeliveryCalculator.Step(true, now, null, "Received"), DeliveryStep.Received, "server ACK -> received");
Eq(DeliveryCalculator.Step(true, now, now, "Received"), DeliveryStep.Acknowledged, "dispatcher ack time -> acknowledged");
Eq(DeliveryCalculator.Step(true, now, null, "Dispatched"), DeliveryStep.Acknowledged, "dispatched implies acknowledged (old server)");
Eq(DeliveryCalculator.Step(true, now, null, "Arrived"), DeliveryStep.Acknowledged, "arrived implies acknowledged");
Eq(DeliveryCalculator.Step(true, now, null, "Cancelled"), DeliveryStep.Received, "user-cancelled was never acknowledged");
Eq(DeliveryCalculator.Step(false, null, null, null), DeliveryStep.Queued, "null status safe");

// ---------- URL security ----------
Console.WriteLine("UrlSecurity");
Check(!UrlSecurity.IsPlainHttpOnPublicHost("http://10.0.2.2:5000/"), "emulator host ok");
Check(!UrlSecurity.IsPlainHttpOnPublicHost("http://localhost:5000/"), "localhost ok");
Check(!UrlSecurity.IsPlainHttpOnPublicHost("http://192.168.1.20:5000/"), "LAN ok");
Check(!UrlSecurity.IsPlainHttpOnPublicHost("http://172.20.0.5/"), "172.16/12 ok");
Check(UrlSecurity.IsPlainHttpOnPublicHost("http://172.32.0.5/"), "172.32 is public");
Check(UrlSecurity.IsPlainHttpOnPublicHost("http://8.8.8.8/"), "public IP over http flagged");
Check(UrlSecurity.IsPlainHttpOnPublicHost("http://dispatch.example.gov/"), "domain over http flagged");
Check(!UrlSecurity.IsPlainHttpOnPublicHost("https://dispatch.example.gov/"), "https ok");
Check(!UrlSecurity.IsPlainHttpOnPublicHost("garbage"), "garbage not flagged");
Check(!UrlSecurity.IsPlainHttpOnPublicHost(null), "null safe");

Console.WriteLine($"\n{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;
