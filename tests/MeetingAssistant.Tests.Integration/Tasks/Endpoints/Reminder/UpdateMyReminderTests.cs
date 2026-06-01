using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using MeetingAssistant.Features.Identity.Entites;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Features.Tasks.Contracts.Responses;
using MeetingAssistant.Features.Tasks.Models.Enums;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ReminderEntity = MeetingAssistant.Features.Tasks.Models.Entities.Reminder;

namespace MeetingAssistant.Tests.Integration.Tasks.Endpoints.Reminder;

public class UpdateMyReminderTests : IntegrationTestBase
{
    public UpdateMyReminderTests(MeetingAssistantWebFactory factory) : base(factory)
    {
    }

    private HttpClient CreateAuthenticatedClient(Guid userId, Guid orgId)
    {
        var client = Factory.CreateClient();
        var token = TestJwtTokenHelper.GenerateToken(userId, orgId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<ReminderEntity> SeedReminderAsync(
        Guid orgId,
        Guid createdByUserId,
        Guid? targetUserId = null,
        ReminderScope scope = ReminderScope.Personal,
        ReminderChannel channel = ReminderChannel.User,
        ReminderStatus status = ReminderStatus.Active,
        DateTime? reminderAt = null)
    {
        await using var scope1 = Factory.Services.CreateAsyncScope();
        var db = scope1.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var reminder = new ReminderEntity
        {
            OrganizationId = orgId,
            Text = "Original reminder",
            Scope = scope,
            Channel = channel,
            CreatedByUserId = createdByUserId,
            TargetUserId = targetUserId ?? createdByUserId,
            ReminderAtUtc = reminderAt ?? DateTime.UtcNow.AddHours(1),
            Status = status
        };

        db.Reminders.Add(reminder);
        await db.SaveChangesAsync();
        return reminder;
    }

    private async Task SeedMemberAsync(Guid userId, Guid orgId)
    {
        await using var scope1 = Factory.Services.CreateAsyncScope();
        var db = scope1.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Users.Add(new ApplicationUser
        {
            Id = userId,
            Email = $"test-{userId:N}@test.com",
            UserName = $"test-{userId:N}@test.com",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString()
        });
        db.UserOrgMemberships.Add(new UserOrgMembership
        {
            UserId = userId,
            OrganizationId = orgId,
            IsEnabled = true,
            OrgRole = OrganizationRole.Member
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Patch_OwnActiveUserReminder_ShouldUpdateTextAndTime()
    {
        var reminder = await SeedReminderAsync(TestOrganizationId, TestUserId, TestUserId);
        var newTime = DateTime.UtcNow.AddDays(2);

        var response = await Client.PatchAsJsonAsync($"/api/me/reminders/{reminder.Id}", new
        {
            text = " Updated reminder text ",
            reminderAtUtc = newTime
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ReminderResponse>();
        body.Should().NotBeNull();
        body!.Text.Should().Be("Updated reminder text");
        body.ReminderAtUtc.Should().BeCloseTo(newTime, TimeSpan.FromSeconds(1));

        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var persisted = await db.Reminders.IgnoreQueryFilters().SingleAsync(x => x.Id == reminder.Id);
        persisted.Text.Should().Be("Updated reminder text");
        persisted.ReminderAtUtc.Should().BeCloseTo(newTime, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Patch_OwnActiveUserReminder_WithTextOnly_ShouldPreserveReminderTime()
    {
        var originalTime = DateTime.UtcNow.AddDays(3);
        var reminder = await SeedReminderAsync(TestOrganizationId, TestUserId, TestUserId, reminderAt: originalTime);

        var response = await Client.PatchAsJsonAsync($"/api/me/reminders/{reminder.Id}", new { text = "Text only" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ReminderResponse>();
        body.Should().NotBeNull();
        body!.Text.Should().Be("Text only");
        body.ReminderAtUtc.Should().BeCloseTo(originalTime, TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData(ReminderStatus.Delivered)]
    [InlineData(ReminderStatus.Cancelled)]
    public async Task Patch_NonActiveReminder_ShouldReturnConflict(ReminderStatus status)
    {
        var reminder = await SeedReminderAsync(TestOrganizationId, TestUserId, TestUserId, status: status);

        var response = await Client.PatchAsJsonAsync($"/api/me/reminders/{reminder.Id}", new { text = "Cannot update" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Patch_PublicReminder_ShouldReturnForbidden()
    {
        var reminder = await SeedReminderAsync(TestOrganizationId, TestUserId, TestUserId, scope: ReminderScope.Public);

        var response = await Client.PatchAsJsonAsync($"/api/me/reminders/{reminder.Id}", new { text = "Cannot update" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Patch_AgentOwnedReminder_ShouldReturnForbidden()
    {
        var reminder = await SeedReminderAsync(
            TestOrganizationId,
            TestUserId,
            TestUserId,
            channel: ReminderChannel.Agent);

        var response = await Client.PatchAsJsonAsync($"/api/me/reminders/{reminder.Id}", new { text = "Cannot update" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Patch_OtherUsersReminder_ShouldReturnForbidden()
    {
        var otherUserId = Guid.NewGuid();
        await SeedMemberAsync(otherUserId, TestOrganizationId);
        var reminder = await SeedReminderAsync(TestOrganizationId, TestUserId, TestUserId);
        using var client = CreateAuthenticatedClient(otherUserId, TestOrganizationId);

        var response = await client.PatchAsJsonAsync($"/api/me/reminders/{reminder.Id}", new { text = "Cannot update" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Patch_EmptyText_ShouldReturnBadRequest()
    {
        var reminder = await SeedReminderAsync(TestOrganizationId, TestUserId, TestUserId);

        var response = await Client.PatchAsJsonAsync($"/api/me/reminders/{reminder.Id}", new { text = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
