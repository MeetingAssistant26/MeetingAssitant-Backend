# Research: User Identity & Authentication

**Feature**: `001-user-identity-auth` | **Date**: 2026-03-07

## Topic 1: ASP.NET Identity + JWT in .NET 10

### 1.1 Extending IdentityUser

- **Decision**: `ApplicationUser : IdentityUser<Guid>` with custom fields (`DisplayName`, `CreatedAtUtc`) directly on the entity.
- **Rationale**: `Guid` key aligns with the project's `BaseEntity` pattern. Custom domain fields belong on the entity, not in `AspNetUserClaims`.
- **Alternatives considered**: Claims-only approach (rejected — claims are for auth context, not domain data), separate `UserProfile` entity (rejected — unnecessary join at this scale), `string` key (rejected — wider indexes, mismatch with other entities).

### 1.2 JWT Token Generation

- **Decision**: Use `JwtSecurityTokenHandler.CreateToken()` with shared `TokenValidationParameters` singleton. Set `ClockSkew = TimeSpan.Zero` for exact 15-minute expiry.
- **Rationale**: Identity's built-in token providers produce opaque data-protection tokens, not JWTs. Sharing `TokenValidationParameters` between issuance and validation eliminates configuration drift. Zero clock skew prevents the default 5-minute extension.
- **Alternatives considered**: Identity's `GenerateUserTokenAsync` (rejected — not JWT), OpenIddict/Duende IdentityServer (rejected — massive overhead for single-app).

### 1.3 Refresh Token Storage

- **Decision**: Generate 32-byte random values (`RandomNumberGenerator`), return Base64 to client, store SHA-256 hash in DB. Separate `RefreshToken` entity (one-to-many with `ApplicationUser`).
- **Rationale**: SHA-256 hash protects against DB breach. Fast hash is secure because tokens have 256-bit entropy (bcrypt is for low-entropy passwords). Separate entity supports multi-device sessions.
- **Alternatives considered**: Plaintext storage (rejected — DB breach = session compromise), bcrypt (rejected — not needed for high-entropy tokens, prevents O(1) lookup), Redis-only (rejected — 7-day tokens need durable storage).

## Topic 2: Refresh Token Grace Period (30s)

- **Decision**: On rotation, set `GracePeriodExpiresAtUtc = UtcNow + 30s` on the old token. If a revoked token arrives within the grace window, accept it and issue a new pair. After the grace window, treat reuse as compromise → revoke entire family.
- **Rationale**: Solves multi-tab race condition without distributed locks. Database-level optimistic concurrency with timestamps is simpler and horizontally scalable. Same pattern used by Auth0.
- **Alternatives considered**: Compute from `RevokedAtUtc` (rejected — less explicit), distributed Redis lock (rejected — adds latency and hard dependency), no grace period (rejected — false-positive lockouts).

### Schema

| Column | Type | Purpose |
|--------|------|---------|
| `Id` | `Guid` | PK |
| `TokenHash` | `string` (indexed, unique) | SHA-256 hash for lookup |
| `UserId` | `Guid` (FK) | Owner |
| `FamilyId` | `Guid` | Token family for lineage tracking |
| `ExpiresAtUtc` | `DateTime` | Absolute expiry (7 days) |
| `CreatedAtUtc` | `DateTime` | When issued |
| `RevokedAtUtc` | `DateTime?` | When revoked (null if active) |
| `ReplacedByTokenHash` | `string?` | Successor token hash |
| `GracePeriodExpiresAtUtc` | `DateTime?` | Grace window end |
| `IsRevoked` | `bool` | Explicit revocation flag |

## Topic 3: JWT Dual-Key Rotation (15-min overlap)

- **Decision**: Use `TokenValidationParameters.IssuerSigningKeys` (plural) with both old and new keys during rotation. Include `kid` (Key ID) in token headers. After 15 minutes (one access token lifetime), remove old key.
- **Rationale**: Built-in framework mechanism — no custom validators needed. `kid` header makes validation O(1). 15 minutes is mathematically minimal (worst-case token has ~15 min remaining).
- **Alternatives considered**: Custom `ISecurityTokenValidator` (rejected — reinvents framework), JWKS endpoint (rejected — over-engineering for single API), immediate swap (rejected — invalidates in-flight tokens).

## Topic 4: Compromise Detection (Token Families)

- **Decision**: Token family tracking via `FamilyId` column. All tokens from the same login share a `FamilyId`. On reuse after grace period → `UPDATE WHERE FamilyId = @family` to revoke entire family.
- **Rationale**: O(1) bulk revocation. A simple `IsUsed` flag can't link related tokens. Walking `ReplacedByTokenHash` chains is O(n) recursive. This is Auth0's documented pattern (RFC 6819).
- **Alternatives considered**: Simple `IsUsed` flag (rejected — can't do bulk revocation), chain traversal only (rejected — O(n) recursive queries), revoke all user tokens (rejected — too aggressive for single-session compromise).

### Revocation Scope

| Trigger | Scope | Rationale |
|---------|-------|-----------|
| Token reuse (after grace) | Same family only | Attacker got one session, not credentials |
| Password change | All families (global) | Credential compromise → all sessions suspect |
| Explicit logout | Current token only | User action on one device |
