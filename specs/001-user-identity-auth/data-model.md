# Data Model: User Identity & Authentication

**Feature**: `001-user-identity-auth` | **Date**: 2026-03-07

## Entities

### ApplicationUser

Extends ASP.NET Identity's `IdentityUser<Guid>`.

| Field | Type | Constraints | Description |
|-------|------|-------------|-------------|
| `Id` | `Guid` | PK (inherited) | Unique user identifier |
| `Email` | `string` | Unique, required (inherited) | User's email address |
| `DisplayName` | `string` | Required, max 100 chars | User-facing display name |
| `PasswordHash` | `string` | Required (inherited) | Hashed password (managed by Identity) |
| `CreatedAtUtc` | `DateTime` | Required, UTC | Account creation timestamp |
| `UpdatedAtUtc` | `DateTime` | Required, UTC | Last profile update timestamp |

**Notes**:
- Inherits `UserName`, `NormalizedEmail`, `NormalizedUserName`, `SecurityStamp`, `ConcurrencyStamp` etc. from `IdentityUser<Guid>`
- `UserName` is set equal to `Email` (email is the login identifier)
- Not org-scoped — users exist before joining organizations (compliant with constitution §II)
- `SecurityStamp` is regenerated on password change (triggers token invalidation)

---

### RefreshToken

Separate entity — one user can have many active refresh tokens (multi-device).

| Field | Type | Constraints | Description |
|-------|------|-------------|-------------|
| `Id` | `Guid` | PK | Unique identifier |
| `TokenHash` | `string` | Unique, indexed, required | SHA-256 hash of the token value |
| `UserId` | `Guid` | FK → ApplicationUser, required | Token owner |
| `FamilyId` | `Guid` | Required, indexed | Groups tokens from the same login session |
| `ExpiresAtUtc` | `DateTime` | Required, UTC | Absolute expiry (7 days from creation) |
| `CreatedAtUtc` | `DateTime` | Required, UTC | When token was issued |
| `RevokedAtUtc` | `DateTime?` | UTC | When token was revoked (null = active) |
| `ReplacedByTokenHash` | `string?` | | Hash of the successor token (rotation chain) |
| `GracePeriodExpiresAtUtc` | `DateTime?` | UTC | End of 30-second grace window after rotation |
| `IsRevoked` | `bool` | Required, default false | Explicit revocation flag |

**Notes**:
- `TokenHash` is SHA-256 of the raw Base64 token — raw value is never stored
- `FamilyId` enables O(1) bulk revocation on compromise detection
- `GracePeriodExpiresAtUtc` prevents false-positive compromise detection in multi-tab scenarios
- `ReplacedByTokenHash` creates a forward-linked chain for lineage traversal

---

## Relationships

```
ApplicationUser (1) ──── (N) RefreshToken
                              ├── FamilyId groups tokens by login session
                              └── ReplacedByTokenHash chains rotation lineage
```

## Validation Rules

| Entity | Field | Rule |
|--------|-------|------|
| ApplicationUser | `Email` | Well-formed email, unique across all accounts |
| ApplicationUser | `DisplayName` | Non-empty, max 100 characters, trimmed |
| ApplicationUser | Password | Min 8 chars, 1 uppercase, 1 lowercase, 1 digit, 1 special |
| RefreshToken | `TokenHash` | Non-empty, unique |
| RefreshToken | `ExpiresAtUtc` | Must be in the future at creation time |

## State Transitions

### RefreshToken Lifecycle

```
Active (IsRevoked=false, RevokedAtUtc=null)
  │
  ├──→ Rotated (IsRevoked=true, RevokedAtUtc=now, GracePeriodExpiresAtUtc=now+30s)
  │       │
  │       ├──→ Grace period active → reuse allowed (new pair issued)
  │       └──→ Grace period expired → reuse = COMPROMISE
  │               → Revoke all tokens with same FamilyId
  │
  ├──→ Revoked by logout (IsRevoked=true, RevokedAtUtc=now)
  │
  ├──→ Revoked by password change (bulk: all user's tokens)
  │
  └──→ Expired (ExpiresAtUtc < UtcNow) → rejected on use, cleaned up by background job
```

## Domain Events

| Event | Trigger | Published By |
|-------|---------|-------------|
| `UserRegisteredEvent` | New user account created | Registration service |
| `UserLoggedInEvent` | Successful login (credentials validated) | Login service |
