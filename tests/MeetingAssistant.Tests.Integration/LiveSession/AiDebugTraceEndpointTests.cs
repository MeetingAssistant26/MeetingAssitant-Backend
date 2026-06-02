using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using MeetingAssistant.Features.Identity.Entites;
using MeetingAssistant.Features.LiveSession.Contracts.AiDebug;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeetingAssistant.Tests.Integration.LiveSession;

public class AiDebugTraceEndpointTests : IntegrationTestBase
{
    public AiDebugTraceEndpointTests(MeetingAssistantWebFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task AgentIngest_WhenDisabled_ShouldReturnNotFoundAndNotPersist()
    {
        var meetingId = await SeedMeetingAsync(Factory, TestOrganizationId);
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            TestJwtTokenHelper.GenerateAgentToken(TestOrganizationId, meetingId));

        var response = await Client.PostAsJsonAsync(
            $"/api/agent/meetings/{meetingId}/ai-debug/traces",
            TraceRequest(sequence: 1, eventType: "turn_started"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.AiAssistantTraceEvents.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task AgentIngest_WithMismatchedMeetingScope_ShouldReturnForbidden()
    {
        using var factory = Factory.WithConfiguration(EnabledDebugConfig());
        var ids = await SeedBaseAndMeetingAsync(factory, OrganizationRole.Admin);
        var otherMeetingId = await SeedMeetingAsync(factory, ids.OrganizationId);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            TestJwtTokenHelper.GenerateAgentToken(ids.OrganizationId, ids.MeetingId));

        var response = await client.PostAsJsonAsync(
            $"/api/agent/meetings/{otherMeetingId}/ai-debug/traces",
            TraceRequest(sequence: 1, eventType: "turn_started"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AgentIngest_WhenPayloadPersistenceDisabled_ShouldSuppressPayloadsAndText()
    {
        using var factory = Factory.WithConfiguration(EnabledDebugConfig(persistPayloads: false));
        var ids = await SeedBaseAndMeetingAsync(factory, OrganizationRole.Admin);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            TestJwtTokenHelper.GenerateAgentToken(ids.OrganizationId, ids.MeetingId));

        var response = await client.PostAsJsonAsync(
            $"/api/agent/meetings/{ids.MeetingId}/ai-debug/traces",
            new
            {
                sessionId = "session-1",
                turnId = "turn-1",
                sequence = 2,
                eventType = "llm_completed",
                occurredAtUtc = DateTime.UtcNow,
                participantIdentity = "participant-1",
                state = "thinking",
                step = new
                {
                    type = "llm",
                    provider = "openai",
                    endpoint = "/v1/chat/completions",
                    model = "gpt-test",
                    durationMs = 123,
                    promptTokens = 1,
                    completionTokens = 2,
                    totalTokens = 3,
                    requestPayload = new { prompt = "secret prompt" },
                    responsePayload = new { answer = "secret answer" },
                    text = "full answer content"
                }
            });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var persisted = await db.AiAssistantTraceEvents.IgnoreQueryFilters().SingleAsync();
        persisted.RequestPayloadJson.Should().BeNull();
        persisted.ResponsePayloadJson.Should().BeNull();
        persisted.Text.Should().BeNull();
        persisted.CharactersCount.Should().Be("full answer content".Length);
        persisted.PromptTokens.Should().Be(1);
        persisted.TotalTokens.Should().Be(3);
    }

    [Fact]
    public async Task AgentIngest_AssistantSpeechCompleted_WhenPayloadPersistenceDisabled_ShouldPersistTranscriptText()
    {
        using var factory = Factory.WithConfiguration(EnabledDebugConfig(persistPayloads: false));
        var ids = await SeedBaseAndMeetingAsync(factory, OrganizationRole.Admin);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            TestJwtTokenHelper.GenerateAgentToken(ids.OrganizationId, ids.MeetingId));

        var response = await client.PostAsJsonAsync(
            $"/api/agent/meetings/{ids.MeetingId}/ai-debug/traces",
            new
            {
                sessionId = "session-1",
                turnId = "turn-1",
                sequence = 50,
                eventType = AiAssistantTraceEventTypes.AssistantSpeechCompleted,
                occurredAtUtc = DateTime.UtcNow,
                state = "speaking",
                step = new
                {
                    type = "tts",
                    provider = "livekit-agent",
                    voice = "alloy",
                    durationMs = 1500,
                    text = "I can help summarize that."
                }
            });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var persisted = await db.AiAssistantTraceEvents.IgnoreQueryFilters().SingleAsync();
        persisted.EventType.Should().Be(AiAssistantTraceEventTypes.AssistantSpeechCompleted);
        persisted.RequestPayloadJson.Should().BeNull();
        persisted.ResponsePayloadJson.Should().BeNull();
        persisted.Text.Should().Be("I can help summarize that.");
        persisted.CharactersCount.Should().Be("I can help summarize that.".Length);
        persisted.StepType.Should().Be("tts");
        persisted.StepVoice.Should().Be("alloy");
    }

    [Fact]
    public async Task WebRead_WhenEnabled_ShouldGroupTurnsAndRequireOrganizationAdmin()
    {
        using var factory = Factory.WithConfiguration(EnabledDebugConfig());
        var ids = await SeedBaseAndMeetingAsync(factory, OrganizationRole.Admin);
        var memberId = await SeedMembershipAsync(factory, ids.OrganizationId, OrganizationRole.Member);
        var startedAt = DateTime.UtcNow.AddSeconds(-4);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.AiAssistantTraceEvents.AddRange(
                new AiAssistantTraceEvent
                {
                    OrganizationId = ids.OrganizationId,
                    MeetingId = ids.MeetingId,
                    SessionId = "session-1",
                    TurnId = "turn-1",
                    Sequence = 1,
                    EventType = "turn_started",
                    OccurredAtUtc = startedAt,
                    ParticipantIdentity = "participant-1",
                    StepType = "turn"
                },
                new AiAssistantTraceEvent
                {
                    OrganizationId = ids.OrganizationId,
                    MeetingId = ids.MeetingId,
                    SessionId = "session-1",
                    TurnId = "turn-1",
                    Sequence = 2,
                    EventType = "llm_completed",
                    OccurredAtUtc = startedAt.AddSeconds(1),
                    State = "thinking",
                    StepType = "llm",
                    StepModel = "gpt-test",
                    Text = "answer",
                    RequestPayloadJson = "{\"prompt\":\"hello\"}"
                },
                new AiAssistantTraceEvent
                {
                    OrganizationId = ids.OrganizationId,
                    MeetingId = ids.MeetingId,
                    SessionId = "session-1",
                    TurnId = "turn-1",
                    Sequence = 3,
                    EventType = "turn_completed",
                    OccurredAtUtc = startedAt.AddSeconds(2),
                    StepType = "turn"
                });
            await db.SaveChangesAsync();
        }

        using var adminClient = AuthenticatedClient(factory, ids.UserId, ids.OrganizationId, "Admin");
        var response = await adminClient.GetAsync(
            $"/api/organizations/{ids.OrganizationId}/meetings/{ids.MeetingId}/ai-debug/traces");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AiDebugTraceResponse>();
        body.Should().NotBeNull();
        body!.Enabled.Should().BeTrue();
        body.PersistPayloads.Should().BeTrue();
        body.Turns.Should().ContainSingle();
        var turn = body.Turns.Single();
        turn.SessionId.Should().Be("session-1");
        turn.TurnId.Should().Be("turn-1");
        turn.Status.Should().Be("completed");
        turn.Events.Select(e => e.Sequence).Should().Equal(1, 2, 3);
        turn.Events[1].Step!.Text.Should().Be("answer");
        turn.Events[1].Step!.RequestPayload.Should().NotBeNull();

        using var memberClient = AuthenticatedClient(factory, memberId, ids.OrganizationId, "Member");
        var forbidden = await memberClient.GetAsync(
            $"/api/organizations/{ids.OrganizationId}/meetings/{ids.MeetingId}/ai-debug/traces");
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task WebRead_WhenDisabled_ShouldReturnEmptyDisabledResponse()
    {
        var meetingId = await SeedMeetingAsync(Factory, TestOrganizationId);

        var response = await Client.GetAsync(
            $"/api/organizations/{TestOrganizationId}/meetings/{meetingId}/ai-debug/traces");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AiDebugTraceResponse>();
        body.Should().NotBeNull();
        body!.Enabled.Should().BeFalse();
        body.PersistPayloads.Should().BeFalse();
        body.Turns.Should().BeEmpty();
    }

    private static object TraceRequest(int sequence, string eventType) => new
    {
        sessionId = "session-1",
        turnId = "turn-1",
        sequence,
        eventType,
        occurredAtUtc = DateTime.UtcNow
    };

    private static IReadOnlyDictionary<string, string?> EnabledDebugConfig(bool persistPayloads = true)
        => new Dictionary<string, string?>
        {
            ["AiDebug:Enabled"] = "true",
            ["AiDebug:PersistPayloads"] = persistPayloads ? "true" : "false"
        };

    private static HttpClient AuthenticatedClient(
        MeetingAssistantWebFactory factory,
        Guid userId,
        Guid organizationId,
        string role)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            TestJwtTokenHelper.GenerateToken(userId, organizationId, role));
        return client;
    }

    private static async Task<(Guid UserId, Guid OrganizationId, Guid MeetingId)> SeedBaseAndMeetingAsync(
        MeetingAssistantWebFactory factory,
        OrganizationRole role)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.EnsureCreatedAsync();

        var userId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser
        {
            Id = userId,
            UserName = $"debug-{userId:N}@test.com",
            NormalizedUserName = $"DEBUG-{userId:N}@TEST.COM",
            Email = $"debug-{userId:N}@test.com",
            NormalizedEmail = $"DEBUG-{userId:N}@TEST.COM",
            EmailConfirmed = true,
            DisplayName = "Debug User",
            SecurityStamp = Guid.NewGuid().ToString()
        });
        db.Organizations.Add(new Organization
        {
            Id = organizationId,
            Name = "Debug Org",
            Slug = $"debug-org-{organizationId:N}"
        });
        db.UserOrgMemberships.Add(new UserOrgMembership
        {
            UserId = userId,
            OrganizationId = organizationId,
            OrgRole = role,
            IsEnabled = true
        });
        var meetingId = Guid.NewGuid();
        db.Meetings.Add(new Meeting
        {
            Id = meetingId,
            OrganizationId = organizationId,
            Title = "AI Debug Meeting",
            ScheduledStartUtc = DateTime.UtcNow.AddMinutes(-5),
            ScheduledEndUtc = DateTime.UtcNow.AddMinutes(55),
            Status = MeetingStatus.InProgress
        });
        await db.SaveChangesAsync();

