# API Contracts: Reminders — User-Facing

**Base URL**: `/api/me/reminders`
**Authentication**: Bearer token (User JWT with `userId` and `organizationId` claims)

---

## POST /api/me/reminders

Create a personal reminder.

### Request

```http
POST /api/me/reminders
Content-Type: application/json
Authorization: Bearer {user-jwt}
```

```json
{
  "text": "Follow up with client about proposal",
  "reminderAtUtc": "2026-05-01T09:00:00Z"
}
```

### Validation Rules

- `text`: Required, non-empty, max 500 characters
- `reminderAtUtc`: Required, valid ISO 8601 UTC datetime
- Any `scope` field in body is ignored (always set to `Personal`)

### Response: 201 Created

```json
{
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "text": "Follow up with client about proposal",
  "scope": "Personal",
  "channel": "User",
  "createdByUserId": "user-guid-here",
  "targetUserId": "user-guid-here",
  "meetingId": null,
  "reminderAtUtc": "2026-05-01T09:00:00Z",
  "status": "Active",
  "deliveredAtUtc": null,
  "createdAtUtc": "2026-04-27T14:30:00Z",
  "updatedAtUtc": "2026-04-27T14:30:00Z"
}
```

### Response: 400 Bad Request

Generic model binding error (malformed JSON).

### Response: 422 Unprocessable Entity

```json
{
  "type": "ValidationError",
  "title": "One or more validation errors occurred.",
  "status": 422,
  "errors": {
    "Text": ["Text is required."],
    "ReminderAtUtc": ["ReminderAtUtc must be a valid UTC datetime."]
  }
}
```

### Response: 401 Unauthorized

Missing or invalid JWT.

---

## GET /api/me/reminders

List all reminders affecting the current user (personal + public for meetings they participate in), filtered to `Status=Active` and `ReminderAtUtc <= now`, sorted by `ReminderAtUtc` ascending.

### Request

```http
GET /api/me/reminders?page=1&pageSize=20
Authorization: Bearer {user-jwt}
```

### Query Parameters

| Parameter | Type | Default | Constraints |
|-----------|------|---------|-------------|
| `page` | integer | 1 | >= 1 |
| `pageSize` | integer | 20 | 1–50 (enforced server-side) |

### Response: 200 OK

```json
{
  "items": [
    {
      "id": "550e8400-e29b-41d4-a716-446655440000",
      "text": "Follow up with client",
      "scope": "Personal",
      "channel": "User",
      "createdByUserId": "user-guid-here",
      "targetUserId": "user-guid-here",
      "meetingId": null,
      "reminderAtUtc": "2026-05-01T09:00:00Z",
      "status": "Active",
      "deliveredAtUtc": null,
      "createdAtUtc": "2026-04-27T14:30:00Z"
    },
    {
      "id": "660e8400-e29b-41d4-a716-446655440001",
      "text": "Prepare standup notes",
      "scope": "Public",
      "channel": "Agent",
      "createdByUserId": "agent-system",
      "targetUserId": null,
      "meetingId": "meeting-guid-here",
      "reminderAtUtc": "2026-05-01T10:00:00Z",
      "status": "Active",
      "deliveredAtUtc": null,
      "createdAtUtc": "2026-04-26T10:00:00Z"
    }
  ],
  "page": 1,
  "pageSize": 20,
  "totalCount": 2,
  "totalPages": 1
}
```

### Response: 401 Unauthorized

Missing or invalid JWT.

---

## POST /api/me/reminders/{id}/mark-delivered

Mark a personal reminder as delivered. Idempotent — repeated calls return 200 OK with no state change.

### Request

```http
POST /api/me/reminders/550e8400-e29b-41d4-a716-446655440000/mark-delivered
Authorization: Bearer {user-jwt}
```

### Response: 200 OK

```json
{
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "text": "Follow up with client",
  "scope": "Personal",
  "channel": "User",
  "createdByUserId": "user-guid-here",
  "targetUserId": "user-guid-here",
  "meetingId": null,
  "reminderAtUtc": "2026-05-01T09:00:00Z",
  "status": "Delivered",
  "deliveredAtUtc": "2026-04-27T15:00:00Z",
  "createdAtUtc": "2026-04-27T14:30:00Z",
  "updatedAtUtc": "2026-04-27T15:00:00Z"
}
```

### Response: 403 Forbidden

Returned when:
- User attempts to mark another user's personal reminder as delivered
- User attempts to mark a Public reminder as delivered (regardless of ownership)

```json
{
  "type": "Forbidden",
  "title": "You can only mark your own personal reminders as delivered.",
  "status": 403,
  "correlationId": "abc-123"
}
```

### Response: 404 Not Found

Reminder does not exist or is not visible to the user (tenant isolation).

### Response: 401 Unauthorized

Missing or invalid JWT.

---

## DELETE /api/me/reminders/{id}

Soft-cancel a personal reminder (set `Status = Cancelled`).

### Request

```http
DELETE /api/me/reminders/550e8400-e29b-41d4-a716-446655440000
Authorization: Bearer {user-jwt}
```

### Response: 204 No Content

No body.

### Response: 403 Forbidden

Returned when:
- User attempts to cancel another user's reminder (personal or public)
- User attempts to cancel a Public reminder

```json
{
  "type": "Forbidden",
  "title": "You can only cancel your own personal reminders.",
  "status": 403,
  "correlationId": "abc-123"
}
```

### Response: 409 Conflict

Returned when the reminder is already `Delivered` or already `Cancelled`.

```json
{
  "type": "Conflict",
  "title": "Reminder cannot be cancelled because it is already delivered or cancelled.",
  "status": 409,
  "correlationId": "abc-123"
}
```

### Response: 404 Not Found

Reminder does not exist or is not visible to the user.

### Response: 401 Unauthorized

Missing or invalid JWT.
