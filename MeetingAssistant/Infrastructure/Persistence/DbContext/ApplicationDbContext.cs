using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using System.Security.Claims;
using MeetingAssistant.Features.Identity.Entites;
using MeetingAssistant.Shared;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Api.Shared;

namespace MeetingAssistant.Infrastructure.Persistence.DbContext
{
    public class ApplicationDbContext(
        DbContextOptions<ApplicationDbContext> options,
        IHttpContextAccessor httpContextAccessor,
        ITenantProvider tenantProvider) :
        IdentityDbContext<ApplicationUser>(options)
    {
        private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;
        private readonly Guid? _currentTenantId = tenantProvider.CurrentOrganizationId;


        protected  override void OnModelCreating(ModelBuilder modelBuilder)
        {
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

            base.OnModelCreating(modelBuilder);
        }

        private void ApplyTenantFilter<T>(ModelBuilder builder) where T : class, IHasOrganizationId
        {
            // EF Core translates field accesses on "this" context correctly into parameters
            builder.Entity<T>().HasQueryFilter(e => !_currentTenantId.HasValue || e.OrganizationId == _currentTenantId.Value);
        }
        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
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
            return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

    }
}
