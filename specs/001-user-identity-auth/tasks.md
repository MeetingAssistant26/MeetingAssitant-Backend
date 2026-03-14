# Tasks: User Identity & Authentication

**Input**: Design documents from `/specs/001-user-identity-auth/`
**Prerequisites**: plan.md (required), spec.md (required for user stories), research.md, data-model.md, contracts/

**Tests**: Tests are included — spec.md defines unit and integration tests per story, and plan.md explicitly lists test expectations.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Identity feature project structure and dependency registration

- [ ] T001 Create Identity feature folder structure per plan: `src/Features/Identity/Endpoints/Auth/`, `Endpoints/Token/`, `Endpoints/Profile/`, `Services/`, `Models/Requests/`, `Models/Responses/`, `Validators/`, `Events/`
- [ ] T002 [P] Create unit test project structure: `tests/Unit/Identity/`
- [ ] T003 [P] Create integration test project structure: `tests/Integration/Identity/`

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core entities, services, and configuration that ALL user stories depend on

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

- [ ] T004 Create `ApplicationUser` entity extending `IdentityUser<Guid>` with `DisplayName`, `CreatedAtUtc`, `UpdatedAtUtc` in `src/Features/Identity/ApplicationUser.cs`
- [ ] T005 Create `RefreshToken` entity with `TokenHash`, `UserId`, `FamilyId`, `ExpiresAtUtc`, `CreatedAtUtc`, `RevokedAtUtc`, `ReplacedByTokenHash`, `GracePeriodExpiresAtUtc`, `IsRevoked` in `src/Features/Identity/RefreshToken.cs`
- [ ] T006 Add `DbSet<ApplicationUser>` and `DbSet<RefreshToken>` to `AppDbContext` with Identity configuration and `RefreshToken` indexes (`TokenHash` unique, `UserId`, `FamilyId`) in `src/Infrastructure/Persistence/AppDbContext.cs`
- [ ] T007 Configure ASP.NET Identity in `Program.cs`: `AddIdentity<ApplicationUser, IdentityRole<Guid>>()`, password policy (min 8 chars, require uppercase/lowercase/digit/special), `AddEntityFrameworkStores<AppDbContext>()` in `src/Program.cs`
- [ ] T008 Configure JWT Bearer authentication in `Program.cs`: `AddAuthentication(JwtBearerDefaults)`, `AddJwtBearer()` with single `IssuerSigningKey`, `ClockSkew = TimeSpan.Zero` in `src/Program.cs` (dual-key rotation upgrade deferred to T057)
- [ ] T009 Create `ITokenService` interface with `GenerateAccessToken`, `GenerateRefreshToken`, `HashToken` methods in `src/Features/Identity/Services/ITokenService.cs`
- [ ] T010 Implement `TokenService`: JWT generation with claims (`sub`, `email`, `name`, `jti`, `iat`, `exp`, `iss`, `aud`), cryptographic refresh token generation (32-byte `RandomNumberGenerator`), SHA-256 hashing in `src/Features/Identity/Services/TokenService.cs`
- [ ] T011 Create `IAuthService` interface with `RegisterAsync`, `LoginAsync`, `RefreshAsync`, `LogoutAsync` methods in `src/Features/Identity/Services/IAuthService.cs`
- [ ] T011.1 Create `IProfileService` interface with `GetProfileAsync`, `UpdateProfileAsync`, `ChangePasswordAsync` methods in `src/Features/Identity/Services/IProfileService.cs`
- [ ] T012 [P] Create all domain event classes: `UserRegisteredEvent`, `UserLoggedInEvent`, `TokenRefreshedEvent`, `UserLoggedOutEvent`, `PasswordChangedEvent`, `RefreshTokenCompromiseDetectedEvent` in `src/Features/Identity/Events/`
- [ ] T013 [P] Create shared response model: `AuthTokenResponse` (accessToken, refreshToken, expiresAtUtc) in `src/Features/Identity/Models/Responses/AuthTokenResponse.cs`
- [ ] T014 Register Identity DI services (`ITokenService`/`TokenService`, `IAuthService`/`AuthService`, `IProfileService`/`ProfileService`) in `src/Program.cs`. Controllers are auto-discovered via `AddControllers()` — no manual endpoint mapping needed.
- [ ] T014.1 [P] Create `AuthController.cs` controller definition (partial class, `[ApiController]`, `[Route("api/auth")]`, ctor with `IAuthService`, `ITokenService`) in `src/Features/Identity/Endpoints/Auth/AuthController.cs`
- [ ] T014.2 [P] Create `TokenController.cs` controller definition (partial class, `[ApiController]`, `[Route("api/auth/tokens")]`, ctor with `ITokenService`) in `src/Features/Identity/Endpoints/Token/TokenController.cs`
- [ ] T014.3 [P] Create `ProfileController.cs` controller definition (partial class, `[ApiController]`, `[Route("api/auth/profile")]`, ctor with `IProfileService`) in `src/Features/Identity/Endpoints/Profile/ProfileController.cs`
- [ ] T060 Set up MediatR logging pipeline behavior to automatically log all published domain events with correlation IDs in `src/Shared/Behaviors/DomainEventLoggingBehavior.cs`
- [ ] T015 Generate EF Core migration for Identity tables and `RefreshToken` entity

