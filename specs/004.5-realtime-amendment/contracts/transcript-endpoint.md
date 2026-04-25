# Contract: `GET /api/organizations/{orgId}/meetings/{meetingId}/transcript`

**Introduced by**: Phase 4.5 — Opus Realtime Amendment  
**Source**: [spec.md FR-006..FR-012](../spec.md) · [plan.md](../plan.md) · [data-model.md](../data-model.md)

This is an **organization-administrator-only debugging surface**. It is not a participant feature.

## Request

| Element | Value |
|---|---|
| Method | `GET` |
| Path | `/api/organizations/{orgId}/meetings/{meetingId}/transcript` |
| Route params | `orgId: Guid`, `meetingId: Guid` (both enforced by `:guid` route constraints) |
| Query params | *(none)* |
| Headers | `Authorization: Bearer <access-token>` (required) |
| Body | *(none)* |

### Authorization

- Endpoint-level: `[Authorize(Policy = "RequireOrgAdmin")]` (see [AuthDI.cs:104](../../../MeetingAssistant/Infrastructure/DependencyInjection/AuthDI.cs#L104)).
- Controller-level: `[EnforceOrgAccess]` — the `orgId` route parameter must match the caller's active org membership (constitution §III).
- No exceptions for meeting host, co-host, participant, or observer (FR-006).

## Response — 200 OK

```json
{
  "segments": [
    {
      "id":             "1c7c8b5e-3c5e-4b8a-bd5e-58f07b5e4e01",
      "speakerUserId":  "e1d2fd10-3c5e-4b8a-bd5e-58f07b5e4e02",
      "startTime":      "00:00:00.400",
      "endTime":        "00:00:03.120",
      "text":           "Good morning everyone.",
      "sequenceNumber": 0
    }
  ],
  "pipelineStatus": "NotStarted"
}
```

### Invariants

- `segments` — array, non-null. Empty list is a legal value.
- `segments[*]` — ordered by `startTime` ascending (FR-008).
- `pipelineStatus` — exactly one of the strings `"NotStarted"`, `"Processing"`, `"Completed"`, `"Failed"` (FR-007). Serialized as the enum name, not the integer.
- The response body MUST NOT carry `segments` without `pipelineStatus` or vice versa (FR-007).

### Shape × state matrix

| Meeting status (from `MeetingStatus`) | `pipelineStatus` | `segments` | HTTP |
|---|---|---|---|
| `Completed`, STT pipeline never queued | `NotStarted` | `[]` | 200 |
| `Completed`, STT jobs in flight | `Processing` | partial list (ordered) | 200 |
| `Completed`, STT finished | `Completed` | full list (ordered) | 200 |
| `Completed`, STT exhausted retries | `Failed` | `[]` or partial list (ordered) | 200 |
| `Cancelled` | `NotStarted` | `[]` | 200 |
| `Failed` (meeting-level, not STT) | `NotStarted` | `[]` | 200 |
| `InProgress` | *(n/a — problem body, see 409 below)* | *(n/a)* | **409** |
| `Scheduled` | *(n/a — problem body, see 409 below)* | *(n/a)* | **409** |

In this phase (stub `ITranscriptReadService`), every `Completed`/`Cancelled`/`Failed` meeting resolves to `(NotStarted, [])`. Phase 5.5 lights up the other status values when the real reader lands.

## Response — 401 Unauthorized

No access token, expired token, or invalid token. Standard RFC 7807 problem details from the existing auth middleware. Not emitted by this endpoint's code — falls through ASP.NET's auth pipeline.

## Response — 403 Forbidden

Caller is authenticated but:

- is not an organization administrator of `{orgId}` (policy denial), **or**
- is an organization administrator of a *different* org from `{orgId}` (filter denial via `[EnforceOrgAccess]`).

```json
{
  "type":     "https://httpstatuses.io/403",
  "title":    "Forbidden",
  "status":   403,
  "detail":   "Organization administrator access is required to read transcript segments.",
  "instance": "/api/organizations/…",
  "traceId":  "00-…-00"
}
```

Meeting Hosts, CoHosts, Participants, and Observers who are not OrgAdmins all land here (FR-006, SC-004).

## Response — 404 Not Found

Meeting does not exist inside `{orgId}` (either the meeting id is unknown, or the meeting belongs to a different org and the tenant filter hid it — indistinguishable by design).

```json
{
  "type":    "https://httpstatuses.io/404",
  "title":   "Meeting not found",
  "status":  404,
  "detail":  "No meeting with the given identifier exists in this organization.",
  "traceId": "00-…-00"
}
```

## Response — 409 Conflict (FR-012)

Meeting status is `InProgress` or `Scheduled` — the transcript has no post-meeting state to report.

```json
{
  "type":    "https://httpstatuses.io/409",
  "title":   "Meeting is still in progress",
  "status":  409,
  "detail":  "Transcript is available only after the meeting has ended. Retry after the meeting completes.",
  "meetingStatus": "InProgress",
  "traceId": "00-…-00"
}
```

- `meetingStatus` is included in the problem body so the administrator can see whether the meeting is `InProgress` or `Scheduled` without another round-trip.
- Response MUST NOT include `segments` or `pipelineStatus` fields (FR-012).

## Response — 500 Internal Server Error

Unhandled exception. Standard RFC 7807 problem details. Not part of the normal contract.

---

## Contract test checklist (for `/speckit.tasks`)

Each row below maps to an integration scenario in `tests/MeetingAssistant.Tests.Integration/LiveSession/TranscriptDebugEndpointTests.cs`:

1. OrgAdmin + `Completed` meeting, stub returns `(NotStarted, [])` → **200**, body shape validated, `pipelineStatus == "NotStarted"`, `segments == []`.
2. OrgAdmin + `Completed` meeting, test-double returns `(Processing, 2 segments ordered)` → **200**, `pipelineStatus == "Processing"`, segments in ascending `startTime` order.
3. OrgAdmin + `Completed` meeting, test-double returns `(Failed, [])` → **200**, `pipelineStatus == "Failed"`, segments empty, no error body.
4. **Non-admin org member** + `Completed` meeting → **403**, problem body, no `segments` leakage.
5. **Meeting host who is not OrgAdmin** + `Completed` meeting → **403** (FR-006 exhaustive).
6. OrgAdmin of a **different org** than the meeting's org → **404** or **403** (caller cannot learn which; `[EnforceOrgAccess]` chooses).
7. OrgAdmin + `InProgress` meeting → **409**, problem body, `meetingStatus == "InProgress"`, no `segments`/`pipelineStatus`.
8. OrgAdmin + `Scheduled` meeting → **409**, problem body, `meetingStatus == "Scheduled"`.
9. OrgAdmin + unknown `meetingId` → **404**.
10. No `Authorization` header → **401**.

Scenarios 1 + 4 + 5 + 7 + 8 cover the spec's SC-004 / SC-005 / SC-007 directly. Scenarios 2/3 exercise FR-007's non-`NotStarted` response shapes and will remain meaningful once Phase 5.5 swaps the stub.
