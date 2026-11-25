# EmergencyApp.User (.NET 10 MAUI, Android)

Target framework: .NET 10 MAUI for Android. Use Visual Studio 2026 with the .NET MAUI workload (or the .NET 10 SDK plus the Android workload), pick an Android emulator, and run `EmergencyApp.User.csproj`.

## Architecture
MVVM (CommunityToolkit.Mvvm) · Shell navigation · sqlite-net-pcl (local cache, offline-first) · HttpClient + JWT bearer.
- `Services/DatabaseService` local SQLite (user, contacts, requests)
- `Services/ApiService` REST client; base URL stored in Preferences (default `http://10.0.2.2:5000/` = host PC from emulator)
- `Services/SyncService` pushes unsent SOS requests, pulls status updates
- SOS is always saved locally first, then sent; unsent requests retry on History refresh

## Server API contract (ASP.NET Core) the app expects (JSON, camelCase)
| Method | Route | Body | Returns |
|---|---|---|---|
| POST | `/api/auth/register` | `{fullName, phone, email, password}` | `{userId, fullName, phone, email, token}` |
| POST | `/api/auth/login` | `{email, password}` | same as above |
| POST | `/api/emergencies` (Bearer) | `{clientId, userId, type, description, latitude, longitude, createdAtUtc}` | `{serverId, clientId, status, responderName}` |
| GET | `/api/emergencies/mine` (Bearer) | – | `[{serverId, clientId, status, responderName}]` |

Status values: `Received`, `Dispatched`, `Arrived`, `Resolved`, `Cancelled` (other words are mapped in `RequestStatus.Normalize`). The server should treat `clientId` as an idempotency key so retries don't create duplicates.

## Dev notes
- `usesCleartextTraffic="true"` in the manifest is for http dev only; use HTTPS in production.
- DTOs live in `Models/Models.cs`; mirror them in your shared/server project.

## Live tracking endpoints (new)
| Method | Route | Body | Returns |
|---|---|---|---|
| POST | `/api/emergencies/{serverId}/track` (Bearer) | `{latitude, longitude, accuracyMeters}` (user's live position, every ~5 s) | `LiveEmergencyDto` |
| POST | `/api/emergencies/{serverId}/cancel` (Bearer) | `{}` | `LiveEmergencyDto` (status `Cancelled`) |

`LiveEmergencyDto` (camelCase JSON, everything except serverId/clientId/status is optional):
`serverId, clientId, status, responderName, responderUnit, responderPhone, responderAddress, responderLatitude, responderLongitude, distanceKm, etaMinutes, receivedAtUtc, dispatchedAtUtc, arrivedAtUtc, resolvedAtUtc`.
`GET /api/emergencies/mine` may return the same object per item. If `/track` is missing the app falls back to `/mine` for status only.

## SOS payload additions
`POST /api/emergencies` now also carries `peopleCount` (1, 2, 3 or 4 = "4 or more") and `source` (`Hold`, `Tap`, `Shake`, `Volume button`). Add them to your server DTO / MySQL table; older servers simply ignore them.
Incident `type` values: `Medical`, `Fire`, `Police`, `Accident`, `Other`, `General` (nothing selected).

## Transmission & reliability (new)
The alert must get out even on a bad network. Delivery is run per SOS by `Services/TransmissionService`:

| Feature | How it works | Where |
|---|---|---|
| Primary channel | HTTPS POST to `/api/emergencies` with type, people, location, timestamp and `deviceId`. The server's reply is the ACK. 10 s timeout so a bad network fails fast. | `ApiService`, `SyncService.TryPushAsync` |
| Offline queue | The SOS is saved to SQLite first, then delivered/retried until the server ACKs it. Resumes after an app restart. | `SosService`, `TransmissionService` |
| Retry with backoff | 2, 4, 8, 15, 30, 60 s (+/-20 %) between attempts. A connectivity change cancels the wait and retries at once. | `Reliability/BackoffPolicy` |
| SMS fallback | No ACK after 10 s offline / 25 s online: one compact SMS (type, people, name, phone, maps link, ref, time) to the **dispatch SMS number** (Profile). Up to 3 tries. | `Reliability/SmsFormatter`, `Platforms/Android/AndroidReliability.cs` |
| Auto-call fallback | If neither internet nor SMS worked after 45 s: a 10-second cancellable countdown, then it calls the local emergency number (dials directly with Phone permission, otherwise opens the dialer). Never for an SOS older than 10 minutes. A one-tap "Call now" button is also shown. | `Reliability/FallbackPlanner`, `TransmissionService` |
| Delivery status | Sent -> Received (server ACK) -> Acknowledged by dispatcher. Shown on the Live screen and in the Home card. | `Reliability/DeliveryCalculator`, `LiveEmergencyPage` |
| Low-battery mode | At 20 % (critical 10 %) or Power Saver: live updates every 20 s (45 s) instead of 5 s, lower-power GPS, and a warning banner. Never affects the SOS upload, SMS or call. Setting in Profile. | `Services/PowerService`, `Reliability/BatteryPolicy` |
| Network state monitor | Listens to connectivity changes, shows "Online / No internet - will send by SMS" on Home and Live, wakes the retry and tracking loops immediately. | `Services/NetworkMonitor` |

### Setup
1. Profile > Reliability: enter the **dispatch SMS number** and switch SMS fallback on (this asks for the SMS permission). Switch auto-call on to allow direct dialing (Phone permission). Without permissions the app still works: SMS is skipped and the call opens the dialer.
2. Use **https://** for the server URL. Profile warns when a public server is plain `http://`.
3. New Android permissions in the manifest: `SEND_SMS`, `CALL_PHONE`. Google Play restricts SMS permissions, so a Play release needs the emergency-use declaration.

### API additions (server already updated in EmergencyDesk.Server)
`POST /api/emergencies` also carries `deviceId`. Responses (`LiveEmergencyDto`) add `acknowledgedAtUtc`; `receivedAtUtc` is now the server receive time. Older servers still work: missing fields fall back to the status (Dispatched/Arrived imply acknowledged).

### Tests
`../EmergencyApp.User.Tests` is a console test project for the pure logic in `Reliability/` (backoff, fallback timeline simulations, SMS text, battery modes, delivery steps, URL check): `dotnet run`.
