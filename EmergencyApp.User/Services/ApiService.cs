using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EmergencyApp.User.Models;

namespace EmergencyApp.User.Services;

public class ApiService
{
    public const string DefaultUrl = "http://10.0.2.2:5000/";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string BaseUrl
    {
        get => Preferences.Get("server_url", DefaultUrl);
        set => Preferences.Set("server_url", value.EndsWith('/') ? value : value + "/");
    }

    public Task<ApiResult<AuthResponse>> LoginAsync(LoginRequest r) =>
        SendAsync<AuthResponse>(HttpMethod.Post, "api/auth/login", r);

    public Task<ApiResult<AuthResponse>> RegisterAsync(RegisterRequest r) =>
        SendAsync<AuthResponse>(HttpMethod.Post, "api/auth/register", r);

    /// The SOS upload. The server's reply is the ACK (it carries the serverId and the time it was received).
    /// Uses a short timeout so a bad network fails fast and the retry / SMS fallback can take over.
    public Task<ApiResult<LiveEmergencyDto>> CreateEmergencyAsync(CreateEmergencyDto d, string? token) =>
        SendAsync<LiveEmergencyDto>(HttpMethod.Post, "api/emergencies", d, token, TimeSpan.FromSeconds(10));

    public Task<ApiResult<List<LiveEmergencyDto>>> GetMyEmergenciesAsync(string? token) =>
        SendAsync<List<LiveEmergencyDto>>(HttpMethod.Get, "api/emergencies/mine", null, token);

    /// Sends the user's live position and returns the latest live info (status, responder, distance...).
    public Task<ApiResult<LiveEmergencyDto>> TrackAsync(int serverId, TrackRequest body, string? token) =>
        SendAsync<LiveEmergencyDto>(HttpMethod.Post, $"api/emergencies/{serverId}/track", body, token);

    public Task<ApiResult<LiveEmergencyDto>> CancelAsync(int serverId, string? token) =>
        SendAsync<LiveEmergencyDto>(HttpMethod.Post, $"api/emergencies/{serverId}/cancel", new { }, token);

    private async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string path, object? body, string? token = null,
                                                  TimeSpan? timeout = null)
    {
        try
        {
            using var req = new HttpRequestMessage(method, new Uri(new Uri(BaseUrl), path));
            if (body is not null) req.Content = JsonContent.Create(body, options: Json);
            if (!string.IsNullOrEmpty(token))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(15));
            using var res = await _http.SendAsync(req, cts.Token);
            var text = await res.Content.ReadAsStringAsync(cts.Token);
            var code = (int)res.StatusCode;

            if (res.IsSuccessStatusCode)
                return string.IsNullOrWhiteSpace(text)
                    ? new(true, default, null, false, code)
                    : new(true, JsonSerializer.Deserialize<T>(text, Json), null, false, code);

            var msg = string.IsNullOrWhiteSpace(text) || text.Length > 200 ? $"Server error ({code})" : text;
            return new(false, default, msg, false, code);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new(false, default, "Cannot reach the server. Check your connection and server URL.", Offline: true);
        }
        catch (JsonException)
        {
            return new(false, default, "Unexpected response from the server.");
        }
        catch (Exception ex)
        {
            return new(false, default, ex.Message);
        }
    }
}
