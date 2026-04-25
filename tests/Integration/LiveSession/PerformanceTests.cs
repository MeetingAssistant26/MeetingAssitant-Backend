using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using MeetingAssistant.Features.Identity.Entites;
using MeetingAssistant.Features.LiveSession.Contracts.Requests;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Tests.Integration.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace tests.Integration.LiveSession
{
    public class PerformanceTests : IClassFixture<MeetingAssistantWebFactory>, IAsyncLifetime
    {
        private readonly MeetingAssistantWebFactory _factory;
        private HttpClient _client = null!;

        private readonly Guid _testUserId = Guid.NewGuid();
        private readonly Guid _testOrganizationId = Guid.NewGuid();

        public PerformanceTests(MeetingAssistantWebFactory factory)
        {
            _factory = factory;
        }

        public async Task InitializeAsync()
        {
            _client = _factory.CreateClient();
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                TestJwtTokenHelper.GenerateToken(_testUserId, _testOrganizationId));

            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            db.Users.Add(new ApplicationUser
            {
                Id = _testUserId,
                UserName = $"perf-user-{_testUserId:N}@test.com",
                NormalizedUserName = $"PERF-USER-{_testUserId:N}@TEST.COM",
                Email = $"perf-user-{_testUserId:N}@test.com",
                NormalizedEmail = $"PERF-USER-{_testUserId:N}@TEST.COM",
                EmailConfirmed = true,
                DisplayName = "Performance User",
                SecurityStamp = Guid.NewGuid().ToString()
            });

            db.Organizations.Add(new Organization
            {
                Id = _testOrganizationId,
                Name = "Perf Org",
                Slug = $"perf-org-{Guid.NewGuid():N}".ToLowerInvariant()
            });

            db.UserOrgMemberships.Add(new UserOrgMembership
            {
                Id = Guid.NewGuid(),
                UserId = _testUserId,
                OrganizationId = _testOrganizationId,
                OrgRole = OrganizationRole.Admin,
                IsEnabled = true
            });

            await db.SaveChangesAsync();
        }

        public Task DisposeAsync() => Task.CompletedTask;

        [Fact]
        public async Task SC001_JoinToken_ShouldBeUnderOneSecond()
        {
            var meetingId = await SeedMeetingWithParticipantAsync(_testUserId);
            var route = $"/api/organizations/{_testOrganizationId}/meetings/{meetingId}/session/join-token";

            // Warm-up to reduce first-call JIT noise.
            await _client.PostAsJsonAsync(route, new JoinTokenRequest("warmup"));

            var stopwatch = Stopwatch.StartNew();
            var response = await _client.PostAsJsonAsync(route, new JoinTokenRequest("Perf User"));
            stopwatch.Stop();

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
        }

        [Fact]
        public async Task SC010_JoinTokenLatency_ShouldHoldAtFiftyParticipants()
        {
            var users = await SeedMeetingWithFiftyParticipantsAsync();
            var meetingId = users.MeetingId;
            var userIds = users.UserIds;

            var route = $"/api/organizations/{_testOrganizationId}/meetings/{meetingId}/session/join-token";

            var measurements = new List<(HttpStatusCode StatusCode, long ElapsedMs)>();

            foreach (var userId in userIds)
            {
                using var client = CreateAuthenticatedClient(userId);
                var sw = Stopwatch.StartNew();
                var response = await client.PostAsJsonAsync(route, new JoinTokenRequest(null));
                sw.Stop();
                measurements.Add((response.StatusCode, sw.ElapsedMilliseconds));
            }

            measurements.Should().OnlyContain(x => x.StatusCode == HttpStatusCode.OK);

            var ordered = measurements.Select(x => x.ElapsedMs).OrderBy(x => x).ToList();
            var p95Index = (int)Math.Ceiling(ordered.Count * 0.95) - 1;
            var p95Ms = ordered[Math.Max(0, p95Index)];

            p95Ms.Should().BeLessThan(1000);
        }

        private HttpClient CreateAuthenticatedClient(Guid userId)
        {
            var client = _factory.CreateClient();
            var token = TestJwtTokenHelper.GenerateToken(userId, _testOrganizationId);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }

        private async Task<Guid> SeedMeetingWithParticipantAsync(Guid userId)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var meeting = new Meeting
            {
                Id = Guid.NewGuid(),
                OrganizationId = _testOrganizationId,
                Title = "Performance Meeting",
                ScheduledStartUtc = DateTime.UtcNow.AddMinutes(5),
                ScheduledEndUtc = DateTime.UtcNow.AddMinutes(65),
                Status = MeetingStatus.Scheduled
            };

            db.Meetings.Add(meeting);
            db.MeetingParticipants.Add(new MeetingParticipant
            {
                MeetingId = meeting.Id,
                OrganizationId = _testOrganizationId,
                UserId = userId,
                MeetingRole = MeetingRole.Host
            });

            await db.SaveChangesAsync();
            return meeting.Id;
        }

        private async Task<(Guid MeetingId, List<Guid> UserIds)> SeedMeetingWithFiftyParticipantsAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var meeting = new Meeting
            {
                Id = Guid.NewGuid(),
                OrganizationId = _testOrganizationId,
                Title = "Perf 50",
                ScheduledStartUtc = DateTime.UtcNow.AddMinutes(5),
                ScheduledEndUtc = DateTime.UtcNow.AddMinutes(65),
                Status = MeetingStatus.Scheduled
            };

            db.Meetings.Add(meeting);

            var userIds = new List<Guid> { _testUserId };

            for (var i = 0; i < 49; i++)
            {
                var userId = Guid.NewGuid();
                userIds.Add(userId);

                db.Users.Add(new ApplicationUser
                {
                    Id = userId,
                    UserName = $"perf-{i}-{userId:N}@test.com",
                    Email = $"perf-{i}-{userId:N}@test.com",
                    DisplayName = $"Perf {i}"
                });

                db.UserOrgMemberships.Add(new UserOrgMembership
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    OrganizationId = _testOrganizationId,
                    OrgRole = OrganizationRole.Member,
                    IsEnabled = true
                });
            }

            foreach (var (participantId, index) in userIds.Select((id, idx) => (id, idx)))
            {
                db.MeetingParticipants.Add(new MeetingParticipant
                {
                    MeetingId = meeting.Id,
                    OrganizationId = _testOrganizationId,
                    UserId = participantId,
                    MeetingRole = index == 0 ? MeetingRole.Host : MeetingRole.Participant
                });
            }

            await db.SaveChangesAsync();

            return (meeting.Id, userIds);
        }
    }
}
