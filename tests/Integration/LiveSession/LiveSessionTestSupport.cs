using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Livekit.Server.Sdk.Dotnet;
using MediatR;
using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Features.Identity.Entites;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Models.Events;
using MeetingAssistant.Features.LiveSession.Services;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;

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

    internal sealed class CollectingPublisher(
        Func<object, CancellationToken, Task>? onPublish = null) : IPublisher
    {
        private readonly Func<object, CancellationToken, Task>? _onPublish = onPublish;

        public ConcurrentBag<object> Notifications { get; } = new();

        public Task Publish(object notification, CancellationToken cancellationToken = default)
            => PublishCoreAsync(notification, cancellationToken);

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
            => PublishCoreAsync(notification!, cancellationToken);

        private async Task PublishCoreAsync(object notification, CancellationToken cancellationToken)
        {
            Notifications.Add(notification);

            if (_onPublish != null)
            {
                await _onPublish(notification, cancellationToken);
            }
        }
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

    internal sealed class FakeEgressService : IEgressService
    {
        public List<(Guid MeetingId, string RoomName, string TrackId, string ParticipantIdentity)> Starts { get; } = new();
        public Queue<Exception> Failures { get; } = new();

        public Task<EgressStartResult> StartTrackEgressAsync(Guid meetingId, string roomName, string trackId, string participantIdentity, CancellationToken ct = default)
        {
            Starts.Add((meetingId, roomName, trackId, participantIdentity));
            if (Failures.TryDequeue(out var failure))
            {
                throw failure;
            }

            return Task.FromResult(new EgressStartResult(
                $"EG_{trackId}",
                $"tracks/{roomName}/{participantIdentity}/track-{trackId}.ogg"));
        }
    }

    internal sealed class FakeStorageService : MeetingAssistant.Features.LiveSession.Services.IStorageService
    {
        public List<(string SourceUrl, string ObjectKey)> Uploads { get; } = new();
        public List<(string SourceFilePath, string ObjectKey)> FileUploads { get; } = new();
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

        public Task<StorageUploadResult> UploadFileAsync(string sourceFilePath, string objectKey, CancellationToken cancellationToken = default)
        {
            if (ThrowOnUpload)
            {
                throw new InvalidOperationException("simulated upload failure");
            }

            FileUploads.Add((sourceFilePath, objectKey));
            return Task.FromResult(new StorageUploadResult(NextSizeBytes, $"http://minio:9000/recordings/{objectKey}"));
        }

        public Task EnsureBucketExistsAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    internal sealed class StubSttService(
        IReadOnlyDictionary<string, TrackTranscriptionResult> resultsByObjectKey,
        IReadOnlyDictionary<string, Exception>? failuresByObjectKey = null) : ISttService
    {
        private readonly IReadOnlyDictionary<string, TrackTranscriptionResult> _resultsByObjectKey = resultsByObjectKey;
        private readonly IReadOnlyDictionary<string, Exception> _failuresByObjectKey = failuresByObjectKey
            ?? new Dictionary<string, Exception>();

        public ConcurrentBag<string> Calls { get; } = new();

        public Task<TrackTranscriptionResult> TranscribeTrackAsync(
            Guid participantUserId,
            string storageObjectKey,
            CancellationToken ct = default)
        {
            Calls.Add(storageObjectKey);

            if (_failuresByObjectKey.TryGetValue(storageObjectKey, out var failure))
            {
                throw failure;
            }

            if (_resultsByObjectKey.TryGetValue(storageObjectKey, out var result))
            {
                return Task.FromResult(result);
            }

            throw new KeyNotFoundException($"No STT result configured for object key '{storageObjectKey}'.");
        }
    }

    internal sealed class StubSummarizerService(SummaryResult result) : ISummarizerService
    {
        private readonly SummaryResult _result = result;
        private readonly Queue<SummaryResult> _personalizedResults = new();

        public string? LastTranscript { get; private set; }
        public List<(string Transcript, string Participant, string? PersonalizationContext)> PersonalizedCalls { get; } = [];

        public void EnqueuePersonalizedResult(SummaryResult result)
        {
            _personalizedResults.Enqueue(result);
        }

        public Task<SummaryResult> SummarizeAsync(string fullTranscript, CancellationToken ct = default)
        {
            LastTranscript = fullTranscript;
            return Task.FromResult(_result);
        }

        public Task<SummaryResult> SummarizePersonalizedAsync(
            string fullTranscript,
            string participant,
            string? personalizationContext = null,
            CancellationToken ct = default)
        {
            PersonalizedCalls.Add((fullTranscript, participant, personalizationContext));
            return Task.FromResult(_personalizedResults.Count > 0 ? _personalizedResults.Dequeue() : _result);
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
            return await CreateWithAmbientTenantAsync(effectiveTenantId, effectiveTenantId);
        }

        public static async Task<LiveSessionTestDb> CreateWithAmbientTenantAsync(
            Guid? ambientTenantOrganizationId,
            Guid? defaultOrganizationId = null)
        {
            var effectiveTenantId = defaultOrganizationId ?? ambientTenantOrganizationId ?? Guid.NewGuid();
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection)
                .Options;

            var dbContext = new ApplicationDbContext(
                options,
                new HttpContextAccessor(),
                new StaticTenantProvider(ambientTenantOrganizationId),
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

        public Guid AddParticipant(Guid meetingId, Guid organizationId, Guid userId, MeetingRole role = MeetingRole.Participant)
        {
            var participant = new MeetingParticipant
            {
                MeetingId = meetingId,
                OrganizationId = organizationId,
                UserId = userId,
                MeetingRole = role
            };
            DbContext.MeetingParticipants.Add(participant);
            DbContext.SaveChanges();
            return participant.Id;
        }

        public Guid AddAvailableAudioFragment(
            Guid meetingId,
            Guid organizationId,
            Guid participantUserId,
            string storageObjectKey,
            string trackSid,
            DateTime? trackPublishedAtUtc = null,
            Guid? participantAudioTrackId = null)
        {
            var fragment = new ParticipantAudioFragment
            {
                MeetingId = meetingId,
                OrganizationId = organizationId,
                ParticipantUserId = participantUserId,
                ParticipantAudioTrackId = participantAudioTrackId,
                TrackSid = trackSid,
                StorageObjectKey = storageObjectKey,
                StorageLocation = $"s3://recordings/{storageObjectKey}",
                Status = ParticipantAudioFragmentStatus.Available,
                TrackPublishedAtUtc = trackPublishedAtUtc,
                StorageAvailableAtUtc = trackPublishedAtUtc?.AddSeconds(5),
                SizeBytes = 1024
            };

            DbContext.ParticipantAudioFragments.Add(fragment);
            DbContext.SaveChanges();
            return fragment.Id;
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
                Filename = participantUserId.HasValue ? $"tracks/mtg-{meetingId}/user:{participantUserId.Value}/file.ogg" : string.Empty,
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

        public static WebhookEvent TrackPublished(Guid meetingId, string eventId, Guid userId, string trackSid)
            => new()
            {
                Event = "track_published",
                Id = eventId,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Room = new Room { Name = $"mtg:{meetingId}" },
                Participant = new ParticipantInfo { Identity = $"user:{userId}" },
                Track = new TrackInfo
                {
                    Sid = trackSid,
                    Type = TrackType.Audio,
                    Source = TrackSource.Microphone
                }
            };
    }
}
