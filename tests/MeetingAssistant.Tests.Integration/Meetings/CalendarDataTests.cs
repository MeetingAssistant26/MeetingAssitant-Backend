using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using MeetingAssistant.Features.Identity.Entites;
using MeetingAssistant.Features.Meetings.Contracts.Responses;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeetingAssistant.Tests.Integration.Meetings;

public class CalendarDataTests : IntegrationTestBase
{
    public CalendarDataTests(MeetingAssistantWebFactory factory) : base(factory) { }

    private string CalendarUrl => $"/api/organizations/{TestOrganizationId}/calendar";

    [Fact]
    public async Task GetCalendarData_WithMeetingsInWeek_ReturnsOnlyMeetingsInRange()
    {
        // Arrange — pick a target Monday so we control the week boundary
        var targetMonday = GetNextMonday();
        var weekParam = targetMonday.ToString("yyyy-MM-dd");

        var meetingInWeek1 = new Meeting
        {
            OrganizationId = TestOrganizationId,
            Title = "Tuesday Standup",
            ScheduledStartUtc = targetMonday.AddDays(1).AddHours(9),  // Tuesday 09:00
            ScheduledEndUtc = targetMonday.AddDays(1).AddHours(10),
            Status = MeetingStatus.Scheduled
        };

        var meetingInWeek2 = new Meeting
        {
            OrganizationId = TestOrganizationId,
            Title = "Thursday Review",
            ScheduledStartUtc = targetMonday.AddDays(3).AddHours(14), // Thursday 14:00
            ScheduledEndUtc = targetMonday.AddDays(3).AddHours(15),
            Status = MeetingStatus.Scheduled
        };

        var meetingOutsideWeek = new Meeting
        {
            OrganizationId = TestOrganizationId,
            Title = "Next Week Meeting",
            ScheduledStartUtc = targetMonday.AddDays(8).AddHours(10), // Next Monday+1
            ScheduledEndUtc = targetMonday.AddDays(8).AddHours(11),
            Status = MeetingStatus.Scheduled
        };

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Meetings.AddRange(meetingInWeek1, meetingInWeek2, meetingOutsideWeek);
            await db.SaveChangesAsync();
        }

        // Act
        var response = await Client.GetAsync($"{CalendarUrl}?week={weekParam}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<CalendarDataResponse>();
        result.Should().NotBeNull();
        result!.WeekStartUtc.Should().Be(targetMonday);
        result.WeekEndUtc.Should().Be(targetMonday.AddDays(7));
        result.Meetings.Should().HaveCount(2);
        result.Meetings.Select(m => m.Title).Should().ContainInOrder("Tuesday Standup", "Thursday Review");
    }

    [Fact]
    public async Task GetCalendarData_ResultsAreOrderedByScheduledStartUtc()
    {
        // Arrange — insert in reverse order to verify ordering
        var targetMonday = GetNextMonday();
        var weekParam = targetMonday.ToString("yyyy-MM-dd");

        var lateMeeting = new Meeting
        {
            OrganizationId = TestOrganizationId,
            Title = "Friday Retro",
            ScheduledStartUtc = targetMonday.AddDays(4).AddHours(16), // Friday 16:00
            ScheduledEndUtc = targetMonday.AddDays(4).AddHours(17),
            Status = MeetingStatus.Scheduled
        };

        var earlyMeeting = new Meeting
        {
            OrganizationId = TestOrganizationId,
            Title = "Monday Planning",
            ScheduledStartUtc = targetMonday.AddHours(8), // Monday 08:00
            ScheduledEndUtc = targetMonday.AddHours(9),
            Status = MeetingStatus.Scheduled
        };

        var midMeeting = new Meeting
        {
            OrganizationId = TestOrganizationId,
            Title = "Wednesday Sync",
            ScheduledStartUtc = targetMonday.AddDays(2).AddHours(11), // Wednesday 11:00
            ScheduledEndUtc = targetMonday.AddDays(2).AddHours(12),
            Status = MeetingStatus.Scheduled
        };

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            // Insert out of order intentionally
            db.Meetings.AddRange(lateMeeting, earlyMeeting, midMeeting);
            await db.SaveChangesAsync();
        }

        // Act
        var response = await Client.GetAsync($"{CalendarUrl}?week={weekParam}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<CalendarDataResponse>();
        result!.Meetings.Should().HaveCount(3);
        result.Meetings.Select(m => m.Title).Should().ContainInOrder(
            "Monday Planning", "Wednesday Sync", "Friday Retro");

        result.Meetings.Should().BeInAscendingOrder(m => m.ScheduledStartUtc);
    }

