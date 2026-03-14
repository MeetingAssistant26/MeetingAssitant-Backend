# Feature Specification: User Identity & Authentication

**Feature Branch**: `001-user-identity-auth`  
**Created**: 2026-03-07  
**Status**: Draft  
**Input**: User description: "User identity and authentication system — registration, login, JWT access tokens, refresh token rotation, profile management, and password change"

## User Scenarios & Testing *(mandatory)*

### User Story 1 - New User Registration (Priority: P1)

A new user registers in the system. Registration **requires** an organization — standalone registration is not allowed. Two flows are supported:

- **Scenario A — Create Organization**: User provides name, email, password, and organization name. The system creates the user account, creates the organization, creates a `UserOrgMembership` (org_role = Admin, is_enabled = true), and issues a JWT with `userId` + `organizationId`.
- **Scenario B — Join via Invitation**: User opens an invitation link, provides name, email, and password. The system creates the user account, creates a `UserOrgMembership` (org_role = Member, is_enabled = true), and issues a JWT with `userId` + `organizationId`.

**Why this priority**: Without registration, no other feature in the system is accessible. This is the entry point for every user journey.

**Independent Test**: Can be fully tested by submitting a registration request with valid credentials (with org name for Scenario A, or invitation token for Scenario B) and verifying the account exists, the membership is created, and a JWT with `organizationId` is returned.

**Acceptance Scenarios**:

1. **Given** a user provides a valid email, name, password, and organization name (Scenario A), **When** they submit the registration request, **Then** the user account is created, an organization is created, a `UserOrgMembership` is created with org_role = Admin, and a JWT with `userId` + `organizationId` is returned.
2. **Given** a user opens a valid invitation link and provides a valid email, name, and password (Scenario B), **When** they submit the registration request, **Then** the user account is created, a `UserOrgMembership` is created with org_role = Member, and a JWT with `userId` + `organizationId` is returned.
3. **Given** a user provides an email that is already registered, **When** they submit the registration request, **Then** the system rejects the request with a clear error indicating the email is taken.
4. **Given** a user provides a password that does not meet strength requirements, **When** they submit the registration request, **Then** the system rejects the request with a clear error describing the password policy.
5. **Given** a user already has an active `UserOrgMembership`, **When** they attempt to register via invitation (Scenario B), **Then** the system rejects the request with error: "You already belong to an organization."

---

### User Story 2 - User Login & Token Issuance (Priority: P1)

A registered user provides their email and password to authenticate. On success, the system issues a short-lived JWT access token and a longer-lived refresh token. The user uses the access token to authorize subsequent requests.

**Why this priority**: Login is the gateway to all authenticated functionality. Without it, no protected resources can be accessed.

**Independent Test**: Can be fully tested by registering a user, logging in, and verifying that a valid JWT access token and refresh token are returned in the response.

**Acceptance Scenarios**:

1. **Given** a registered user provides correct credentials, **When** they submit the login request, **Then** the system returns a JWT access token (15-minute expiry) containing `userId` + `organizationId` claims, and a refresh token (7-day expiry). The `organizationId` is resolved from the user's active `UserOrgMembership`.
2. **Given** a user provides an incorrect password, **When** they submit the login request, **Then** the system rejects the request with a generic "invalid credentials" error (no indication of which field is wrong).
3. **Given** a user provides a non-existent email, **When** they submit the login request, **Then** the system returns the same generic "invalid credentials" error (preventing user enumeration).

---

### User Story 3 - Token Refresh (Priority: P1)

When a user's access token expires, the client submits the refresh token to obtain a new access token without requiring the user to re-enter credentials. The old refresh token is invalidated and a new one is issued (rotation).

**Why this priority**: Without token refresh, users are forced to re-login every 15 minutes, making the system unusable for any sustained workflow (e.g., meetings that last longer than 15 minutes).

**Independent Test**: Can be fully tested by logging in, waiting for or simulating access token expiry, submitting the refresh token, and verifying a new access token and rotated refresh token are returned.

**Acceptance Scenarios**:

1. **Given** a user has a valid refresh token, **When** they submit it to the refresh endpoint, **Then** the system returns a new JWT access token and a new refresh token, and the old refresh token is invalidated.
2. **Given** a user submits an already-used (rotated) refresh token, **When** the system processes the request, **Then** the request is rejected and all refresh tokens for that user are revoked (compromise detection).
3. **Given** a user submits an expired refresh token (older than 7 days), **When** the system processes the request, **Then** the request is rejected and the user must re-authenticate via login.

---

### User Story 4 - User Logout (Priority: P2)

A logged-in user explicitly ends their session. The system invalidates their current refresh token, preventing further token refresh. The access token remains valid until its natural expiry (15 minutes) but no new tokens can be issued.

**Why this priority**: Logout is important for security hygiene, but the system remains functional without it (tokens expire naturally). Prioritized after core auth flows.

**Independent Test**: Can be fully tested by logging in, logging out, and verifying the refresh token can no longer be used to obtain a new access token.

