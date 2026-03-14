# Implementation Plan: User Identity & Authentication

**Branch**: `001-user-identity-auth` | **Date**: 2026-03-07 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/001-user-identity-auth/spec.md`

## Summary

Implement the user identity and authentication system - the foundational security layer for the AI-Powered Meeting Assistant. Covers user registration, login with JWT access tokens (15-minute expiry), refresh token rotation (7-day expiry, SHA-256 hashed, 30-second grace period), profile management, and password change with session revocation. Built on ASP.NET Identity with `IdentityUser<Guid>`, ASP.NET Controllers using the Partial Controller Pattern (one endpoint per file via partial classes), FluentValidation, and Mapster. Supports JWT signing key rotation via dual-key validation (15-minute overlap window).

## Technical Context

**Language/Version**: C# / .NET 10
**Primary Dependencies**: ASP.NET Identity, JWT Bearer (`Microsoft.AspNetCore.Authentication.JwtBearer`), FluentValidation, Mapster, MediatR, Serilog
**Storage**: PostgreSQL via EF Core (`Npgsql.EntityFrameworkCore.PostgreSQL`)
**Testing**: xUnit, `WebApplicationFactory<Program>`, FluentAssertions
**Target Platform**: Linux container (Docker), local demo deployment
**Project Type**: Web service (ASP.NET Controllers — Partial Controller Pattern)
**Performance Goals**: API responses < 300 ms (constitution), login < 2 s, refresh < 1 s, 100 concurrent auth requests
**Constraints**: JWT ClockSkew = Zero, no synchronous AI calls, all timestamps UTC, CancellationToken on all async methods
**Scale/Scope**: Single-tenant initially (multi-tenant OrganizationId added in Phase 2), 3 controllers, 7 endpoint files

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| # | Constitution Rule | Status | Notes |
|---|-------------------|--------|-------|
| 1 | Feature-based modular monolith structure | PASS | All code under `src/Features/Identity/` with own Endpoints (Partial Controller Pattern), Services, Models, Validators, Events |
| 2 | No cross-feature direct service calls | PASS | Identity is Phase 1 - no other features exist yet. Domain events defined for downstream consumers |
| 3 | Cross-feature via domain events | PASS | Events defined: `UserRegisteredEvent`, `UserLoggedInEvent`, `PasswordChangedEvent`, etc. |
| 4 | Multi-tenancy with OrganizationId | N/A | Identity is user-scoped, not org-scoped. OrganizationId added in Phase 2 |
| 5 | ASP.NET Identity + JWT (15-min) + Refresh (7-day hashed, rotation) | PASS | Exactly as specified in Security Rules section V |
| 6 | Policy-based authorization | PASS | FR-016 scaffolds policy-based authorization for downstream features |
| 7 | No hardcoded secrets | PASS | JWT signing keys from configuration (user-secrets dev, env vars Docker) |
| 8 | FluentValidation for input | PASS | Validators for all request DTOs |
| 9 | CancellationToken on all async methods | PASS | Will be enforced in all service and endpoint signatures |
| 10 | UTC timestamps | PASS | `CreatedAtUtc`, `UpdatedAtUtc`, `ExpiresAtUtc`, `RevokedAtUtc`, `GracePeriodExpiresAtUtc` |
| 11 | Structured logging with correlation ID | PASS | All auth events logged (FR-015), correlation ID from Phase 0 middleware |
| 12 | Explicit DTOs for all API responses | PASS | Separate request/response DTOs per endpoint (sealed records in Models/) |
| 13 | Dependency Injection, no static services | PASS | `ITokenService`, `IAuthService` injected into controllers |
| 14 | API responses < 300 ms | PASS | All endpoints are simple DB operations, no AI calls |
| 15 | No synchronous AI calls | N/A | No AI calls in this feature |
| 16 | ASP.NET Controllers with Partial Controller Pattern | PASS | 3 controllers (Auth, Token, Profile), 7 endpoint files. One action per file via partial classes. |
| 17 | Mapster for object mapping | PASS | Mapster used for entity-to-DTO mapping |
| 18 | Endpoints use `result.ToProblem(correlationIdProvider)` for error responses | PASS | All endpoints use `result.ToProblem(correlationIdProvider)` for business failures — includes `CorrelationId` from `ICorrelationIdProvider` |

**Gate Result**: PASS - no violations.

## Project Structure

### Documentation (this feature)

```text
specs/001-user-identity-auth/
+-- plan.md              # This file
+-- research.md          # Phase 0: ASP.NET Identity + JWT, grace period, dual-key, compromise detection
+-- data-model.md        # Phase 1: ApplicationUser + RefreshToken entities
+-- quickstart.md        # Phase 1: Getting started guide
+-- contracts/
|   +-- api.md           # Phase 1: All 7 endpoint request/response contracts
+-- tasks.md             # Phase 2 output (/speckit.tasks command)
```

### Source Code (repository root)

```text
src/
+-- Features/
|   +-- Identity/
|       +-- Endpoints/
|       |   +-- Auth/
|       |   |   +-- AuthController.cs
|       |   |   +-- RegisterEndpoint.cs
|       |   |   +-- LoginEndpoint.cs
|       |   |   +-- LogoutEndpoint.cs
|       |   +-- Token/
|       |   |   +-- TokenController.cs
|       |   |   +-- RefreshTokenEndpoint.cs
|       |   +-- Profile/
|       |       +-- ProfileController.cs
|       |       +-- GetCurrentUserEndpoint.cs
|       |       +-- UpdateProfileEndpoint.cs
|       |       +-- ChangePasswordEndpoint.cs
|       +-- Services/
|       |   +-- ITokenService.cs
|       |   +-- TokenService.cs
|       |   +-- IAuthService.cs
|       |   +-- AuthService.cs
|       |   +-- IProfileService.cs
|       |   +-- ProfileService.cs
|       +-- Models/
|       |   +-- Requests/
|       |   |   +-- RegisterRequest.cs
|       |   |   +-- LoginRequest.cs
|       |   |   +-- RefreshTokenRequest.cs
|       |   |   +-- LogoutRequest.cs
|       |   |   +-- UpdateProfileRequest.cs
|       |   |   +-- ChangePasswordRequest.cs
|       |   +-- Responses/
|       |       +-- AuthResponse.cs
|       |       +-- TokenResponse.cs
|       |       +-- UserProfileResponse.cs
|       +-- Validators/
|       |   +-- RegisterRequestValidator.cs
|       |   +-- LoginRequestValidator.cs
|       |   +-- RefreshTokenRequestValidator.cs
|       |   +-- LogoutRequestValidator.cs
|       |   +-- UpdateProfileRequestValidator.cs
|       |   +-- ChangePasswordRequestValidator.cs
|       +-- Events/
|       |   +-- UserRegisteredEvent.cs
|       |   +-- UserLoggedInEvent.cs
|       |   +-- TokenRefreshedEvent.cs
|       |   +-- UserLoggedOutEvent.cs
|       |   +-- PasswordChangedEvent.cs
|       |   +-- RefreshTokenCompromiseDetectedEvent.cs
|       +-- ApplicationUser.cs
|       +-- RefreshToken.cs
+-- Infrastructure/
|   +-- Persistence/
|       +-- AppDbContext.cs          # Add DbSet<RefreshToken>, Identity config
+-- Shared/
|   +-- (base entities, common DTOs)
+-- Program.cs                       # Add Identity + JWT + auth middleware + controller registration

tests/
+-- Unit/
|   +-- Identity/
|       +-- TokenServiceTests.cs
|       +-- AuthServiceTests.cs
|       +-- ValidatorTests.cs
+-- Integration/
    +-- Identity/
        +-- Auth/
        |   +-- RegisterEndpointTests.cs
        |   +-- LoginEndpointTests.cs
        |   +-- LogoutEndpointTests.cs
        +-- Token/
        |   +-- RefreshTokenEndpointTests.cs
        +-- Profile/
            +-- GetCurrentUserEndpointTests.cs
            +-- UpdateProfileEndpointTests.cs
            +-- ChangePasswordEndpointTests.cs
```

**Structure Decision**: Feature-based modular monolith per constitution section I. All Identity code lives under `src/Features/Identity/` with Endpoints following the Partial Controller Pattern (3 controllers: Auth, Token, Profile — each with a definition file and endpoint files), Services, Models (Requests/Responses as sealed records), Validators, and Events. `DTOs/` folder renamed to `Models/` per project-wide convention. Integration tests mirror the controller group structure.

## Complexity Tracking

No constitution violations - table not required.
