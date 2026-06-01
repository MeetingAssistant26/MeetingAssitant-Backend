using FluentAssertions;
using MediatR;
using MeetingAssistant.Features.LiveSession.Contracts.Responses;
using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Features.Identity.Entites;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Services;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Shared.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace tests.Integration.LiveSession
{
    public class JoinTokenTests
    {
        [Theory]
        [InlineData(MeetingRole.Host, true, true, true, true)]
        [InlineData(MeetingRole.CoHost, true, true, true, true)]
        [InlineData(MeetingRole.Participant, true, true, false, true)]
        [InlineData(MeetingRole.Observer, false, true, false, false)]
        public async Task ParticipantRole_Request_ShouldIssueRoleScopedPermissions(
            MeetingRole role,
            bool canPublish,
            bool canSubscribe,
            bool canModerate,
            bool canPublishData)
        {
            await using var fixture = await TestFixture.CreateAsync();

            var callerId = fixture.SeedMeetingWithParticipantRole(role, MeetingStatus.Scheduled);
            var sut = new SessionService(fixture.DbContext, new StubTokenIssuer());

            var result = await sut.IssueJoinTokenAsync(fixture.MeetingId, callerId, "Caller Name");

            result.IsSuccess.Should().BeTrue();
            result.Value.Permissions.CanPublish.Should().Be(canPublish);
            result.Value.Permissions.CanSubscribe.Should().Be(canSubscribe);
            result.Value.Permissions.CanModerate.Should().Be(canModerate);
            result.Value.Permissions.CanPublishData.Should().Be(canPublishData);
            result.Value.RoomName.Should().Be($"mtg:{fixture.MeetingId}");
        }

        [Fact]
        public async Task DefaultEnabledMeeting_Request_ShouldAutoDispatchAssistantWithoutBlockingJoinToken()
        {
            await using var fixture = await TestFixture.CreateAsync();

            var callerId = fixture.SeedMeetingWithParticipantRole(MeetingRole.Participant, MeetingStatus.Scheduled);
            var dispatcher = new StubAiAssistantDispatchService(Result.Success(new AiAssistantStatusResponse(true, false, "dispatch-1")));
            var sut = new SessionService(fixture.DbContext, new StubTokenIssuer(), dispatcher);

            var result = await sut.IssueJoinTokenAsync(fixture.MeetingId, callerId, "Caller Name");

            result.IsSuccess.Should().BeTrue();
            dispatcher.Calls.Should().ContainSingle();
            dispatcher.Calls[0].OrganizationId.Should().Be(fixture.OrganizationId);
            dispatcher.Calls[0].MeetingId.Should().Be(fixture.MeetingId);
            dispatcher.Calls[0].RequestedByUserId.Should().Be(callerId);
        }

        [Fact]
        public async Task DefaultEnabledMeeting_WhenAutoDispatchThrows_ShouldStillIssueJoinToken()
        {
            await using var fixture = await TestFixture.CreateAsync();

            var callerId = fixture.SeedMeetingWithParticipantRole(MeetingRole.Participant, MeetingStatus.Scheduled);
            var dispatcher = new StubAiAssistantDispatchService(
                Result.Success(new AiAssistantStatusResponse(true, false, null)),
                new HttpRequestException("LiveKit is unavailable"));
            var sut = new SessionService(fixture.DbContext, new StubTokenIssuer(), dispatcher);

            var result = await sut.IssueJoinTokenAsync(fixture.MeetingId, callerId, "Caller Name");

            result.IsSuccess.Should().BeTrue();
            result.Value.RoomName.Should().Be($"mtg:{fixture.MeetingId}");
            dispatcher.Calls.Should().ContainSingle();
        }

        [Fact]
        public async Task DisabledMeeting_Request_ShouldNotAutoDispatchAssistant()
        {
            await using var fixture = await TestFixture.CreateAsync();

            var callerId = fixture.SeedMeetingWithParticipantRole(
                MeetingRole.Participant,
                MeetingStatus.Scheduled,
                aiAssistantEnabled: false);
            var dispatcher = new StubAiAssistantDispatchService(Result.Success(new AiAssistantStatusResponse(true, false, "dispatch-1")));
            var sut = new SessionService(fixture.DbContext, new StubTokenIssuer(), dispatcher);

            var result = await sut.IssueJoinTokenAsync(fixture.MeetingId, callerId, "Caller Name");

            result.IsSuccess.Should().BeTrue();
            dispatcher.Calls.Should().BeEmpty();
        }

        [Fact]
        public async Task NonParticipant_Request_ShouldReturnForbidden()
        {
            await using var fixture = await TestFixture.CreateAsync();

            fixture.SeedMeetingWithParticipantRole(MeetingRole.Participant, MeetingStatus.Scheduled);
            var outsiderId = Guid.NewGuid();
            fixture.DbContext.Users.Add(new ApplicationUser
            {
                Id = outsiderId,
                UserName = "outsider@example.com",
                Email = "outsider@example.com"
            });
            await fixture.DbContext.SaveChangesAsync();

            var sut = new SessionService(fixture.DbContext, new StubTokenIssuer());
            var result = await sut.IssueJoinTokenAsync(fixture.MeetingId, outsiderId, null);

            result.IsFailure.Should().BeTrue();
            result.Error.Code.Should().Be("LiveSession.NotAParticipant");
        }

        [Theory]
        [InlineData(MeetingStatus.Cancelled)]
        [InlineData(MeetingStatus.Completed)]
        public async Task ClosedMeeting_Request_ShouldReturnConflict(MeetingStatus status)
        {
            await using var fixture = await TestFixture.CreateAsync();

            var callerId = fixture.SeedMeetingWithParticipantRole(MeetingRole.Host, status);
            var sut = new SessionService(fixture.DbContext, new StubTokenIssuer());

            var result = await sut.IssueJoinTokenAsync(fixture.MeetingId, callerId, null);

            result.IsFailure.Should().BeTrue();
            result.Error.Code.Should().Be("LiveSession.MeetingNotJoinable");
        }

        private sealed class StubTokenIssuer : ILiveKitTokenIssuer
        {
            public MeetingRole? LastRole { get; private set; }

            public Result<IssuedJoinToken> Issue(
                Guid meetingId,
                Guid organizationId,
                Guid userId,
                string displayName,
                MeetingRole role,
                SessionPermissions permissions)
            {
                LastRole = role;
                return Result.Success(new IssuedJoinToken(
                    $"token-{meetingId}-{userId}",
                    $"mtg:{meetingId}",
                    "wss://livekit.example",
                    DateTime.UtcNow.AddMinutes(15)));
            }
        }

        private sealed class StubAiAssistantDispatchService(
            Result<AiAssistantStatusResponse> ensureResult,
            Exception? ensureException = null) : IAiAssistantDispatchService
        {
            public List<EnsureCall> Calls { get; } = [];

            public Task<Result<AiAssistantStatusResponse>> GetStatusAsync(
                Guid organizationId,
                Guid meetingId,
                Guid callerUserId,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public Task<Result<AiAssistantStatusResponse>> EnableAsync(
                Guid organizationId,
                Guid meetingId,
                Guid callerUserId,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public Task<Result<AiAssistantStatusResponse>> DisableAsync(
                Guid organizationId,
                Guid meetingId,
                Guid callerUserId,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public Task<Result<AiAssistantStatusResponse>> EnsureDispatchedForMeetingAsync(
                Guid organizationId,
                Guid meetingId,
                Guid? requestedByUserId = null,
                CancellationToken cancellationToken = default)
            {
                Calls.Add(new EnsureCall(organizationId, meetingId, requestedByUserId));
                if (ensureException is not null)
                {
                    throw ensureException;
                }

                return Task.FromResult(ensureResult);
            }
        }

        private sealed record EnsureCall(Guid OrganizationId, Guid MeetingId, Guid? RequestedByUserId);

        private sealed class StaticTenantProvider(Guid organizationId) : ITenantProvider
        {
            public Guid? CurrentOrganizationId => organizationId;
        }

        private sealed class NoopPublisher : IPublisher
        {
            public Task Publish(object notification, CancellationToken cancellationToken = default)
                => Task.CompletedTask;

            public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
                where TNotification : INotification
                => Task.CompletedTask;
        }

        private sealed class TestFixture : IAsyncDisposable
        {
            public Guid OrganizationId { get; }
            public Guid MeetingId { get; }
            public ApplicationDbContext DbContext { get; }
            private SqliteConnection Connection { get; }

            private TestFixture(
                Guid organizationId,
                Guid meetingId,
                ApplicationDbContext dbContext,
                SqliteConnection connection)
            {
                OrganizationId = organizationId;
                MeetingId = meetingId;
                DbContext = dbContext;
                Connection = connection;
            }

            public static async Task<TestFixture> CreateAsync()
            {
                var connection = new SqliteConnection("DataSource=:memory:");
                await connection.OpenAsync();

                var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                    .UseSqlite(connection)
                    .Options;

                var organizationId = Guid.NewGuid();
                var meetingId = Guid.NewGuid();
                var httpContextAccessor = new HttpContextAccessor();
                var tenantProvider = new StaticTenantProvider(organizationId);
                var dbContext = new ApplicationDbContext(options, httpContextAccessor, tenantProvider, new NoopPublisher());

                await dbContext.Database.EnsureCreatedAsync();

                return new TestFixture(organizationId, meetingId, dbContext, connection);
            }

            public Guid SeedMeetingWithParticipantRole(
                MeetingRole role,
                MeetingStatus status,
                bool aiAssistantEnabled = true)
            {
                var org = new Organization
                {
                    Id = OrganizationId,
                    Name = "Test Org",
                    Slug = "test-org"
                };

                var user = new ApplicationUser
                {
                    Id = Guid.NewGuid(),
                    UserName = $"{role.ToString().ToLowerInvariant()}@example.com",
                    Email = $"{role.ToString().ToLowerInvariant()}@example.com",
                    DisplayName = role.ToString()
                };

                var meeting = new Meeting
                {
                    Id = MeetingId,
                    OrganizationId = OrganizationId,
                    Title = "Live Session Test",
                    ScheduledStartUtc = DateTime.UtcNow.AddMinutes(10),
                    ScheduledEndUtc = DateTime.UtcNow.AddMinutes(40),
                    Status = status,
                    AiAssistantEnabled = aiAssistantEnabled
                };

                var participant = new MeetingParticipant
                {
                    MeetingId = MeetingId,
                    OrganizationId = OrganizationId,
                    UserId = user.Id,
                    MeetingRole = role
                };

                DbContext.Organizations.Add(org);
                DbContext.Users.Add(user);
                DbContext.Meetings.Add(meeting);
                DbContext.MeetingParticipants.Add(participant);
                DbContext.SaveChanges();

                return user.Id;
            }

            public async ValueTask DisposeAsync()
            {
                await DbContext.DisposeAsync();
                await Connection.DisposeAsync();
            }
        }
    }
}
