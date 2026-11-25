using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Telephony;
using EmergencyApp.User.Services;

// NOTE: namespace is EmergencyApp.User (not ...Platforms.Android) so it does not hide the Android.* namespaces.
namespace EmergencyApp.User;

/// Runtime permission for sending SMS (the fallback channel).
public class SendSmsPermission : Permissions.BasePlatformPermission
{
    public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
        new[] { (Android.Manifest.Permission.SendSms, true) };
}

/// Runtime permission for placing a call directly (without the user pressing the green button).
public class CallPhonePermission : Permissions.BasePlatformPermission
{
    public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
        new[] { (Android.Manifest.Permission.CallPhone, true) };
}

/// Sends the compact fallback SMS through the phone's SIM. Resolves true only when the carrier accepted it.
public class AndroidSmsSender : ISmsSender
{
    public bool IsSupported =>
        Platform.AppContext.PackageManager?.HasSystemFeature(PackageManager.FeatureTelephony) ?? false;

    public async Task<bool> HasPermissionAsync()
    {
        try { return await Permissions.CheckStatusAsync<SendSmsPermission>() == PermissionStatus.Granted; }
        catch { return false; }
    }

    public Task<bool> RequestPermissionAsync() => MainThread.InvokeOnMainThreadAsync(async () =>
    {
        try
        {
            var status = await Permissions.CheckStatusAsync<SendSmsPermission>();
            if (status != PermissionStatus.Granted) status = await Permissions.RequestAsync<SendSmsPermission>();
            return status == PermissionStatus.Granted;
        }
        catch { return false; }
    });

    public async Task<bool> SendAsync(string number, string message, CancellationToken ct = default)
    {
        if (!IsSupported || string.IsNullOrWhiteSpace(number) || string.IsNullOrWhiteSpace(message)) return false;
        if (!await HasPermissionAsync()) return false;

        var ctx = Platform.AppContext;
        var action = $"{ctx.PackageName}.SMS_SENT_{Guid.NewGuid():N}";
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiver = new SmsSentReceiver(ok => result.TrySetResult(ok));
        var filter = new IntentFilter(action);

        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            ctx.RegisterReceiver(receiver, filter, ReceiverFlags.NotExported);
        else
            ctx.RegisterReceiver(receiver, filter);

        try
        {
            var manager = GetSmsManager();
            if (manager is null) return false;

            var parts = manager.DivideMessage(message);
            if (parts is { Count: > 1 })
            {
                // Should not happen (the text is built to fit one SMS); send it anyway without a per-part result.
                manager.SendMultipartTextMessage(number.Trim(), null, parts, null, null);
                return true;
            }

            var sentIntent = new Intent(action);
            sentIntent.SetPackage(ctx.PackageName);
            var pending = PendingIntent.GetBroadcast(ctx, 0, sentIntent,
                PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

            manager.SendTextMessage(number.Trim(), null, message, pending, null);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var registration = timeout.Token.Register(() => result.TrySetResult(false));
            return await result.Task;
        }
        catch { return false; }
        finally { try { ctx.UnregisterReceiver(receiver); } catch { } }
    }

    private static SmsManager? GetSmsManager()
    {
#pragma warning disable CA1422 // SmsManager.Default is deprecated on Android 12+, only used on older versions
        return OperatingSystem.IsAndroidVersionAtLeast(31)
            ? Platform.AppContext.GetSystemService(Java.Lang.Class.FromType(typeof(SmsManager))) as SmsManager
            : SmsManager.Default;
#pragma warning restore CA1422
    }
}

/// Receives the "SMS was sent" result from the system (RESULT_OK means the carrier accepted it).
public class SmsSentReceiver : BroadcastReceiver
{
    private readonly Action<bool> _done;
    public SmsSentReceiver(Action<bool> done) => _done = done;
    public override void OnReceive(Context? context, Intent? intent) => _done(ResultCode == Result.Ok);
}

/// Places the emergency call. Dials directly when the Phone permission is granted, otherwise opens the dialer
/// with the number filled in, so the user only has to press call.
public class AndroidPhoneCaller : IPhoneCaller
{
    public async Task<bool> HasPermissionAsync()
    {
        try { return await Permissions.CheckStatusAsync<CallPhonePermission>() == PermissionStatus.Granted; }
        catch { return false; }
    }

    public Task<bool> RequestPermissionAsync() => MainThread.InvokeOnMainThreadAsync(async () =>
    {
        try
        {
            var status = await Permissions.CheckStatusAsync<CallPhonePermission>();
            if (status != PermissionStatus.Granted) status = await Permissions.RequestAsync<CallPhonePermission>();
            return status == PermissionStatus.Granted;
        }
        catch { return false; }
    });

    public async Task<bool> CallAsync(string number)
    {
        if (string.IsNullOrWhiteSpace(number)) return false;
        try
        {
            var direct = await HasPermissionAsync();
            var uri = Android.Net.Uri.Parse("tel:" + Android.Net.Uri.Encode(number.Trim()));
            var intent = new Intent(direct ? Intent.ActionCall : Intent.ActionDial, uri);
            intent.AddFlags(ActivityFlags.NewTask);
            Platform.AppContext.StartActivity(intent);
            return true;
        }
        catch { return false; }
    }
}