**Checkpoint**: Foundation ready — Identity entities, token service, auth service interface, and JWT configuration in place. User story implementation can now begin.

---

## Phase 3: User Story 1 — New User Registration (Priority: P1) 🎯 MVP

**Goal**: Allow new users to register with email, display name, and password. Account is created and immediately usable for login.

**Independent Test**: Submit a registration request with valid credentials → verify account exists → verify login works with registered credentials.

### Tests for User Story 1

- [ ] T016 [P] [US1] Unit test for `RegisterRequestValidator` (valid input, duplicate email, weak password, malformed email) in `tests/Unit/Identity/ValidatorTests.cs`
- [ ] T017 [P] [US1] Integration test for register endpoint (success, duplicate email, validation errors) in `tests/Integration/Identity/RegisterEndpointTests.cs`

### Implementation for User Story 1

- [ ] T018 [P] [US1] Create `RegisterRequest` model (email, displayName, password) in `src/Features/Identity/Models/Requests/RegisterRequest.cs`
- [ ] T019 [P] [US1] Create `RegisterResponse` model (userId, email, displayName) in `src/Features/Identity/Models/Responses/RegisterResponse.cs`
- [ ] T020 [US1] Create `RegisterRequestValidator` with FluentValidation: email format + uniqueness, displayName required (max 100), password strength policy in `src/Features/Identity/Validators/RegisterRequestValidator.cs`
- [ ] T021 [US1] Implement `RegisterAsync` in `AuthService`: create user via `UserManager`, set `DisplayName`, `CreatedAtUtc`, publish `UserRegisteredEvent`, return `RegisterResponse` in `src/Features/Identity/Services/AuthService.cs`
- [ ] T022 [US1] Create `RegisterEndpoint.cs` (partial class `AuthController`, POST `register`): validate request, call `AuthService.RegisterAsync`, return `Ok(result.Value)` on success or `result.ToProblem(correlationIdProvider)` on failure in `src/Features/Identity/Endpoints/Auth/RegisterEndpoint.cs`

**Checkpoint**: User Story 1 complete — new users can register. Verify by submitting a registration request and confirming account creation.

---

## Phase 4: User Story 2 — User Login & Token Issuance (Priority: P1)

**Goal**: Registered users authenticate with email/password and receive a JWT access token (15-min) and refresh token (7-day). Generic error on failure prevents user enumeration.

**Independent Test**: Register a user → login → verify JWT access token and refresh token returned → verify invalid credentials return generic error.

### Tests for User Story 2

- [ ] T023 [P] [US2] Unit test for `TokenService`: JWT generation with correct claims and exact 15-minute expiry (SC-008), refresh token generation with exact 7-day expiry (SC-008), SHA-256 hashing in `tests/Unit/Identity/TokenServiceTests.cs`
- [ ] T024 [P] [US2] Unit test for `LoginRequestValidator` (valid input, missing fields) in `tests/Unit/Identity/ValidatorTests.cs`
- [ ] T025 [P] [US2] Integration test for login endpoint (success, wrong password, non-existent email, user enumeration prevention with response timing uniformity assertion per SC-004) in `tests/Integration/Identity/LoginEndpointTests.cs`

### Implementation for User Story 2

