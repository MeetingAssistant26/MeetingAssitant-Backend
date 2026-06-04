using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingAssistant.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260604150000_EnforceKnowledgeDocumentCurrentPublishedArtifact")]
    public partial class EnforceKnowledgeDocumentCurrentPublishedArtifact : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                WITH ranked AS (
                    SELECT
                        d."Id",
                        d."OrganizationId",
                        ROW_NUMBER() OVER (
                            PARTITION BY
                                d."OrganizationId",
                                d."MeetingId",
                                d."ArtifactType",
                                d."ArtifactId",
                                d."ArtifactVersion"
                            ORDER BY d."GeneratedAtUtc" DESC, d."Id"
                        ) AS rn
                    FROM "KnowledgeDocuments" d
                    WHERE d."MeetingId" IS NOT NULL
                      AND d."Visibility" = 1
                      AND d."IsCurrent" = true
                ),
                losers AS (
                    SELECT "Id", "OrganizationId"
                    FROM ranked
                    WHERE rn > 1
                )
                UPDATE "KnowledgeDocuments" d
                SET
                    "Visibility" = 2,
                    "IsCurrent" = false,
                    "UpdatedAtUtc" = NOW()
                FROM losers l
                WHERE d."Id" = l."Id";
                """);

            migrationBuilder.Sql(
                """
                UPDATE "KnowledgeChunks" c
                SET
                    "Visibility" = 2,
                    "IsCurrent" = false,
                    "UpdatedAtUtc" = NOW()
                FROM "KnowledgeDocuments" d
                WHERE c."DocumentId" = d."Id"
                  AND d."Visibility" = 2
                  AND d."IsCurrent" = false
                  AND c."Visibility" = 1
                  AND c."IsCurrent" = true;
                """);

            migrationBuilder.CreateIndex(
                name: "UX_KnowledgeDocuments_CurrentPublishedArtifact",
                table: "KnowledgeDocuments",
                columns: new[] { "OrganizationId", "MeetingId", "ArtifactType", "ArtifactId", "ArtifactVersion" },
                unique: true,
                filter: "\"MeetingId\" IS NOT NULL AND \"Visibility\" = 1 AND \"IsCurrent\" = true");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_KnowledgeDocuments_CurrentPublishedArtifact",
                table: "KnowledgeDocuments");
        }
    }
}
