using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using FluentAssertions;
using MeetingAssistant.Api.Shared;
using MeetingAssistant.Features.ActionItems.Models.Entities;
using MeetingAssistant.Features.ActionItems.Models.Enums;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeetingAssistant.Tests.Integration.Infrastructure;

public class PlatformEndpointConventionsTests : IntegrationTestBase
{
    private static readonly string[] SharedPassEndpointFiles =
    [
        "MeetingAssistant/Features/Organizations/Endpoints/Invitation/PreviewInvitationEndpoint.cs",
        "MeetingAssistant/Features/Organizations/Endpoints/Organization/ListOrganizationsEndpoint.cs",
        "MeetingAssistant/Features/Organizations/Endpoints/Organization/GetOrganizationEndpoint.cs",
        "MeetingAssistant/Features/Meetings/Endpoints/Meeting/GetMeetingEndpoint.cs",
        "MeetingAssistant/Features/Meetings/Endpoints/Calendar/GetCalendarDataEndpoint.cs",
        "MeetingAssistant/Features/ActionItems/Endpoints/Organization/ListOrganizationActionItemsEndpoint.cs",
        "MeetingAssistant/Features/LiveSession/Endpoints/Artifacts/GetMeetingTranscriptEndpoint.cs",
        "MeetingAssistant/Features/LiveSession/Endpoints/Artifacts/GetMeetingSummaryEndpoint.cs",
        "MeetingAssistant/Features/Tasks/Endpoints/Reminder/ListMyRemindersEndpoint.cs",
        "MeetingAssistant/Features/Tasks/Endpoints/Reminder/UpdateMyReminderEndpoint.cs"
    ];

    public PlatformEndpointConventionsTests(MeetingAssistantWebFactory factory) : base(factory)
    {
    }

    [Fact]
    public void SharedPassEndpointFiles_ShouldDelegateFailurePathsThroughToProblem()
    {
        var repositoryRoot = FindRepositoryRoot();
        var violations = SharedPassEndpointFiles
            .Select(relativePath => Path.Combine(repositoryRoot, relativePath))
            .Where(file => !File.Exists(file) || !File.ReadAllText(file).Contains("ToProblem("))
            .ToList();

        violations.Should().BeEmpty("all shared web/mobile endpoint files must emit the standard error envelope through Result.ToProblem()");
    }

    [Fact]
    public void SharedPassEndpointActions_ShouldAcceptCancellationTokenAsLastParameter()
    {
        var assembly = typeof(MeetingAssistant.Api.Program).Assembly;
        var actionNames = new HashSet<string>
        {
            "MeetingAssistant.Features.Organizations.Endpoints.Invitation.PublicInvitationPreviewController.PreviewInvitation",
            "MeetingAssistant.Features.Organizations.Endpoints.Organization.OrganizationController.ListOrganizations",
            "MeetingAssistant.Features.Organizations.Endpoints.Organization.OrganizationController.GetOrganization",
            "MeetingAssistant.Features.Meetings.Endpoints.Meeting.MeetingController.GetMeeting",
            "MeetingAssistant.Features.Meetings.Endpoints.Calendar.CalendarController.GetCalendarData",
            "MeetingAssistant.Features.ActionItems.Endpoints.Organization.OrganizationActionItemController.ListOrganizationActionItems",
            "MeetingAssistant.Features.LiveSession.Endpoints.Artifacts.MeetingArtifactController.GetTranscript",
            "MeetingAssistant.Features.LiveSession.Endpoints.Artifacts.MeetingArtifactController.GetSummary",
            "MeetingAssistant.Features.Tasks.Endpoints.Reminder.ReminderController.ListMyReminders",
            "MeetingAssistant.Features.Tasks.Endpoints.Reminder.ReminderController.UpdateMyReminder"
        };

        var discoveredActions = assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Select(m => new { Type = t, Method = m, FullName = $"{t.FullName}.{m.Name}" }))
            .Where(x => actionNames.Contains(x.FullName))
            .ToList();

        discoveredActions.Select(x => x.FullName).Should().BeEquivalentTo(actionNames);

        var violations = discoveredActions
            .Where(x =>
            {
                x.Method.ReturnType.Should().Be(typeof(Task<IActionResult>));
                var parameters = x.Method.GetParameters();
                return parameters.Length == 0 || parameters[^1].ParameterType != typeof(CancellationToken);
            })
            .Select(x => x.FullName)
            .ToList();

