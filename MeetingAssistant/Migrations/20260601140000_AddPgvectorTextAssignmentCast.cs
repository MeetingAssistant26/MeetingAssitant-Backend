using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingAssistant.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260601140000_AddPgvectorTextAssignmentCast")]
    public partial class AddPgvectorTextAssignmentCast : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    CREATE CAST (text AS vector) WITH INOUT AS ASSIGNMENT;
                EXCEPTION
                    WHEN duplicate_object THEN NULL;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    DROP CAST IF EXISTS (text AS vector);
                EXCEPTION
                    WHEN undefined_object THEN NULL;
                END $$;
                """);
        }
    }
}
