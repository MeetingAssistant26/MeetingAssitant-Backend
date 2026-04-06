# API Contracts: Organizations

## Standard Error Response

All endpoints return errors using `StandardErrorResponse` via `result.ToProblem(correlationIdProvider)` for business errors and `ValidationResultFactory` for validation errors.

```json
{
  "type": "string",
  "title": "string",
  "status": 0,
  "errors": { "fieldName": ["error message"] },
  "correlationId": "string"
}
```

- `type`: Error code (e.g., `"Organization.AlreadyHasMembership"`, `"ValidationError"`)
- `title`: Human-readable description
- `status`: HTTP status code
- `errors`: Field-level validation errors (present only for 400 validation responses)
- `correlationId`: Request correlation ID for tracing

---

## 1. Organization Endpoints (OrganizationController)

### Create Organization
`POST /api/organizations`
**Headers**: `Authorization: Bearer <token>`
**Request**:
```json
{
  "name": "string (1-200 chars)"
}
```
**Response (200 OK)**: `OrganizationResponse` (Id, Name, Slug, CreatedAt)
**Errors**: 400 Validation, 401 Unauthorized, 403 Forbidden (If already has org/membership), 422 Unprocessable (business logic)

### Leave Organization
`POST /api/organizations/{organizationId}/leave`
**Headers**: `Authorization: Bearer <token>`
**Request**: Empty body
**Response**: 204 No Content
**Errors**: 403 Forbidden (If user is the last Admin), 404 Not Found

---

## 2. Member Endpoints (MemberController)

### List Members
`GET /api/organizations/{organizationId}/members`
**Headers**: `Authorization: Bearer <token>`
**Response (200 OK)**: Array of `MemberResponse`
```json
[
  {
    "userId": "guid",
    "email": "string",
    "displayName": "string",
    "orgRole": "Admin|Member|Guest",
    "jobRole": "string?",
    "context": "string?",
    "contextStatus": "Pending|Processing|Processed|Outdated|null",
    "isEnabled": boolean
  }
]
```

### Update Member Role
`PUT /api/organizations/{organizationId}/members/{userId}/role`
**Requires Policy**: `RequireOrgAdmin`
**Request**:
```json
{
  "orgRole": "Admin" // or Member/Guest
}
```
**Response**: 204 No Content
**Errors**: 403 Forbidden (If demoting last admin)

### Update Member Context
`PUT /api/organizations/{organizationId}/members/{userId}/context`
**Requires Policy**: `RequireOrgMember` (Target user can self-edit, Admin can edit anyone)
**Request**:
```json
{
  "context": "string (max 2000)",
  "jobRole": "string (max 100)"
}
```
**Response**: 204 No Content

---

## 3. Invitation Endpoints (InvitationController)

### Create Invitation
`POST /api/organizations/{organizationId}/invitations`
**Requires Policy**: `RequireOrgAdmin`
**Request**:
```json
{
  "emailWhitelist": ["test1@test.com", "test2@test.com"]
}
```
**Response (200 OK)**:
```json
{
  "id": "guid",
  "token": "string",
  "expiresAtUtc": "datetime"
}
```

### Join via Invitation
`POST /api/invitations/{token}/join`
**Headers**: `Authorization: Bearer <token>` (Must have no active org membership)
**Request**: Empty body
**Response (200 OK)**: `MembershipResponse`
**Errors**: 403 Forbidden (Bad email or already belongs to Org), 404 Not Found (Expired/Invalid Token)

---

## 4. Meeting Tag Endpoints (MeetingTagController)

### List Meeting Tags
`GET /api/organizations/{organizationId}/meeting-tags`
**Headers**: `Authorization: Bearer <token>`
**Requires Policy**: `RequireOrgMember`
**Query Parameters**: None
**Response (200 OK)**: Array of `MeetingTagResponse` (active tags only — `IsActive = true`), ordered by `CreatedAtUtc ASC` (oldest first)
```json
[
  {
    "id": "guid",
    "organizationId": "guid",
    "name": "string",
    "color": "string?",
    "createdAtUtc": "datetime",
    "updatedAtUtc": "datetime"
  }
]
```

### Create Meeting Tag
`POST /api/organizations/{organizationId}/meeting-tags`
**Headers**: `Authorization: Bearer <token>`
**Requires Policy**: `RequireOrgAdmin`
**Request**:
```json
{
  "name": "string (1-50 chars, required)",
  "color": "string (optional, hex format #RRGGBB)"
}
```
**Validation Rules:**
- `name`: Required, not empty, max 50 characters
- `name`: Unique within organization (case-insensitive comparison: "Standup" = "standup")
- `color`: Optional. If provided, must match regex `^#[0-9A-Fa-f]{6}$`
**Response (201 Created)**: `MeetingTagResponse`
**Errors**: 
- 400 Validation (invalid name format or color format)
- 401 Unauthorized
- 403 Forbidden (non-admin)
- 409 Conflict (duplicate name)

**Example Error (409 Conflict)**:
```json
{
  "type": "MeetingTag.DuplicateName",
  "title": "A tag with this name already exists in the organization.",
  "status": 409,
  "errors": {},
  "correlationId": "string"
}
```

### Update Meeting Tag
`PUT /api/organizations/{organizationId}/meeting-tags/{tagId}`
**Headers**: `Authorization: Bearer <token>`
**Requires Policy**: `RequireOrgAdmin`
**Request**: Partial update — only provided fields are updated:
```json
{
  "name": "string (1-50 chars)",
  "color": "string (hex format #RRGGBB or null to clear)"
}
```
**Validation Rules:**
- Only fields present in request are updated (partial update)
- `name`: If provided, same rules as create (unique within org, case-insensitive, excluding this tag itself)
- `color`: If provided, must match hex format or be `null` to remove color
- Setting `color` to `null` clears the color (UI shows default)
**Response (200 OK)**: `MeetingTagResponse` (full tag object after update)
**Errors**: 
- 400 Validation
- 401 Unauthorized
- 403 Forbidden
- 404 Not Found (tag doesn't exist or is inactive)
- 409 Conflict (duplicate name)

**Example Request (rename only)**:
```json
{ "name": "Sprint Planning" }
```

**Example Request (change color only)**:
```json
{ "color": "#4CAF50" }
```

**Example Request (clear color)**:
```json
{ "color": null }
```

**Example Success Response**:
```json
{
  "id": "987fcdeb-51a2-43f7-9876-543210987654",
  "organizationId": "123e4567-e89b-12d3-a456-426614174000",
  "name": "Sprint Planning",
  "color": "#4CAF50",
  "createdAtUtc": "2026-03-10T08:00:00Z",
  "updatedAtUtc": "2026-04-06T14:30:00Z"
}
```

### Delete Meeting Tag (Soft Delete)
`DELETE /api/organizations/{organizationId}/meeting-tags/{tagId}`
**Headers**: `Authorization: Bearer <token>`
**Requires Policy**: `RequireOrgAdmin`
**Behavior**: 
- Sets `IsActive = false` (soft delete)
- Tag immediately disappears from `GET /meeting-tags` list
- Tag remains in database for referential integrity with existing meetings
- Tag name becomes available for reuse (unique constraint is `WHERE IsActive = true`)
**Response**: 204 No Content
**Errors**: 
- 401 Unauthorized
- 403 Forbidden
- 404 Not Found (tag doesn't exist or already inactive)

**Note**: Hard delete intentionally not provided. Tags marked inactive remain in DB for historical meeting references. Admin can create a new tag with the same name after deletion.