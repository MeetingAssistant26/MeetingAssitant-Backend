# Research: Agent-Callable API Surface

**Feature**: Agent-Callable API Surface (Phase 5.7)  
**Date**: 2026-05-03  
**Status**: Complete

## Research Questions

### Q1: How to implement dual JWT authentication in ASP.NET Core?

**Decision**: Use `AddAuthentication().AddJwtBearer("AgentScheme", ...)` with separate validation parameters.

**Rationale**: ASP.NET Core supports multiple authentication schemes out of the box. By registering a second JWT bearer scheme with a distinct issuer/signing key, we achieve clean separation between user and agent identities without custom middleware.

**Implementation**:
```csharp
services.AddAuthentication()
    .AddJwtBearer("UserJwt", options => { /* existing user config */ })
    .AddJwtBearer("AgentJwt", options => 
    {
        options.Authority = null; // use local validation
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(agentKeyBytes),
            ValidateIssuer = true,
            ValidIssuer = "MeetingAssistant-Agent",
            ValidateAudience = true,
            ValidAudience = "MeetingAssistant-Agent",
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };
    });

services.AddAuthorization(options =>
{
    options.AddPolicy("AgentOnly", policy =>
        policy.RequireAuthenticatedUser()
              .RequireClaim("agent", "true")
              .AddAuthenticationSchemes("AgentJwt"));
});
```

**Alternatives considered**:
- Single scheme with role claim discrimination: Rejected — risk of token confusion and privilege escalation.
- Custom middleware with HMAC validation: Rejected — `AddJwtBearer` is standard and well-tested.

---

### Q2: How to implement per-meeting rate limiting?

**Decision**: Use ASP.NET Core 7+ Rate Limiting middleware with a custom `PartitionedRateLimiter` keyed on `meetingId` from the agent token.

**Rationale**: Built-in rate limiting is sufficient for single-instance deployment. Partitioning by `meetingId` ensures one noisy agent doesn't throttle others.

**Implementation**:
```csharp
services.AddRateLimiter(options =>
{
    options.AddPolicy("AgentPerMeeting", httpContext =>
    {
        var meetingId = httpContext.User.FindFirst("meetingId")?.Value;
        if (string.IsNullOrEmpty(meetingId))
            return RateLimitPartition.GetNoLimiter("anonymous");
        
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: $"agent:{meetingId}",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            });
    });
});
```

**Alternatives considered**:
- Redis-backed rate limiting: Rejected — premature optimization; can migrate later.
- Custom action filter: Rejected — middleware approach is more maintainable.

---

### Q3: Where should agent token refresh live?

**Decision**: Add `POST /api/agent/refresh` as part of `AgentContextController` group.

**Rationale**: Token refresh is an agent auth concern. Grouping it with context endpoints keeps the auth surface together. If the surface grows, it can be extracted to a dedicated `AgentAuthController`.

---

## Summary

No external research was required. All technical questions were resolved using established ASP.NET Core patterns that align with the existing project architecture. The project already uses JWT authentication, FluentValidation, Mapster, and EF Core — all patterns needed for this feature.

| Decision | Technology | Status |
|----------|------------|--------|
| Dual JWT schemes | `AddJwtBearer` x2 | ✅ Established pattern |
| Per-meeting rate limiting | `PartitionedRateLimiter` | ✅ Built-in .NET 7+ |
| Token refresh | `POST /api/agent/refresh` | ✅ Simple endpoint |
| Response mapping | Mapster | ✅ Already in use |
| Validation | FluentValidation | ✅ Already in use |
