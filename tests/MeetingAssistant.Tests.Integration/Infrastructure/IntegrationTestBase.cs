using System.Net.Http.Headers;
using MeetingAssistant.Features.Identity.Entites;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeetingAssistant.Tests.Integration.Infrastructure;

public abstract class IntegrationTestBase : IClassFixture<MeetingAssistantWebFactory>, IAsyncLifetime
{
    protected readonly MeetingAssistantWebFactory Factory;
    protected HttpClient Client = null!;

    protected Guid TestUserId { get; } = Guid.NewGuid();
    protected Guid TestOrganizationId { get; } = Guid.NewGuid();

    protected IntegrationTestBase(MeetingAssistantWebFactory factory)
    {
        Factory = factory;
    }

    public virtual async Task InitializeAsync()
    {
        Client = Factory.CreateClient();

        var token = TestJwtTokenHelper.GenerateToken(TestUserId, TestOrganizationId);
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // First test: create schema. Subsequent tests: already exists.
        await db.Database.EnsureCreatedAsync();

        // Truncate all application tables to ensure clean state between tests
        await CleanDatabaseAsync(db);

        // Seed base entities required by EnforceOrgAccess
        await SeedBaseEntitiesAsync(db);
    }

    public virtual Task DisposeAsync() => Task.CompletedTask;

    private static async Task CleanDatabaseAsync(ApplicationDbContext db)
    {
        // Delete in dependency order (children before parents)
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"Reminders\"");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"ActionItems\"");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"KnowledgeChunkTags\"");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"KnowledgeChunks\"");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"KnowledgeDocuments\"");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"AiAssistantTraceEvents\"");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"PostMeetingProcessingEvents\"");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"PostMeetingProcessingSteps\"");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"PostMeetingProcessingRuns\"");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"ExternalMemberMappings\"");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"ExternalAccountLinks\"");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"OrganizationIntegrationConfigs\"");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"OrganizationIntegrations\"");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"SessionEvents\"");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"ParticipantAudioFragments\"");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"ParticipantAudioTracks\"");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"PersonalizedMeetingSummaries\"");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"MeetingSummaries\"");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"MeetingTranscripts\"");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"MeetingTagSuggestions\"");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"MeetingMeetingTags\"");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"MeetingParticipants\"");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"Meetings\"");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"RecurringMeetingSeries\"");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"MeetingTags\"");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"Invitations\"");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"UserOrgMemberships\"");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"Organizations\"");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"RefreshTokens\"");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"AspNetUsers\"");
    }

    private async Task SeedBaseEntitiesAsync(ApplicationDbContext db)
    {
        var user = new ApplicationUser
        {
            Id = TestUserId,
            UserName = $"testuser-{TestUserId:N}@test.com",
            NormalizedUserName = $"TESTUSER-{TestUserId:N}@TEST.COM",
            Email = $"testuser-{TestUserId:N}@test.com",
            NormalizedEmail = $"TESTUSER-{TestUserId:N}@TEST.COM",
            EmailConfirmed = true,
            DisplayName = "Test User",
            SecurityStamp = Guid.NewGuid().ToString()
        };

        var org = new Organization
        {
            Id = TestOrganizationId,
            Name = "Test Organization",
            Slug = "test-org"
        };

        var membership = new UserOrgMembership
        {
            UserId = TestUserId,
            OrganizationId = TestOrganizationId,
            OrgRole = OrganizationRole.Admin,
            IsEnabled = true
        };

        db.Users.Add(user);
        db.Organizations.Add(org);
        db.UserOrgMemberships.Add(membership);
        await db.SaveChangesAsync();
    }
}


