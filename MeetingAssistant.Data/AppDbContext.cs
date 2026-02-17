using MeetingAssistant.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace MeetingAssistant.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options)
            : base(options)
        {
        }

        public DbSet<HealthCheckRecord> HealthCheckRecords => Set<HealthCheckRecord>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<HealthCheckRecord>(entity =>
            {
                entity.ToTable("health_check_records");
                entity.HasKey(record => record.Id);
                entity.Property(record => record.Source).HasMaxLength(100).IsRequired();
                entity.Property(record => record.CreatedAtUtc).HasColumnName("created_at_utc");
            });
        }
    }
}
