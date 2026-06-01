using System.Net;
using System.Text;
using FluentAssertions;
using MeetingAssistant.Features.LiveSession.Infrastructure;
using MeetingAssistant.Features.LiveSession.Services;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Organizations.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace tests.Integration.LiveSession
{
    public class AiAssistantDispatchServiceTests
    {
        [Fact]
        public async Task EnableAsync_OrganizationAdmin_ShouldCreateNamedAgentDispatch()
        {
            await using var fixture = await LiveSessionTestDb.CreateAsync();
            var orgId = fixture.SeedOrganization();
            var userId = fixture.SeedUser();
            AddMembership(fixture, orgId, userId, OrganizationRole.Admin);
            var meetingId = fixture.SeedMeeting(orgId, MeetingStatus.Scheduled);
            var handler = new FakeLiveKitDispatchHandler();
            var sut = CreateService(fixture, handler);

            var result = await sut.EnableAsync(orgId, meetingId, userId);

            result.IsSuccess.Should().BeTrue();
            result.Value.Enabled.Should().BeTrue();
            result.Value.AgentIsConnected.Should().BeTrue();
            result.Value.DispatchId.Should().Be("dispatch-1");
            fixture.DbContext.Meetings.Find(meetingId)!.AiAssistantEnabled.Should().BeTrue();
            handler.RequestPaths.Should().Equal(
                "/twirp/livekit.RoomService/CreateRoom",
                "/twirp/livekit.AgentDispatchService/ListDispatch",
                "/twirp/livekit.AgentDispatchService/CreateDispatch");
            handler.RequestBodies.Last().Should().Contain("\"agent_name\":\"meeting-assistant\"");
            handler.RequestBodies.Last().Should().Contain($"\"room\":\"mtg:{meetingId}\"");
            handler.RequestBodies.First().Should().Contain($"\"name\":\"mtg:{meetingId}\"");
        }

        [Fact]
        public async Task EnableAsync_NonAdminOrganizationMember_ShouldReturnForbiddenWithoutCallingLiveKit()
        {
            await using var fixture = await LiveSessionTestDb.CreateAsync();
            var orgId = fixture.SeedOrganization();
            var userId = fixture.SeedUser();
            AddMembership(fixture, orgId, userId, OrganizationRole.Member);
            var meetingId = fixture.SeedMeeting(orgId, MeetingStatus.Scheduled);
            var handler = new FakeLiveKitDispatchHandler();
            var sut = CreateService(fixture, handler);

            var result = await sut.EnableAsync(orgId, meetingId, userId);

            result.IsFailure.Should().BeTrue();
            result.Error.Code.Should().Be("Organization.Unauthorized");
            handler.RequestPaths.Should().BeEmpty();
        }

        [Fact]
        public async Task DisableAsync_ExistingDispatch_ShouldDeleteNamedDispatch()
        {
            await using var fixture = await LiveSessionTestDb.CreateAsync();
            var orgId = fixture.SeedOrganization();
            var userId = fixture.SeedUser();
            AddMembership(fixture, orgId, userId, OrganizationRole.Admin);
            var meetingId = fixture.SeedMeeting(orgId, MeetingStatus.InProgress);
            var handler = new FakeLiveKitDispatchHandler(existingDispatch: true);
            var sut = CreateService(fixture, handler);

            var result = await sut.DisableAsync(orgId, meetingId, userId);

            result.IsSuccess.Should().BeTrue();
            result.Value.Enabled.Should().BeFalse();
            fixture.DbContext.Meetings.Find(meetingId)!.AiAssistantEnabled.Should().BeFalse();
            handler.RequestPaths.Should().Equal(
                "/twirp/livekit.AgentDispatchService/ListDispatch",
                "/twirp/livekit.AgentDispatchService/DeleteDispatch");
            handler.RequestBodies.Last().Should().Contain("\"dispatch_id\":\"dispatch-1\"");
        }

        [Fact]
        public async Task GetStatusAsync_DefaultEnabledMeetingWithoutRoom_ShouldReturnEnabledWithoutDispatch()
        {
            await using var fixture = await LiveSessionTestDb.CreateAsync();
            var orgId = fixture.SeedOrganization();
            var userId = fixture.SeedUser();
            AddMembership(fixture, orgId, userId, OrganizationRole.Admin);
            var meetingId = fixture.SeedMeeting(orgId, MeetingStatus.Scheduled);
            var handler = new FakeLiveKitDispatchHandler(roomUnavailable: true);
            var sut = CreateService(fixture, handler);

            var result = await sut.GetStatusAsync(orgId, meetingId, userId);

            result.IsSuccess.Should().BeTrue();
            result.Value.Enabled.Should().BeTrue();
            result.Value.AgentIsConnected.Should().BeFalse();
            result.Value.DispatchId.Should().BeNull();
            handler.RequestPaths.Should().Equal("/twirp/livekit.AgentDispatchService/ListDispatch");
        }

        [Fact]
        public async Task EnsureDispatchedForMeetingAsync_EnabledMeeting_ShouldCreateNamedAgentDispatchWithoutAdminCheck()
        {
            await using var fixture = await LiveSessionTestDb.CreateAsync();
            var orgId = fixture.SeedOrganization();
            var userId = fixture.SeedUser();
            var meetingId = fixture.SeedMeeting(orgId, MeetingStatus.Scheduled);
            var handler = new FakeLiveKitDispatchHandler();
            var sut = CreateService(fixture, handler);

            var result = await sut.EnsureDispatchedForMeetingAsync(orgId, meetingId, userId);

            result.IsSuccess.Should().BeTrue();
            result.Value.Enabled.Should().BeTrue();
            result.Value.DispatchId.Should().Be("dispatch-1");
            handler.RequestPaths.Should().Equal(
                "/twirp/livekit.RoomService/CreateRoom",
                "/twirp/livekit.AgentDispatchService/ListDispatch",
                "/twirp/livekit.AgentDispatchService/CreateDispatch");
        }

        private static AiAssistantDispatchService CreateService(
            LiveSessionTestDb fixture,
            FakeLiveKitDispatchHandler handler)
        {
            return new AiAssistantDispatchService(
                fixture.DbContext,
                new FakeHttpClientFactory(handler),
                Options.Create(new LiveKitOptions
                {
                    ApiKey = "test-livekit-key",
                    ApiSecret = "0123456789abcdef0123456789abcdef",
                    EgressHost = "http://livekit.example",
                    AgentName = "meeting-assistant"
                }),
                NullLogger<AiAssistantDispatchService>.Instance);
        }

        private static void AddMembership(
            LiveSessionTestDb fixture,
            Guid orgId,
            Guid userId,
            OrganizationRole role)
        {
            fixture.DbContext.UserOrgMemberships.Add(new UserOrgMembership
            {
                OrganizationId = orgId,
                UserId = userId,
                OrgRole = role,
                IsEnabled = true
            });
            fixture.DbContext.SaveChanges();
        }

        private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
        }

        private sealed class FakeLiveKitDispatchHandler(
            bool existingDispatch = false,
            bool roomUnavailable = false) : HttpMessageHandler
        {
            public List<string> RequestPaths { get; } = [];
            public List<string> RequestBodies { get; } = [];

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                RequestPaths.Add(request.RequestUri!.AbsolutePath);
                RequestBodies.Add(request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken));

                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/CreateRoom", StringComparison.Ordinal))
                {
                    return Json("{}");
                }

                if (path.EndsWith("/ListDispatch", StringComparison.Ordinal))
                {
                    if (roomUnavailable)
                    {
                        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                        {
                            Content = new StringContent(
                                "{\"code\":\"unavailable\",\"msg\":\"twirp error unknown: no response from servers\"}",
                                Encoding.UTF8,
                                "application/json")
                        };
                    }

                    return Json(existingDispatch ? ListWithDispatchJson : "{\"agent_dispatches\":[]}");
                }

                if (path.EndsWith("/CreateDispatch", StringComparison.Ordinal))
                {
                    return Json(DispatchJson);
                }

                if (path.EndsWith("/DeleteDispatch", StringComparison.Ordinal))
                {
                    return Json("{}");
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            private static HttpResponseMessage Json(string json)
                => new(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };

            private const string DispatchJson =
                """
                {
                  "id":"dispatch-1",
                  "agent_name":"meeting-assistant",
                  "state":{
                    "jobs":[{"state":{"status":"JS_RUNNING","participant_identity":"meeting-assistant"}}]
                  }
                }
                """;

            private const string ListWithDispatchJson =
                """
                {
                  "agent_dispatches":[
                    {
                      "id":"dispatch-1",
                      "agent_name":"meeting-assistant",
                      "state":{
                        "jobs":[{"state":{"status":"JS_RUNNING","participant_identity":"meeting-assistant"}}]
                      }
                    }
                  ]
                }
                """;
        }
    }
}
