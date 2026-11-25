# Dispatch-desk features added

| Feature | Where | Notes |
|---|---|---|
| Real-time alert popup | `Views/AlertWindow.*` | Topmost, flashing border, repeating sound. Shows highest-priority unacknowledged report. Acknowledge / Open / Remind me later. |
| Incident queue / inbox | "Incident queue" page | Chips: Open, New, Acknowledged, In progress, Escalated, Closed. Sorted: escalated, priority, longest waiting. |
| Auto-assignment rules | "Rules & SLA" page | Type -> region -> workload. Region = place names on a responder (Responders page) matched against the incident address. |
| Acknowledge | Popup + queue details | Stops re-alerts. Assigning a responder / changing status also counts as acknowledging. |
| Alert escalation | Rules & SLA | Unacknowledged after N s -> "Escalated": red row, banner on every page, popup overrides "remind me later". |
| Duplicate detection | Queue flags + details | Same phone, or same type within R metres, inside a time window. "Merge duplicates" closes extras as Cancelled and links them. |
| Priority scoring | Queue "Priority" column | 0-100 from type + description keywords (critical / vulnerable person / serious), multiple reports, unclear location, waiting time. Hover for the reasons. |
| SLA timers | Queue columns + details + popup | Live countdown for time-to-acknowledge and time-to-dispatch; turns amber, then red "OVERDUE". |

Rules and per-incident desk records are stored in `%LocalAppData%\EmergencyDesk\` (`rules.json`, `state.json`).

## Optional server support (the desk works without it)

Without these, acknowledgement is per-desk: other desks still see the report as new.

```
PUT api/incidents/{id}/acknowledge     body: { "acknowledgedBy": "Dana" }
```
Add to the incident JSON (all optional): `acknowledgedAt`, `acknowledgedBy`, `dispatchedAt` (UTC).
When present, the desk uses them, so SLA timers and "stop re-alerting" are shared by every operator.

# Reporting & analytics (section 7)

| Feature | Where | Notes |
|---|---|---|
| Incident reports (MVP) | "Export PDF / Export CSV" in the incident details panel | Summary, response times, full timeline, duplicates, who viewed it. Timeline is also shown live in the details panel. |
| Audit log (MVP) | "Audit log" page (admin) | Append-only `audit.jsonl`, SHA-256 hash chain, "Verify integrity", search, CSV export. Logs sign-in/out, incident views, acknowledge, assign, status, merge, auto-assign, responder/rules/report-settings edits (with old -> new values), user (de)activation, exports, page views of Users/Operators/Audit. |
| Dashboard KPIs | Dashboard | Alerts today, avg response time (report -> acknowledged), false-alarm rate (cancelled / all, last 7 days), open incidents. |
| Trend analytics | "Analytics" page | Per-day chart; by type, time of day, day of week, location, outcome. Ranges: today / 7 / 30 / 90 days / all. Summary export (PDF/CSV). |
| Operator performance | "Operator performance" page (admin) | Acknowledge and dispatch counts, average, slowest and % within SLA target per operator. |
| Scheduled reports (nice-to-have) | "Scheduled reports" page (admin) | Daily or weekly summary by e-mail (SMTP, optional PDF attachment). SMTP password stored with Windows DPAPI. Desk must be running. |

Definitions: incident = report not merged into another; response time = received -> acknowledged; dispatch time = received -> left "Received";
false alarm = closed as Cancelled and not a merged duplicate.

Extra files in `%LocalAppData%\EmergencyDesk\`: `audit.jsonl`, `reports.json`. Per-incident records (timelines) are now kept 180 days (was 14).

Optional server field (like `acknowledgedBy`): `dispatchedBy` on the incident JSON, so dispatch times can be attributed to operators on other desks.