- [ ] T026 [P] [US2] Create `LoginRequest` model (email, password) in `src/Features/Identity/Models/Requests/LoginRequest.cs`
- [ ] T027 [US2] Create `LoginRequestValidator` with FluentValidation: email required, password required in `src/Features/Identity/Validators/LoginRequestValidator.cs`
- [ ] T028 [US2] Implement `LoginAsync` in `AuthService`: validate credentials via `UserManager.CheckPasswordAsync`, generate access token + refresh token via `TokenService`, store hashed refresh token with `FamilyId`, publish `UserLoggedInEvent`, return `AuthTokenResponse` in `src/Features/Identity/Services/AuthService.cs`
- [ ] T029 [US2] Create `LoginEndpoint.cs` (partial class `AuthController`, POST `login`): validate request, call `AuthService.LoginAsync`, return `Ok(result.Value)` on success or `result.ToProblem(correlationIdProvider)` on failure in `src/Features/Identity/Endpoints/Auth/LoginEndpoint.cs`

**Checkpoint**: User Story 2 complete — users can register and login. Verify by registering, logging in, and confirming valid tokens are returned.

---

## Phase 5: User Story 3 — Token Refresh (Priority: P1)

**Goal**: Exchange a valid refresh token for a new access token + rotated refresh token. Detect compromise via family-based reuse detection with 30-second grace period.

**Independent Test**: Login → use refresh token → verify new tokens returned and old refresh token is invalidated → reuse old token after grace period → verify all family tokens revoked.

### Tests for User Story 3

- [ ] T030 [P] [US3] Unit test for refresh token rotation logic: successful rotation, grace period acceptance, post-grace reuse triggers family revocation, expired token rejection in `tests/Unit/Identity/AuthServiceTests.cs`
- [ ] T031 [P] [US3] Integration test for refresh endpoint (success, expired token, reuse after grace period → compromise detection, cross-user token rejection) in `tests/Integration/Identity/RefreshEndpointTests.cs`

### Implementation for User Story 3

- [ ] T032 [P] [US3] Create `RefreshTokenRequest` model (refreshToken) in `src/Features/Identity/Models/Requests/RefreshTokenRequest.cs`
- [ ] T061 [US3] Create `RefreshRequestValidator` with FluentValidation: refreshToken required, non-empty in `src/Features/Identity/Validators/RefreshRequestValidator.cs`
- [ ] T033 [US3] Implement `RefreshAsync` in `AuthService`: hash incoming token, lookup by `TokenHash`, check expiry, check revocation status, handle grace period logic (`GracePeriodExpiresAtUtc`), detect reuse after grace → revoke all tokens with same `FamilyId`, on success: mark old token as revoked with `GracePeriodExpiresAtUtc = UtcNow + 30s`, generate new token pair with same `FamilyId`, set `ReplacedByTokenHash`, publish `TokenRefreshedEvent` or `RefreshTokenCompromiseDetectedEvent`, return `AuthTokenResponse` in `src/Features/Identity/Services/AuthService.cs`
- [ ] T034 [US3] Create `RefreshTokenEndpoint.cs` (partial class `TokenController`, POST `refresh`): call `AuthService.RefreshAsync`, return `Ok(result.Value)` on success or `result.ToProblem(correlationIdProvider)` on failure in `src/Features/Identity/Endpoints/Token/RefreshTokenEndpoint.cs`

**Checkpoint**: User Story 3 complete — full token lifecycle works. Verify by logging in, refreshing tokens, and testing compromise detection.

---

## Phase 6: User Story 4 — User Logout (Priority: P2)

**Goal**: Authenticated user explicitly ends session by invalidating current refresh token. Access token remains valid until natural expiry.

**Independent Test**: Login → logout with refresh token → verify refresh token can no longer be used to obtain new tokens.

### Tests for User Story 4

- [ ] T035 [P] [US4] Integration test for logout endpoint (success, already-logged-out token reuse fails) in `tests/Integration/Identity/LogoutEndpointTests.cs`

### Implementation for User Story 4

