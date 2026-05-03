using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using MeetingAssistant.Features.Identity.Entites;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Features.Tasks.Contracts.Requests;
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

public class CreateMyReminderTests : IntegrationTestBase
{
    public CreateMyReminderTests(MeetingAssistantWebFactory factory) : base(factory)
    {
    }

    private static CreateMyReminderRequest CreateValidRequest(
        string text = "Follow up with client",
        DateTime? reminderAt = null)
    {
        return new CreateMyReminderRequest(
            text,
            reminderAt ?? DateTime.UtcNow.AddDays(1));
    }

    [Fact]
    public async Task CreateMyReminder_ValidRequest_ShouldReturnCreated()
    {
        // Arrange
        var request = CreateValidRequest();

        // Act
        var response = await Client.PostAsJsonAsync("/api/me/reminders", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var result = await response.Content.ReadFromJsonAsync<ReminderResponse>();
        result.Should().NotBeNull();
        result!.Text.Should().Be(request.Text);
        result.Scope.Should().Be(ReminderScope.Personal);
        result.Channel.Should().Be(ReminderChannel.User);
        result.CreatedByUserId.Should().Be(TestUserId);
        result.TargetUserId.Should().Be(TestUserId);
        result.Status.Should().Be(ReminderStatus.Active);
        result.MeetingId.Should().BeNull();
    }

    [Fact]
    public async Task CreateMyReminder_EmptyText_ShouldReturnBadRequest()
    {
        // Arrange
        var request = CreateValidRequest(text: string.Empty);

        // Act
        var response = await Client.PostAsJsonAsync("/api/me/reminders", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.BadRequest);
        error.Errors.Should().NotBeNull();
        error.Errors.Should().ContainKey("Text");
    }

    [Fact]
    public async Task CreateMyReminder_TextExceedsLength_ShouldReturnBadRequest()
    {
        // Arrange
        var request = CreateValidRequest(text: new string('a', 501));

        // Act
        var response = await Client.PostAsJsonAsync("/api/me/reminders", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.BadRequest);
        error.Errors.Should().NotBeNull();
        error.Errors.Should().ContainKey("Text");
    }

    [Fact]
    public async Task CreateMyReminder_ReminderAtUtcBefore2000_ShouldReturnBadRequest()
    {
        // Arrange
        var request = CreateValidRequest(reminderAt: new DateTime(1999, 12, 31, 0, 0, 0, DateTimeKind.Utc));

        // Act
        var response = await Client.PostAsJsonAsync("/api/me/reminders", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.BadRequest);
        error.Errors.Should().NotBeNull();
        error.Errors.Should().ContainKey("ReminderAtUtc");
    }

    [Fact]
    public async Task CreateMyReminder_ReminderAtUtcWithoutUtcKind_ShouldReturnBadRequest()
    {
        // Arrange
        var request = CreateValidRequest(reminderAt: new DateTime(2026, 4, 28, 10, 0, 0, DateTimeKind.Unspecified));

        // Act
        var response = await Client.PostAsJsonAsync("/api/me/reminders", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        error.Should().NotBeNull();
        error!.Status.Should().Be((int)HttpStatusCode.BadRequest);
        error.Errors.Should().NotBeNull();
        error.Errors.Should().ContainKey("ReminderAtUtc");
    }

    [Fact]
    public async Task CreateMyReminder_Unauthenticated_ShouldReturnUnauthorized()
    {
        // Arrange
        var unauthClient = Factory.CreateClient();
        var request = CreateValidRequest();

        // Act
        var response = await unauthClient.PostAsJsonAsync("/api/me/reminders", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
