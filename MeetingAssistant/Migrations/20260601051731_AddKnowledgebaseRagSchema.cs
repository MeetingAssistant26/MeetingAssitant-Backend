using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingAssistant.Migrations
{
    /// <inheritdoc />
    public partial class AddKnowledgebaseRagSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS vector;");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "KnowledgeDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    MeetingId = table.Column<Guid>(type: "uuid", nullable: true),
                    ArtifactType = table.Column<int>(type: "integer", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactVersion = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IndexGenerationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Visibility = table.Column<int>(type: "integer", nullable: false),
                    IsCurrent = table.Column<bool>(type: "boolean", nullable: false),
                    EmbeddingProvider = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    EmbeddingModel = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    EmbeddingDimension = table.Column<int>(type: "integer", nullable: false),
                    MetadataJson = table.Column<string>(type: "jsonb", nullable: false),
                    GeneratedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KnowledgeDocuments_Meetings_MeetingId",
                        column: x => x.MeetingId,
                        principalTable: "Meetings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeChunks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    MeetingId = table.Column<Guid>(type: "uuid", nullable: true),
                    ArtifactType = table.Column<int>(type: "integer", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactVersion = table.Column<int>(type: "integer", nullable: false),
                    ChunkIndex = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    CharacterCount = table.Column<int>(type: "integer", nullable: false),
                    TokenCount = table.Column<int>(type: "integer", nullable: true),
                    ContentHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    EmbeddingProvider = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    EmbeddingModel = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    EmbeddingDimension = table.Column<int>(type: "integer", nullable: false),
                    IndexGenerationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Visibility = table.Column<int>(type: "integer", nullable: false),
                    IsCurrent = table.Column<bool>(type: "boolean", nullable: false),
                    MetadataJson = table.Column<string>(type: "jsonb", nullable: false),
                    GeneratedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Embedding = table.Column<string>(type: "vector", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeChunks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KnowledgeChunks_KnowledgeDocuments_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "KnowledgeDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_KnowledgeChunks_Meetings_MeetingId",
                        column: x => x.MeetingId,
                        principalTable: "Meetings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeChunkTags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeChunkId = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeDocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    MeetingTagId = table.Column<Guid>(type: "uuid", nullable: false),
                    TagNameSnapshot = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    TagColorSnapshot = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeChunkTags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KnowledgeChunkTags_KnowledgeChunks_KnowledgeChunkId",
                        column: x => x.KnowledgeChunkId,
                        principalTable: "KnowledgeChunks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_KnowledgeChunkTags_KnowledgeDocuments_KnowledgeDocumentId",
                        column: x => x.KnowledgeDocumentId,
                        principalTable: "KnowledgeDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_KnowledgeChunkTags_MeetingTags_MeetingTagId",
                        column: x => x.MeetingTagId,
                        principalTable: "MeetingTags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeChunks_MeetingId",
                table: "KnowledgeChunks",
                column: "MeetingId");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeChunks_Org_Artifact",
                table: "KnowledgeChunks",
                columns: new[] { "OrganizationId", "ArtifactType", "ArtifactId", "ArtifactVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeChunks_Org_ContentHash",
                table: "KnowledgeChunks",
                columns: new[] { "OrganizationId", "ContentHash" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeChunks_Org_IndexGeneration",
                table: "KnowledgeChunks",
                columns: new[] { "OrganizationId", "IndexGenerationId" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeChunks_Org_Meeting",
                table: "KnowledgeChunks",
                columns: new[] { "OrganizationId", "MeetingId" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeChunks_Org_Visibility_Current_Model_Dim",
                table: "KnowledgeChunks",
                columns: new[] { "OrganizationId", "Visibility", "IsCurrent", "EmbeddingModel", "EmbeddingDimension" });

            migrationBuilder.CreateIndex(
                name: "UX_KnowledgeChunks_Document_ChunkIndex",
                table: "KnowledgeChunks",
                columns: new[] { "DocumentId", "ChunkIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeChunkTags_KnowledgeDocumentId",
                table: "KnowledgeChunkTags",
                column: "KnowledgeDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeChunkTags_MeetingTagId",
                table: "KnowledgeChunkTags",
                column: "MeetingTagId");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeChunkTags_Org_Document",
                table: "KnowledgeChunkTags",
                columns: new[] { "OrganizationId", "KnowledgeDocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeChunkTags_Org_Tag_Chunk",
                table: "KnowledgeChunkTags",
                columns: new[] { "OrganizationId", "MeetingTagId", "KnowledgeChunkId" });

            migrationBuilder.CreateIndex(
                name: "UX_KnowledgeChunkTags_Chunk_Tag",
                table: "KnowledgeChunkTags",
                columns: new[] { "KnowledgeChunkId", "MeetingTagId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeDocuments_MeetingId",
                table: "KnowledgeDocuments",
                column: "MeetingId");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeDocuments_Org_Artifact",
                table: "KnowledgeDocuments",
                columns: new[] { "OrganizationId", "ArtifactType", "ArtifactId", "ArtifactVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeDocuments_Org_ContentHash",
                table: "KnowledgeDocuments",
                columns: new[] { "OrganizationId", "ContentHash" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeDocuments_Org_IndexGeneration",
                table: "KnowledgeDocuments",
                columns: new[] { "OrganizationId", "IndexGenerationId" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeDocuments_Org_Visibility_Current_Meeting",
                table: "KnowledgeDocuments",
                columns: new[] { "OrganizationId", "Visibility", "IsCurrent", "MeetingId" });

            migrationBuilder.CreateIndex(
                name: "UX_KnowledgeDocuments_Org_Artifact_Generation",
                table: "KnowledgeDocuments",
                columns: new[] { "OrganizationId", "ArtifactType", "ArtifactId", "ArtifactVersion", "IndexGenerationId" },
                unique: true);

            migrationBuilder.Sql("""
                CREATE INDEX "IX_KnowledgeChunks_Embedding_Cosine_1536_CurrentPublished"
                ON "KnowledgeChunks"
                USING hnsw (("Embedding"::vector(1536)) vector_cosine_ops)
                WHERE "Visibility" = 1 AND "IsCurrent" = true AND "EmbeddingDimension" = 1536;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_KnowledgeChunks_Embedding_Cosine_1536_CurrentPublished\";");

            migrationBuilder.DropTable(
                name: "KnowledgeChunkTags");

            migrationBuilder.DropTable(
                name: "KnowledgeChunks");

            migrationBuilder.DropTable(
                name: "KnowledgeDocuments");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");
        }
    }
}
