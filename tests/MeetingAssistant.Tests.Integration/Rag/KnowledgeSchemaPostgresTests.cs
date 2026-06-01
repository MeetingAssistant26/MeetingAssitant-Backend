using FluentAssertions;
using MediatR;
using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Features.Rag.Services;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using System.Data;
using Xunit;

namespace MeetingAssistant.Tests.Integration.Rag;

public sealed class KnowledgeSchemaPostgresTests
{
    private const string ConnectionEnv = "POSTGRES_SCHEMA_TEST_CONNECTION";
    private const string EmbeddingModel = "deterministic-test-v1";

    [Fact]
    public async Task Migration_creates_pgvector_knowledge_tables_and_indexes_in_postgres()
    {
        var connectionString = GetConnectionStringOrSkip();
        if (connectionString is null)
        {
            return;
        }

        await using var db = CreateDbContext(connectionString);
        await db.Database.MigrateAsync();

        var extension = await ScalarAsync<string>(db, "SELECT extname FROM pg_extension WHERE extname = 'vector';");
        extension.Should().Be("vector");

        var knowledgeDocuments = await ScalarAsync<string>(db, "SELECT to_regclass('\"KnowledgeDocuments\"')::text;");
        var knowledgeChunks = await ScalarAsync<string>(db, "SELECT to_regclass('\"KnowledgeChunks\"')::text;");
        var knowledgeChunkTags = await ScalarAsync<string>(db, "SELECT to_regclass('\"KnowledgeChunkTags\"')::text;");

        knowledgeDocuments.Should().Be("\"KnowledgeDocuments\"");
        knowledgeChunks.Should().Be("\"KnowledgeChunks\"");
        knowledgeChunkTags.Should().Be("\"KnowledgeChunkTags\"");

        var embeddingType = await ScalarAsync<string>(db, """
            SELECT format_type(a.atttypid, a.atttypmod)
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            WHERE c.relname = 'KnowledgeChunks' AND a.attname = 'Embedding';
            """);
        embeddingType.Should().Be("vector");

        var indexCount = await ScalarAsync<long>(db, """
            SELECT COUNT(*)
            FROM pg_indexes
            WHERE tablename IN ('KnowledgeDocuments', 'KnowledgeChunks', 'KnowledgeChunkTags')
                AND indexname IN (
                    'IX_KnowledgeDocuments_Org_Visibility_Current_Meeting',
                    'IX_KnowledgeChunks_Org_Visibility_Current_Model_Dim',
                    'IX_KnowledgeChunkTags_Org_Tag_Chunk',
                    'IX_KnowledgeChunks_Embedding_Cosine_1536_CurrentPublished'
                );
            """);
        indexCount.Should().Be(4);
    }

