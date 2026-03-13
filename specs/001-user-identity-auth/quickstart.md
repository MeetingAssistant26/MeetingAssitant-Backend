# Quickstart: User Identity & Authentication

**Feature**: `001-user-identity-auth` | **Branch**: `001-user-identity-auth`

## Prerequisites

- .NET 10 SDK installed
- PostgreSQL running (Docker or local) with database created
- Phase 0 infrastructure in place (EF Core DbContext, correlation middleware, JWT config)

## Key Decisions (from research)

| Decision | Choice |
|----------|--------|
| Identity framework | ASP.NET Identity with `IdentityUser<Guid>` |
| Token format | JWT access (15-min) + opaque refresh (7-day, SHA-256 hashed) |
| Refresh token rotation | Rotate on every use, 30-second grace period for old token |
| Key rotation | Dual-key validation via `IssuerSigningKeys`, 15-minute overlap |
| Compromise detection | Family-based — `FamilyId` links token lineage; reuse after grace revokes entire family |

## Data Model (summary)

Two entities — see [data-model.md](data-model.md) for full details:

- **`ApplicationUser`** — extends `IdentityUser<Guid>`. Adds `DisplayName`, `CreatedAtUtc`, `UpdatedAtUtc`.
- **`RefreshToken`** — owns hashed token, `FamilyId` for lineage, `GracePeriodExpiresAtUtc`, `ReplacedByTokenHash`.

## API Endpoints (summary)

See [contracts/api.md](contracts/api.md) for full request/response schemas.

| Method | Path | Auth | Controller | Endpoint File |
|--------|------|------|------------|---------------|
| POST | `/api/auth/register` | None | AuthController | RegisterEndpoint.cs |
| POST | `/api/auth/login` | None | AuthController | LoginEndpoint.cs |
| POST | `/api/auth/tokens/refresh` | None | TokenController | RefreshTokenEndpoint.cs |
| POST | `/api/auth/logout` | Bearer | AuthController | LogoutEndpoint.cs |
| GET | `/api/auth/profile` | Bearer | ProfileController | GetCurrentUserEndpoint.cs |
| PUT | `/api/auth/profile` | Bearer | ProfileController | UpdateProfileEndpoint.cs |
| POST | `/api/auth/profile/change-password` | Bearer | ProfileController | ChangePasswordEndpoint.cs |

## Project Structure

```text
src/Features/Identity/
├── Endpoints/
│   ├── Auth/
│   │   ├── AuthController.cs          # [ApiController], [Route("api/auth")], ctor with IAuthService
│   │   ├── RegisterEndpoint.cs        # POST register
│   │   ├── LoginEndpoint.cs           # POST login
│   │   └── LogoutEndpoint.cs          # POST logout
│   ├── Token/
│   │   ├── TokenController.cs         # [ApiController], [Route("api/auth/tokens")], ctor with ITokenService
│   │   └── RefreshTokenEndpoint.cs    # POST refresh
│   └── Profile/
│       ├── ProfileController.cs       # [ApiController], [Route("api/auth/profile")], ctor with IProfileService
│       ├── GetCurrentUserEndpoint.cs  # GET (own profile)
│       ├── UpdateProfileEndpoint.cs   # PUT (update display name)
│       └── ChangePasswordEndpoint.cs  # POST change-password
├── Services/
│   ├── IAuthService.cs
│   ├── AuthService.cs
│   ├── ITokenService.cs
│   ├── TokenService.cs
│   ├── IProfileService.cs
│   └── ProfileService.cs
├── Models/
│   ├── Requests/
│   │   ├── RegisterRequest.cs
│   │   ├── LoginRequest.cs
│   │   ├── RefreshTokenRequest.cs
│   │   ├── LogoutRequest.cs
│   │   ├── UpdateProfileRequest.cs
│   │   └── ChangePasswordRequest.cs
│   └── Responses/
│       ├── AuthResponse.cs
│       ├── TokenResponse.cs
│       └── UserProfileResponse.cs
├── Validators/
│   ├── RegisterRequestValidator.cs
│   ├── LoginRequestValidator.cs
│   ├── RefreshTokenRequestValidator.cs
│   ├── LogoutRequestValidator.cs
│   ├── UpdateProfileRequestValidator.cs
│   └── ChangePasswordRequestValidator.cs
├── Events/
│   ├── UserRegisteredEvent.cs
│   ├── UserLoggedInEvent.cs
│   ├── TokenRefreshedEvent.cs
│   ├── UserLoggedOutEvent.cs
│   ├── PasswordChangedEvent.cs
│   └── RefreshTokenCompromiseDetectedEvent.cs
├── ApplicationUser.cs
└── RefreshToken.cs
```

## Getting Started

1. **Add the Identity entities** to the EF Core `DbContext`:
   - `DbSet<ApplicationUser>` (mapped via ASP.NET Identity)
   - `DbSet<RefreshToken>` with index on `TokenHash` and `UserId`

2. **Configure ASP.NET Identity** in `Program.cs`:
   - `AddIdentity<ApplicationUser, IdentityRole<Guid>>()`
   - Password policy options (min 8 chars, require uppercase/lowercase/digit/special)
   - `AddEntityFrameworkStores<AppDbContext>()`

3. **Configure JWT authentication**:
   - `AddAuthentication(JwtBearerDefaults)` → `AddJwtBearer()`
   - `IssuerSigningKeys` (plural) for dual-key rotation support
   - `ClockSkew = TimeSpan.Zero`
   - Claims: `sub`, `email`, `name`, `jti`, `iat`, `exp`, `iss`, `aud`

4. **Implement `TokenService`**:
   - `GenerateAccessToken(user)` → creates signed JWT
   - `GenerateRefreshToken()` → cryptographically random string
   - `HashToken(token)` → SHA-256 hash for storage
   - `ValidateRefreshToken(tokenHash)` → checks expiry, revocation, grace period

5. **Register controllers** in `Program.cs` using `builder.Services.AddControllers()` and `app.MapControllers()`. Controllers are auto-discovered via the `[ApiController]` attribute on each controller definition file. No manual endpoint mapping needed.

6. **Run migrations**: `dotnet ef migrations add AddIdentity` → `dotnet ef database update`.

## Testing Strategy

- **Unit tests**: Token generation, hash validation, grace period logic, compromise detection
- **Integration tests**: Full register → login → refresh → logout flow against test PostgreSQL
- **Security tests**: User enumeration prevention (timing), expired token rejection, family revocation
