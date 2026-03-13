# API Contracts: User Identity & Authentication

**Feature**: `001-user-identity-auth` | **Date**: 2026-03-07  
**Base path**: `/api/auth`

---

## POST /api/auth/register

Register a new user account.

**Controller**: `AuthController` · **Endpoint file**: `RegisterEndpoint.cs`

**Request**:
```json
{
  "email": "ahmed@example.com",
  "displayName": "Ahmed",
  "password": "SecurePass1!"
}
```

**Response 200** (success):
```json
{
  "userId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "email": "ahmed@example.com",
  "displayName": "Ahmed"
}
```

**Response 400** (validation error):
```json
{
  "type": "ValidationError",
  "title": "One or more validation errors occurred.",
  "errors": {
    "email": ["Email is already taken."],
    "password": ["Password must contain at least one uppercase letter."]
  }
}
```

**Notes**: No tokens returned on registration. User must login separately.

---

## POST /api/auth/login

Authenticate with email and password.

**Controller**: `AuthController` · **Endpoint file**: `LoginEndpoint.cs`

**Request**:
```json
{
  "email": "ahmed@example.com",
  "password": "SecurePass1!"
}
```

**Response 200** (success):
```json
{
  "accessToken": "eyJhbGciOiJIUzI1NiIs...",
  "refreshToken": "dGhpcyBpcyBhIHJlZnJlc2...",
  "expiresAtUtc": "2026-03-07T12:15:00Z"
}
```

**Response 401** (invalid credentials):
```json
{
  "type": "AuthenticationError",
  "title": "Invalid credentials."
}
```

**Notes**: Same error for wrong email and wrong password (prevents user enumeration per FR-014).

---

## POST /api/auth/tokens/refresh

Exchange a refresh token for a new access token + rotated refresh token.

**Controller**: `TokenController` · **Endpoint file**: `RefreshTokenEndpoint.cs`

**Request**:
```json
{
  "refreshToken": "dGhpcyBpcyBhIHJlZnJlc2..."
}
```

**Response 200** (success):
```json
{
  "accessToken": "eyJhbGciOiJIUzI1NiIs...",
  "refreshToken": "bmV3IHJlZnJlc2ggdG9r...",
  "expiresAtUtc": "2026-03-07T12:30:00Z"
}
```

**Response 401** (invalid/expired/compromised):
```json
{
  "type": "AuthenticationError",
  "title": "Invalid or expired refresh token."
}
```

**Notes**: 
- Old refresh token is revoked with 30-second grace period.
- If a previously-rotated token is presented after the grace window, all tokens in the same family are revoked (compromise detection).

---

## POST /api/auth/logout

Invalidate the current refresh token.

**Controller**: `AuthController` · **Endpoint file**: `LogoutEndpoint.cs`

**Request**:
```json
{
  "refreshToken": "dGhpcyBpcyBhIHJlZnJlc2..."
}
```

**Response 200** (success):
```json
{
  "message": "Logged out successfully."
}
```

**Response 401**: Missing or invalid access token.

**Notes**: Requires a valid access token in the `Authorization` header. Only the specified refresh token is revoked (other devices remain active).

---

## GET /api/auth/profile

Retrieve the authenticated user's profile.

**Controller**: `ProfileController` · **Endpoint file**: `GetCurrentUserEndpoint.cs`

**Headers**: `Authorization: Bearer {accessToken}`

**Response 200**:
```json
{
  "userId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "email": "ahmed@example.com",
  "displayName": "Ahmed",
  "createdAtUtc": "2026-03-07T10:00:00Z"
}
```

**Response 401**: Missing or invalid access token.

---

## PUT /api/auth/profile

Update the authenticated user's profile.

**Controller**: `ProfileController` · **Endpoint file**: `UpdateProfileEndpoint.cs`

**Headers**: `Authorization: Bearer {accessToken}`

**Request**:
```json
{
  "displayName": "Ahmed Al-Farsi"
}
```

**Response 200**:
```json
{
  "userId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "email": "ahmed@example.com",
  "displayName": "Ahmed Al-Farsi"
}
```

**Response 400** (validation error):
```json
{
  "type": "ValidationError",
  "title": "One or more validation errors occurred.",
  "errors": {
    "displayName": ["Display name is required."]
  }
}
```

**Response 401**: Missing or invalid access token.

**Notes**: Email cannot be changed via this endpoint.

---

## POST /api/auth/profile/change-password

Change the authenticated user's password.

**Controller**: `ProfileController` · **Endpoint file**: `ChangePasswordEndpoint.cs`

**Headers**: `Authorization: Bearer {accessToken}`

**Request**:
```json
{
  "currentPassword": "SecurePass1!",
  "newPassword": "EvenMoreSecure2@"
}
```

**Response 200**:
```json
{
  "message": "Password changed successfully."
}
```

**Response 400** (validation error):
```json
{
  "type": "ValidationError",
  "title": "One or more validation errors occurred.",
  "errors": {
    "currentPassword": ["Current password is incorrect."],
    "newPassword": ["Password must be at least 8 characters."]
  }
}
```

**Response 401**: Missing or invalid access token.

**Notes**: All refresh tokens for the user are revoked on successful password change (FR-013). User must re-login on all devices.

---

## Standard Error Response Format

All error responses follow this structure:
- **Unexpected exceptions**: Enforced by Phase 0 global `ExceptionHandlingMiddleware`
- **Expected business failures**: Produced by `ResultExtensions.ToProblem()` in endpoint code (includes `CorrelationId` from `ICorrelationIdProvider`)

Endpoints use `result.ToProblem(correlationIdProvider)` to convert failed `Result` objects into error responses. Manual construction of `StandardErrorResponse` in endpoints is prohibited.

```json
{
  "type": "string",
  "title": "string",
  "status": 400,
  "errors": {
    "fieldName": ["Error message 1", "Error message 2"]
  },
  "correlationId": "abc-123-def"
}
```

**Notes**: `correlationId` is included on every response (from Phase 0 correlation ID middleware).

---

## JWT Access Token Claims

| Claim | Type | Description |
|-------|------|-------------|
| `sub` | `string` (Guid) | User ID |
| `email` | `string` | User's email |
| `name` | `string` | Display name |
| `jti` | `string` (Guid) | Unique token ID |
| `iat` | `number` (Unix) | Issued at |
| `exp` | `number` (Unix) | Expiry (15 minutes from `iat`) |
| `iss` | `string` | Issuer |
| `aud` | `string` | Audience |

**Notes**: No organization or role claims at this phase. Those are added in Phase 2 (org roles) and Phase 4 (meeting roles).
