using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using MeetingAssistant.Features.DevQa;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Tests.Integration.Infrastructure;
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

    private async Task<QaScenarioResponse> CreateScenarioAsync(
        string scenario,
        IReadOnlyList<QaScenarioUserRequest>? users = null,
        IReadOnlyList<string>? tags = null)
    {
        var response = await Client.PostAsJsonAsync(
            "/api/dev/qa/scenario",
            new QaScenarioRequest(
                Scenario: scenario,
                RunId: Guid.NewGuid().ToString("N"),
                Users: users,
                Meeting: new QaScenarioMeetingRequest(Title: $"QA {scenario}"),
                Tags: tags));

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<QaScenarioResponse>();
        body.Should().NotBeNull();
        return body!;
    }
}