        violations.Should().BeEmpty("all shared web/mobile endpoint actions must preserve request cancellation");
    }

    [Fact]
    public async Task UnauthenticatedSharedRoute_ShouldReturn401ProblemEnvelopeWithCorrelationId()
    {
        using var anonymousClient = Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/organizations");
        request.Headers.Add("X-Correlation-Id", "platform-unauthenticated-test");

        var response = await anonymousClient.SendAsync(request);

        await AssertProblemEnvelopeAsync(response, HttpStatusCode.Unauthorized, "platform-unauthenticated-test");
    }

    [Fact]
    public async Task ForbiddenOrgScopedRoute_ShouldReturn403ProblemEnvelopeWithCorrelationId()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/organizations/{Guid.NewGuid()}/calendar?week=2030-06-03");
        request.Headers.Add("X-Correlation-Id", "platform-forbidden-test");

        var response = await Client.SendAsync(request);

        await AssertProblemEnvelopeAsync(response, HttpStatusCode.Forbidden, "platform-forbidden-test");
    }

    [Fact]
    public async Task MissingOrNotVisibleMeeting_ShouldReturn404ProblemEnvelopeWithCorrelationId()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/organizations/{TestOrganizationId}/meetings/{Guid.NewGuid()}");
        request.Headers.Add("X-Correlation-Id", "platform-notfound-test");

        var response = await Client.SendAsync(request);

        await AssertProblemEnvelopeAsync(response, HttpStatusCode.NotFound, "platform-notfound-test");
    }

    [Fact]
    public async Task ExpiredInvitationPreview_ShouldReturn410ProblemEnvelopeWithCorrelationId()
    {
        const string token = "platform-expired-invite";
        await SeedInvitationAsync(token, DateTime.UtcNow.AddMinutes(-5));
        using var anonymousClient = Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/invitations/{token}/preview");
        request.Headers.Add("X-Correlation-Id", "platform-expired-invite-test");

        var response = await anonymousClient.SendAsync(request);

        await AssertProblemEnvelopeAsync(response, HttpStatusCode.Gone, "platform-expired-invite-test");
    }

    [Theory]
    [InlineData(null, 428, "platform-missing-if-match-test")]
    [InlineData("stale-etag", 409, "platform-stale-if-match-test")]
    public async Task ActionItemConcurrencyFailures_ShouldReturnProblemEnvelopeWithCorrelationId(
        string? ifMatch,
        int expectedStatusCode,
        string correlationId)
    {
        var meeting = await SeedMeetingAsync();
        await SeedMeetingParticipantAsync(meeting.Id, TestUserId, MeetingRole.Host);
        var item = await SeedActionItemAsync(meeting.Id);

        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/organizations/{TestOrganizationId}/meetings/{meeting.Id}/action-items/{item.Id}/approve");
        request.Headers.Add("X-Correlation-Id", correlationId);
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        var response = await Client.SendAsync(request);

        await AssertProblemEnvelopeAsync(response, (HttpStatusCode)expectedStatusCode, correlationId);
    }

    private static async Task AssertProblemEnvelopeAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatusCode,
        string expectedCorrelationId)
    {
        response.StatusCode.Should().Be(expectedStatusCode);
        response.Headers.TryGetValues("X-Correlation-Id", out var headerValues).Should().BeTrue();
        headerValues.Should().Contain(expectedCorrelationId);

        var problem = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        problem.Should().NotBeNull();
        problem!.Status.Should().Be((int)expectedStatusCode);
        problem.Type.Should().NotBeNullOrWhiteSpace();
        problem.Title.Should().NotBeNullOrWhiteSpace();
        problem.CorrelationId.Should().Be(expectedCorrelationId);
    }

    private async Task<Meeting> SeedMeetingAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var meeting = new Meeting
        {
            Id = Guid.NewGuid(),
            OrganizationId = TestOrganizationId,
            Title = "Platform convention meeting",
            ScheduledStartUtc = DateTime.UtcNow.AddHours(1),
            ScheduledEndUtc = DateTime.UtcNow.AddHours(2),
            Status = MeetingStatus.Scheduled
        };

        db.Meetings.Add(meeting);
        await db.SaveChangesAsync();
        return meeting;
    }

    private async Task SeedMeetingParticipantAsync(Guid meetingId, Guid userId, MeetingRole role)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.MeetingParticipants.Add(new MeetingParticipant
        {
            Id = Guid.NewGuid(),
            MeetingId = meetingId,
            OrganizationId = TestOrganizationId,
            UserId = userId,
            MeetingRole = role
        });

        await db.SaveChangesAsync();
    }

    private async Task<ActionItem> SeedActionItemAsync(Guid meetingId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var item = new ActionItem
        {
            Id = Guid.NewGuid(),
            OrganizationId = TestOrganizationId,
            MeetingId = meetingId,
            Title = "Platform convention action item",
            Description = "Verify If-Match error envelope.",
            AssignedToUserId = TestUserId,
            Status = ActionItemStatus.PendingReview,
            ExtractedAtUtc = DateTime.UtcNow
        };

        db.ActionItems.Add(item);
        await db.SaveChangesAsync();
        return item;
    }

    private async Task SeedInvitationAsync(string token, DateTime expiresAtUtc)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Invitations.Add(new Invitation
        {
            Id = Guid.NewGuid(),
            OrganizationId = TestOrganizationId,
            InvitedByUserId = TestUserId,
            Token = token,
            EmailWhitelist = [],
            ExpiresAtUtc = expiresAtUtc
        });

        await db.SaveChangesAsync();
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            var marker = Path.Combine(current.FullName, "AGENTS.md");
            if (File.Exists(marker))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root could not be resolved from test runtime path.");
    }
}