    [Fact]
    public async Task GetCalendarData_EmptyWeek_ReturnsEmptyList()
    {
        // Arrange — use a far-future week with no meetings
        var emptyMonday = new DateTime(2099, 1, 5, 0, 0, 0, DateTimeKind.Utc); // A Monday
        var weekParam = emptyMonday.ToString("yyyy-MM-dd");

        // Act
        var response = await Client.GetAsync($"{CalendarUrl}?week={weekParam}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<CalendarDataResponse>();
        result.Should().NotBeNull();
        result!.Meetings.Should().BeEmpty();
        result.WeekStartUtc.Should().Be(emptyMonday);
    }

    [Fact]
    public async Task GetCalendarData_WithoutWeekParam_UsesCurrentWeek()
    {
        // Act — no week query param
        var response = await Client.GetAsync(CalendarUrl);

        // Assert — should succeed and snap to current week's Monday
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<CalendarDataResponse>();
        result.Should().NotBeNull();

        var today = DateTime.UtcNow.Date;
        var diff = (7 + (today.DayOfWeek - DayOfWeek.Monday)) % 7;
        var expectedMonday = today.AddDays(-diff);

        result!.WeekStartUtc.Should().Be(expectedMonday);
        result.WeekEndUtc.Should().Be(expectedMonday.AddDays(7));
    }

    [Fact]
    public async Task GetCalendarData_WithUtcDateTimeWeekInput_SnapsToUtcMonday()
    {
        // Arrange — mobile sends the selected week as a full UTC timestamp, not just a date.
        var targetMonday = GetNextMonday();
        var utcWeekInput = targetMonday.AddDays(2).AddHours(23).AddMinutes(45); // Wednesday 23:45 UTC
        var weekParam = Uri.EscapeDataString(utcWeekInput.ToString("O"));

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Meetings.AddRange(
                new Meeting
                {
                    OrganizationId = TestOrganizationId,
                    Title = "UTC Week Input Meeting",
                    ScheduledStartUtc = targetMonday.AddDays(4).AddHours(13),
                    ScheduledEndUtc = targetMonday.AddDays(4).AddHours(14),
                    Status = MeetingStatus.Scheduled
                },
                new Meeting
                {
                    OrganizationId = TestOrganizationId,
                    Title = "Previous UTC Week Meeting",
                    ScheduledStartUtc = targetMonday.AddTicks(-1),
                    ScheduledEndUtc = targetMonday.AddMinutes(30),
                    Status = MeetingStatus.Scheduled
                });

            await db.SaveChangesAsync();
        }

        // Act
        var response = await Client.GetAsync($"{CalendarUrl}?week={weekParam}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<CalendarDataResponse>();
        result.Should().NotBeNull();
        result!.WeekStartUtc.Should().Be(targetMonday);
        result.WeekStartUtc.Kind.Should().Be(DateTimeKind.Utc);
        result.WeekEndUtc.Should().Be(targetMonday.AddDays(7));
        result.Meetings.Select(m => m.Title).Should().ContainSingle().Which.Should().Be("UTC Week Input Meeting");
    }

    [Fact]
    public async Task GetCalendarData_Unauthenticated_Returns401()
    {
        // Arrange — client with no auth header
        var unauthClient = Factory.CreateClient();

        // Act
        var response = await unauthClient.GetAsync($"{CalendarUrl}?week=2026-04-13");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetCalendarData_MultiTenancy_ReturnsOnlyCurrentOrgMeetings()
    {
        // Arrange — create a second org with its own user, membership, and meeting
        var orgBId = Guid.NewGuid();
        var userBId = Guid.NewGuid();
        var targetMonday = GetNextMonday();
        var weekParam = targetMonday.ToString("yyyy-MM-dd");
        Guid orgBMeetingId;

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // Seed Org B infrastructure (bypassing query filters)
            db.Users.Add(new ApplicationUser
            {
                Id = userBId,
                UserName = $"userb-{userBId:N}@test.com",
                NormalizedUserName = $"USERB-{userBId:N}@TEST.COM",
                Email = $"userb-{userBId:N}@test.com",
                NormalizedEmail = $"USERB-{userBId:N}@TEST.COM",
                EmailConfirmed = true,
                DisplayName = "User B",
                SecurityStamp = Guid.NewGuid().ToString()
            });
            db.Organizations.Add(new Organization { Id = orgBId, Name = "Org B", Slug = "org-b" });
            db.UserOrgMemberships.Add(new UserOrgMembership
            {
                UserId = userBId,
                OrganizationId = orgBId,
                OrgRole = OrganizationRole.Admin,
                IsEnabled = true
            });

            // Meeting in Org A (our test org)
            db.Meetings.Add(new Meeting
            {
                OrganizationId = TestOrganizationId,
                Title = "Org A Meeting",
                ScheduledStartUtc = targetMonday.AddDays(1).AddHours(10),
                ScheduledEndUtc = targetMonday.AddDays(1).AddHours(11),
                Status = MeetingStatus.Scheduled
            });

            // Meeting in Org B (should NOT appear)
            var orgBMeeting = new Meeting
            {
                OrganizationId = orgBId,
                Title = "Org B Meeting",
                ScheduledStartUtc = targetMonday.AddDays(1).AddHours(10),
                ScheduledEndUtc = targetMonday.AddDays(1).AddHours(11),
                Status = MeetingStatus.Scheduled
            };
            db.Meetings.Add(orgBMeeting);

            await db.SaveChangesAsync();
            orgBMeetingId = orgBMeeting.Id;
        }

        // Act — authenticated as Org A user, calling Org A endpoint
        var response = await Client.GetAsync($"{CalendarUrl}?week={weekParam}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<CalendarDataResponse>();
        result!.Meetings.Should().HaveCount(1);
        result.Meetings.Single().Title.Should().Be("Org A Meeting");
        result.Meetings.Select(m => m.Id).Should().NotContain(orgBMeetingId);
        result.Meetings.Select(m => m.Title).Should().NotContain("Org B Meeting");
    }

    [Fact]
    public async Task GetCalendarData_BoundaryCondition_IncludesStartExcludesEnd()
    {
        // Arrange — meeting exactly at week start (inclusive) and exactly at week end (exclusive)
        var targetMonday = GetNextMonday();
        var weekParam = targetMonday.ToString("yyyy-MM-dd");
        var nextMonday = targetMonday.AddDays(7);

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // Starts exactly at Monday 00:00:00 — should be INCLUDED
            db.Meetings.Add(new Meeting
            {
                OrganizationId = TestOrganizationId,
                Title = "Week Start Boundary",
                ScheduledStartUtc = targetMonday,
                ScheduledEndUtc = targetMonday.AddHours(1),
                Status = MeetingStatus.Scheduled
            });

            // Starts exactly at next Monday 00:00:00 — should be EXCLUDED
            db.Meetings.Add(new Meeting
            {
                OrganizationId = TestOrganizationId,
                Title = "Week End Boundary",
                ScheduledStartUtc = nextMonday,
                ScheduledEndUtc = nextMonday.AddHours(1),
                Status = MeetingStatus.Scheduled
            });

            await db.SaveChangesAsync();
        }

        // Act
        var response = await Client.GetAsync($"{CalendarUrl}?week={weekParam}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<CalendarDataResponse>();
        result!.Meetings.Should().HaveCount(1);
        result.Meetings.Single().Title.Should().Be("Week Start Boundary");
    }

    [Fact]
    public async Task GetCalendarData_OrgIdMismatchInRoute_Returns403()
    {
        // Arrange — JWT has organizationId = TestOrganizationId, but route uses a different orgId
        var differentOrgId = Guid.NewGuid();

        // Act — call calendar for an org the user doesn't belong to
        var response = await Client.GetAsync($"/api/organizations/{differentOrgId}/calendar?week=2030-06-03");

        // Assert — EnforceOrgAccess should reject with 403
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetCalendarData_SundayLateNight_IsIncludedInWeek()
    {
        // Arrange — meeting on Sunday at 23:59 (last minute of the week)
        var targetMonday = GetNextMonday();
        var weekParam = targetMonday.ToString("yyyy-MM-dd");
        var sundayLateNight = targetMonday.AddDays(6).AddHours(23).AddMinutes(59); // Sunday 23:59

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            db.Meetings.Add(new Meeting
            {
                OrganizationId = TestOrganizationId,
                Title = "Sunday Late Night",
                ScheduledStartUtc = sundayLateNight,
                ScheduledEndUtc = sundayLateNight.AddMinutes(30),
                Status = MeetingStatus.Scheduled
            });

            await db.SaveChangesAsync();
        }

        // Act
        var response = await Client.GetAsync($"{CalendarUrl}?week={weekParam}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<CalendarDataResponse>();
        result!.Meetings.Should().HaveCount(1);
        result.Meetings.Single().Title.Should().Be("Sunday Late Night");
    }

    private static DateTime GetNextMonday()
    {
        var date = new DateTime(2030, 6, 3, 0, 0, 0, DateTimeKind.Utc); // A known Monday
        return date;
    }
}
