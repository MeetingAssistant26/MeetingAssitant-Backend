using FluentAssertions;
using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Features.LiveSession.Jobs;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Models.Events;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using tests.Integration.LiveSession;
using Xunit;

namespace tests.Unit.LiveSession
{
    public class JoinBarrierIdempotencyTests
    {
        [Fact]
        public async Task ConcurrentTrackIngest_ShouldDispatchParticipantAudioReadyExactlyOnce()
        {
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                $"live-session-join-barrier-{Guid.NewGuid():N}.db");

            var organizationId = Guid.NewGuid();
            var meetingId = Guid.NewGuid();
            var participantA = Guid.NewGuid();
            var participantB = Guid.NewGuid();

            try
            {
                await SeedAsync(databasePath, organizationId, meetingId, participantA, participantB);

                var publisher = new CollectingPublisher();

                var ingestTasks = new[]
                {
                    RunIngestAsync(databasePath, organizationId, participantA, $"https://egress.example/bucket/tracks/{meetingId}/{participantA}.ogg", publisher),
                    RunIngestAsync(databasePath, organizationId, participantB, $"https://egress.example/bucket/tracks/{meetingId}/{participantB}.ogg", publisher)
                };

                await Task.WhenAll(ingestTasks);

                await using var assertionContext = CreateDbContext(databasePath, organizationId);

                var readyEventCount = await assertionContext.SessionEvents
                    .CountAsync(x => x.MeetingId == meetingId && x.EventType == SessionEventType.ParticipantAudioReady);

                readyEventCount.Should().Be(1);
                publisher.Notifications
                    .OfType<ParticipantAudioReadyEvent>()
                    .Should()
                    .ContainSingle(x => x.MeetingId == meetingId);
            }
            finally
            {
                if (File.Exists(databasePath))
                {
                    try
                    {
                        File.Delete(databasePath);
                    }
                    catch (IOException)
                    {
                    }
                }
            }
        }

        private static async Task RunIngestAsync(
            string databasePath,
            Guid organizationId,
            Guid participantUserId,
            string sourceUrl,
            IPublisher publisher)
        {
            await using var dbContext = CreateDbContext(databasePath, organizationId);

            var trackId = await dbContext.ParticipantAudioTracks
                .Where(x => x.ParticipantUserId == participantUserId)
                .Select(x => x.Id)
                .SingleAsync();

            var job = new IngestParticipantAudioJob(
                dbContext,
                publisher,
                NullLogger<IngestParticipantAudioJob>.Instance);

            await job.RunAsync(trackId, sourceUrl, 1024L);
        }

        private static async Task SeedAsync(
            string databasePath,
            Guid organizationId,
            Guid meetingId,
            Guid participantA,
            Guid participantB)
        {
            await using var dbContext = CreateDbContext(databasePath, organizationId);
            await dbContext.Database.EnsureCreatedAsync();

            dbContext.Organizations.Add(new Organization
            {
                Id = organizationId,
                Name = "Test Org",
                Slug = $"test-org-{organizationId:N}"
            });

            dbContext.Meetings.Add(new Meeting
            {
                Id = meetingId,
                OrganizationId = organizationId,
                Title = "Join Barrier Test",
                ScheduledStartUtc = DateTime.UtcNow,
                ScheduledEndUtc = DateTime.UtcNow.AddHours(1),
                Status = MeetingStatus.Completed
            });

            dbContext.ParticipantAudioTracks.AddRange(
                new ParticipantAudioTrack
                {
                    MeetingId = meetingId,
                    OrganizationId = organizationId,
                    ParticipantUserId = participantA,
                    Status = ParticipantAudioTrackStatus.Pending
                },
                new ParticipantAudioTrack
                {
                    MeetingId = meetingId,
                    OrganizationId = organizationId,
                    ParticipantUserId = participantB,
                    Status = ParticipantAudioTrackStatus.Pending
                });

            await dbContext.SaveChangesAsync();
        }

        private static ApplicationDbContext CreateDbContext(string databasePath, Guid organizationId)
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite($"Data Source={databasePath};Cache=Shared")
                .Options;

            return new ApplicationDbContext(
                options,
                new HttpContextAccessor(),
                new StaticTenantProvider(organizationId),
                new NoopPublisher());
        }
    }
}
