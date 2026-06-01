using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using MeetingAssistant.Api.Shared;
using MeetingAssistant.Features.Meetings.Contracts.Responses;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeetingAssistant.Tests.Integration.Meetings;

public class GetMeetingTests : IntegrationTestBase
{
    public GetMeetingTests(MeetingAssistantWebFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task GetMeeting_ExistingMeeting_ShouldReturnMeetingResponseWithListCoreFields()
    {
        var meeting = await SeedMeetingWithParticipantAndTagAsync(TestOrganizationId);

        var listResponse = await Client.GetAsync($"/api/organizations/{TestOrganizationId}/meetings?page=1&pageSize=20");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await listResponse.Content.ReadFromJsonAsync<MeetingListResponse>();
        var listItem = list!.Items.Single(m => m.Id == meeting.Id);

        var detailResponse = await Client.GetAsync($"/api/organizations/{TestOrganizationId}/meetings/{meeting.Id}");

        detailResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var detail = await detailResponse.Content.ReadFromJsonAsync<MeetingResponse>();
        detail.Should().NotBeNull();
        detail!.Id.Should().Be(listItem.Id);
        detail.Title.Should().Be(listItem.Title);
        detail.Description.Should().Be(listItem.Description);
        detail.ScheduledStartUtc.Should().Be(listItem.ScheduledStartUtc);
        detail.ScheduledEndUtc.Should().Be(listItem.ScheduledEndUtc);
        detail.Status.Should().Be(listItem.Status);
        detail.TagIds.Should().BeEquivalentTo(listItem.TagIds);
        detail.Participants.Select(p => new { p.UserId, p.Role }).Should()
            .BeEquivalentTo(listItem.Participants.Select(p => new { p.UserId, p.Role }));
    }

    [Fact]
    public async Task GetMeeting_Unauthenticated_ShouldReturnUnauthorized()
    {
        var meetingId = Guid.NewGuid();
        using var anonymousClient = Factory.CreateClient();

        var response = await anonymousClient.GetAsync($"/api/organizations/{TestOrganizationId}/meetings/{meetingId}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetMeeting_RouteOrgIdDiffersFromJwtOrgId_ShouldReturnForbidden()
    {
        var otherOrgId = Guid.NewGuid();
        var meetingId = Guid.NewGuid();

        var response = await Client.GetAsync($"/api/organizations/{otherOrgId}/meetings/{meetingId}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetMeeting_MissingMeeting_ShouldReturnNotFoundProblem()
    {
        var missingMeetingId = Guid.NewGuid();

        var response = await Client.GetAsync($"/api/organizations/{TestOrganizationId}/meetings/{missingMeetingId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var problem = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        problem.Should().NotBeNull();
        problem!.Status.Should().Be((int)HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetMeeting_MeetingInAnotherOrganization_ShouldReturnNotFound()
    {
        var otherOrgId = Guid.NewGuid();
        var otherOrgMeeting = await SeedMeetingWithParticipantAndTagAsync(otherOrgId);

        var response = await Client.GetAsync($"/api/organizations/{TestOrganizationId}/meetings/{otherOrgMeeting.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task<Meeting> SeedMeetingWithParticipantAndTagAsync(Guid organizationId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        if (!await db.Organizations.IgnoreQueryFilters().AnyAsync(o => o.Id == organizationId))
        {
            db.Organizations.Add(new Organization
            {
                Id = organizationId,
                Name = $"Org {organizationId:N}",
                Slug = $"org-{organizationId:N}"[..20]
            });
        }

        var tag = new MeetingTag
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Name = "Detail Tag",
            IsActive = true
        };

        var start = DateTime.UtcNow.AddDays(2).Date.AddHours(15);
        var meeting = new Meeting
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Title = "Detail Meeting",
            Description = "Shared detail contract",
            ScheduledStartUtc = start,
            ScheduledEndUtc = start.AddHours(1),
            Status = MeetingStatus.Scheduled
        };

        db.MeetingTags.Add(tag);
        db.Meetings.Add(meeting);
        db.MeetingParticipants.Add(new MeetingParticipant
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            MeetingId = meeting.Id,
            UserId = TestUserId,
            MeetingRole = MeetingRole.Host
        });
        db.MeetingMeetingTags.Add(new MeetingMeetingTag
        {
            MeetingId = meeting.Id,
            MeetingTagId = tag.Id
        });

        await db.SaveChangesAsync();
        return meeting;
    }
}