**Acceptance Scenarios**:

1. **Given** a logged-in user with a valid refresh token, **When** they submit a logout request, **Then** their refresh token is invalidated and a success response is returned.
2. **Given** a user has already logged out, **When** they attempt to use the old refresh token, **Then** the system rejects the request.

---

### User Story 5 - View Own Profile (Priority: P2)

An authenticated user retrieves their own profile information (name, email, and other basic details). This allows the user to verify their account details.

**Why this priority**: Useful for user awareness and downstream features (e.g., displaying name in meeting context), but not required for core auth functionality.

**Independent Test**: Can be fully tested by logging in, calling the profile endpoint with a valid access token, and verifying the returned data matches the registered information.

**Acceptance Scenarios**:

1. **Given** an authenticated user with a valid access token, **When** they request their profile, **Then** the system returns their name, email, and account details.
2. **Given** a request without a valid access token, **When** sent to the profile endpoint, **Then** the system returns a 401 Unauthorized response.

---

### User Story 6 - Update Profile (Priority: P3)

An authenticated user updates their profile information (e.g., display name). Email changes are not permitted through this endpoint to maintain account integrity.

**Why this priority**: Nice-to-have for user personalization, but not critical for the core authentication system or downstream features.

**Independent Test**: Can be fully tested by logging in, updating the display name, and verifying the profile endpoint returns the updated value.

**Acceptance Scenarios**:

1. **Given** an authenticated user, **When** they submit a profile update with a new display name, **Then** the profile is updated and the new name is returned.
2. **Given** an authenticated user, **When** they submit a profile update with invalid data (e.g., empty name), **Then** the system rejects the request with validation errors.

---

### User Story 7 - Change Password (Priority: P3)

An authenticated user changes their password by providing their current password and a new password. All existing refresh tokens (other sessions) are invalidated upon password change for security.

**Why this priority**: Important for security but not required for core auth flow. Users can manage with their initial password during early development.

**Independent Test**: Can be fully tested by logging in, changing the password, logging out, and verifying the new password works for login while the old password does not.

**Acceptance Scenarios**:

1. **Given** an authenticated user provides the correct current password and a valid new password, **When** they submit the change, **Then** the password is updated and all existing refresh tokens are revoked.
2. **Given** an authenticated user provides an incorrect current password, **When** they submit the change, **Then** the request is rejected.
3. **Given** an authenticated user provides a new password that does not meet strength requirements, **When** they submit the change, **Then** the request is rejected with a clear error describing the policy.

---

### Edge Cases

- What happens when a user tries to register with a malformed email address (e.g., missing @, special characters)?
- Concurrent refresh token usage from two clients (race condition) → **Resolved**: 30-second grace period on rotated tokens prevents false-positive compromise detection. Reuse after the grace window triggers full revocation.
- JWT signing key rotation → **Resolved**: Dual-key validation — system accepts both old and new signing keys for 15 minutes (one access token lifetime), then drops the old key. No forced user logout.
- How does the system behave when the database is temporarily unreachable during a login attempt? → **Deferred**: Infrastructure resilience (connection retries, circuit breaker) is out of scope for this feature. The global error handler returns a 500 response.
- What happens when a user submits a refresh token that belongs to a different user (token theft attempt)? → **Resolved**: Tokens are looked up by SHA-256 hash. A token always resolves to its original owner — cross-user submission simply fails hash lookup and is rejected as invalid.
- How does the system handle rapid repeated login attempts (brute force protection)? → **Deferred**: Account lockout is deferred to security hardening phase (see Assumptions). Failed attempts are logged for auditing.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST allow new users to register with a unique email address, display name, and password.
- **FR-002**: System MUST validate that email addresses are well-formed and unique across all accounts.
- **FR-003**: System MUST enforce a password strength policy (minimum length, complexity rules).
- **FR-004**: System MUST authenticate users via email and password, returning a JWT access token and a refresh token on success.
- **FR-005**: System MUST issue JWT access tokens with a 15-minute expiry containing the user's identity claims (`userId`) **and `organizationId` claim** (resolved from the user's active `UserOrgMembership` record during login).
- **FR-006**: System MUST issue refresh tokens with a 7-day expiry, stored as hashed values (never plaintext).
- **FR-007**: System MUST implement refresh token rotation — every use of a refresh token invalidates the old one and issues a new one.
- **FR-008**: System MUST detect refresh token reuse (compromise detection) and revoke all tokens for the affected user when a previously-rotated token is submitted **after a 30-second grace period**. Within the grace window, the old token is still accepted to prevent false-positive lockouts in multi-tab/multi-device scenarios.
- **FR-009**: System MUST allow authenticated users to log out, invalidating their current refresh token.
- **FR-010**: System MUST allow authenticated users to retrieve their own profile information.
- **FR-011**: System MUST allow authenticated users to update their display name.
- **FR-012**: System MUST allow authenticated users to change their password by providing the current password and a valid new password.
- **FR-013**: System MUST revoke all refresh tokens for a user when their password is changed (force re-authentication on other sessions).
- **FR-014**: System MUST return generic error messages for failed login attempts to prevent user enumeration.
- **FR-015**: System MUST log all authentication events (registration, login, logout, token refresh, password change) for auditing.
- **FR-016**: System MUST expose a policy-based authorization scaffolding that downstream features can extend with role-based and organization-scoped policies.
- **FR-017**: System MUST support JWT signing key rotation with dual-key validation — both old and new keys are accepted for 15 minutes (one access token lifetime), after which the old key is dropped. No forced user logout during rotation.
- **FR-018**: System MUST require an organization during registration. Two registration flows MUST be supported: (A) register with `organization_name` to create a new organization (user becomes Admin), (B) register with `invitation_token` to join an existing organization (user becomes Member). Standalone registration without an organization MUST NOT be allowed.
- **FR-019**: System MUST reject any attempt by a user who already has an active `UserOrgMembership` to join another organization. The error MUST clearly state the user already belongs to an organization.
- **FR-020**: System MUST carry `organizationId` from the existing token during token refresh. If the user's `UserOrgMembership` has been deactivated (`is_enabled = false`), token refresh MUST fail.

