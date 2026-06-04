using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using MeetingAssistant.Features.AgentApi.Models.Responses;
using MeetingAssistant.Features.DevQa;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Features.Tasks.Models.Enums;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeetingAssistant.Tests.Integration.DevQa;

public sealed class DevQaEndpointTests : IntegrationTestBase
{
    public DevQaEndpointTests(MeetingAssistantWebFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task CreateScenario_ShouldProvisionOrgUsersMeetingTagsAndJoinTokens()
    {
        var request = new QaScenarioRequest(
            Scenario: "qa-account-meeting-setup",
            RunId: Guid.NewGuid().ToString("N"),
            Users:
            [
                new QaScenarioUserRequest("alice", "QA Alice", OrganizationRole.Admin, MeetingRole.Host, null),
                new QaScenarioUserRequest("bob", "QA Bob", OrganizationRole.Member, MeetingRole.Participant, null)
            ],
            Meeting: new QaScenarioMeetingRequest(Title: "QA Endpoint Test"),
            Tags: ["qa", "automation"]);

        var response = await Client.PostAsJsonAsync("/api/dev/qa/scenario", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<QaScenarioResponse>();
        body.Should().NotBeNull();
        body!.OrganizationId.Should().NotBeEmpty();
        body.MeetingId.Should().NotBeEmpty();
        body.LiveKitRoomName.Should().Be($"mtg:{body.MeetingId}");
        body.Users.Keys.Should().BeEquivalentTo("alice", "bob");
        body.Users["alice"].AccessToken.Should().NotBeNullOrWhiteSpace();
        body.JoinTokens["alice"].RoomName.Should().Be(body.LiveKitRoomName);
        body.Tags.Select(tag => tag.Name).Should().BeEquivalentTo("qa", "automation");
    }

    [Fact]
    public async Task CreateScenario_WithVeryLongRunId_ShouldPersistBoundedGeneratedFields()
    {
        var longRunId = $"validate-user-story-tts-full-stt-oracle-after-backend-fix-20260604025026-{Guid.NewGuid():N}-{new string('r', 180)}";
        var longTagName = $"qa-long-tag-{new string('t', 80)}";
        var request = new QaScenarioRequest(
            Scenario: "qa-long-run-id-stability",
            RunId: longRunId,
            Tags: [longTagName]);

        var response = await Client.PostAsJsonAsync("/api/dev/qa/scenario", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<QaScenarioResponse>();
        body.Should().NotBeNull();
        body!.RunId.Should().NotBeNullOrWhiteSpace();
        body.OrganizationId.Should().NotBeEmpty();
        body.MeetingId.Should().NotBeEmpty();
        body.OrganizationSlug.Length.Should().BeLessThanOrEqualTo(100);
        body.OrganizationName.Length.Should().BeLessThanOrEqualTo(150);
        body.MeetingTitle.Length.Should().BeLessThanOrEqualTo(200);
        body.Users.Values.Should().OnlyContain(user => user.Email.Length <= 100);
        body.Tags.Should().OnlyContain(tag => tag.Name.Length <= 50);
    }

    [Fact]
    public async Task ProcessingStatus_ShouldExposeEmptyArtifactStateForNewScenario()
    {
        var scenarioResponse = await Client.PostAsJsonAsync(
            "/api/dev/qa/scenario",
            new QaScenarioRequest(
                Scenario: "qa-processing-status",
                RunId: Guid.NewGuid().ToString("N"),
                Meeting: new QaScenarioMeetingRequest(Title: "QA Status Test")));
        scenarioResponse.EnsureSuccessStatusCode();

        var scenario = await scenarioResponse.Content.ReadFromJsonAsync<QaScenarioResponse>();
        scenario.Should().NotBeNull();

        var statusResponse = await Client.GetAsync(
            $"/api/dev/qa/meetings/{scenario!.MeetingId}/processing-status?organizationId={scenario.OrganizationId}");

        statusResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var status = await statusResponse.Content.ReadFromJsonAsync<QaProcessingStatusResponse>();
        status.Should().NotBeNull();
        status!.Meeting.Id.Should().Be(scenario.MeetingId);
        status.Participants.Should().HaveCount(2);
        status.AudioFragments.Should().BeEmpty();
        status.Transcript.Should().BeNull();
        status.Summary.Should().BeNull();
        status.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAdditionalMeeting_ShouldCopyParticipantsAndTagsFromSourceMeeting()
    {
        var scenario = await CreateScenarioAsync(
            "qa-additional-meeting-copy",
            users:
            [
                new QaScenarioUserRequest("alice", "QA Alice", OrganizationRole.Admin, MeetingRole.Host, null),
                new QaScenarioUserRequest("bob", "QA Bob", OrganizationRole.Member, MeetingRole.Participant, null)
            ],
            tags: ["qa", "automation"]);

        var response = await Client.PostAsJsonAsync(
            $"/api/dev/qa/organizations/{scenario.OrganizationId}/meetings",
            new QaCreateAdditionalMeetingRequest(scenario.MeetingId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<QaAdditionalMeetingResponse>();
        body.Should().NotBeNull();
        body!.OrganizationId.Should().Be(scenario.OrganizationId);
        body.SourceMeetingId.Should().Be(scenario.MeetingId);
        body.MeetingId.Should().NotBe(scenario.MeetingId);
        body.LiveKitRoomName.Should().Be($"mtg:{body.MeetingId}");
        body.Participants.Should().HaveCount(2);
        body.Participants.Select(participant => participant.MeetingRole)
            .Should().BeEquivalentTo(MeetingRole.Host.ToString(), MeetingRole.Participant.ToString());
        body.Tags.Select(tag => tag.Name).Should().BeEquivalentTo("qa", "automation");
        body.RecurringSeriesId.Should().BeNull();
        body.RecurringOccurrenceIndex.Should().BeNull();
    }

    [Fact]
    public async Task CreateAdditionalMeeting_WithLinkRecurringSeries_ShouldLinkSourceAndAdditionalOccurrences()
    {
        var scenario = await CreateScenarioAsync("qa-additional-meeting-linked-series");
        var m2Start = DateTime.UtcNow.AddDays(7);
        var m3Start = m2Start.AddDays(7);

        var m2Response = await Client.PostAsJsonAsync(
            $"/api/dev/qa/organizations/{scenario.OrganizationId}/meetings",
            new QaCreateAdditionalMeetingRequest(
                scenario.MeetingId,
                ScheduledStartUtc: m2Start,
                ScheduledEndUtc: m2Start.AddHours(1),
                LinkRecurringSeries: true,
                RecurringOccurrenceIndex: 1));
        m2Response.StatusCode.Should().Be(HttpStatusCode.OK);
        var m2 = await m2Response.Content.ReadFromJsonAsync<QaAdditionalMeetingResponse>();
        m2.Should().NotBeNull();

        var m3Response = await Client.PostAsJsonAsync(
            $"/api/dev/qa/organizations/{scenario.OrganizationId}/meetings",
            new QaCreateAdditionalMeetingRequest(
                m2!.MeetingId,
                ScheduledStartUtc: m3Start,
                ScheduledEndUtc: m3Start.AddHours(1),
                LinkRecurringSeries: true,
                RecurringOccurrenceIndex: 2));
        m3Response.StatusCode.Should().Be(HttpStatusCode.OK);
        var m3 = await m3Response.Content.ReadFromJsonAsync<QaAdditionalMeetingResponse>();
        m3.Should().NotBeNull();

        m2.RecurringSeriesId.Should().NotBeNull();
        m2.RecurringOccurrenceIndex.Should().Be(1);
        m3!.RecurringSeriesId.Should().Be(m2.RecurringSeriesId);
        m3.RecurringOccurrenceIndex.Should().Be(2);

        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var m1 = await db.Meetings
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstAsync(x => x.Id == scenario.MeetingId);
        m1.RecurringSeriesId.Should().Be(m2.RecurringSeriesId);
        m1.RecurringOccurrenceIndex.Should().Be(0);
    }

    [Fact]
    public async Task CreateAdditionalMeeting_WithoutLinkRecurringSeries_ShouldRemainOneOff()
    {
        var scenario = await CreateScenarioAsync("qa-additional-meeting-one-off");

        var response = await Client.PostAsJsonAsync(
            $"/api/dev/qa/organizations/{scenario.OrganizationId}/meetings",
            new QaCreateAdditionalMeetingRequest(scenario.MeetingId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<QaAdditionalMeetingResponse>();
        body.Should().NotBeNull();
        body!.RecurringSeriesId.Should().BeNull();
        body.RecurringOccurrenceIndex.Should().BeNull();
    }

    [Fact]
    public async Task CreateAdditionalMeeting_WithNegativeRecurringOccurrenceIndex_ShouldReturnBadRequest()
    {
        var scenario = await CreateScenarioAsync("qa-additional-meeting-negative-index");

        var response = await Client.PostAsJsonAsync(
            $"/api/dev/qa/organizations/{scenario.OrganizationId}/meetings",
            new QaCreateAdditionalMeetingRequest(
                scenario.MeetingId,
                LinkRecurringSeries: true,
                RecurringOccurrenceIndex: -1));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateAdditionalMeeting_WithSourceMeetingFromAnotherOrganization_ShouldReturnNotFound()
    {
        var scenario1 = await CreateScenarioAsync("qa-additional-source-org1");
        var scenario2 = await CreateScenarioAsync("qa-additional-source-org2");

        var response = await Client.PostAsJsonAsync(
            $"/api/dev/qa/organizations/{scenario2.OrganizationId}/meetings",
            new QaCreateAdditionalMeetingRequest(scenario1.MeetingId));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task MintAgentToken_ShouldReturnAgentJwtForExactMeeting()
    {
        var scenario = await CreateScenarioAsync("qa-agent-token-mint");

        var response = await Client.PostAsJsonAsync(
            $"/api/dev/qa/meetings/{scenario.MeetingId}/agent-token",
            new QaMintAgentTokenRequest(scenario.OrganizationId, ExpiresInMinutes: 15));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<QaAgentTokenResponse>();
        body.Should().NotBeNull();
        body!.OrganizationId.Should().Be(scenario.OrganizationId);
        body.MeetingId.Should().Be(scenario.MeetingId);
        body.TokenType.Should().Be("Bearer");
        body.AccessToken.Should().NotBeNullOrWhiteSpace();
        body.ExpiresInMinutes.Should().Be(15);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(body.AccessToken);
        jwt.Claims.Should().Contain(claim => claim.Type == "agent" && claim.Value == "true");
        jwt.Claims.Should().Contain(claim => claim.Type == "organizationId" && claim.Value == scenario.OrganizationId.ToString());
        jwt.Claims.Should().Contain(claim => claim.Type == "meetingId" && claim.Value == scenario.MeetingId.ToString());
        jwt.Claims.Should().Contain(claim => claim.Type == JwtRegisteredClaimNames.Exp);
    }

    [Fact]
    public async Task MintAgentToken_ShouldAuthenticateAgainstAgentApiAndEnforceMeetingClaim()
    {
        var scenario = await CreateScenarioAsync("qa-agent-token-enforces-meeting", tags: ["qa", "automation"]);
        var additionalMeetingResponse = await Client.PostAsJsonAsync(
            $"/api/dev/qa/organizations/{scenario.OrganizationId}/meetings",
            new QaCreateAdditionalMeetingRequest(scenario.MeetingId));
        additionalMeetingResponse.EnsureSuccessStatusCode();
        var additionalMeeting = await additionalMeetingResponse.Content.ReadFromJsonAsync<QaAdditionalMeetingResponse>();
        additionalMeeting.Should().NotBeNull();

        var tokenResponse = await Client.PostAsJsonAsync(
            $"/api/dev/qa/meetings/{additionalMeeting!.MeetingId}/agent-token",
            new QaMintAgentTokenRequest(scenario.OrganizationId, ExpiresInMinutes: 15));
        tokenResponse.EnsureSuccessStatusCode();
        var token = await tokenResponse.Content.ReadFromJsonAsync<QaAgentTokenResponse>();
        token.Should().NotBeNull();

        var previousAuthorization = Client.DefaultRequestHeaders.Authorization;
        try
        {
            Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);

            var wrongMeetingResponse = await Client.PostAsJsonAsync(
                $"/api/agent/meetings/{scenario.MeetingId}/context/query",
                new { question = "Context?" });
            wrongMeetingResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);

            var validMeetingValidationResponse = await Client.PostAsJsonAsync(
                $"/api/agent/meetings/{additionalMeeting.MeetingId}/context/query",
                new { question = "" });
            validMeetingValidationResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally
        {
            Client.DefaultRequestHeaders.Authorization = previousAuthorization;
        }
    }

    [Fact]
    public async Task MintAgentToken_WithWrongOrganization_ShouldReturnNotFound()
    {
        var scenario = await CreateScenarioAsync("qa-agent-token-wrong-org");

        var response = await Client.PostAsJsonAsync(
            $"/api/dev/qa/meetings/{scenario.MeetingId}/agent-token",
            new QaMintAgentTokenRequest(Guid.NewGuid(), ExpiresInMinutes: 15));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task MintAgentToken_WithInvalidExpiry_ShouldReturnBadRequest()
    {
        var scenario = await CreateScenarioAsync("qa-agent-token-invalid-expiry");

        var response = await Client.PostAsJsonAsync(
            $"/api/dev/qa/meetings/{scenario.MeetingId}/agent-token",
            new QaMintAgentTokenRequest(scenario.OrganizationId, ExpiresInMinutes: 0));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ConfigureSttFailures_ShouldPersistRulesAndReturnAttemptLedger()
    {
        var scenario = await CreateScenarioAsync("qa-stt-failure-injection");

        var configureResponse = await Client.PostAsJsonAsync(
            $"/api/dev/qa/meetings/{scenario.MeetingId}/stt-failures",
            new QaConfigureSttFailuresRequest(
                scenario.OrganizationId,
                [
                    new QaSttFailureRuleRequest(
                        "qa/mtg:test/user:bob/track-bob.wav",
                        FailCount: 1,
                        Mode: "throw",
                        Message: "QA injected STT failure for retry capture")
                ]));

        configureResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var configured = await configureResponse.Content.ReadFromJsonAsync<QaSttFailureStateResponse>();
        configured.Should().NotBeNull();
        configured!.MeetingId.Should().Be(scenario.MeetingId);
        configured.OrganizationId.Should().Be(scenario.OrganizationId);
        configured.Rules.Should().ContainSingle();
        configured.Rules[0].StorageObjectKey.Should().Be("qa/mtg:test/user:bob/track-bob.wav");
        configured.Rules[0].FailCount.Should().Be(1);
        configured.Attempts.Should().BeEmpty();

        var getResponse = await Client.GetAsync(
            $"/api/dev/qa/meetings/{scenario.MeetingId}/stt-failures?organizationId={scenario.OrganizationId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var fetched = await getResponse.Content.ReadFromJsonAsync<QaSttFailureStateResponse>();
        fetched.Should().NotBeNull();
        fetched!.Rules.Should().ContainSingle();
        fetched.Rules[0].Message.Should().Be("QA injected STT failure for retry capture");
    }

    [Fact]
    public async Task ConfigureSttFailures_WithWrongOrganization_ShouldReturnNotFound()
    {
        var scenario = await CreateScenarioAsync("qa-stt-failure-injection-wrong-org");

        var response = await Client.PostAsJsonAsync(
            $"/api/dev/qa/meetings/{scenario.MeetingId}/stt-failures",
            new QaConfigureSttFailuresRequest(
                Guid.NewGuid(),
                [new QaSttFailureRuleRequest("qa/mtg:test/user:bob/track-bob.wav", FailCount: 1)]));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ReminderStatus_ShouldExposeAgentReminderLifecycleAcrossLinkedSeries()
    {
        var m1Start = DateTime.UtcNow.AddHours(1);
        var scenario = await CreateScenarioAsync(
            "qa-reminder-status-linked-series",
            meeting: new QaScenarioMeetingRequest(
                Title: "QA reminder status linked series",
                ScheduledStartUtc: m1Start,
                ScheduledEndUtc: m1Start.AddHours(1)));

        var m2Start = m1Start.AddDays(7);
        var additionalMeetingResponse = await Client.PostAsJsonAsync(
            $"/api/dev/qa/organizations/{scenario.OrganizationId}/meetings",
            new QaCreateAdditionalMeetingRequest(
                scenario.MeetingId,
                ScheduledStartUtc: m2Start,
                ScheduledEndUtc: m2Start.AddHours(1),
                LinkRecurringSeries: true,
                RecurringOccurrenceIndex: 1));
        additionalMeetingResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var m2 = await additionalMeetingResponse.Content.ReadFromJsonAsync<QaAdditionalMeetingResponse>();
        m2.Should().NotBeNull();

        SetAgentAuthorization(scenario.OrganizationId, scenario.MeetingId);
        var createReminderResponse = await Client.PostAsJsonAsync(
            $"/api/agent/meetings/{scenario.MeetingId}/reminders",
            new
            {
                text = "Confirm carry-forward reminder lifecycle",
                scope = "Public",
                targetUserId = (Guid?)null,
                reminderAtUtc = m2!.ScheduledStartUtc,
                createdByUserId = scenario.Users.Values.First().UserId
            });
        createReminderResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var reminder = await createReminderResponse.Content.ReadFromJsonAsync<AgentReminderResponse>();
        reminder.Should().NotBeNull();

        var activeStatusResponse = await Client.GetAsync(
            $"/api/dev/qa/organizations/{scenario.OrganizationId}/reminders?meetingId={m2.MeetingId}&includeSeries=true");
        activeStatusResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var activeStatuses = await activeStatusResponse.Content.ReadFromJsonAsync<List<QaReminderStatusResponse>>();
        activeStatuses.Should().NotBeNull();
        activeStatuses!.Should().ContainSingle(x => x.Id == reminder!.Id)
            .Which.Status.Should().Be(ReminderStatus.Active.ToString());

        SetAgentAuthorization(scenario.OrganizationId, m2.MeetingId);
        var markDeliveredResponse = await Client.PostAsync($"/api/agent/reminders/{reminder!.Id}/mark-delivered", null);
        markDeliveredResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var deliveredStatusResponse = await Client.GetAsync(
            $"/api/dev/qa/organizations/{scenario.OrganizationId}/reminders?meetingId={m2.MeetingId}&includeSeries=true");
        deliveredStatusResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var deliveredStatuses = await deliveredStatusResponse.Content.ReadFromJsonAsync<List<QaReminderStatusResponse>>();
        deliveredStatuses.Should().NotBeNull();
        var delivered = deliveredStatuses!.Should().ContainSingle(x => x.Id == reminder.Id).Which;
        delivered.Status.Should().Be(ReminderStatus.Delivered.ToString());
        delivered.DeliveredAtUtc.Should().NotBeNull();
    }

    private async Task<QaScenarioResponse> CreateScenarioAsync(
        string scenario,
        IReadOnlyList<QaScenarioUserRequest>? users = null,
        IReadOnlyList<string>? tags = null,
        QaScenarioMeetingRequest? meeting = null)
    {
        var response = await Client.PostAsJsonAsync(
            "/api/dev/qa/scenario",
            new QaScenarioRequest(
                Scenario: scenario,
                RunId: Guid.NewGuid().ToString("N"),
                Users: users,
                Meeting: meeting ?? new QaScenarioMeetingRequest(Title: $"QA {scenario}"),
                Tags: tags));

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<QaScenarioResponse>();
        body.Should().NotBeNull();
        return body!;
    }

    private void SetAgentAuthorization(Guid organizationId, Guid meetingId)
    {
        var token = TestJwtTokenHelper.GenerateAgentToken(organizationId, meetingId);
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}
