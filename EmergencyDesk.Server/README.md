# EmergencyDesk Server

This server connects the EmergencyDesk desktop application and EmergencyApp Android client. It uses ASP.NET Core 8 and MySQL.

## First run with XAMPP

1. Install the **.NET 8 SDK** and XAMPP on Windows.
2. In XAMPP Control Panel, start **MySQL**. Apache is not required.
3. Extract this folder and double-click `Run-Server.bat`.
4. On first startup, the server creates the `emergency_db` database and its tables in MySQL. When it finds an older server schema, it adds the new columns automatically.
5. Open [http://localhost:5000/swagger](http://localhost:5000/swagger) to see the API.

The included local defaults expect MySQL at `localhost:3306` with user `root` and no password, as in a typical XAMPP installation. If your MySQL root account has a password or uses a different port, edit `ConnectionStrings:Default` in `appsettings.json` before running.

The starter desktop login for a newly created database is **admin / EmergencyDesk123!**. It is created only when the Staff table is empty. If you use a database that already has staff accounts, keep using its existing account. The JWT key and starter login in this package are for local development; change them before exposing the server to a network.

## App addresses

- Desktop app on the same computer: `http://localhost:5000/`
- Android emulator: `http://10.0.2.2:5000/`

The Android app's API base URL must point to the address above. A physical Android phone needs the computer's local network IP, plus firewall access to port 5000.

## Supported routes

Android:

- `POST /api/auth/register`, `POST /api/auth/login`
- `POST /api/emergencies` with `clientId`, `type`, `description`, optional coordinates, `createdAtUtc`, `peopleCount`, and `source`. The authenticated token determines the account; body `userId` is ignored.
- `GET /api/emergencies/mine`
- `POST /api/emergencies/{serverId}/track` with live latitude, longitude, and optional accuracy.
- `POST /api/emergencies/{serverId}/cancel`

Desktop:

- `POST /api/staff/login`
- `GET /api/incidents`, `PUT /api/incidents/{id}/assign`, `PUT /api/incidents/{id}/status`, and `PUT /api/incidents/{id}/acknowledge`
- `GET/POST/PUT /api/responders` and `GET /api/users`, `PUT /api/users/{id}/active`

Desktop statuses `EnRoute` and `OnScene` map to mobile `Dispatched` and `Arrived`. The server stores the latest reported user location per incident, not a location trail.

`GET /api/incidents` returns all incidents by default, so the desktop's existing reports are not silently limited to the newest 500 records. The endpoint also accepts optional UTC filters and pagination for clients that need them, for example `GET /api/incidents?fromUtc=2026-01-01T00:00:00Z&toUtc=2026-02-01T00:00:00Z&skip=0&take=500`. `take` must be from 1 to 5000.

## Transmission & reliability fields (phone app)

`POST /api/emergencies` now also accepts `deviceId` (random per-install ID, stored on the incident and returned to the desk as `deviceId`).

The reply to that POST is the phone's **ACK**. The mobile DTO now carries two separate times:

| Field | Meaning |
|---|---|
| `receivedAtUtc` | when this **server** received the alert (new `Incidents.ReceivedAt`; falls back to the phone's `ReportedAt` for old rows) |
| `acknowledgedAtUtc` | when a **dispatcher** acknowledged it (`Incidents.AcknowledgedAt`) |

The phone shows these as Sent -> Received -> Acknowledged by dispatcher. Earlier versions returned the acknowledgement time as `receivedAtUtc`, which made the phone show "acknowledged" the moment the alert was sent. The two new columns (`ReceivedAt`, `DeviceId`) are added automatically at startup by `DatabaseBootstrapper`.

### SMS fallback (not implemented on the server)
When the phone has no internet it texts `SOS <TYPE> <n>P - <name> <phone> - <maps link> - ref <8 chars> - <HH:mm>Z` to the configured dispatch SMS number. The server does not read carrier SMS; this requires an SMS gateway or a connected dispatch device and a provider-specific webhook integration. No gateway endpoint is enabled in this package because the provider, webhook authentication, and delivery format have not been configured. `ref` is the first 8 characters of the phone's `clientId`, so a future integration can match the SMS with a later internet upload.

## Client compatibility notes

The desktop `IncidentType` enum includes `Accident`, `Other` and `General`, and the Android app's `ApiService`, `DatabaseService` and `LiveTrackingService` are present, so the contract here matches the current clients. The desktop UI does not yet display `deviceId`.
