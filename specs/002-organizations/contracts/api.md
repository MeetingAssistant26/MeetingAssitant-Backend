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