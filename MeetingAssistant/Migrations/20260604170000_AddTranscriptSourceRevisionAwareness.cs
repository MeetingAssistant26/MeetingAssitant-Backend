using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingAssistant.Migrations
{
    /// <inheritdoc />
    public partial class AddTranscriptSourceRevisionAwareness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SourceTranscriptId",
                table: "PersonalizedMeetingSummaries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceTranscriptHash",
                table: "PersonalizedMeetingSummaries",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceTranscriptRevision",
                table: "PersonalizedMeetingSummaries",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SourceTranscriptGeneratedAtUtc",
                table: "PersonalizedMeetingSummaries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceTranscriptId",
                table: "MeetingSummaries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceTranscriptHash",
                table: "MeetingSummaries",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceTranscriptRevision",
                table: "MeetingSummaries",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SourceTranscriptGeneratedAtUtc",
                table: "MeetingSummaries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TranscriptHash",
                table: "MeetingTranscripts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "TranscriptRevision",
                table: "MeetingTranscripts",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceTranscriptId",
                table: "KnowledgeDocuments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceTranscriptHash",
                table: "KnowledgeDocuments",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceTranscriptRevision",
                table: "KnowledgeDocuments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SourceTranscriptGeneratedAtUtc",
                table: "KnowledgeDocuments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceTranscriptId",
                table: "KnowledgeChunks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceTranscriptHash",
                table: "KnowledgeChunks",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceTranscriptRevision",
                table: "KnowledgeChunks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SourceTranscriptGeneratedAtUtc",
                table: "KnowledgeChunks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceTranscriptId",
                table: "ActionItems",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceTranscriptHash",
                table: "ActionItems",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceTranscriptRevision",
                table: "ActionItems",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SourceTranscriptGeneratedAtUtc",
                table: "ActionItems",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SupersededAtUtc",
                table: "ActionItems",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SupersededByTranscriptHash",
                table: "ActionItems",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SupersededReason",
                table: "ActionItems",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.Sql(
                """
                CREATE EXTENSION IF NOT EXISTS pgcrypto;
                """);

            migrationBuilder.Sql(
                """
                UPDATE "MeetingTranscripts"
                SET
                    "TranscriptHash" = lower(encode(digest(coalesce("FullText", ''), 'sha256'), 'hex')),
                    "TranscriptRevision" = 1
                WHERE coalesce("TranscriptHash", '') = '';
                """);

            migrationBuilder.Sql(
                """
                UPDATE "MeetingSummaries" s
                SET
                    "SourceTranscriptId" = t."Id",
                    "SourceTranscriptHash" = t."TranscriptHash",
                    "SourceTranscriptRevision" = t."TranscriptRevision",
                    "SourceTranscriptGeneratedAtUtc" = t."GeneratedAtUtc"
                FROM "MeetingTranscripts" t
                WHERE s."OrganizationId" = t."OrganizationId"
                  AND s."MeetingId" = t."MeetingId";
                """);

            migrationBuilder.Sql(
                """
                UPDATE "PersonalizedMeetingSummaries" p
                SET
                    "SourceTranscriptId" = t."Id",
                    "SourceTranscriptHash" = t."TranscriptHash",
                    "SourceTranscriptRevision" = t."TranscriptRevision",
                    "SourceTranscriptGeneratedAtUtc" = t."GeneratedAtUtc"
                FROM "MeetingTranscripts" t
                WHERE p."OrganizationId" = t."OrganizationId"
                  AND p."MeetingId" = t."MeetingId";
                """);

            migrationBuilder.Sql(
                """
                UPDATE "ActionItems" a
                SET
                    "SourceTranscriptId" = t."Id",
                    "SourceTranscriptHash" = t."TranscriptHash",
                    "SourceTranscriptRevision" = t."TranscriptRevision",
                    "SourceTranscriptGeneratedAtUtc" = t."GeneratedAtUtc"
                FROM "MeetingTranscripts" t
                WHERE a."OrganizationId" = t."OrganizationId"
                  AND a."MeetingId" = t."MeetingId";
                """);

            migrationBuilder.Sql(
                """
                UPDATE "KnowledgeDocuments" d
                SET
                    "SourceTranscriptId" = t."Id",
                    "SourceTranscriptHash" = t."TranscriptHash",
                    "SourceTranscriptRevision" = t."TranscriptRevision",
                    "SourceTranscriptGeneratedAtUtc" = t."GeneratedAtUtc"
                FROM "MeetingTranscripts" t
                WHERE d."MeetingId" IS NOT NULL
                  AND d."OrganizationId" = t."OrganizationId"
                  AND d."MeetingId" = t."MeetingId";
                """);

            migrationBuilder.Sql(
                """
                UPDATE "KnowledgeChunks" c
                SET
                    "SourceTranscriptId" = t."Id",
                    "SourceTranscriptHash" = t."TranscriptHash",
                    "SourceTranscriptRevision" = t."TranscriptRevision",
                    "SourceTranscriptGeneratedAtUtc" = t."GeneratedAtUtc"
                FROM "MeetingTranscripts" t
                WHERE c."MeetingId" IS NOT NULL
                  AND c."OrganizationId" = t."OrganizationId"
                  AND c."MeetingId" = t."MeetingId";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_ActionItems_OrganizationId_MeetingId_SourceTranscriptHash",
                table: "ActionItems",
                columns: new[] { "OrganizationId", "MeetingId", "SourceTranscriptHash" });

            migrationBuilder.CreateIndex(
                name: "IX_ActionItems_OrganizationId_MeetingId_SupersededAtUtc",
                table: "ActionItems",
                columns: new[] { "OrganizationId", "MeetingId", "SupersededAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeChunks_OrganizationId_MeetingId_SourceTranscriptHash",
                table: "KnowledgeChunks",
                columns: new[] { "OrganizationId", "MeetingId", "SourceTranscriptHash" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeDocuments_OrganizationId_MeetingId_SourceTranscriptHash",
                table: "KnowledgeDocuments",
                columns: new[] { "OrganizationId", "MeetingId", "SourceTranscriptHash" });

            migrationBuilder.CreateIndex(
                name: "IX_MeetingTranscripts_OrganizationId_MeetingId_TranscriptHash",
                table: "MeetingTranscripts",
                columns: new[] { "OrganizationId", "MeetingId", "TranscriptHash" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MeetingTranscripts_OrganizationId_MeetingId_TranscriptHash",
                table: "MeetingTranscripts");

            migrationBuilder.DropIndex(
                name: "IX_KnowledgeDocuments_OrganizationId_MeetingId_SourceTranscriptHash",
                table: "KnowledgeDocuments");

            migrationBuilder.DropIndex(
                name: "IX_KnowledgeChunks_OrganizationId_MeetingId_SourceTranscriptHash",
                table: "KnowledgeChunks");

            migrationBuilder.DropIndex(
                name: "IX_ActionItems_OrganizationId_MeetingId_SupersededAtUtc",
                table: "ActionItems");

            migrationBuilder.DropIndex(
                name: "IX_ActionItems_OrganizationId_MeetingId_SourceTranscriptHash",
                table: "ActionItems");

            migrationBuilder.DropColumn(name: "SupersededReason", table: "ActionItems");
            migrationBuilder.DropColumn(name: "SupersededByTranscriptHash", table: "ActionItems");
            migrationBuilder.DropColumn(name: "SupersededAtUtc", table: "ActionItems");
            migrationBuilder.DropColumn(name: "SourceTranscriptGeneratedAtUtc", table: "ActionItems");
            migrationBuilder.DropColumn(name: "SourceTranscriptRevision", table: "ActionItems");
            migrationBuilder.DropColumn(name: "SourceTranscriptHash", table: "ActionItems");
            migrationBuilder.DropColumn(name: "SourceTranscriptId", table: "ActionItems");

            migrationBuilder.DropColumn(name: "SourceTranscriptGeneratedAtUtc", table: "KnowledgeChunks");
            migrationBuilder.DropColumn(name: "SourceTranscriptRevision", table: "KnowledgeChunks");
            migrationBuilder.DropColumn(name: "SourceTranscriptHash", table: "KnowledgeChunks");
            migrationBuilder.DropColumn(name: "SourceTranscriptId", table: "KnowledgeChunks");

            migrationBuilder.DropColumn(name: "SourceTranscriptGeneratedAtUtc", table: "KnowledgeDocuments");
            migrationBuilder.DropColumn(name: "SourceTranscriptRevision", table: "KnowledgeDocuments");
            migrationBuilder.DropColumn(name: "SourceTranscriptHash", table: "KnowledgeDocuments");
            migrationBuilder.DropColumn(name: "SourceTranscriptId", table: "KnowledgeDocuments");

            migrationBuilder.DropColumn(name: "TranscriptRevision", table: "MeetingTranscripts");
            migrationBuilder.DropColumn(name: "TranscriptHash", table: "MeetingTranscripts");

            migrationBuilder.DropColumn(name: "SourceTranscriptGeneratedAtUtc", table: "MeetingSummaries");
            migrationBuilder.DropColumn(name: "SourceTranscriptRevision", table: "MeetingSummaries");
            migrationBuilder.DropColumn(name: "SourceTranscriptHash", table: "MeetingSummaries");
            migrationBuilder.DropColumn(name: "SourceTranscriptId", table: "MeetingSummaries");

            migrationBuilder.DropColumn(name: "SourceTranscriptGeneratedAtUtc", table: "PersonalizedMeetingSummaries");
            migrationBuilder.DropColumn(name: "SourceTranscriptRevision", table: "PersonalizedMeetingSummaries");
            migrationBuilder.DropColumn(name: "SourceTranscriptHash", table: "PersonalizedMeetingSummaries");
            migrationBuilder.DropColumn(name: "SourceTranscriptId", table: "PersonalizedMeetingSummaries");
        }
    }
}