### Key Entities

- **User Account**: Represents a registered individual. Key attributes: unique identifier, email, display name, hashed password. Extends the platform's identity framework.
- **Refresh Token**: Represents an active session token. Key attributes: hashed token value, expiry date, creation date, revocation status, associated user. Supports rotation tracking to detect reuse.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Users can complete the full registration flow (submit form → account created) in under 30 seconds.
- **SC-002**: Users can complete the login flow (submit credentials → receive tokens) in under 2 seconds.
- **SC-003**: Token refresh completes in under 1 second without user interaction.
- **SC-004**: 100% of login attempts with invalid credentials return a response within 3 seconds (no timing-based user enumeration).
- **SC-005**: Refresh token reuse is detected and all tokens for the affected user are revoked immediately on the first reuse attempt **after the 30-second grace period**. Reuse within the grace window is accepted per FR-008.
- **SC-006**: 100% of authentication events (register, login, logout, refresh, password change) are recorded in the audit log with correlation IDs.
- **SC-007**: System supports at least 100 concurrent authentication requests with p95 response time < 300 ms and 0% error rate.
- **SC-008**: All access tokens expire exactly at 15 minutes; all refresh tokens expire exactly at 7 days — verified by automated tests.

## Clarifications

### Session 2026-03-07

- Q: How should the system handle concurrent refresh token usage from two clients (race condition — e.g., two browser tabs sending the same token simultaneously)? → A: Grace period (30 seconds) — the old refresh token remains valid for 30 seconds after rotation, preventing false-positive compromise detection for legitimate multi-tab/multi-device users. Reuse after the grace window triggers full token revocation.
- Q: What happens when the JWT signing key is rotated — are existing tokens still validated or immediately invalidated? → A: Dual-key validation — accept both old and new signing keys for 15 minutes (one access token lifetime), then drop the old key. Zero user disruption; all old tokens expire naturally.

## Assumptions

- Password strength policy follows industry standards: minimum 8 characters, at least one uppercase letter, one lowercase letter, one digit, and one special character. This can be adjusted during implementation.
- Email verification (confirm email via link) is out of scope for the initial implementation. Users can log in immediately after registration.
- "Forgot password" / password reset via email is out of scope for this feature. It may be added as a separate feature later.
- Account lockout after repeated failed login attempts is deferred to a security hardening phase. For now, the system logs failed attempts for auditing.
- The JWT signing key is loaded from secure configuration (user-secrets in dev, environment variables in Docker) per the project constitution.
- Rate limiting on authentication endpoints is deferred to infrastructure-level configuration (e.g., reverse proxy), not implemented at the application level in this feature.

## Dependencies

- **Phase 0 (Infrastructure & Foundation)**: Requires the completed foundation — database context with tenant isolation, base entity, correlation ID middleware, Hangfire configuration, JWT configuration, secrets management, and input validation registration.
- **Constitution v1.4.0**: All security rules (§V) must be followed — JWT 15-min expiry with `userId` + `organizationId` claims, refresh token 7-day expiry with hashed storage and rotation, policy-based authorization, no hardcoded secrets. Registration must require organization (§II). Endpoint architecture must follow the Partial Controller Pattern (§I). Endpoint error responses must use `result.ToProblem(correlationIdProvider)` (§I).

## Out of Scope

- Email verification / confirmation flows
- Password reset ("forgot password") via email
- Social login / OAuth2 provider integration (Google, GitHub, etc.)
- Multi-factor authentication (MFA/2FA)
- Account lockout after failed attempts (deferred to hardening phase)
- Organization-scoped authorization policies (handled in Phase 2)
- Meeting-role authorization policies (handled in Phase 4)