- [ ] T036 [P] [US4] Create `LogoutRequest` model (refreshToken) in `src/Features/Identity/Models/Requests/LogoutRequest.cs`
- [ ] T062 [US4] Create `LogoutRequestValidator` with FluentValidation: refreshToken required, non-empty in `src/Features/Identity/Validators/LogoutRequestValidator.cs`
- [ ] T037 [US4] Implement `LogoutAsync` in `AuthService`: hash incoming token, lookup by `TokenHash`, mark as revoked (`IsRevoked = true`, `RevokedAtUtc = UtcNow`), publish `UserLoggedOutEvent` in `src/Features/Identity/Services/AuthService.cs`
- [ ] T038 [US4] Create `LogoutEndpoint.cs` (partial class `AuthController`, POST `logout`, requires Bearer auth): validate request, call `AuthService.LogoutAsync`, return `Ok(result.Value)` on success or `result.ToProblem(correlationIdProvider)` on failure in `src/Features/Identity/Endpoints/Auth/LogoutEndpoint.cs`

**Checkpoint**: User Story 4 complete — users can explicitly end sessions. Verify by logging in, logging out, and confirming the refresh token is invalid.

---

## Phase 7: User Story 5 — View Own Profile (Priority: P2)

**Goal**: Authenticated user retrieves their own profile information (name, email, creation date).

**Independent Test**: Login → call GET `/api/auth/profile` with valid access token → verify returned data matches registered information.

### Tests for User Story 5

- [ ] T039 [P] [US5] Integration test for profile endpoint (success with valid token, 401 without token) in `tests/Integration/Identity/ProfileEndpointTests.cs`

### Implementation for User Story 5

- [ ] T040 [P] [US5] Create `UserProfileResponse` model (userId, email, displayName, createdAtUtc) in `src/Features/Identity/Models/Responses/UserProfileResponse.cs`
- [ ] T041 [US5] Implement `GetProfileAsync` in `ProfileService`: extract user ID from claims, load user via `UserManager`, map to `UserProfileResponse` via Mapster in `src/Features/Identity/Services/ProfileService.cs`
- [ ] T042 [US5] Create `GetCurrentUserEndpoint.cs` (partial class `ProfileController`, GET, requires Bearer auth): extract authenticated user, call `ProfileService.GetProfileAsync`, return `Ok(result.Value)` on success or `result.ToProblem(correlationIdProvider)` on failure in `src/Features/Identity/Endpoints/Profile/GetCurrentUserEndpoint.cs`

**Checkpoint**: User Story 5 complete — authenticated users can view their profile.

---

## Phase 8: User Story 6 — Update Profile (Priority: P3)

**Goal**: Authenticated user updates display name. Email changes are not permitted through this endpoint.

**Independent Test**: Login → update display name → call GET `/api/auth/me` → verify updated name is returned.

### Tests for User Story 6

- [ ] T043 [P] [US6] Unit test for `UpdateProfileRequestValidator` (valid name, empty name, exceeds max length) in `tests/Unit/Identity/ValidatorTests.cs`
- [ ] T044 [P] [US6] Integration test for profile update endpoint (success, validation errors, 401 without token) in `tests/Integration/Identity/ProfileEndpointTests.cs`

### Implementation for User Story 6

- [ ] T045 [P] [US6] Create `UpdateProfileRequest` model (displayName) in `src/Features/Identity/Models/Requests/UpdateProfileRequest.cs`
- [ ] T046 [US6] Create `UpdateProfileRequestValidator` with FluentValidation: displayName required, max 100 chars, trimmed in `src/Features/Identity/Validators/UpdateProfileRequestValidator.cs`
- [ ] T047 [US6] Implement `UpdateProfileAsync` in `ProfileService`: extract user ID from claims, load user, update `DisplayName` and `UpdatedAtUtc`, save, map to `UserProfileResponse` via Mapster in `src/Features/Identity/Services/ProfileService.cs`
- [ ] T048 [US6] Create `UpdateProfileEndpoint.cs` (partial class `ProfileController`, PUT, requires Bearer auth): validate request, call `ProfileService.UpdateProfileAsync`, return `Ok(result.Value)` on success or `result.ToProblem(correlationIdProvider)` on failure in `src/Features/Identity/Endpoints/Profile/UpdateProfileEndpoint.cs`

**Checkpoint**: User Story 6 complete — authenticated users can update their display name.

---

## Phase 9: User Story 7 — Change Password (Priority: P3)

**Goal**: Authenticated user changes password by providing current password and valid new password. All existing refresh tokens for the user are revoked (force re-authentication on other sessions).

**Independent Test**: Login → change password → verify old password fails login → verify new password works for login → verify all existing refresh tokens are revoked.

