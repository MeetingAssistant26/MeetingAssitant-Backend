using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using MeetingAssistant.Features.Identity.Entites;
using MeetingAssistant.Features.LiveSession.Contracts.AiDebug;
using MeetingAssistant.Features.LiveSession.Models.PostProcessing;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeetingAssistant.Tests.Integration.LiveSession;

public class PostMeetingProcessingTraceEndpointTests : IntegrationTestBase
{
    public PostMeetingProcessingTraceEndpointTests(MeetingAssistantWebFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task WebRead_WhenDisabled_ShouldReturnEmptyDisabledResponse()
    {
        var meetingId = await SeedMeetingAsync(Factory, TestOrganizationId);

        var response = await Client.GetAsync(TraceUrl(TestOrganizationId, meetingId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PostMeetingProcessingTraceResponse>();
        body.Should().NotBeNull();
        body!.Enabled.Should().BeFalse();
        body.Runs.Should().BeEmpty();
        body.Steps.Should().BeEmpty();
        body.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task WebRead_WhenEnabledAndAdminAuthorized_ShouldReturnRunsStepsEventsWithoutRawMetadataSecrets()
    {
        using var factory = Factory.WithConfiguration(EnabledDebugConfig());
        var ids = await SeedBaseAndMeetingAsync(factory, OrganizationRole.Admin);
        var runId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var relatedArtifactId = Guid.NewGuid();
        var startedAt = DateTime.UtcNow.AddMinutes(-3);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.PostMeetingProcessingRuns.Add(new PostMeetingProcessingRun
            {
                Id = runId,
                OrganizationId = ids.OrganizationId,
                MeetingId = ids.MeetingId,
                PipelineGenerationId = Guid.NewGuid(),
                Status = PostMeetingProcessingStatus.InProgress,
                AttemptCount = 1,
                StartedAtUtc = startedAt,
                RelatedHangfireJobId = "job-run-1"
            });
            db.PostMeetingProcessingSteps.Add(new PostMeetingProcessingStep
            {
                Id = stepId,
                OrganizationId = ids.OrganizationId,
                MeetingId = ids.MeetingId,
                RunId = runId,
                StepType = PostMeetingProcessingStepType.SummaryGeneration,
                Status = PostMeetingProcessingStatus.Failed,
                AttemptCount = 2,
                LastAttemptAtUtc = startedAt.AddSeconds(30),
                StartedAtUtc = startedAt,
                FailedAtUtc = startedAt.AddSeconds(45),
                RelatedHangfireJobId = "job-step-1",
                ArtifactType = "meeting-summary",
                ArtifactId = artifactId,
                ArtifactIdsJson = $"[\"{artifactId}\",\"{relatedArtifactId}\"]",
                ErrorCode = "LLM.Timeout",
                ErrorMessage = "Provider timed out."
            });
            db.PostMeetingProcessingEvents.Add(new PostMeetingProcessingEvent
            {
                OrganizationId = ids.OrganizationId,
                MeetingId = ids.MeetingId,
                RunId = runId,
                StepId = stepId,
                StepType = PostMeetingProcessingStepType.SummaryGeneration,
                EventType = PostMeetingProcessingEventType.StepFailed,
                Status = PostMeetingProcessingStatus.Failed,
                OccurredAtUtc = startedAt.AddSeconds(45),
                RelatedHangfireJobId = "job-step-1",
                Message = "Summary generation failed and will be retried.",
                ArtifactType = "meeting-summary",
                ArtifactId = artifactId,
                ErrorCode = "LLM.Timeout",
                ErrorMessage = "Provider timed out.",
                MetadataJson = "{\"accessToken\":\"secret-token-should-not-leak\"}"
            });
            await db.SaveChangesAsync();
        }

        using var client = AuthenticatedClient(factory, ids.UserId, ids.OrganizationId, "Admin");
        var response = await client.GetAsync(TraceUrl(ids.OrganizationId, ids.MeetingId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rawJson = await response.Content.ReadAsStringAsync();
        rawJson.Should().NotContain("secret-token-should-not-leak");
        rawJson.Should().NotContain("accessToken");

        var body = await response.Content.ReadFromJsonAsync<PostMeetingProcessingTraceResponse>();
        body.Should().NotBeNull();
        body!.Enabled.Should().BeTrue();
        body.Runs.Should().ContainSingle(r => r.Id == runId && r.Status == "Failed" && r.AttemptCount == 1);
        body.Steps.Should().ContainSingle();
        var step = body.Steps.Single();
        step.StepType.Should().Be("SummaryGeneration");
        step.Status.Should().Be("Failed");
        step.AttemptCount.Should().Be(2);
        step.Artifact.Should().NotBeNull();
        step.Artifact!.Type.Should().Be("meeting-summary");
        step.Artifact.Id.Should().Be(artifactId);
        step.Artifact.Ids.Should().BeEquivalentTo([artifactId, relatedArtifactId]);
        step.Error.Should().Be(new PostMeetingProcessingErrorDto("LLM.Timeout", "Provider timed out."));
        body.Events.Should().ContainSingle();
        body.Events.Single().EventType.Should().Be("StepFailed");
    }

    [Fact]
    public async Task WebRead_WhenEnabledAndMemberAuthorized_ShouldReturnForbidden()
    {
        using var factory = Factory.WithConfiguration(EnabledDebugConfig());
        var ids = await SeedBaseAndMeetingAsync(factory, OrganizationRole.Member);
        using var client = AuthenticatedClient(factory, ids.UserId, ids.OrganizationId, "Member");

        var response = await client.GetAsync(TraceUrl(ids.OrganizationId, ids.MeetingId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task WebRead_WhenErrorMessagesContainSecrets_ShouldRedactUserFacingTraceErrors()
    {
        using var factory = Factory.WithConfiguration(EnabledDebugConfig());
        var ids = await SeedBaseAndMeetingAsync(factory, OrganizationRole.Admin);
        var runId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        const string rawError = "Provider call failed with Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjMifQ.signature "
                                + "at https://minio.local/audio.wav?X-Amz-Signature=very-secret-signature&X-Amz-Credential=very-secret-credential "
                                + "using Host=db;Username=postgres;Password=super-secret-password; and {\"accessToken\":\"secret-token-should-not-leak\"}.";

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.PostMeetingProcessingRuns.Add(new PostMeetingProcessingRun
            {
                Id = runId,
                OrganizationId = ids.OrganizationId,
                MeetingId = ids.MeetingId,
                PipelineGenerationId = Guid.NewGuid(),
                Status = PostMeetingProcessingStatus.Failed,
                AttemptCount = 1,
                StartedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                FailedAtUtc = DateTime.UtcNow,
                ErrorCode = "Provider.SecretLeak",
                ErrorMessage = rawError
            });
            db.PostMeetingProcessingSteps.Add(new PostMeetingProcessingStep
            {
                Id = stepId,
                OrganizationId = ids.OrganizationId,
                MeetingId = ids.MeetingId,
                RunId = runId,
                StepType = PostMeetingProcessingStepType.Stt,
                Status = PostMeetingProcessingStatus.Failed,
                AttemptCount = 1,
                ErrorCode = "Provider.SecretLeak",
                ErrorMessage = rawError
            });
            db.PostMeetingProcessingEvents.Add(new PostMeetingProcessingEvent
            {
                OrganizationId = ids.OrganizationId,
                MeetingId = ids.MeetingId,
                RunId = runId,
                StepId = stepId,
                StepType = PostMeetingProcessingStepType.Stt,
                EventType = PostMeetingProcessingEventType.StepFailed,
                Status = PostMeetingProcessingStatus.Failed,
                ErrorCode = "Provider.SecretLeak",
                ErrorMessage = rawError,
                Message = "STT failed."
            });
            await db.SaveChangesAsync();
        }

        using var client = AuthenticatedClient(factory, ids.UserId, ids.OrganizationId, "Admin");
        var response = await client.GetAsync(TraceUrl(ids.OrganizationId, ids.MeetingId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rawJson = await response.Content.ReadAsStringAsync();
        rawJson.Should().NotContain("very-secret-signature");
        rawJson.Should().NotContain("very-secret-credential");
        rawJson.Should().NotContain("super-secret-password");
        rawJson.Should().NotContain("secret-token-should-not-leak");
        rawJson.Should().NotContain("eyJhbGciOiJIUzI1NiJ9");
        rawJson.Should().Contain("[redacted]");
        rawJson.Should().Contain("?[redacted]");

        var body = await response.Content.ReadFromJsonAsync<PostMeetingProcessingTraceResponse>();
        body.Should().NotBeNull();
        body!.Runs.Single().Error!.Message.Should().NotContain("super-secret-password");
        body.Steps.Single().Error!.Message.Should().NotContain("very-secret-signature");
        body.Events.Single().Error!.Message.Should().NotContain("secret-token-should-not-leak");
    }

    [Fact]
    public async Task WebRead_WhenRunLeftInProgressButAllKnownStepsAreTerminal_ShouldDeriveSafeRunStatus()
    {
        using var factory = Factory.WithConfiguration(EnabledDebugConfig());
        var ids = await SeedBaseAndMeetingAsync(factory, OrganizationRole.Admin);
        var runId = Guid.NewGuid();
        var completedAt = DateTime.UtcNow.AddSeconds(-10);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.PostMeetingProcessingRuns.Add(new PostMeetingProcessingRun
            {
                Id = runId,
                OrganizationId = ids.OrganizationId,
                MeetingId = ids.MeetingId,
                PipelineGenerationId = Guid.NewGuid(),
                Status = PostMeetingProcessingStatus.InProgress,
                AttemptCount = 1,
                StartedAtUtc = completedAt.AddMinutes(-1)
            });
            db.PostMeetingProcessingSteps.AddRange(
                new PostMeetingProcessingStep
                {
                    OrganizationId = ids.OrganizationId,
                    MeetingId = ids.MeetingId,
                    RunId = runId,
                    StepType = PostMeetingProcessingStepType.SummaryGeneration,
                    Status = PostMeetingProcessingStatus.Completed,
                    AttemptCount = 1,
                    StartedAtUtc = completedAt.AddSeconds(-30),
                    CompletedAtUtc = completedAt
                },
                new PostMeetingProcessingStep
                {
                    OrganizationId = ids.OrganizationId,
                    MeetingId = ids.MeetingId,
                    RunId = runId,
                    StepType = PostMeetingProcessingStepType.ActionExtraction,
                    Status = PostMeetingProcessingStatus.Skipped,
                    AttemptCount = 0,
                    StartedAtUtc = completedAt.AddSeconds(-20),
                    CompletedAtUtc = completedAt.AddSeconds(2)
                });
            await db.SaveChangesAsync();
        }

        using var client = AuthenticatedClient(factory, ids.UserId, ids.OrganizationId, "Admin");
        var response = await client.GetAsync(TraceUrl(ids.OrganizationId, ids.MeetingId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PostMeetingProcessingTraceResponse>();
        body.Should().NotBeNull();
        body!.Runs.Should().ContainSingle();
        body.Runs.Single().Status.Should().Be("Completed");
        body.Runs.Single().CompletedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task WebRead_WhenUnauthenticated_ShouldReturnUnauthorized()
    {
        var meetingId = await SeedMeetingAsync(Factory, TestOrganizationId);
        using var client = Factory.CreateClient();

        var response = await client.GetAsync(TraceUrl(TestOrganizationId, meetingId));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task WebRead_WithAgentToken_ShouldNotAuthorizeBrowserEndpoint()
    {
        using var factory = Factory.WithConfiguration(EnabledDebugConfig());
        var ids = await SeedBaseAndMeetingAsync(factory, OrganizationRole.Admin);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            TestJwtTokenHelper.GenerateAgentToken(ids.OrganizationId, ids.MeetingId));

        var response = await client.GetAsync(TraceUrl(ids.OrganizationId, ids.MeetingId));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task WebRead_WhenCallerUsesAnotherOrganization_ShouldReturnForbidden()
    {
        using var factory = Factory.WithConfiguration(EnabledDebugConfig());
        var ids = await SeedBaseAndMeetingAsync(factory, OrganizationRole.Admin);
        var otherOrganizationId = Guid.NewGuid();
        var otherUserId = await SeedOrganizationMemberAsync(factory, otherOrganizationId, OrganizationRole.Admin);
        using var client = AuthenticatedClient(factory, otherUserId, otherOrganizationId, "Admin");

        var response = await client.GetAsync(TraceUrl(ids.OrganizationId, ids.MeetingId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task WebRead_WhenMeetingBelongsToAnotherOrganization_ShouldReturnNotFound()
    {
        using var factory = Factory.WithConfiguration(EnabledDebugConfig());
        var ids = await SeedBaseAndMeetingAsync(factory, OrganizationRole.Admin);
        var otherOrganizationId = Guid.NewGuid();
        await SeedOrganizationMemberAsync(factory, otherOrganizationId, OrganizationRole.Admin);
        var otherMeetingId = await SeedMeetingAsync(factory, otherOrganizationId);
        using var client = AuthenticatedClient(factory, ids.UserId, ids.OrganizationId, "Admin");

        var response = await client.GetAsync(TraceUrl(ids.OrganizationId, otherMeetingId));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static string TraceUrl(Guid organizationId, Guid meetingId)
        => $"/api/organizations/{organizationId}/meetings/{meetingId}/ai-debug/post-processing-traces";

    private static IReadOnlyDictionary<string, string?> EnabledDebugConfig()
        => new Dictionary<string, string?>
        {
            ["AiDebug:Enabled"] = "true",
            ["AiDebug:PersistPayloads"] = "true"
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
        await SeedOrganizationMemberAsync(db, organizationId, userId, role);
        var meetingId = await SeedMeetingAsync(db, organizationId);
        await db.SaveChangesAsync();

        return (userId, organizationId, meetingId);
    }

    private static async Task<Guid> SeedOrganizationMemberAsync(
        MeetingAssistantWebFactory factory,
        Guid organizationId,
        OrganizationRole role)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.EnsureCreatedAsync();
        var userId = Guid.NewGuid();
        await SeedOrganizationMemberAsync(db, organizationId, userId, role);
        await db.SaveChangesAsync();
        return userId;
    }

    private static async Task SeedOrganizationMemberAsync(
        ApplicationDbContext db,
        Guid organizationId,
        Guid userId,
        OrganizationRole role)
    {
        if (!await db.Organizations.IgnoreQueryFilters().AnyAsync(x => x.Id == organizationId))
        {
            db.Organizations.Add(new Organization
            {
                Id = organizationId,
                Name = "Trace Org",
                Slug = $"trace-org-{organizationId:N}"
            });
        }

        db.Users.Add(new ApplicationUser
        {
            Id = userId,
            UserName = $"trace-{userId:N}@test.com",
            NormalizedUserName = $"TRACE-{userId:N}@TEST.COM",
            Email = $"trace-{userId:N}@test.com",
            NormalizedEmail = $"TRACE-{userId:N}@TEST.COM",
            EmailConfirmed = true,
            DisplayName = "Trace User",
            SecurityStamp = Guid.NewGuid().ToString()
        });
        db.UserOrgMemberships.Add(new UserOrgMembership
        {
            UserId = userId,
            OrganizationId = organizationId,
            OrgRole = role,
            IsEnabled = true
        });
    }

    private static async Task<Guid> SeedMeetingAsync(MeetingAssistantWebFactory factory, Guid organizationId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.EnsureCreatedAsync();
        var meetingId = await SeedMeetingAsync(db, organizationId);
        await db.SaveChangesAsync();
        return meetingId;
    }

    private static Task<Guid> SeedMeetingAsync(ApplicationDbContext db, Guid organizationId)
    {
        var meetingId = Guid.NewGuid();
        db.Meetings.Add(new Meeting
        {
            Id = meetingId,
            OrganizationId = organizationId,
            Title = "Post-processing Trace Meeting",
            ScheduledStartUtc = DateTime.UtcNow.AddMinutes(-10),
            ScheduledEndUtc = DateTime.UtcNow.AddMinutes(50),
            Status = MeetingStatus.Completed
        });

        return Task.FromResult(meetingId);
    }
}
