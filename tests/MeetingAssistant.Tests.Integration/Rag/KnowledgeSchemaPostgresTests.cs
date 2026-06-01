using FluentAssertions;
using MediatR;
using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Features.Rag.Services;
using MeetingAssistant.Infrastructure.AI;
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
    public async Task Retrieval_embeds_query_filters_org_and_uses_only_current_published_chunks()
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
        var otherOrgTagId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            await ExecuteAsync(db, """
                INSERT INTO "Organizations" ("Id", "Name", "Slug", "CreatedAtUtc", "UpdatedAtUtc")
                VALUES (@orgId, 'RAG Org', @orgSlug, @now, @now),
                       (@otherOrgId, 'Other RAG Org', @otherOrgSlug, @now, @now);

                INSERT INTO "MeetingTags" ("Id", "OrganizationId", "Name", "Color", "IsActive", "CreatedAtUtc", "UpdatedAtUtc")
                VALUES (@tagId, @orgId, 'Roadmap', '#336699', true, @now, @now),
                       (@otherOrgTagId, @otherOrgId, 'Roadmap', '#993366', true, @now, @now);
                """,
                ("orgId", orgId),
                ("orgSlug", $"rag-org-{orgId:N}"),
                ("otherOrgId", otherOrgId),
                ("otherOrgSlug", $"rag-other-{otherOrgId:N}"),
                ("tagId", tagId),
                ("otherOrgTagId", otherOrgTagId),
                ("now", now));

            var currentMeetingId = await InsertMeetingAsync(db, orgId, "Current planning", now, tagId);
            var taggedMeetingId = await InsertMeetingAsync(db, orgId, "Roadmap source", now.AddDays(-3), tagId);
            var untaggedMeetingId = await InsertMeetingAsync(db, orgId, "Untagged source", now.AddDays(-2));
            var draftMeetingId = await InsertMeetingAsync(db, orgId, "Draft source", now.AddDays(-1));
            var otherOrgMeetingId = await InsertMeetingAsync(db, otherOrgId, "Other org source", now.AddDays(-4), otherOrgTagId);

            var taggedDocumentId = await InsertDocumentAndChunkAsync(
                db,
                orgId,
                taggedMeetingId,
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
                untaggedMeetingId,
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
                draftMeetingId,
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
                otherOrgMeetingId,
                "Other org summary",
                "Other organization chunks must not leak.",
                "[1,0,0]",
                isCurrent: true,
                visibility: 1,
                tagId: otherOrgTagId,
                now: now);

            var embeddingService = new FixedEmbeddingService([1f, 0f, 0f]);
            var service = new KnowledgeRetrievalService(db, embeddingService);
            var results = await service.RetrieveAsync(
                new KnowledgeRetrievalRequest(
                    orgId,
                    "roadmap launch context",
                    TopK: 10,
                    CurrentMeetingId: currentMeetingId,
                    EmbeddingModel: EmbeddingModel,
                    EmbeddingDimension: 3),
                CancellationToken.None);

            embeddingService.LastText.Should().Be("roadmap launch context");
            results.Should().HaveCount(2);
            results[0].DocumentId.Should().Be(taggedDocumentId);
            results[0].SourceMeetingId.Should().Be(taggedMeetingId);
            results[0].SourceMeetingTitle.Should().Be("Roadmap source");
            results[0].SourceMeetingScheduledStartUtc.Should().BeCloseTo(now.AddDays(-3), TimeSpan.FromSeconds(1));
            results[0].Tags.Should().ContainSingle(tag => tag.Id == tagId && tag.Name == "Roadmap" && tag.Color == "#336699");
            results[0].SharedTagCount.Should().Be(1);
            results[0].IsTagPreferredResult.Should().BeTrue();
            results[0].TagBoost.Should().BeGreaterThan(0);
            results.Should().OnlyContain(result => result.Text.Contains("must not", StringComparison.OrdinalIgnoreCase) == false);
            results.Should().OnlyContain(result => result.SourceMeetingTitle != "Other org source");
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    [Fact]
    public async Task Retrieval_prefers_all_shared_tag_results_then_falls_back_to_broader_org_context()
    {
        var connectionString = GetConnectionStringOrSkip();
        if (connectionString is null)
        {
            return;
        }

        await using var db = CreateDbContext(connectionString);
        await db.Database.MigrateAsync();

        var orgId = Guid.NewGuid();
        var roadmapTagId = Guid.NewGuid();
        var hiringTagId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            await ExecuteAsync(db, """
                INSERT INTO "Organizations" ("Id", "Name", "Slug", "CreatedAtUtc", "UpdatedAtUtc")
                VALUES (@orgId, 'RAG Boost Org', @orgSlug, @now, @now);

                INSERT INTO "MeetingTags" ("Id", "OrganizationId", "Name", "Color", "IsActive", "CreatedAtUtc", "UpdatedAtUtc")
                VALUES (@roadmapTagId, @orgId, 'Roadmap', '#336699', true, @now, @now),
                       (@hiringTagId, @orgId, 'Hiring', '#663399', true, @now, @now);
                """,
                ("orgId", orgId),
                ("orgSlug", $"rag-boost-{orgId:N}"),
                ("roadmapTagId", roadmapTagId),
                ("hiringTagId", hiringTagId),
                ("now", now));

            var currentMeetingId = await InsertMeetingAsync(db, orgId, "Current roadmap", now, roadmapTagId);
            var pastRelatedMeetingId = await InsertMeetingAsync(db, orgId, "Past roadmap", now.AddDays(-7), roadmapTagId);
            var unrelatedMeetingId = await InsertMeetingAsync(db, orgId, "Unrelated hiring", now.AddDays(-6), hiringTagId);
            var broadMeetingId = await InsertMeetingAsync(db, orgId, "Broad source", now.AddDays(-5));

            var veryCloseUntagged = await InsertDocumentAndChunkAsync(
                db,
                orgId,
                broadMeetingId,
                "Very close untagged",
                "Untagged but nearly identical vector.",
                "[1,0,0]",
                isCurrent: true,
                visibility: 1,
                tagId: null,
                now: now);
            var currentRelated = await InsertDocumentAndChunkAsync(
                db,
                orgId,
                currentMeetingId,
                "Current related",
                "Current meeting context with confirmed roadmap tag.",
                "[0.96,0.04,0]",
                isCurrent: true,
                visibility: 1,
                tagId: roadmapTagId,
                now: now);
            var pastRelated = await InsertDocumentAndChunkAsync(
                db,
                orgId,
                pastRelatedMeetingId,
                "Past related",
                "Past meeting context with confirmed roadmap tag.",
                "[0.94,0.06,0]",
                isCurrent: true,
                visibility: 1,
                tagId: roadmapTagId,
                now: now);
            await InsertDocumentAndChunkAsync(
                db,
                orgId,
                unrelatedMeetingId,
                "Unrelated hiring",
                "Hiring context has a confirmed tag but not the current meeting tag.",
                "[0.93,0.07,0]",
                isCurrent: true,
                visibility: 1,
                tagId: hiringTagId,
                now: now);

            var service = new KnowledgeRetrievalService(db, new FixedEmbeddingService([1f, 0f, 0f]));
            var results = await service.RetrieveAsync(
                new KnowledgeRetrievalRequest(
                    orgId,
                    "roadmap",
                    TopK: 3,
                    CurrentMeetingId: currentMeetingId,
                    EmbeddingModel: EmbeddingModel,
                    EmbeddingDimension: 3),
                CancellationToken.None);

            results.Select(x => x.DocumentId).Should().Equal(currentRelated, pastRelated, veryCloseUntagged);
            results.Take(2).Should().OnlyContain(x => x.SharedTagCount == 1 && x.IsTagPreferredResult);
            results[2].SharedTagCount.Should().Be(0);
            results[2].IsTagPreferredResult.Should().BeFalse("broader org fallback should fill remaining topK slots only after shared-tag results");
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    [Fact]
    public async Task Retrieval_falls_back_to_broader_org_context_when_no_chunks_share_current_meeting_tags()
    {
        var connectionString = GetConnectionStringOrSkip();
        if (connectionString is null)
        {
            return;
        }

        await using var db = CreateDbContext(connectionString);
        await db.Database.MigrateAsync();

        var orgId = Guid.NewGuid();
        var roadmapTagId = Guid.NewGuid();
        var hiringTagId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            await ExecuteAsync(db, """
                INSERT INTO "Organizations" ("Id", "Name", "Slug", "CreatedAtUtc", "UpdatedAtUtc")
                VALUES (@orgId, 'RAG Fallback Org', @orgSlug, @now, @now);

                INSERT INTO "MeetingTags" ("Id", "OrganizationId", "Name", "Color", "IsActive", "CreatedAtUtc", "UpdatedAtUtc")
                VALUES (@roadmapTagId, @orgId, 'Roadmap', '#336699', true, @now, @now),
                       (@hiringTagId, @orgId, 'Hiring', '#663399', true, @now, @now);
                """,
                ("orgId", orgId),
                ("orgSlug", $"rag-fallback-{orgId:N}"),
                ("roadmapTagId", roadmapTagId),
                ("hiringTagId", hiringTagId),
                ("now", now));

            var currentMeetingId = await InsertMeetingAsync(db, orgId, "Current roadmap", now, roadmapTagId);
            var hiringMeetingId = await InsertMeetingAsync(db, orgId, "Hiring source", now.AddDays(-2), hiringTagId);
            var generalMeetingId = await InsertMeetingAsync(db, orgId, "General source", now.AddDays(-1));

            var closest = await InsertDocumentAndChunkAsync(
                db,
                orgId,
                generalMeetingId,
                "General close",
                "General org context should be returned when no tags overlap.",
                "[1,0,0]",
                isCurrent: true,
                visibility: 1,
                tagId: null,
                now: now);
            var nextClosest = await InsertDocumentAndChunkAsync(
                db,
                orgId,
                hiringMeetingId,
                "Hiring close",
                "Different tag context can be used as fallback without shared tags.",
                "[0.98,0.02,0]",
                isCurrent: true,
                visibility: 1,
                tagId: hiringTagId,
                now: now);

            var service = new KnowledgeRetrievalService(db, new FixedEmbeddingService([1f, 0f, 0f]));
            var results = await service.RetrieveAsync(
                new KnowledgeRetrievalRequest(
                    orgId,
                    "roadmap",
                    TopK: 2,
                    CurrentMeetingId: currentMeetingId,
                    EmbeddingModel: EmbeddingModel,
                    EmbeddingDimension: 3),
                CancellationToken.None);

            results.Select(x => x.DocumentId).Should().Equal(closest, nextClosest);
            results.Should().OnlyContain(x => x.SharedTagCount == 0 && x.IsTagPreferredResult == false);
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private static async Task<Guid> InsertMeetingAsync(
        ApplicationDbContext db,
        Guid orgId,
        string title,
        DateTime scheduledStartUtc,
        Guid? tagId = null)
    {
        var meetingId = Guid.NewGuid();
        await ExecuteAsync(db, """
            INSERT INTO "Meetings"
                ("Id", "OrganizationId", "Title", "Description", "ScheduledStartUtc", "ScheduledEndUtc",
                 "Status", "CreatedAtUtc", "UpdatedAtUtc", "AiAssistantEnabled")
            VALUES
                (@meetingId, @orgId, @title, NULL, @scheduledStartUtc, @scheduledEndUtc, 2, @now, @now, true);
            """,
            ("meetingId", meetingId),
            ("orgId", orgId),
            ("title", title),
            ("scheduledStartUtc", scheduledStartUtc),
            ("scheduledEndUtc", scheduledStartUtc.AddHours(1)),
            ("now", DateTime.UtcNow));

        if (tagId.HasValue)
        {
            await ExecuteAsync(db, """
                INSERT INTO "MeetingMeetingTags" ("MeetingId", "MeetingTagId")
                VALUES (@meetingId, @tagId);
                """,
                ("meetingId", meetingId),
                ("tagId", tagId.Value));
        }

        return meetingId;
    }

    private static async Task<Guid> InsertDocumentAndChunkAsync(
        ApplicationDbContext db,
        Guid orgId,
        Guid? meetingId,
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
                (@documentId, @orgId, @meetingId, 1, @artifactId, 1, @title, @documentHash, @generationId, @visibility,
                 @isCurrent, 'deterministic-test', @embeddingModel, 3, '{}'::jsonb, @now, @now, @now);

            INSERT INTO "KnowledgeChunks"
                ("Id", "OrganizationId", "DocumentId", "MeetingId", "ArtifactType", "ArtifactId", "ArtifactVersion",
                 "ChunkIndex", "Text", "CharacterCount", "TokenCount", "ContentHash", "EmbeddingProvider", "EmbeddingModel",
                 "EmbeddingDimension", "IndexGenerationId", "Visibility", "IsCurrent", "MetadataJson", "GeneratedAtUtc",
                 "Embedding", "CreatedAtUtc", "UpdatedAtUtc")
            VALUES
                (@chunkId, @orgId, @documentId, @meetingId, 1, @artifactId, 1, 0, @text, @characterCount, NULL,
                 @chunkHash, 'deterministic-test', @embeddingModel, 3, @generationId, @visibility, @isCurrent,
                 '{}'::jsonb, @now, CAST(@embedding AS vector), @now, @now);
            """,
            ("documentId", documentId),
            ("chunkId", chunkId),
            ("orgId", orgId),
            ("meetingId", meetingId.HasValue ? meetingId.Value : DBNull.Value),
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

    private sealed class FixedEmbeddingService(float[] embedding) : IEmbeddingService
    {
        public string? LastText { get; private set; }

        public EmbeddingMetadata Metadata { get; } = new("deterministic-test", EmbeddingModel, 3);

        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken)
        {
            LastText = text;
            return Task.FromResult(embedding.ToArray());
        }

        public Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
            Task.FromResult(texts.Select(_ => embedding.ToArray()).ToArray());
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
