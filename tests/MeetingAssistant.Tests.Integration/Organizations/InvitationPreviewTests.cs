using System.Net;
using System.Text.Json;
using FluentAssertions;
using MeetingAssistant.Features.Organizations.Contracts.Responses;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Tests.Integration.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeetingAssistant.Tests.Integration.Organizations;

public class InvitationPreviewTests : IntegrationTestBase
{
    public InvitationPreviewTests(MeetingAssistantWebFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task PreviewInvitation_ValidToken_ShouldAllowAnonymousAndReturnPreviewSafeMetadata()
    {
        // Arrange
        const string token = "valid-preview-token";
        await SeedInvitationAsync(token, DateTime.UtcNow.AddDays(1));
        using var anonymousClient = Factory.CreateClient();

        // Act
        var response = await anonymousClient.GetAsync($"/api/invitations/{token}/preview");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        var preview = JsonSerializer.Deserialize<InvitationPreviewResponse>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        preview.Should().NotBeNull();
        preview!.OrganizationName.Should().Be("Test Organization");
        preview.OrganizationSlug.Should().Be("test-org");
        preview.ExpiresAtUtc.Should().BeAfter(DateTime.UtcNow);

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        root.TryGetProperty("token", out _).Should().BeFalse();
        root.TryGetProperty("id", out _).Should().BeFalse();
        root.TryGetProperty("organizationId", out _).Should().BeFalse();
        root.TryGetProperty("createdAtUtc", out _).Should().BeFalse();
    }

    [Fact]
    public async Task PreviewInvitation_InvalidToken_ShouldReturnNotFoundForAnonymous()
    {
        using var anonymousClient = Factory.CreateClient();

        var response = await anonymousClient.GetAsync("/api/invitations/not-a-real-token/preview");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PreviewInvitation_ExpiredToken_ShouldReturnGoneForAnonymous()
    {
        const string token = "expired-preview-token";
        await SeedInvitationAsync(token, DateTime.UtcNow.AddMinutes(-5));
        using var anonymousClient = Factory.CreateClient();

        var response = await anonymousClient.GetAsync($"/api/invitations/{token}/preview");

        response.StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task PreviewInvitation_RevokedToken_ShouldReturnGoneForAnonymous()
    {
        const string token = "revoked-preview-token";
        await SeedInvitationAsync(token, DateTime.UtcNow.AddDays(1), revokedAtUtc: DateTime.UtcNow.AddMinutes(-1));
        using var anonymousClient = Factory.CreateClient();

        var response = await anonymousClient.GetAsync($"/api/invitations/{token}/preview");

        response.StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    private async Task SeedInvitationAsync(string token, DateTime expiresAtUtc, DateTime? revokedAtUtc = null)
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
            ExpiresAtUtc = expiresAtUtc,
            RevokedAtUtc = revokedAtUtc,
            RevokedByUserId = revokedAtUtc.HasValue ? TestUserId : null
        });

        await db.SaveChangesAsync();
    }
}