        return (userId, organizationId, meetingId);
    }

    private static async Task<Guid> SeedMeetingAsync(MeetingAssistantWebFactory factory, Guid organizationId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.EnsureCreatedAsync();

        var meetingId = Guid.NewGuid();
        db.Meetings.Add(new Meeting
        {
            Id = meetingId,
            OrganizationId = organizationId,
            Title = "AI Debug Meeting",
            ScheduledStartUtc = DateTime.UtcNow.AddMinutes(-5),
            ScheduledEndUtc = DateTime.UtcNow.AddMinutes(55),
            Status = MeetingStatus.InProgress
        });
        await db.SaveChangesAsync();
        return meetingId;
    }

    private static async Task<Guid> SeedMembershipAsync(
        MeetingAssistantWebFactory factory,
        Guid organizationId,
        OrganizationRole role)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser
        {
            Id = userId,
            UserName = $"member-{userId:N}@test.com",
            NormalizedUserName = $"MEMBER-{userId:N}@TEST.COM",
            Email = $"member-{userId:N}@test.com",
            NormalizedEmail = $"MEMBER-{userId:N}@TEST.COM",
            EmailConfirmed = true,
            DisplayName = "Debug Member",
            SecurityStamp = Guid.NewGuid().ToString()
        });
        db.UserOrgMemberships.Add(new UserOrgMembership
        {
            UserId = userId,
            OrganizationId = organizationId,
            OrgRole = role,
            IsEnabled = true
        });
        await db.SaveChangesAsync();
        return userId;
    }
}
