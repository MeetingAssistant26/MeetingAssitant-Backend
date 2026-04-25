using MeetingAssistant.Features.Identity.Entites;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MeetingAssistant.Features.DevSeeding;

public sealed class DevDbSeeder(
    UserManager<ApplicationUser> userManager,
    ApplicationDbContext dbContext,
    ILogger<DevDbSeeder> logger)
{
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Running dev database seeder...");

        // ─── 1. Users ──────────────────────────────────────────
        var user1 = await EnsureUserAsync(
            email: "dev-user-1@example.com",
            password: "Password#123",
            displayName: "Test User 1",
            cancellationToken);

        var user2 = await EnsureUserAsync(
            email: "dev-user-2@example.com",
            password: "Password#123",
            displayName: "Test User 2",
            cancellationToken);

        // ─── 2. Organization ───────────────────────────────────
        var org = await EnsureOrganizationAsync(
            name: "Dev Test Org",
            slug: "dev-test-org",
            cancellationToken);

        // ─── 3. Memberships ────────────────────────────────────
        await EnsureMembershipAsync(user1.Id, org.Id, OrganizationRole.Admin, cancellationToken);
        await EnsureMembershipAsync(user2.Id, org.Id, OrganizationRole.Member, cancellationToken);

        // ─── 4. Meeting ────────────────────────────────────────
        var meeting = await EnsureMeetingAsync(
            orgId: org.Id,
            title: "Dev LiveKit Test Meeting",
            description: "Multi-user prototype testing room",
            hostId: user1.Id,
            participantId: user2.Id,
            cancellationToken);

        logger.LogInformation(
            "Dev seed complete. Org={OrgId} Meeting={MeetingId} User1={User1Id} User2={User2Id}",
            org.Id, meeting.Id, user1.Id, user2.Id);
    }

    private async Task<ApplicationUser> EnsureUserAsync(
        string email, string password, string displayName, CancellationToken ct)
    {
        var existing = await userManager.FindByEmailAsync(email);
        if (existing is not null)
        {
            logger.LogInformation("Dev user already exists: {Email}", email);
            return existing;
        }

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            DisplayName = displayName,
            EmailConfirmed = true,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        var result = await userManager.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            throw new InvalidOperationException($"Failed to create dev user {email}: {errors}");
        }

        // Ensure email is confirmed
        var token = await userManager.GenerateEmailConfirmationTokenAsync(user);
        var confirmResult = await userManager.ConfirmEmailAsync(user, token);
        if (!confirmResult.Succeeded)
        {
            logger.LogWarning("Could not confirm email for dev user {Email}", email);
        }

        logger.LogInformation("Created dev user: {Email} ({UserId})", email, user.Id);
        return user;
    }

    private async Task<Organization> EnsureOrganizationAsync(
        string name, string slug, CancellationToken ct)
    {
        var existing = await dbContext.Organizations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Slug == slug, ct);

        if (existing is not null)
        {
            logger.LogInformation("Dev org already exists: {Slug}", slug);
            return existing;
        }

        var org = new Organization
        {
            Name = name,
            Slug = slug,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        dbContext.Organizations.Add(org);
        await dbContext.SaveChangesAsync(ct);

        logger.LogInformation("Created dev org: {Name} ({OrgId})", name, org.Id);
        return org;
    }

    private async Task EnsureMembershipAsync(
        Guid userId, Guid orgId, OrganizationRole role, CancellationToken ct)
    {
        var exists = await dbContext.UserOrgMemberships
            .IgnoreQueryFilters()
            .AnyAsync(m => m.UserId == userId && m.OrganizationId == orgId, ct);

        if (exists)
        {
            logger.LogInformation("Dev membership already exists: User={UserId} Org={OrgId}", userId, orgId);
            return;
        }

        var membership = new UserOrgMembership
        {
            UserId = userId,
            OrganizationId = orgId,
            OrgRole = role,
            IsEnabled = true,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        dbContext.UserOrgMemberships.Add(membership);
        await dbContext.SaveChangesAsync(ct);

        logger.LogInformation("Created dev membership: User={UserId} Org={OrgId} Role={Role}", userId, orgId, role);
    }

    private async Task<Meeting> EnsureMeetingAsync(
        Guid orgId, string title, string description,
        Guid hostId, Guid participantId, CancellationToken ct)
    {
        var exists = await dbContext.Meetings
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(m => m.OrganizationId == orgId && m.Title == title, ct);

        if (exists)
        {
            var existing = await dbContext.Meetings
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstAsync(m => m.OrganizationId == orgId && m.Title == title, ct);
            logger.LogInformation("Dev meeting already exists: {Title} ({MeetingId})", title, existing.Id);
            return existing;
        }

        var meeting = new Meeting
        {
            OrganizationId = orgId,
            Title = title,
            Description = description,
            ScheduledStartUtc = DateTime.UtcNow.AddMinutes(-5),
            ScheduledEndUtc = DateTime.UtcNow.AddHours(2),
            Status = MeetingStatus.Scheduled,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            Participants =
            {
                new MeetingParticipant
                {
                    OrganizationId = orgId,
                    UserId = hostId,
                    MeetingRole = MeetingRole.Host,
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow
                },
                new MeetingParticipant
                {
                    OrganizationId = orgId,
                    UserId = participantId,
                    MeetingRole = MeetingRole.Participant,
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow
                }
            }
        };

        dbContext.Meetings.Add(meeting);
        await dbContext.SaveChangesAsync(ct);

        logger.LogInformation(
            "Created dev meeting: {Title} ({MeetingId}) Host={HostId} Participant={ParticipantId}",
            title, meeting.Id, hostId, participantId);

        return meeting;
    }
}
