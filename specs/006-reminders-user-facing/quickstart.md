# Quick Start: Reminders — User-Facing

## Prerequisites

- Backend API running (Phase 0.3 infrastructure bootstrapped)
- User authenticated with valid JWT (Phase 1 Identity)
- User belongs to an organization (Phase 2 Organizations)
- PostgreSQL database migrated with `Reminders` table

## Postman / curl Examples

### 1. Create a Personal Reminder

```bash
curl -X POST https://localhost:5001/api/me/reminders \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer {user-jwt}" \
  -d '{
    "text": "Follow up with client about proposal",
    "reminderAtUtc": "2026-05-01T09:00:00Z"
  }'
```

**Expected**: `201 Created` with reminder JSON.

---

### 2. List My Reminders

```bash
curl -X GET "https://localhost:5001/api/me/reminders?page=1&pageSize=20" \
  -H "Authorization: Bearer {user-jwt}"
```

**Expected**: `200 OK` with paginated list of active reminders affecting the user.

---

### 3. Mark Reminder as Delivered

```bash
curl -X POST "https://localhost:5001/api/me/reminders/{reminder-id}/mark-delivered" \
  -H "Authorization: Bearer {user-jwt}"
```

**Expected**: 
- First call: `200 OK` with `status: "Delivered"` and `deliveredAtUtc` set
- Repeat call: `200 OK` idempotently (no error, state unchanged)

---

### 4. Cancel a Reminder

```bash
curl -X DELETE "https://localhost:5001/api/me/reminders/{reminder-id}" \
  -H "Authorization: Bearer {user-jwt}"
```

**Expected**: `204 No Content`

---

## Common Error Scenarios

### Validation Error (422)

```bash
curl -X POST https://localhost:5001/api/me/reminders \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer {user-jwt}" \
  -d '{ "text": "" }'
```

**Expected**: `422 Unprocessable Entity` with validation errors for missing `text` and `reminderAtUtc`.

---

### Forbidden — Marking Another User's Reminder (403)

```bash
curl -X POST "https://localhost:5001/api/me/reminders/{other-user-reminder-id}/mark-delivered" \
  -H "Authorization: Bearer {user-jwt}"
```

**Expected**: `403 Forbidden`

---

### Conflict — Cancelling Already-Delivered Reminder (409)

```bash
curl -X DELETE "https://localhost:5001/api/me/reminders/{already-delivered-id}" \
  -H "Authorization: Bearer {user-jwt}"
```

**Expected**: `409 Conflict`

---

## Testing Checklist

- [ ] Create reminder with valid input → 201 Created
- [ ] Create reminder without `text` → 422
- [ ] Create reminder without `reminderAtUtc` → 422
- [ ] List reminders when none exist → 200 OK, empty `items` array
- [ ] List reminders with default pagination → 20 items max
- [ ] List reminders with `pageSize=50` → 50 items max
- [ ] List reminders with `pageSize=100` → 422 (exceeds max)
- [ ] Mark own reminder delivered → 200 OK, status changed
- [ ] Re-mark same reminder delivered → 200 OK idempotent
- [ ] Mark another user's reminder delivered → 403
- [ ] Mark public reminder delivered → 403
- [ ] Cancel own active reminder → 204 No Content
- [ ] Cancel already-delivered reminder → 409 Conflict
- [ ] Cancel already-cancelled reminder → 409 Conflict
- [ ] Cancel another user's reminder → 403
- [ ] Fetch reminders from different org → tenant isolation enforced (no cross-org leakage)
