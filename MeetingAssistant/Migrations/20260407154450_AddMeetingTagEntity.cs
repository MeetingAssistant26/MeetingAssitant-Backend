using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingAssistant.Migrations
{
    /// <inheritdoc />
    public partial class AddMeetingTagEntity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MeetingTags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Color = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeetingTags", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MeetingTags_OrganizationId",
                table: "MeetingTags",
                column: "OrganizationId");

            // Case-insensitive unique index using LOWER() — EF can't express this natively
            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX "IX_MeetingTags_OrgId_LowerName_ActiveOnly"
                ON "MeetingTags" ("OrganizationId", LOWER("Name"))
                WHERE "IsActive" = true;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_MeetingTags_OrgId_LowerName_ActiveOnly";""");

            migrationBuilder.DropTable(
                name: "MeetingTags");
        }
    }
}
