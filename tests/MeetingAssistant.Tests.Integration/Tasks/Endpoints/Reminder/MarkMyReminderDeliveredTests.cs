using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using MeetingAssistant.Features.Identity.Entites;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Features.Tasks.Contracts.Responses;
using ReminderEntity = MeetingAssistant.Features.Tasks.Models.Entities.Reminder;
using MeetingAssistant.Features.Tasks.Models.Enums;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Api.Shared;
using MeetingAssistant.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeetingAssistant.Tests.Integration.Tasks.Endpoints.Reminder;

public class MarkMyReminderDeliveredTests : IntegrationTestBase
{
    public MarkMyReminderDeliveredTests(MeetingAssistantWebFactory factory) : base(factory)
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
        ReminderStatus status = ReminderStatus.Active)
    {
        using var scope1 = Factory.Services.CreateScope();
        var db = scope1.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var reminder = new ReminderEntity
        {
            OrganizationId = orgId,
            Text = "Test reminder",
            Scope = scope,
            Channel = ReminderChannel.User,
            CreatedByUserId = createdByUserId,
            TargetUserId = targetUserId ?? createdByUserId,
            ReminderAtUtc = DateTime.UtcNow.AddMinutes(-1),
            Status = status
        };

        db.Reminders.Add(reminder);
        await db.SaveChangesAsync();
        return reminder;
    }

    [Fact]
    public async Task MarkDelivered_OwnActiveReminder_ShouldReturnOk_AndSetStatusDelivered()
    {
        // Arrange
        var reminder = await SeedReminderAsync(TestOrganizationId, TestUserId, TestUserId);

        // Act
        var response = await Client.PostAsync($"/api/me/reminders/{reminder.Id}/mark-delivered", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ReminderResponse>();
        result.Should().NotBeNull();
        result!.Status.Should().Be(ReminderStatus.Delivered);
        result.DeliveredAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task MarkDelivered_AlreadyDelivered_ShouldReturnOk_Idempotently()
    {
        // Arrange
        var reminder = await SeedReminderAsync(TestOrganizationId, TestUserId, TestUserId, status: ReminderStatus.Delivered);

        // Act
        var response = await Client.PostAsync($"/api/me/reminders/{reminder.Id}/mark-delivered", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ReminderResponse>();
        result.Should().NotBeNull();
        result!.Status.Should().Be(ReminderStatus.Delivered);
    }

    [Fact]
    public async Task MarkDelivered_OtherUsersReminder_ShouldReturnForbidden()
    {
        // Arrange
        var otherUserId = Guid.NewGuid();
        // Reminder belongs to TestUserId, otherUserId tries to mark it
        var reminder = await SeedReminderAsync(TestOrganizationId, TestUserId, TestUserId);
        var client = CreateAuthenticatedClient(otherUserId, TestOrganizationId);
        using (var scope1 = Factory.Services.CreateScope())
        {
            var db = scope1.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = new ApplicationUser
            {
                Id = otherUserId,
                Email = $"test-{otherUserId}@test.com",
                UserName = $"test-{otherUserId}@test.com"
            };
            db.Users.Add(user);
            db.UserOrgMemberships.Add(new UserOrgMembership
            {
                UserId = otherUserId,
                OrganizationId = TestOrganizationId,
                IsEnabled = true,
                OrgRole = OrganizationRole.Member
            });
            await db.SaveChangesAsync();
        }

        // Act
        var response = await client.PostAsync($"/api/me/reminders/{reminder.Id}/mark-delivered", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task MarkDelivered_PublicReminder_ShouldReturnForbidden()
    {
        // Arrange
        var reminder = await SeedReminderAsync(TestOrganizationId, TestUserId, TestUserId, scope: ReminderScope.Public);

        // Act
        var response = await Client.PostAsync($"/api/me/reminders/{reminder.Id}/mark-delivered", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task MarkDelivered_NonExistentReminder_ShouldReturnNotFound()
    {
        // Arrange
        var fakeId = Guid.NewGuid();

        // Act
        var response = await Client.PostAsync($"/api/me/reminders/{fakeId}/mark-delivered", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task MarkDelivered_CancelledReminder_ShouldReturnConflict()
    {
        // Arrange
        var reminder = await SeedReminderAsync(TestOrganizationId, TestUserId, TestUserId, status: ReminderStatus.Cancelled);

        // Act
        var response = await Client.PostAsync($"/api/me/reminders/{reminder.Id}/mark-delivered", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task MarkDelivered_Unauthenticated_ShouldReturnUnauthorized()
    {
        // Arrange
        var unauthClient = Factory.CreateClient();
        var fakeId = Guid.NewGuid();

        // Act
        var response = await unauthClient.PostAsync($"/api/me/reminders/{fakeId}/mark-delivered", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
