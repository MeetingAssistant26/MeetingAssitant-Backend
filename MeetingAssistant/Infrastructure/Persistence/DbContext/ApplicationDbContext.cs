using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using MediatR;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using System.Security.Claims;
using MeetingAssistant.Features.Identity.Entites;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Tasks.Models.Entities;
using MeetingAssistant.Features.ActionItems.Models.Entities;
using MeetingAssistant.Features.LiveSession.Models.PostProcessing;
using MeetingAssistant.Features.Rag.Models;
using MeetingAssistant.Shared;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Api.Shared;

namespace MeetingAssistant.Infrastructure.Persistence.DbContext
{
    public class ApplicationDbContext(
        DbContextOptions<ApplicationDbContext> options,
        IHttpContextAccessor httpContextAccessor,
        ITenantProvider tenantProvider,
        IPublisher publisher) :
        IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
    {
        private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;
        private readonly Guid? _currentTenantId = tenantProvider.CurrentOrganizationId;
        private readonly IPublisher _publisher = publisher;

        public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
        public DbSet<Organization> Organizations => Set<Organization>();
        public DbSet<UserOrgMembership> UserOrgMemberships => Set<UserOrgMembership>();
        public DbSet<Invitation> Invitations => Set<Invitation>();
        public DbSet<MeetingTag> MeetingTags => Set<MeetingTag>();
        public DbSet<RecurringMeetingSeries> RecurringMeetingSeries => Set<RecurringMeetingSeries>();
        public DbSet<Meeting> Meetings => Set<Meeting>();
        public DbSet<MeetingParticipant> MeetingParticipants => Set<MeetingParticipant>();
        public DbSet<MeetingMeetingTag> MeetingMeetingTags => Set<MeetingMeetingTag>();
        public DbSet<MeetingTagSuggestion> MeetingTagSuggestions => Set<MeetingTagSuggestion>();
        public DbSet<SessionEvent> SessionEvents => Set<SessionEvent>();
        public DbSet<AiAssistantTraceEvent> AiAssistantTraceEvents => Set<AiAssistantTraceEvent>();
        public DbSet<PostMeetingProcessingRun> PostMeetingProcessingRuns => Set<PostMeetingProcessingRun>();
        public DbSet<PostMeetingProcessingStep> PostMeetingProcessingSteps => Set<PostMeetingProcessingStep>();
        public DbSet<PostMeetingProcessingEvent> PostMeetingProcessingEvents => Set<PostMeetingProcessingEvent>();
        public DbSet<ParticipantAudioFragment> ParticipantAudioFragments => Set<ParticipantAudioFragment>();
        public DbSet<ParticipantAudioTrack> ParticipantAudioTracks => Set<ParticipantAudioTrack>();
        public DbSet<MeetingTranscript> MeetingTranscripts => Set<MeetingTranscript>();
        public DbSet<MeetingSummary> MeetingSummaries => Set<MeetingSummary>();
        public DbSet<Reminder> Reminders => Set<Reminder>();
        public DbSet<ActionItem> ActionItems => Set<ActionItem>();
        public DbSet<KnowledgeDocument> KnowledgeDocuments => Set<KnowledgeDocument>();
        public DbSet<KnowledgeChunk> KnowledgeChunks => Set<KnowledgeChunk>();
        public DbSet<KnowledgeChunkTag> KnowledgeChunkTags => Set<KnowledgeChunkTag>();
        public DbSet<OrganizationIntegration> OrganizationIntegrations => Set<OrganizationIntegration>();
        public DbSet<OrganizationIntegrationConfig> OrganizationIntegrationConfigs => Set<OrganizationIntegrationConfig>();
        public DbSet<ExternalAccountLink> ExternalAccountLinks => Set<ExternalAccountLink>();
        public DbSet<ExternalMemberMapping> ExternalMemberMappings => Set<ExternalMemberMapping>();
        protected  override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.HasPostgresExtension("vector");

            modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());

            var applyTenantFilterMethod = typeof(ApplicationDbContext)
                .GetMethod(nameof(ApplyTenantFilter), BindingFlags.NonPublic | BindingFlags.Instance);

            foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                         .Where(e => typeof(IHasOrganizationId).IsAssignableFrom(e.ClrType)))
            {
                applyTenantFilterMethod?.MakeGenericMethod(entityType.ClrType).Invoke(this, new object[] { modelBuilder });
            }

            var cascadeFks = modelBuilder.Model
                           .GetEntityTypes()
                           .SelectMany(t => t.GetForeignKeys())
                           .Where(fk => fk.DeleteBehavior == DeleteBehavior.Cascade && !fk.IsOwnership);

            foreach ( var fk in cascadeFks)
                    fk.DeleteBehavior = DeleteBehavior.Restrict;
        }

        private void ApplyTenantFilter<T>(ModelBuilder builder) where T : class, IHasOrganizationId
        {
            // EF Core translates field accesses on "this" context correctly into parameters
            builder.Entity<T>().HasQueryFilter(e => !_currentTenantId.HasValue || e.OrganizationId == _currentTenantId.Value);
        }
        public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            var utcNow = DateTime.UtcNow;

            foreach (var entry in ChangeTracker.Entries<BaseEntity>())
            {
                if (entry.State == EntityState.Added)
                {
                    entry.Entity.CreatedAtUtc = utcNow;
                    entry.Entity.UpdatedAtUtc = utcNow;

                    if (entry.Entity is IHasOrganizationId tenantEntity &&
                        tenantEntity.OrganizationId == Guid.Empty &&
                        _currentTenantId.HasValue)
                    {
                        tenantEntity.OrganizationId = _currentTenantId.Value;
                    }
                }
                else if (entry.State == EntityState.Modified)
                {
                    entry.Entity.UpdatedAtUtc = utcNow;
                }
            }

            var entries = ChangeTracker.Entries<AuditableEntity>();
            var currentUserId = _httpContextAccessor.HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? "System";

            foreach (var entityEntry in entries) 
            {
                if (entityEntry.State == EntityState.Added)
                {
                    entityEntry.Property(x => x.CreatedById).CurrentValue = currentUserId;
                }
                else if (entityEntry.State == EntityState.Modified)
                {
                    entityEntry.Property(x => x.UpdatedById).CurrentValue = currentUserId;
                }
            }

            foreach (var actionItemEntry in ChangeTracker.Entries<ActionItem>())
            {
                if (actionItemEntry.State is EntityState.Added or EntityState.Modified)
                {
                    actionItemEntry.Entity.RowVersion = Guid.NewGuid().ToByteArray();
                }
            }

            var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

            await DispatchDomainEventsAsync(cancellationToken);

            return result;
        }

        private async Task DispatchDomainEventsAsync(CancellationToken cancellationToken)
        {
            var entitiesWithEvents = ChangeTracker.Entries<BaseEntity>()
                .Where(e => e.Entity.DomainEvents.Any())
                .Select(e => e.Entity)
                .ToList();

            var domainEvents = entitiesWithEvents
                .SelectMany(e => e.DomainEvents)
                .ToList();

            foreach (var entity in entitiesWithEvents)
                entity.ClearDomainEvents();

            foreach (var domainEvent in domainEvents)
                await _publisher.Publish(domainEvent, cancellationToken);
        }

    }
}