### Tests for User Story 7

- [ ] T049 [P] [US7] Unit test for `ChangePasswordRequestValidator` (valid input, weak new password, missing current password) in `tests/Unit/Identity/ValidatorTests.cs`
- [ ] T050 [P] [US7] Integration test for change password endpoint (success, wrong current password, weak new password, all sessions revoked) in `tests/Integration/Identity/ChangePasswordEndpointTests.cs`

### Implementation for User Story 7

- [ ] T051 [P] [US7] Create `ChangePasswordRequest` model (currentPassword, newPassword) in `src/Features/Identity/Models/Requests/ChangePasswordRequest.cs`
- [ ] T052 [US7] Create `ChangePasswordRequestValidator` with FluentValidation: currentPassword required, newPassword strength policy (min 8, uppercase, lowercase, digit, special) in `src/Features/Identity/Validators/ChangePasswordRequestValidator.cs`
- [ ] T053 [US7] Implement `ChangePasswordAsync` in `ProfileService`: verify current password via `UserManager.CheckPasswordAsync`, change password via `UserManager.ChangePasswordAsync`, revoke ALL refresh tokens for user (`UPDATE WHERE UserId = @userId`), publish `PasswordChangedEvent`, return success in `src/Features/Identity/Services/ProfileService.cs`
- [ ] T054 [US7] Create `ChangePasswordEndpoint.cs` (partial class `ProfileController`, POST `change-password`, requires Bearer auth): validate request, call `ProfileService.ChangePasswordAsync`, return `Ok(result.Value)` on success or `result.ToProblem(correlationIdProvider)` on failure in `src/Features/Identity/Endpoints/Profile/ChangePasswordEndpoint.cs`

**Checkpoint**: User Story 7 complete — users can change passwords and all other sessions are forcibly invalidated.

---

## Phase 10: Polish & Cross-Cutting Concerns

**Purpose**: Audit logging, authorization scaffolding, dual-key rotation support, and end-to-end validation

- [ ] T055 Audit structured logging completeness: verify all authentication events (register, login, logout, refresh, password change, compromise detection) are logged with correlation IDs via domain event logging pipeline (T060) in `src/Features/Identity/Services/AuthService.cs`
- [ ] T056 [P] Implement policy-based authorization scaffolding for downstream features (FR-016) in `src/Program.cs`
- [ ] T057 [P] Implement JWT dual-key rotation support: load multiple signing keys from configuration, include `kid` header in generated tokens, validate against all active keys, support 15-minute overlap window in `src/Features/Identity/Services/TokenService.cs` and upgrade `Program.cs` JWT config from single key (T008) to `IssuerSigningKeys` (plural)
- [ ] T063 [P] Load test: verify 100 concurrent authentication requests complete with p95 < 300 ms and 0% error rate (SC-007)
- [ ] T064 [P] Audit all async method signatures across Identity feature for `CancellationToken` parameter presence (constitution compliance)
- [ ] T058 Run full end-to-end flow: register → login → refresh → access profile → update profile → change password → verify old sessions revoked → re-login with new password
- [ ] T059 Run quickstart.md validation: verify all steps in `specs/001-user-identity-auth/quickstart.md` are accurate and reproducible

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — can start immediately
- **Foundational (Phase 2)**: Depends on Setup completion — BLOCKS all user stories
- **User Story 1 — Registration (Phase 3)**: Depends on Foundational (Phase 2)
- **User Story 2 — Login (Phase 4)**: Depends on Foundational (Phase 2). Needs `AuthService` shell from Phase 2 but implements `LoginAsync` independently
- **User Story 3 — Token Refresh (Phase 5)**: Depends on User Story 2 (needs login to obtain tokens)
- **User Story 4 — Logout (Phase 6)**: Depends on User Story 2 (needs login to have a refresh token to revoke)
- **User Story 5 — View Profile (Phase 7)**: Depends on User Story 2 (needs login for Bearer token)
- **User Story 6 — Update Profile (Phase 8)**: Depends on Foundational (Phase 2). Shares `UserProfileResponse` DTO with User Story 5 — can start in parallel with US5
- **User Story 7 — Change Password (Phase 9)**: Depends on User Story 2 (needs login for Bearer token) and uses token revocation logic from User Story 3
- **Polish (Phase 10)**: Depends on all user stories being complete

