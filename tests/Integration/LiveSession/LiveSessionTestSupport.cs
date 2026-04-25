using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Livekit.Server.Sdk.Dotnet;
using MediatR;
using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Features.Identity.Entites;
using MeetingAssistant.Features.LiveSession.Hubs;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace tests.Integration.LiveSession
{
    using HangfireJob = Hangfire.Common.Job;

    internal sealed class StaticTenantProvider(Guid? organizationId) : ITenantProvider
    {
        public Guid? CurrentOrganizationId => organizationId;
    }

    internal sealed class NoopPublisher : IPublisher
    {
        public Task Publish(object notification, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
            => Task.CompletedTask;
    }

    internal sealed class FakeBackgroundJobClient : IBackgroundJobClient
    {
        public List<HangfireJob> CreatedJobs { get; } = new();

        public string Create(HangfireJob job, IState state)
        {
            CreatedJobs.Add(job);
            return Guid.NewGuid().ToString();
        }

        public bool ChangeState(string jobId, IState state, string expectedState)
            => true;
    }

    internal sealed class FakeLiveSessionNotifier : ILiveSessionNotifier
    {
        public List<Guid> SessionStartedOrgIds { get; } = new();
        public List<Guid> SessionEndedOrgIds { get; } = new();
        public List<(Guid OrgId, Guid UserId)> ParticipantJoined { get; } = new();
        public List<(Guid OrgId, Guid UserId)> ParticipantLeft { get; } = new();

        public Task NotifySessionStartedAsync(Guid organizationId, Guid meetingId, DateTime occurredAtUtc, CancellationToken cancellationToken = default)
        {
            SessionStartedOrgIds.Add(organizationId);
            return Task.CompletedTask;
        }

        public Task NotifySessionEndedAsync(Guid organizationId, Guid meetingId, DateTime occurredAtUtc, CancellationToken cancellationToken = default)
        {
            SessionEndedOrgIds.Add(organizationId);
            return Task.CompletedTask;
        }

        public Task NotifyParticipantJoinedAsync(Guid organizationId, Guid meetingId, Guid participantUserId, DateTime occurredAtUtc, CancellationToken cancellationToken = default)
        {
            ParticipantJoined.Add((organizationId, participantUserId));
            return Task.CompletedTask;
        }

        public Task NotifyParticipantLeftAsync(Guid organizationId, Guid meetingId, Guid participantUserId, DateTime occurredAtUtc, CancellationToken cancellationToken = default)
        {
            ParticipantLeft.Add((organizationId, participantUserId));
            return Task.CompletedTask;
        }
    }

    internal sealed class FakeStorageService : MeetingAssistant.Features.LiveSession.Services.IStorageService
    {
        public List<(string SourceUrl, string ObjectKey)> Uploads { get; } = new();
        public bool ThrowOnUpload { get; set; }
        public long? NextSizeBytes { get; set; } = 1024;

        public Task<long?> UploadFromUrlAsync(string sourceUrl, string objectKey, CancellationToken cancellationToken = default)
        {
            if (ThrowOnUpload)
            {
                throw new InvalidOperationException("simulated upload failure");
            }

            Uploads.Add((sourceUrl, objectKey));
            return Task.FromResult(NextSizeBytes);
        }
    }

    internal sealed class LiveSessionTestDb : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly Guid _tenantOrganizationId;

        public ApplicationDbContext DbContext { get; }
        public Guid TenantOrganizationId => _tenantOrganizationId;

        private LiveSessionTestDb(SqliteConnection connection, ApplicationDbContext dbContext, Guid tenantOrganizationId)
        {
            _connection = connection;
            DbContext = dbContext;
            _tenantOrganizationId = tenantOrganizationId;
        }

        public static async Task<LiveSessionTestDb> CreateAsync(Guid? tenantOrganizationId = null)
        {
            var effectiveTenantId = tenantOrganizationId ?? Guid.NewGuid();
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection)
                .Options;

            var dbContext = new ApplicationDbContext(
                options,
                new HttpContextAccessor(),
                new StaticTenantProvider(effectiveTenantId),
                new NoopPublisher());

            await dbContext.Database.EnsureCreatedAsync();

            return new LiveSessionTestDb(connection, dbContext, effectiveTenantId);
        }

        public Guid SeedOrganization(string name = "org", Guid? organizationId = null)
        {
            var orgId = organizationId ?? _tenantOrganizationId;
            var existing = DbContext.Organizations.FirstOrDefault(x => x.Id == orgId);
            if (existing != null)
            {
                return existing.Id;
            }

            var org = new Organization
            {
                Id = orgId,
                Name = name,
                Slug = $"{name}-{Guid.NewGuid():N}".ToLowerInvariant()
            };

            DbContext.Organizations.Add(org);
            DbContext.SaveChanges();
            return org.Id;
        }

        public Guid SeedUser(string prefix = "user")
        {
            var user = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = $"{prefix}-{Guid.NewGuid():N}@example.com",
                Email = $"{prefix}-{Guid.NewGuid():N}@example.com",
                DisplayName = prefix
            };

            DbContext.Users.Add(user);
            DbContext.SaveChanges();
            return user.Id;
        }

        public Guid SeedMeeting(Guid organizationId, MeetingStatus status = MeetingStatus.Scheduled)
        {
            var meeting = new Meeting
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                Title = "Meeting",
                ScheduledStartUtc = DateTime.UtcNow.AddMinutes(5),
                ScheduledEndUtc = DateTime.UtcNow.AddMinutes(45),
                Status = status
            };

            DbContext.Meetings.Add(meeting);
            DbContext.SaveChanges();
            return meeting.Id;
        }

        public void AddParticipant(Guid meetingId, Guid organizationId, Guid userId, MeetingRole role = MeetingRole.Participant)
        {
            DbContext.MeetingParticipants.Add(new MeetingParticipant
            {
                MeetingId = meetingId,
                OrganizationId = organizationId,
                UserId = userId,
                MeetingRole = role
            });
            DbContext.SaveChanges();
        }

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    internal static class WebhookEventFactory
    {
        public static WebhookEvent EgressEnded(
            Guid meetingId,
            string eventId,
            EgressStatus status,
            Guid? participantUserId = null,
            string? sourceUrl = null)
        {
            var evt = new WebhookEvent
            {
                Event = "egress_ended",
                Id = eventId,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                EgressInfo = new EgressInfo
                {
                    RoomName = $"mtg:{meetingId}",
                    Status = status
                }
            };

            evt.EgressInfo.FileResults.Add(new Livekit.Server.Sdk.Dotnet.FileInfo
            {
                Filename = participantUserId.HasValue ? $"user:{participantUserId.Value}" : string.Empty,
                Location = sourceUrl ?? string.Empty,
            });

            return evt;
        }

        public static WebhookEvent RoomStarted(Guid meetingId, string eventId)
            => new()
            {
                Event = "room_started",
                Id = eventId,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Room = new Room { Name = $"mtg:{meetingId}" }
            };

        public static WebhookEvent RoomFinished(Guid meetingId, string eventId)
            => new()
            {
                Event = "room_finished",
                Id = eventId,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Room = new Room { Name = $"mtg:{meetingId}" }
            };

        public static WebhookEvent ParticipantJoined(Guid meetingId, string eventId, Guid userId)
            => new()
            {
                Event = "participant_joined",
                Id = eventId,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Room = new Room { Name = $"mtg:{meetingId}" },
                Participant = new ParticipantInfo { Identity = $"user:{userId}" }
            };

        public static WebhookEvent ParticipantLeft(Guid meetingId, string eventId, Guid userId)
            => new()
            {
                Event = "participant_left",
                Id = eventId,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Room = new Room { Name = $"mtg:{meetingId}" },
                Participant = new ParticipantInfo { Identity = $"user:{userId}" }
            };
    }
}
