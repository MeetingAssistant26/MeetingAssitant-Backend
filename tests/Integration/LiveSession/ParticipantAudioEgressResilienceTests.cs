using System.Text.Json;
using FluentAssertions;
using Hangfire.Common;
using Livekit.Server.Sdk.Dotnet;
using MeetingAssistant.Features.LiveSession.Infrastructure;
using MeetingAssistant.Features.LiveSession.Jobs;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Models.Events;
using MeetingAssistant.Features.LiveSession.Services;
using MeetingAssistant.Features.Meetings.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace tests.Integration.LiveSession
{
    public class ParticipantAudioEgressResilienceTests
    {
        [Fact]
        public async Task StartEgressJob_WhenTransientStartFails_ShouldLeaveFragmentPendingThenRetryToStoreEgressState()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var participantId = db.SeedUser("speaker");
            db.AddParticipant(meetingId, orgId, participantId);
            var fragmentId = await AddPendingPublishedFragmentAsync(db, meetingId, orgId, participantId, "TR_EGRESS_RETRY");

            var egress = new FakeEgressService();
            egress.Failures.Enqueue(new InvalidOperationException("livekit egress unavailable"));
            var sut = new StartParticipantAudioEgressJob(
                db.DbContext,
                egress,
                NullLogger<StartParticipantAudioEgressJob>.Instance);

            db.DbContext.ChangeTracker.Clear();
            await Assert.ThrowsAsync<InvalidOperationException>(() => sut.RunAsync(fragmentId));

            var afterFailure = db.DbContext.ParticipantAudioFragments.Single(x => x.Id == fragmentId);
            afterFailure.Status.Should().Be(ParticipantAudioFragmentStatus.Pending);
            afterFailure.EgressId.Should().BeNull();
            afterFailure.EgressStartAttemptCount.Should().Be(1);
            afterFailure.EgressStartLeaseExpiresAtUtc.Should().BeNull();
            afterFailure.FailureCode.Should().Be("egress_start_failed");
            afterFailure.FailureMessage.Should().Contain("livekit egress unavailable");

            db.DbContext.ChangeTracker.Clear();
            await sut.RunAsync(fragmentId);

            var afterRetry = db.DbContext.ParticipantAudioFragments.Single(x => x.Id == fragmentId);
            afterRetry.Status.Should().Be(ParticipantAudioFragmentStatus.Pending);
            afterRetry.EgressId.Should().Be("EG_TR_EGRESS_RETRY");
            afterRetry.StorageObjectKey.Should().Be($"tracks/mtg:{meetingId}/user:{participantId}/track-TR_EGRESS_RETRY.ogg");
            afterRetry.EgressStartAttemptCount.Should().Be(2);
            afterRetry.EgressStartLeaseExpiresAtUtc.Should().BeNull();
            afterRetry.FailureCode.Should().BeNull();
            afterRetry.FailureMessage.Should().BeNull();
            egress.Starts.Should().HaveCount(2);
        }

        [Fact]
        public async Task StartEgressJob_AfterSuccess_ShouldBeIdempotentAndNotStartDuplicateEgress()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var participantId = db.SeedUser("speaker");
            db.AddParticipant(meetingId, orgId, participantId);
            var fragmentId = await AddPendingPublishedFragmentAsync(db, meetingId, orgId, participantId, "TR_EGRESS_ONCE");

            var egress = new FakeEgressService();
            var sut = new StartParticipantAudioEgressJob(
                db.DbContext,
                egress,
                NullLogger<StartParticipantAudioEgressJob>.Instance);

            db.DbContext.ChangeTracker.Clear();
            await sut.RunAsync(fragmentId);
            db.DbContext.ChangeTracker.Clear();
            await sut.RunAsync(fragmentId);

            egress.Starts.Should().ContainSingle(x => x.TrackId == "TR_EGRESS_ONCE");
            db.DbContext.ParticipantAudioFragments.Single(x => x.Id == fragmentId).EgressId.Should().Be("EG_TR_EGRESS_ONCE");
        }

        [Fact]
        public async Task EgressEnded_WhenBackupStorageWasUsed_ShouldKeepFragmentPendingAndEnqueueBackupPersistInsteadOfIngest()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var participantId = db.SeedUser("speaker");
            db.AddParticipant(meetingId, orgId, participantId);

            var jobs = new FakeBackgroundJobClient();
            var webhookService = new WebhookService(
                db.DbContext,
                jobs,
                Options.Create(new LiveKitOptions { EgressHost = "http://egress" }),
                NullLogger<WebhookService>.Instance);

            const string trackSid = "TR_BACKUP_FILE";
            await webhookService.ProcessAsync(
                WebhookEventFactory.TrackPublished(meetingId, "evt-track-backup", participantId, trackSid),
                "{}");
            jobs.CreatedJobs.Clear();

            var objectKey = $"tracks/mtg:{meetingId}/user:{participantId}/track-{trackSid}.ogg";
            var backupPath = $"/egress-backup/{objectKey}";
            var evt = new WebhookEvent
            {
                Event = "egress_ended",
                Id = "evt-egress-backup",
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                EgressInfo = new EgressInfo
                {
                    RoomName = $"mtg:{meetingId}",
                    Status = EgressStatus.EgressFailed,
                    BackupStorageUsed = true
                }
            };
            evt.EgressInfo.FileResults.Add(new Livekit.Server.Sdk.Dotnet.FileInfo
            {
                Filename = objectKey,
                Location = backupPath,
                Size = 4096L
            });

            var rawPayload = JsonSerializer.Serialize(new
            {
                egressInfo = new
                {
                    egressId = "EG_backup",
                    trackId = trackSid,
                    backupStorageUsed = true,
                    errorCode = "upload_failed",
                    error = "primary object storage upload failed",
                    fileResults = new[]
                    {
                        new
                        {
                            filename = objectKey,
                            location = backupPath,
                            size = 4096L
                        }
                    }
                }
            });

            await webhookService.ProcessAsync(evt, rawPayload);

            var fragment = db.DbContext.ParticipantAudioFragments.Single(x => x.MeetingId == meetingId && x.TrackSid == trackSid);
            fragment.Status.Should().Be(ParticipantAudioFragmentStatus.Pending);
            fragment.EgressId.Should().Be("EG_backup");
            fragment.StorageLocation.Should().BeNull();
            fragment.StorageObjectKey.Should().Be(objectKey);
            fragment.BackupStoragePath.Should().Be(backupPath);
            fragment.BackupStorageAvailableAtUtc.Should().NotBeNull();
            fragment.FailureCode.Should().BeNull();

            jobs.CreatedJobs.Should().ContainSingle(x => x.Type == typeof(PersistParticipantAudioFragmentJob));
            jobs.CreatedJobs.Should().NotContain(x => x.Type == typeof(IngestParticipantAudioJob));
        }

        [Fact]
        public async Task PersistBackupJob_WhenStorageUploadFails_ShouldRemainObservableAndRetryToAvailableFragment()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId, MeetingStatus.Completed);
            var participantId = db.SeedUser("speaker");
            db.AddParticipant(meetingId, orgId, participantId);
            var track = new ParticipantAudioTrack
            {
                MeetingId = meetingId,
                OrganizationId = orgId,
                ParticipantUserId = participantId,
                Status = ParticipantAudioTrackStatus.Pending
            };
            var objectKey = $"tracks/mtg:{meetingId}/user:{participantId}/track-TR_UPLOAD_RETRY.ogg";
            var backupPath = $"/egress-backup/{objectKey}";
            var fragment = new ParticipantAudioFragment
            {
                MeetingId = meetingId,
                OrganizationId = orgId,
                ParticipantUserId = participantId,
                ParticipantAudioTrack = track,
                TrackSid = "TR_UPLOAD_RETRY",
                EgressId = "EG_upload_retry",
                StorageObjectKey = objectKey,
                BackupStoragePath = backupPath,
                BackupStorageAvailableAtUtc = DateTime.UtcNow,
                Status = ParticipantAudioFragmentStatus.Pending
            };
            db.DbContext.ParticipantAudioTracks.Add(track);
            db.DbContext.ParticipantAudioFragments.Add(fragment);
            await db.DbContext.SaveChangesAsync();

            var storage = new FakeStorageService { ThrowOnUpload = true, NextSizeBytes = 12_345L };
            var publisher = new CollectingPublisher();
            var sut = new PersistParticipantAudioFragmentJob(
                db.DbContext,
                storage,
                publisher,
                NullLogger<PersistParticipantAudioFragmentJob>.Instance);

            db.DbContext.ChangeTracker.Clear();
            await Assert.ThrowsAsync<InvalidOperationException>(() => sut.RunAsync(fragment.Id));

            var afterFailure = db.DbContext.ParticipantAudioFragments.Single(x => x.Id == fragment.Id);
            afterFailure.Status.Should().Be(ParticipantAudioFragmentStatus.Pending);
            afterFailure.StorageLocation.Should().BeNull();
            afterFailure.StorageUploadAttemptCount.Should().Be(1);
            afterFailure.StorageUploadLeaseExpiresAtUtc.Should().BeNull();
            afterFailure.FailureCode.Should().Be("storage_upload_failed");
            afterFailure.FailureMessage.Should().Contain("simulated upload failure");
            publisher.Notifications.OfType<ParticipantAudioReadyEvent>().Should().BeEmpty();

            storage.ThrowOnUpload = false;
            db.DbContext.ChangeTracker.Clear();
            await sut.RunAsync(fragment.Id);

            var afterRetry = db.DbContext.ParticipantAudioFragments.Single(x => x.Id == fragment.Id);
            afterRetry.Status.Should().Be(ParticipantAudioFragmentStatus.Available);
            afterRetry.StorageLocation.Should().Be($"http://minio:9000/recordings/{objectKey}");
            afterRetry.SizeBytes.Should().Be(12_345L);
            afterRetry.StorageUploadAttemptCount.Should().Be(2);
            afterRetry.StorageUploadLeaseExpiresAtUtc.Should().BeNull();
            afterRetry.FailureCode.Should().BeNull();
            storage.FileUploads.Should().ContainSingle(x => x.ObjectKey == objectKey);

            db.DbContext.ParticipantAudioTracks.Single(x => x.Id == track.Id).Status.Should().Be(ParticipantAudioTrackStatus.Available);
            publisher.Notifications.OfType<ParticipantAudioReadyEvent>().Should().ContainSingle(x => x.MeetingId == meetingId);
        }

        [Fact]
        public async Task TranscriptGeneration_WhenBackupFragmentStillPending_ShouldWaitForTerminalAudioOutcome()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId, MeetingStatus.Completed);
            var availableParticipantId = db.SeedUser("available-speaker");
            var pendingParticipantId = db.SeedUser("pending-speaker");
            db.AddParticipant(meetingId, orgId, availableParticipantId);
            db.AddParticipant(meetingId, orgId, pendingParticipantId);
            db.AddAvailableAudioFragment(
                meetingId,
                orgId,
                availableParticipantId,
                $"tracks/mtg:{meetingId}/user:{availableParticipantId}/track-TR_AVAILABLE.ogg",
                "TR_AVAILABLE");
            await AddPendingBackupFragmentAsync(db, meetingId, orgId, pendingParticipantId, "TR_PENDING_BACKUP");

            var stt = new StubSttService(new Dictionary<string, TrackTranscriptionResult>
            {
                [$"tracks/mtg:{meetingId}/user:{availableParticipantId}/track-TR_AVAILABLE.ogg"] = new(
                    "whisper-test",
                    [new TranscriptSegment(availableParticipantId, 0, 1000, "available audio", 0.9)])
            });
            var sut = new GenerateMeetingTranscriptJob(
                db.DbContext,
                stt,
                new CollectingPublisher(),
                NullLogger<GenerateMeetingTranscriptJob>.Instance);

            await sut.RunAsync(meetingId, orgId);

            stt.Calls.Should().BeEmpty();
            db.DbContext.MeetingTranscripts.Where(x => x.MeetingId == meetingId).Should().BeEmpty();
        }

        [Fact]
        public async Task ReconciliationJob_ShouldReenqueueMissedEgressStartAndBackupPersistJobs()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId);
            var startParticipantId = db.SeedUser("start-speaker");
            var backupParticipantId = db.SeedUser("backup-speaker");
            db.AddParticipant(meetingId, orgId, startParticipantId);
            db.AddParticipant(meetingId, orgId, backupParticipantId);
            var startFragmentId = await AddPendingPublishedFragmentAsync(db, meetingId, orgId, startParticipantId, "TR_RECONCILE_START");
            var backupFragmentId = await AddPendingBackupFragmentAsync(db, meetingId, orgId, backupParticipantId, "TR_RECONCILE_BACKUP");

            var jobs = new FakeBackgroundJobClient();
            var sut = new ParticipantAudioEgressReconciliationJob(
                db.DbContext,
                jobs,
                new CollectingPublisher(),
                NullLogger<ParticipantAudioEgressReconciliationJob>.Instance);

            db.DbContext.ChangeTracker.Clear();
            await sut.RunAsync();

            jobs.CreatedJobs.Should().ContainSingle(x => x.Type == typeof(StartParticipantAudioEgressJob) && (Guid)x.Args[0] == startFragmentId);
            jobs.CreatedJobs.Should().ContainSingle(x => x.Type == typeof(PersistParticipantAudioFragmentJob) && (Guid)x.Args[0] == backupFragmentId);
        }

        [Fact]
        public async Task ReconciliationJob_WhenEgressStartIsNoLongerRecoverable_ShouldMarkFragmentFailedAndReleaseReadiness()
        {
            await using var db = await LiveSessionTestDb.CreateAsync();
            var orgId = db.SeedOrganization();
            var meetingId = db.SeedMeeting(orgId, MeetingStatus.Completed);
            var participantId = db.SeedUser("expired-speaker");
            db.AddParticipant(meetingId, orgId, participantId);
            var fragmentId = await AddPendingPublishedFragmentAsync(
                db,
                meetingId,
                orgId,
                participantId,
                "TR_EXPIRED_START",
                DateTime.UtcNow.AddMinutes(-10));

            var jobs = new FakeBackgroundJobClient();
            var publisher = new CollectingPublisher();
            var sut = new ParticipantAudioEgressReconciliationJob(
                db.DbContext,
                jobs,
                publisher,
                NullLogger<ParticipantAudioEgressReconciliationJob>.Instance,
                Options.Create(new LiveKitOptions { ParticipantAudioIngestCeilingMinutes = 1 }));

            db.DbContext.ChangeTracker.Clear();
            await sut.RunAsync();

            var fragment = db.DbContext.ParticipantAudioFragments.Single(x => x.Id == fragmentId);
            fragment.Status.Should().Be(ParticipantAudioFragmentStatus.Failed);
            fragment.FailureCode.Should().Be("egress_start_expired");
            db.DbContext.ParticipantAudioTracks.Single(x => x.Id == fragment.ParticipantAudioTrackId).Status.Should().Be(ParticipantAudioTrackStatus.Failed);
            jobs.CreatedJobs.Should().BeEmpty();
            publisher.Notifications.OfType<ParticipantAudioReadyEvent>().Should().ContainSingle(x => x.MeetingId == meetingId);
        }

        private static async Task<Guid> AddPendingPublishedFragmentAsync(
            LiveSessionTestDb db,
            Guid meetingId,
            Guid organizationId,
            Guid participantUserId,
            string trackSid,
            DateTime? trackPublishedAtUtc = null)
        {
            var track = new ParticipantAudioTrack
            {
                MeetingId = meetingId,
                OrganizationId = organizationId,
                ParticipantUserId = participantUserId,
                Status = ParticipantAudioTrackStatus.Pending
            };
            var fragment = new ParticipantAudioFragment
            {
                MeetingId = meetingId,
                OrganizationId = organizationId,
                ParticipantUserId = participantUserId,
                ParticipantAudioTrack = track,
                TrackSid = trackSid,
                TrackPublishedAtUtc = trackPublishedAtUtc ?? DateTime.UtcNow.AddSeconds(-10),
                Status = ParticipantAudioFragmentStatus.Pending
            };

            db.DbContext.ParticipantAudioTracks.Add(track);
            db.DbContext.ParticipantAudioFragments.Add(fragment);
            await db.DbContext.SaveChangesAsync();
            return fragment.Id;
        }

        private static async Task<Guid> AddPendingBackupFragmentAsync(
            LiveSessionTestDb db,
            Guid meetingId,
            Guid organizationId,
            Guid participantUserId,
            string trackSid)
        {
            var track = new ParticipantAudioTrack
            {
                MeetingId = meetingId,
                OrganizationId = organizationId,
                ParticipantUserId = participantUserId,
                Status = ParticipantAudioTrackStatus.Pending
            };
            var objectKey = $"tracks/mtg:{meetingId}/user:{participantUserId}/track-{trackSid}.ogg";
            var fragment = new ParticipantAudioFragment
            {
                MeetingId = meetingId,
                OrganizationId = organizationId,
                ParticipantUserId = participantUserId,
                ParticipantAudioTrack = track,
                TrackSid = trackSid,
                EgressId = $"EG_{trackSid}",
                StorageObjectKey = objectKey,
                BackupStoragePath = $"/egress-backup/{objectKey}",
                BackupStorageAvailableAtUtc = DateTime.UtcNow.AddSeconds(-10),
                TrackPublishedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                Status = ParticipantAudioFragmentStatus.Pending
            };

            db.DbContext.ParticipantAudioTracks.Add(track);
            db.DbContext.ParticipantAudioFragments.Add(fragment);
            await db.DbContext.SaveChangesAsync();
            return fragment.Id;
        }
    }
}