### User Story Dependencies

- **US1 (Registration)**: Independent after Foundational — no dependencies on other stories
- **US2 (Login)**: Independent after Foundational — uses `TokenService` from Foundational
- **US3 (Token Refresh)**: Requires US2 (needs existing refresh tokens from login)
- **US4 (Logout)**: Requires US2 (needs existing session to end)
- **US5 (View Profile)**: Requires US2 (needs Bearer auth)
- **US6 (Update Profile)**: Independent after Foundational — shares `UserProfileResponse` DTO with US5 (can run in parallel)
- **US7 (Change Password)**: Requires US2 + US3's revocation pattern (reuses bulk token revocation)

### Within Each User Story

- Tests written first, verified to FAIL before implementation
- Request/Response models before validators
- Validators before service implementation
- Service implementation before endpoint
- Endpoint file uses partial class extending the controller definition
- Story complete and testable before moving to next priority

### Parallel Opportunities

- **Phase 1**: T002 and T003 can run in parallel (separate test projects)
- **Phase 2**: T012 and T013 can run in parallel (separate event/model files); T014.1, T014.2, T014.3 can run in parallel (separate controller definition files); T060 after T014
- **Phase 3 (US1)**: T016/T017 (tests) in parallel; T018/T019 (models) in parallel
- **Phase 4 (US2)**: T023/T024/T025 (tests) in parallel; T026 (model) before T027–T029
- **Phase 5 (US3)**: T030/T031 (tests) in parallel; T032 (model) before T061 (validator), then T033–T034
- **Phase 6 (US4)**: T035 (test) and T036 (model) in parallel; T062 (validator) before T037–T038
- **Phase 7 (US5)**: T039 (test) and T040 (model) in parallel
- **Phase 8 (US6)**: T043/T044 (tests) in parallel; T045 (model) in parallel with tests
- **Phase 9 (US7)**: T049/T050 (tests) in parallel; T051 (model) in parallel with tests
- **Phase 10**: T055, T056, T057, T063, T064 can all run in parallel (different files/concerns)

---

## Parallel Example: User Story 1 (Registration)

```text
# Launch tests in parallel (write first, expect them to FAIL):
Task T016: Unit test for RegisterRequestValidator in tests/Unit/Identity/ValidatorTests.cs
Task T017: Integration test for register endpoint in tests/Integration/Identity/RegisterEndpointTests.cs

# Launch Models in parallel:
Task T018: RegisterRequest model in src/Features/Identity/Models/Requests/RegisterRequest.cs
Task T019: RegisterResponse model in src/Features/Identity/Models/Responses/RegisterResponse.cs

# Sequential (dependencies):
Task T020: RegisterRequestValidator (depends on T018 model)
Task T021: AuthService.RegisterAsync (depends on T020 validator)
Task T022: RegisterEndpoint.cs in Auth/ (depends on T021 service, uses AuthController partial class)
```

---

## Implementation Strategy

### MVP First (User Stories 1–3 Only)

1. Complete Phase 1: Setup
2. Complete Phase 2: Foundational (CRITICAL — blocks all stories)
3. Complete Phase 3: User Story 1 — Registration
4. Complete Phase 4: User Story 2 — Login & Token Issuance
5. Complete Phase 5: User Story 3 — Token Refresh
6. **STOP and VALIDATE**: Full register → login → refresh cycle works end-to-end
7. Deploy/demo if ready — core auth is functional

### Incremental Delivery

1. Setup + Foundational → Foundation ready
2. Add US1 (Registration) → Users can create accounts (MVP start)
3. Add US2 (Login) → Users can authenticate and receive tokens
4. Add US3 (Token Refresh) → Sessions persist without re-login (MVP complete!)
5. Add US4 (Logout) → Explicit session termination
6. Add US5 (View Profile) → Users can see their account info
7. Add US6 (Update Profile) → Users can modify their display name
8. Add US7 (Change Password) → Users can rotate credentials with session revocation
9. Polish → Logging, auth scaffolding, key rotation, end-to-end validation

### Notes

- US1, US2, and US3 are all P1 — they form the minimum viable auth system
- US4 and US5 are P2 — important but system works without them
- US6 and US7 are P3 — convenience features, not blocking downstream phases
- Each story adds value without breaking previous stories