    [Fact]
    public async Task Retrieval_uses_only_same_org_published_current_chunks_and_boosts_shared_tags()
    {
        var connectionString = GetConnectionStringOrSkip();
        if (connectionString is null)
        {
            return;
        }

        await using var db = CreateDbContext(connectionString);
        await db.Database.MigrateAsync();

        var orgId = Guid.NewGuid();
        var otherOrgId = Guid.NewGuid();
        var tagId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            await ExecuteAsync(db, """
                INSERT INTO "Organizations" ("Id", "Name", "Slug", "CreatedAtUtc", "UpdatedAtUtc")
                VALUES (@orgId, 'RAG Org', @orgSlug, @now, @now),
                       (@otherOrgId, 'Other RAG Org', @otherOrgSlug, @now, @now);

                INSERT INTO "MeetingTags" ("Id", "OrganizationId", "Name", "Color", "IsActive", "CreatedAtUtc", "UpdatedAtUtc")
                VALUES (@tagId, @orgId, 'Roadmap', '#336699', true, @now, @now);
                """,
                ("orgId", orgId),
                ("orgSlug", $"rag-org-{orgId:N}"),
                ("otherOrgId", otherOrgId),
                ("otherOrgSlug", $"rag-other-{otherOrgId:N}"),
                ("tagId", tagId),
                ("now", now));

            var taggedDocumentId = await InsertDocumentAndChunkAsync(
                db,
                orgId,
                "Tagged roadmap summary",
                "Tagged chunk should win because it shares the current meeting tag.",
                "[0.99,0.01,0]",
                isCurrent: true,
                visibility: 1,
                tagId: tagId,
                now: now);

            await InsertDocumentAndChunkAsync(
                db,
                orgId,
                "Closer but untagged summary",
                "Untagged chunk is closer but has no shared tag.",
                "[1,0,0]",
                isCurrent: true,
                visibility: 1,
                tagId: null,
                now: now);

            await InsertDocumentAndChunkAsync(
                db,
                orgId,
                "Draft summary",
                "Draft chunks must not be returned.",
                "[1,0,0]",
                isCurrent: true,
                visibility: 0,
                tagId: null,
                now: now);

            await InsertDocumentAndChunkAsync(
                db,
                otherOrgId,
                "Other org summary",
                "Other organization chunks must not leak.",
                "[1,0,0]",
                isCurrent: true,
                visibility: 1,
                tagId: null,
                now: now);

            var service = new KnowledgeRetrievalService(db);
            var results = await service.RetrieveAsync(
                new KnowledgeRetrievalRequest(
                    orgId,
                    [1f, 0f, 0f],
                    TopK: 10,
                    PreferredTagIds: [tagId],
                    EmbeddingModel: EmbeddingModel,
                    EmbeddingDimension: 3),
                CancellationToken.None);

            results.Should().HaveCount(2);
            results[0].DocumentId.Should().Be(taggedDocumentId);
            results[0].SharedTagCount.Should().Be(1);
            results.Should().OnlyContain(result => result.Text.Contains("must not", StringComparison.OrdinalIgnoreCase) == false);
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private static async Task<Guid> InsertDocumentAndChunkAsync(
        ApplicationDbContext db,
        Guid orgId,
        string title,
        string text,
        string embedding,
        bool isCurrent,
        int visibility,
        Guid? tagId,
        DateTime now)
    {
        var documentId = Guid.NewGuid();
        var chunkId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var generationId = Guid.NewGuid();

        await ExecuteAsync(db, """
            INSERT INTO "KnowledgeDocuments"
                ("Id", "OrganizationId", "MeetingId", "ArtifactType", "ArtifactId", "ArtifactVersion", "Title",
                 "ContentHash", "IndexGenerationId", "Visibility", "IsCurrent", "EmbeddingProvider", "EmbeddingModel",
                 "EmbeddingDimension", "MetadataJson", "GeneratedAtUtc", "CreatedAtUtc", "UpdatedAtUtc")
            VALUES
                (@documentId, @orgId, NULL, 1, @artifactId, 1, @title, @documentHash, @generationId, @visibility,
                 @isCurrent, 'deterministic-test', @embeddingModel, 3, '{}'::jsonb, @now, @now, @now);

            INSERT INTO "KnowledgeChunks"
                ("Id", "OrganizationId", "DocumentId", "MeetingId", "ArtifactType", "ArtifactId", "ArtifactVersion",
                 "ChunkIndex", "Text", "CharacterCount", "TokenCount", "ContentHash", "EmbeddingProvider", "EmbeddingModel",
                 "EmbeddingDimension", "IndexGenerationId", "Visibility", "IsCurrent", "MetadataJson", "GeneratedAtUtc",
                 "Embedding", "CreatedAtUtc", "UpdatedAtUtc")
            VALUES
                (@chunkId, @orgId, @documentId, NULL, 1, @artifactId, 1, 0, @text, @characterCount, NULL,
                 @chunkHash, 'deterministic-test', @embeddingModel, 3, @generationId, @visibility, @isCurrent,
                 '{}'::jsonb, @now, CAST(@embedding AS vector), @now, @now);
            """,
            ("documentId", documentId),
            ("chunkId", chunkId),
            ("orgId", orgId),
            ("artifactId", artifactId),
            ("title", title),
            ("text", text),
            ("characterCount", text.Length),
            ("documentHash", $"doc-{documentId:N}"),
            ("chunkHash", $"chunk-{chunkId:N}"),
            ("generationId", generationId),
            ("visibility", visibility),
            ("isCurrent", isCurrent),
            ("embeddingModel", EmbeddingModel),
            ("embedding", embedding),
            ("now", now));

        if (tagId.HasValue)
        {
            await ExecuteAsync(db, """
                INSERT INTO "KnowledgeChunkTags"
                    ("Id", "OrganizationId", "KnowledgeChunkId", "KnowledgeDocumentId", "MeetingTagId",
                     "TagNameSnapshot", "TagColorSnapshot", "CreatedAtUtc", "UpdatedAtUtc")
                VALUES (@id, @orgId, @chunkId, @documentId, @tagId, 'Roadmap', '#336699', @now, @now);
                """,
                ("id", Guid.NewGuid()),
                ("orgId", orgId),
                ("chunkId", chunkId),
                ("documentId", documentId),
                ("tagId", tagId.Value),
                ("now", now));
        }

        return documentId;
    }

    private static string? GetConnectionStringOrSkip() => Environment.GetEnvironmentVariable(ConnectionEnv);

    private static ApplicationDbContext CreateDbContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new ApplicationDbContext(
            options,
            new HttpContextAccessor(),
            new StaticTenantProvider(),
            new NoopPublisher());
    }

    private static async Task<T> ScalarAsync<T>(ApplicationDbContext db, string sql)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State == ConnectionState.Closed)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        value.Should().NotBeNull();
        return (T)Convert.ChangeType(value, typeof(T));
    }

    private static async Task ExecuteAsync(ApplicationDbContext db, string sql, params (string Name, object Value)[] parameters)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State == ConnectionState.Closed)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync();
    }

    private sealed class StaticTenantProvider : ITenantProvider
    {
        public Guid? CurrentOrganizationId => null;
    }

    private sealed class NoopPublisher : IPublisher
    {
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }
}
