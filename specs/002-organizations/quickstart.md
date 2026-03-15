# Quickstart: Organizations Feature

This guide demonstrates how to invoke the key use cases for the Organizations module.

## 1. Creating a Root Organization
An authenticated user without an organization can make a self-service organization natively:

```http
POST /api/organizations
Authorization: Bearer <token-with-no-org-id>
{
  "name": "Acme Corp"
}
```

**Expected Response**:
```json
{
  "id": "e44d32a0-abc1-...",
  "name": "Acme Corp",
  "slug": "acme-corp",
  "role": "Admin",
  "createdAtUtc": "2026-03-14T..."
}
```

## 2. Inviting a Member
An Admin can create invitations for specific email addresses:

```http
POST /api/organizations/{orgId}/invitations
Authorization: Bearer <token-with-admin-role>
{
  "emailWhitelist": ["newhire@acmecorp.com"]
}
```

**Expected Response**:
```json
{
  "invitationId": "f99831a2-...",
  "token": "aBk98zZ1...",
  "expiresAtUtc": "2026-03-21T..."
}
```

## 3. Joining via Invitation
The invited user takes the token and uses it to establish membership:

```http
POST /api/invitations/{token}/join
Authorization: Bearer <token-with-no-org-id>
```

*(They are now a `Member` of that organization)*

## 4. Setting AI Context
A user updates their own AI context to aid task extraction:

```http
PUT /api/organizations/{orgId}/members/{userId}/context
Authorization: Bearer <token-for-member>
{
  "context": "Frontend engineer specializing in React and LiveKit."
}
```
