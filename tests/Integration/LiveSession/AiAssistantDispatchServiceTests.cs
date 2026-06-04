using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MeetingAssistant.Api.Infrastructure.Configuration;
using MeetingAssistant.Features.AgentApi.Services;
using MeetingAssistant.Features.LiveSession.Infrastructure;
using MeetingAssistant.Features.LiveSession.Services;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Shared.Abstractions;
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
                "/twirp/livekit.RoomService/ListParticipants",
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
        public async Task EnableAsync_LiveKitTransportFailure_ShouldReturnLiveKitCallFailed()
        {
            await using var fixture = await LiveSessionTestDb.CreateAsync();
            var orgId = fixture.SeedOrganization();
            var userId = fixture.SeedUser();
            AddMembership(fixture, orgId, userId, OrganizationRole.Admin);
            var meetingId = fixture.SeedMeeting(orgId, MeetingStatus.Scheduled);
            var sut = CreateService(fixture, new ThrowingLiveKitDispatchHandler());

            var result = await sut.EnableAsync(orgId, meetingId, userId);

            result.IsFailure.Should().BeTrue();
            result.Error.Code.Should().Be("LiveSession.LiveKitCallFailed");
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
            handler.RequestPaths.Should().Equal(
                "/twirp/livekit.AgentDispatchService/ListDispatch",
                "/twirp/livekit.RoomService/ListParticipants");
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
                "/twirp/livekit.RoomService/ListParticipants",
                "/twirp/livekit.AgentDispatchService/CreateDispatch");
        }

        [Fact]
        public async Task EnableAsync_ShouldIncludeAgentApiMetadataWithAgentTokenForRag()
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
            using var requestBody = JsonDocument.Parse(handler.RequestBodies.Last());
            var metadataJson = requestBody.RootElement.GetProperty("metadata").GetString();
            using var metadata = JsonDocument.Parse(metadataJson!);
            var agentApi = metadata.RootElement.GetProperty("agentApi");
            agentApi.GetProperty("enabled").GetBoolean().Should().BeTrue();
            agentApi.GetProperty("agentToken").GetString().Should().Be("debug-agent-token");
            metadata.RootElement.TryGetProperty("aiDebug", out _).Should().BeFalse();
        }

        [Fact]
        public async Task EnableAsync_WhenAiDebugEnabled_ShouldIncludeDebugMetadataWithAgentToken()
        {
            await using var fixture = await LiveSessionTestDb.CreateAsync();
            var orgId = fixture.SeedOrganization();
            var userId = fixture.SeedUser();
            AddMembership(fixture, orgId, userId, OrganizationRole.Admin);
            var meetingId = fixture.SeedMeeting(orgId, MeetingStatus.Scheduled);
            var handler = new FakeLiveKitDispatchHandler();
            var sut = CreateService(fixture, handler, aiDebugEnabled: true, persistPayloads: false);

            var result = await sut.EnableAsync(orgId, meetingId, userId);

            result.IsSuccess.Should().BeTrue();
            using var requestBody = JsonDocument.Parse(handler.RequestBodies.Last());
            var metadataJson = requestBody.RootElement.GetProperty("metadata").GetString();
            using var metadata = JsonDocument.Parse(metadataJson!);
            var agentApi = metadata.RootElement.GetProperty("agentApi");
            agentApi.GetProperty("enabled").GetBoolean().Should().BeTrue();
            agentApi.GetProperty("agentToken").GetString().Should().Be("debug-agent-token");
            var aiDebug = metadata.RootElement.GetProperty("aiDebug");
            aiDebug.GetProperty("enabled").GetBoolean().Should().BeTrue();
            aiDebug.GetProperty("persistPayloads").GetBoolean().Should().BeFalse();
            aiDebug.GetProperty("agentToken").GetString().Should().Be("debug-agent-token");
        }

        [Fact]
        public async Task GetStatusAsync_ConnectedDispatch_ShouldReportConnectedWithoutListParticipants()
        {
            await using var fixture = await LiveSessionTestDb.CreateAsync();
            var orgId = fixture.SeedOrganization();
            var userId = fixture.SeedUser();
            AddMembership(fixture, orgId, userId, OrganizationRole.Admin);
            var meetingId = fixture.SeedMeeting(orgId, MeetingStatus.Scheduled);
            var handler = new FakeLiveKitDispatchHandler(
                listDispatchScript:
                [
                    FakeLiveKitDispatchHandler.ConnectedDispatchListJson("dispatch-connected")
                ]);
            var sut = CreateService(fixture, handler);

            var result = await sut.GetStatusAsync(orgId, meetingId, userId);

            result.IsSuccess.Should().BeTrue();
            result.Value.Enabled.Should().BeTrue();
            result.Value.AgentIsConnected.Should().BeTrue();
            result.Value.DispatchId.Should().Be("dispatch-connected");
            handler.RequestPaths.Should().Equal("/twirp/livekit.AgentDispatchService/ListDispatch");
        }

        [Fact]
        public async Task GetStatusAsync_StaleDispatch_ShouldReportDisconnectedAndRemainReadOnly()
        {
            await using var fixture = await LiveSessionTestDb.CreateAsync();
            var orgId = fixture.SeedOrganization();
            var userId = fixture.SeedUser();
            AddMembership(fixture, orgId, userId, OrganizationRole.Admin);
            var meetingId = fixture.SeedMeeting(orgId, MeetingStatus.Scheduled);
            var handler = new FakeLiveKitDispatchHandler(
                listDispatchScript: [FakeLiveKitDispatchHandler.StaleDispatchListJson]);
            var sut = CreateService(fixture, handler);

            var result = await sut.GetStatusAsync(orgId, meetingId, userId);

            result.IsSuccess.Should().BeTrue();
            result.Value.Enabled.Should().BeTrue();
            result.Value.DispatchId.Should().Be("dispatch-stale");
            result.Value.AgentIsConnected.Should().BeFalse();
            handler.RequestPaths.Should().Equal(
                "/twirp/livekit.AgentDispatchService/ListDispatch",
                "/twirp/livekit.RoomService/ListParticipants");
        }

        [Fact]
        public async Task EnableAsync_ExistingStaleDispatch_ShouldDeleteAndCreateFreshDispatch()
        {
            await using var fixture = await LiveSessionTestDb.CreateAsync();
            var orgId = fixture.SeedOrganization();
            var userId = fixture.SeedUser();
            AddMembership(fixture, orgId, userId, OrganizationRole.Admin);
            var meetingId = fixture.SeedMeeting(orgId, MeetingStatus.Scheduled);
            var handler = new FakeLiveKitDispatchHandler(
                listDispatchScript: [FakeLiveKitDispatchHandler.StaleDispatchListJson],
                createDispatchScript: [FakeLiveKitDispatchHandler.ConnectedDispatchJson("dispatch-fresh")]);
            var sut = CreateService(fixture, handler);

            var result = await sut.EnableAsync(orgId, meetingId, userId);

            result.IsSuccess.Should().BeTrue();
            result.Value.Enabled.Should().BeTrue();
            result.Value.AgentIsConnected.Should().BeTrue();
            result.Value.DispatchId.Should().Be("dispatch-fresh");
            handler.RequestPaths.Should().Equal(
                "/twirp/livekit.RoomService/CreateRoom",
                "/twirp/livekit.AgentDispatchService/ListDispatch",
                "/twirp/livekit.RoomService/ListParticipants",
                "/twirp/livekit.AgentDispatchService/DeleteDispatch",
                "/twirp/livekit.AgentDispatchService/CreateDispatch");
            handler.RequestBodies
                .Single(body => body.Contains("\"dispatch_id\"", StringComparison.Ordinal))
                .Should().Contain("\"dispatch_id\":\"dispatch-stale\"");
        }

        [Fact]
        public async Task EnableAsync_ExistingConnectedDispatch_ShouldReuseWithoutDeleteOrCreate()
        {
            await using var fixture = await LiveSessionTestDb.CreateAsync();
            var orgId = fixture.SeedOrganization();
            var userId = fixture.SeedUser();
            AddMembership(fixture, orgId, userId, OrganizationRole.Admin);
            var meetingId = fixture.SeedMeeting(orgId, MeetingStatus.Scheduled);
            var handler = new FakeLiveKitDispatchHandler(
                listDispatchScript:
                [
                    FakeLiveKitDispatchHandler.ConnectedAndStaleDispatchListJson(
                        connectedDispatchId: "dispatch-connected",
                        staleDispatchId: "dispatch-stale")
                ]);
            var sut = CreateService(fixture, handler);

            var result = await sut.EnableAsync(orgId, meetingId, userId);

            result.IsSuccess.Should().BeTrue();
            result.Value.Enabled.Should().BeTrue();
            result.Value.AgentIsConnected.Should().BeTrue();
            result.Value.DispatchId.Should().Be("dispatch-connected");
            handler.RequestPaths.Should().Equal(
                "/twirp/livekit.RoomService/CreateRoom",
                "/twirp/livekit.AgentDispatchService/ListDispatch");
        }

        [Fact]
        public async Task EnsureDispatchedForMeetingAsync_ExistingStaleDispatch_ShouldRepairWithoutAdminCheck()
        {
            await using var fixture = await LiveSessionTestDb.CreateAsync();
            var orgId = fixture.SeedOrganization();
            var userId = fixture.SeedUser();
            var meetingId = fixture.SeedMeeting(orgId, MeetingStatus.Scheduled);
            var handler = new FakeLiveKitDispatchHandler(
                listDispatchScript: [FakeLiveKitDispatchHandler.StaleDispatchListJson],
                createDispatchScript: [FakeLiveKitDispatchHandler.ConnectedDispatchJson("dispatch-fresh")]);
            var sut = CreateService(fixture, handler);

            var result = await sut.EnsureDispatchedForMeetingAsync(orgId, meetingId, userId);

            result.IsSuccess.Should().BeTrue();
            result.Value.Enabled.Should().BeTrue();
            result.Value.AgentIsConnected.Should().BeTrue();
            result.Value.DispatchId.Should().Be("dispatch-fresh");
            handler.RequestPaths.Should().Equal(
                "/twirp/livekit.RoomService/CreateRoom",
                "/twirp/livekit.AgentDispatchService/ListDispatch",
                "/twirp/livekit.RoomService/ListParticipants",
                "/twirp/livekit.AgentDispatchService/DeleteDispatch",
                "/twirp/livekit.AgentDispatchService/CreateDispatch");
        }

        [Fact]
        public async Task EnableAsync_CreatedDispatchHasAgentParticipantButListDispatchUnconnected_ShouldNotDeleteOrRetryAndReportConnected()
        {
            await using var fixture = await LiveSessionTestDb.CreateAsync();
            var orgId = fixture.SeedOrganization();
            var userId = fixture.SeedUser();
            AddMembership(fixture, orgId, userId, OrganizationRole.Admin);
            var meetingId = fixture.SeedMeeting(orgId, MeetingStatus.Scheduled);
            var handler = new FakeLiveKitDispatchHandler(
                listDispatchScript:
                [
                    FakeLiveKitDispatchHandler.EmptyDispatchListJson,
                    FakeLiveKitDispatchHandler.UnconnectedDispatchListJson("dispatch-1"),
                    FakeLiveKitDispatchHandler.UnconnectedDispatchListJson("dispatch-1")
                ],
                createDispatchScript:
                [
                    FakeLiveKitDispatchHandler.UnconnectedDispatchJson("dispatch-1")
                ],
                listParticipantsScript:
                [
                    FakeLiveKitDispatchHandler.EmptyParticipantsListJson,
                    FakeLiveKitDispatchHandler.AgentParticipantListJson("agent-AJ_first")
                ]);
            var sut = CreateService(
                fixture,
                handler,
                liveKitOptions: new LiveKitOptions
                {
                    ApiKey = "test-livekit-key",
                    ApiSecret = "0123456789abcdef0123456789abcdef",
                    EgressHost = "http://livekit.example",
                    AgentName = "meeting-assistant",
                    AgentDispatchConnectPollAttempts = 1,
                    AgentDispatchConnectPollIntervalMs = 1
                });

            var result = await sut.EnableAsync(orgId, meetingId, userId);

            result.IsSuccess.Should().BeTrue();
            result.Value.Enabled.Should().BeTrue();
            result.Value.AgentIsConnected.Should().BeTrue();
            result.Value.DispatchId.Should().Be("dispatch-1");
            handler.RequestPaths.Count(path => path.EndsWith("/CreateDispatch", StringComparison.Ordinal))
                .Should().Be(1);
            handler.RequestPaths.Count(path => path.EndsWith("/DeleteDispatch", StringComparison.Ordinal))
                .Should().Be(0);
        }

        [Fact]
        public async Task EnableAsync_ExistingUnconnectedDispatchWithAgentParticipant_ShouldReuseWithoutDeleteOrCreate()
        {
            await using var fixture = await LiveSessionTestDb.CreateAsync();
            var orgId = fixture.SeedOrganization();
            var userId = fixture.SeedUser();
            AddMembership(fixture, orgId, userId, OrganizationRole.Admin);
            var meetingId = fixture.SeedMeeting(orgId, MeetingStatus.Scheduled);
            var handler = new FakeLiveKitDispatchHandler(
                listDispatchScript: [FakeLiveKitDispatchHandler.StaleDispatchListJson],
                listParticipantsScript:
                [
                    FakeLiveKitDispatchHandler.AgentParticipantListJson("agent-AJ_first")
                ]);
            var sut = CreateService(fixture, handler);

            var result = await sut.EnableAsync(orgId, meetingId, userId);

            result.IsSuccess.Should().BeTrue();
            result.Value.Enabled.Should().BeTrue();
            result.Value.AgentIsConnected.Should().BeTrue();
            result.Value.DispatchId.Should().Be("dispatch-stale");
            handler.RequestPaths.Count(path => path.EndsWith("/DeleteDispatch", StringComparison.Ordinal))
                .Should().Be(0);
            handler.RequestPaths.Count(path => path.EndsWith("/CreateDispatch", StringComparison.Ordinal))
                .Should().Be(0);
        }

        [Fact]
        public async Task GetStatusAsync_UnconnectedDispatchWithAgentParticipant_ShouldReportConnectedAndRemainReadOnly()
        {
            await using var fixture = await LiveSessionTestDb.CreateAsync();
            var orgId = fixture.SeedOrganization();
            var userId = fixture.SeedUser();
            AddMembership(fixture, orgId, userId, OrganizationRole.Admin);
            var meetingId = fixture.SeedMeeting(orgId, MeetingStatus.Scheduled);
            var handler = new FakeLiveKitDispatchHandler(
                listDispatchScript: [FakeLiveKitDispatchHandler.StaleDispatchListJson],
                listParticipantsScript:
                [
                    FakeLiveKitDispatchHandler.AgentParticipantListJson("agent-AJ_first")
                ]);
            var sut = CreateService(fixture, handler);

            var result = await sut.GetStatusAsync(orgId, meetingId, userId);

            result.IsSuccess.Should().BeTrue();
            result.Value.Enabled.Should().BeTrue();
            result.Value.AgentIsConnected.Should().BeTrue();
            result.Value.DispatchId.Should().Be("dispatch-stale");
            handler.RequestPaths.Should().Equal(
                "/twirp/livekit.AgentDispatchService/ListDispatch",
                "/twirp/livekit.RoomService/ListParticipants");
            handler.RequestPaths.Should().NotContain(path => path.EndsWith("/DeleteDispatch", StringComparison.Ordinal));
            handler.RequestPaths.Should().NotContain(path => path.EndsWith("/CreateDispatch", StringComparison.Ordinal));
        }

        [Fact]
        public async Task EnableAsync_CreatedDispatchStillUnconnectedAfterPoll_ShouldNotDeleteCreatedDispatchOrRetry()
        {
            await using var fixture = await LiveSessionTestDb.CreateAsync();
            var orgId = fixture.SeedOrganization();
            var userId = fixture.SeedUser();
            AddMembership(fixture, orgId, userId, OrganizationRole.Admin);
            var meetingId = fixture.SeedMeeting(orgId, MeetingStatus.Scheduled);
            var handler = new FakeLiveKitDispatchHandler(
                listDispatchScript:
                [
                    FakeLiveKitDispatchHandler.EmptyDispatchListJson,
                    FakeLiveKitDispatchHandler.UnconnectedDispatchListJson("dispatch-1"),
                    FakeLiveKitDispatchHandler.UnconnectedDispatchListJson("dispatch-1")
                ],
                createDispatchScript:
                [
                    FakeLiveKitDispatchHandler.UnconnectedDispatchJson("dispatch-1")
                ]);
            var sut = CreateService(
                fixture,
                handler,
                liveKitOptions: new LiveKitOptions
                {
                    ApiKey = "test-livekit-key",
                    ApiSecret = "0123456789abcdef0123456789abcdef",
                    EgressHost = "http://livekit.example",
                    AgentName = "meeting-assistant",
                    AgentDispatchConnectPollAttempts = 1,
                    AgentDispatchConnectPollIntervalMs = 1
                });

            var result = await sut.EnableAsync(orgId, meetingId, userId);

            result.IsSuccess.Should().BeTrue();
            result.Value.Enabled.Should().BeTrue();
            result.Value.AgentIsConnected.Should().BeFalse();
            result.Value.DispatchId.Should().Be("dispatch-1");
            handler.RequestPaths.Count(path => path.EndsWith("/CreateDispatch", StringComparison.Ordinal))
                .Should().Be(1);
            handler.RequestPaths.Count(path => path.EndsWith("/DeleteDispatch", StringComparison.Ordinal))
                .Should().Be(0);
        }

        private static AiAssistantDispatchService CreateService(
            LiveSessionTestDb fixture,
            HttpMessageHandler handler,
            bool aiDebugEnabled = false,
            bool persistPayloads = true,
            LiveKitOptions? liveKitOptions = null)
        {
            return new AiAssistantDispatchService(
                fixture.DbContext,
                new FakeHttpClientFactory(handler),
                Options.Create(liveKitOptions ?? new LiveKitOptions
                {
                    ApiKey = "test-livekit-key",
                    ApiSecret = "0123456789abcdef0123456789abcdef",
                    EgressHost = "http://livekit.example",
                    AgentName = "meeting-assistant",
                    AgentDispatchConnectPollAttempts = 0
                }),
                Options.Create(new AiDebugOptions
                {
                    Enabled = aiDebugEnabled,
                    PersistPayloads = persistPayloads
                }),
                Options.Create(new AgentJwtSettings
                {
                    Issuer = "MeetingAssistant",
                    Audience = "MeetingAssistantAgent",
                    SigningKey = "AgentSigningKeyForIntegrationTests_MustBeAtLeast32Chars!!",
                    TokenExpiryMinutes = 360
                }),
                new FakeAgentAuthService(),
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

        private sealed class FakeAgentAuthService : IAgentAuthService
        {
            public Task<Result<string>> MintTokenAsync(
                Guid organizationId,
                Guid meetingId,
                TimeSpan lifetime,
                CancellationToken cancellationToken = default)
                => Task.FromResult(Result.Success("debug-agent-token"));

            public Task<Result<string>> RefreshTokenAsync(
                string currentToken,
                CancellationToken cancellationToken = default)
                => Task.FromResult(Result.Success("debug-agent-token"));
        }

        private sealed class FakeLiveKitDispatchHandler : HttpMessageHandler
        {
            private readonly bool _roomUnavailable;
            private readonly Queue<string> _listDispatchResponses;
            private readonly Queue<string> _createDispatchResponses;
            private readonly Queue<string> _listParticipantsResponses;
            private readonly string _defaultListResponse;
            private readonly string _defaultCreateResponse;
            private readonly string _defaultListParticipantsResponse;

            public FakeLiveKitDispatchHandler(
                bool existingDispatch = false,
                bool roomUnavailable = false,
                IEnumerable<string>? listDispatchScript = null,
                IEnumerable<string>? createDispatchScript = null,
                IEnumerable<string>? listParticipantsScript = null)
            {
                _roomUnavailable = roomUnavailable;
                _defaultListResponse = existingDispatch ? ListWithDispatchJson : EmptyDispatchListJson;
                _defaultCreateResponse = ConnectedDispatchJson("dispatch-1");
                _defaultListParticipantsResponse = EmptyParticipantsListJson;
                _listDispatchResponses = new Queue<string>(
                    listDispatchScript ?? [existingDispatch ? ListWithDispatchJson : EmptyDispatchListJson]);
                _createDispatchResponses = new Queue<string>(
                    createDispatchScript ?? [ConnectedDispatchJson("dispatch-1")]);
                _listParticipantsResponses = new Queue<string>(
                    listParticipantsScript ?? [EmptyParticipantsListJson]);
            }

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
                    if (_roomUnavailable)
                    {
                        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                        {
                            Content = new StringContent(
                                "{\"code\":\"unavailable\",\"msg\":\"twirp error unknown: no response from servers\"}",
                                Encoding.UTF8,
                                "application/json")
                        };
                    }

                    return Json(DequeueOrDefault(_listDispatchResponses, _defaultListResponse));
                }

                if (path.EndsWith("/CreateDispatch", StringComparison.Ordinal))
                {
                    return Json(DequeueOrDefault(_createDispatchResponses, _defaultCreateResponse));
                }

                if (path.EndsWith("/DeleteDispatch", StringComparison.Ordinal))
                {
                    return Json("{}");
                }

                if (path.EndsWith("/ListParticipants", StringComparison.Ordinal))
                {
                    return Json(DequeueOrDefault(_listParticipantsResponses, _defaultListParticipantsResponse));
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            private static string DequeueOrDefault(Queue<string> queue, string fallback)
                => queue.Count > 0 ? queue.Dequeue() : fallback;

            public static string ConnectedDispatchJson(string dispatchId) =>
                "{\"id\":\"" + dispatchId
                + "\",\"agent_name\":\"meeting-assistant\",\"state\":{\"jobs\":[{\"state\":{\"status\":\"JS_RUNNING\",\"participant_identity\":\"meeting-assistant\"}}]}}";

            public static string UnconnectedDispatchJson(string dispatchId) =>
                "{\"id\":\"" + dispatchId
                + "\",\"agent_name\":\"meeting-assistant\",\"state\":{\"jobs\":[{\"state\":{\"status\":\"JS_PENDING\"}}]}}";

            public static string EmptyDispatchListJson => "{\"agent_dispatches\":[]}";

            public static string EmptyParticipantsListJson => "{\"participants\":[]}";

            public static string AgentParticipantListJson(string identity) =>
                "{\"participants\":[{\"identity\":\"" + identity + "\",\"kind\":\"AGENT\"}]}";

            public static string StaleDispatchListJson =>
                "{\"agent_dispatches\":[" + UnconnectedDispatchJson("dispatch-stale") + "]}";

            public static string UnconnectedDispatchListJson(string dispatchId) =>
                "{\"agent_dispatches\":[" + UnconnectedDispatchJson(dispatchId) + "]}";

            public static string ConnectedDispatchListJson(string dispatchId) =>
                "{\"agent_dispatches\":[" + ConnectedDispatchJson(dispatchId) + "]}";

            public static string ConnectedAndStaleDispatchListJson(
                string connectedDispatchId,
                string staleDispatchId) =>
                "{\"agent_dispatches\":["
                + UnconnectedDispatchJson(staleDispatchId)
                + ","
                + ConnectedDispatchJson(connectedDispatchId)
                + "]}";

            private static HttpResponseMessage Json(string json)
                => new(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };

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

        private sealed class ThrowingLiveKitDispatchHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                throw new HttpRequestException("LiveKit host unavailable");
            }
        }
    }
}
