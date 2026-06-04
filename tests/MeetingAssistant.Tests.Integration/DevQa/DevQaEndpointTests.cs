using System.Net;
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
}
