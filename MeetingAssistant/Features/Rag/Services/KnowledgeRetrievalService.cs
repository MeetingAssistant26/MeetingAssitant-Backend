using System.Data;
using System.Text.Json;
using MeetingAssistant.Features.Rag.Models;
using MeetingAssistant.Infrastructure.AI;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;

namespace MeetingAssistant.Features.Rag.Services
{
    public sealed class KnowledgeRetrievalService(
        ApplicationDbContext dbContext,
        IEmbeddingService embeddingService) : IKnowledgeRetrievalService
    {
        private const int MinTopK = 1;
        private const int MaxTopK = 20;
        private const double SharedTagBoost = 0.05d;
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly ApplicationDbContext _dbContext = dbContext;
        private readonly IEmbeddingService _embeddingService = embeddingService;

        public async Task<IReadOnlyList<KnowledgeRetrievalResult>> RetrieveAsync(
            KnowledgeRetrievalRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (request.OrganizationId == Guid.Empty)
            {
                throw new ArgumentException("OrganizationId is required.", nameof(request));
            }

            if (string.IsNullOrWhiteSpace(request.QueryText))
            {
                throw new ArgumentException("QueryText is required.", nameof(request));
            }

            var queryEmbedding = await _embeddingService.EmbedAsync(request.QueryText, cancellationToken);
            var dimension = request.EmbeddingDimension ?? _embeddingService.Metadata.Dimension;
            if (dimension <= 0 || dimension != queryEmbedding.Length)
            {
                throw new ArgumentException("EmbeddingDimension must match the generated query embedding length.", nameof(request));
            }

            var topK = Math.Clamp(request.TopK, MinTopK, MaxTopK);
            var vectorLiteral = KnowledgeVectorSql.ToVectorLiteral(queryEmbedding);
            var embeddingModel = string.IsNullOrWhiteSpace(request.EmbeddingModel)
                ? _embeddingService.Metadata.Model
                : request.EmbeddingModel;
            var preferredTagIds = await ResolvePreferredTagIdsAsync(request, cancellationToken);

            var connection = _dbContext.Database.GetDbConnection();
            var shouldClose = connection.State == ConnectionState.Closed;
            if (shouldClose)
            {
                await connection.OpenAsync(cancellationToken);
            }

            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    WITH query AS (
                        SELECT CAST(@queryEmbedding AS vector) AS embedding
                    ), base AS MATERIALIZED (
                        SELECT
                            c."Id",
                            c."DocumentId",
                            c."MeetingId",
                            c."ArtifactType",
                            c."Text",
                            c."MetadataJson",
                            c."GeneratedAtUtc",
                            d."Title" AS document_title,
                            m."Title" AS meeting_title,
                            m."ScheduledStartUtc" AS meeting_scheduled_start_utc,
                            (c."Embedding" <=> (SELECT embedding FROM query))::double precision AS distance
                        FROM "KnowledgeChunks" c
                        INNER JOIN "KnowledgeDocuments" d ON d."Id" = c."DocumentId"
                        LEFT JOIN "Meetings" m ON m."Id" = c."MeetingId" AND m."OrganizationId" = @organizationId
                        WHERE c."OrganizationId" = @organizationId
                            AND d."OrganizationId" = @organizationId
                            AND c."Visibility" = @publishedVisibility
                            AND d."Visibility" = @publishedVisibility
                            AND c."IsCurrent" = true
                            AND d."IsCurrent" = true
                            AND c."EmbeddingDimension" = @embeddingDimension
                            AND c."EmbeddingModel" = @embeddingModel
                    ), chunk_tags AS (
                        SELECT
                            kct."KnowledgeChunkId",
                            COUNT(*) FILTER (
                                WHERE @hasPreferredTags = true
                                    AND kct."MeetingTagId" = ANY(CAST(@preferredTagIds AS uuid[]))
                            )::integer AS shared_tag_count,
                            jsonb_agg(
                                jsonb_build_object(
                                    'id', kct."MeetingTagId",
                                    'name', kct."TagNameSnapshot",
                                    'color', kct."TagColorSnapshot"
                                )
                                ORDER BY kct."TagNameSnapshot", kct."MeetingTagId"
                            ) AS tags_json
                        FROM "KnowledgeChunkTags" kct
                        WHERE kct."OrganizationId" = @organizationId
                        GROUP BY kct."KnowledgeChunkId"
                    ), scored AS (
                        SELECT
                            base.*,
                            COALESCE(chunk_tags.shared_tag_count, 0) AS shared_tag_count,
                            COALESCE(chunk_tags.tags_json, '[]'::jsonb) AS tags_json,
                            (COALESCE(chunk_tags.shared_tag_count, 0)::double precision * @sharedTagBoost) AS tag_boost,
                            (base.distance - (COALESCE(chunk_tags.shared_tag_count, 0)::double precision * @sharedTagBoost)) AS ranking_score
                        FROM base
                        LEFT JOIN chunk_tags ON chunk_tags."KnowledgeChunkId" = base."Id"
                    ), preferred AS (
                        SELECT scored.*, true AS tag_preferred_result, 0 AS retrieval_pass
                        FROM scored
                        WHERE @hasPreferredTags = true
                            AND scored.shared_tag_count > 0
                        ORDER BY scored.ranking_score ASC, scored.distance ASC, scored."GeneratedAtUtc" DESC, scored."Id" ASC
                        LIMIT @topK
                    ), fallback AS (
                        SELECT scored.*, false AS tag_preferred_result, 1 AS retrieval_pass
                        FROM scored
                        WHERE NOT EXISTS (
                                SELECT 1
                                FROM preferred
                                WHERE preferred."Id" = scored."Id"
                            )
                            AND (SELECT COUNT(*) FROM preferred) < @topK
                        ORDER BY scored.distance ASC, scored."GeneratedAtUtc" DESC, scored."Id" ASC
                        LIMIT GREATEST(@topK - (SELECT COUNT(*) FROM preferred), 0)
                    )
                    SELECT
                        "Id",
                        "DocumentId",
                        "MeetingId",
                        meeting_title,
                        meeting_scheduled_start_utc,
                        "ArtifactType",
                        "Text",
                        document_title,
                        "MetadataJson"::text,
                        tags_json::text,
                        distance,
                        shared_tag_count,
                        tag_boost,
                        ranking_score,
                        tag_preferred_result
                    FROM preferred
                    UNION ALL
                    SELECT
                        "Id",
                        "DocumentId",
                        "MeetingId",
                        meeting_title,
                        meeting_scheduled_start_utc,
                        "ArtifactType",
                        "Text",
                        document_title,
                        "MetadataJson"::text,
                        tags_json::text,
                        distance,
                        shared_tag_count,
                        tag_boost,
                        ranking_score,
                        tag_preferred_result
                    FROM fallback
                    ORDER BY tag_preferred_result DESC, ranking_score ASC, distance ASC, "Id" ASC;
                    """;

                AddParameter(command, "organizationId", request.OrganizationId);
                AddParameter(command, "queryEmbedding", vectorLiteral);
                AddParameter(command, "publishedVisibility", (int)KnowledgeVisibility.Published);
                AddParameter(command, "embeddingDimension", dimension);
                AddParameter(command, "embeddingModel", embeddingModel);
                AddParameter(command, "preferredTagIds", preferredTagIds);
                AddParameter(command, "hasPreferredTags", preferredTagIds.Length > 0);
                AddParameter(command, "sharedTagBoost", SharedTagBoost);
                AddParameter(command, "topK", topK);

                var results = new List<KnowledgeRetrievalResult>();
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    results.Add(new KnowledgeRetrievalResult(
                        reader.GetGuid(0),
                        reader.GetGuid(1),
                        reader.IsDBNull(2) ? null : reader.GetGuid(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetDateTime(4),
                        (KnowledgeArtifactType)reader.GetInt32(5),
                        reader.GetString(6),
                        reader.GetString(7),
                        reader.GetString(8),
                        DeserializeTags(reader.GetString(9)),
                        reader.GetDouble(10),
                        reader.GetInt32(11),
                        reader.GetDouble(12),
                        reader.GetDouble(13),
                        reader.GetBoolean(14)));
                }

                return results;
            }
            finally
            {
                if (shouldClose)
                {
                    await connection.CloseAsync();
                }
            }
        }

        private async Task<Guid[]> ResolvePreferredTagIdsAsync(
            KnowledgeRetrievalRequest request,
            CancellationToken cancellationToken)
        {
            var tagIds = new HashSet<Guid>();

            if (request.CurrentMeetingId.HasValue && request.CurrentMeetingId.Value != Guid.Empty)
            {
                var currentMeetingTagIds = await _dbContext.MeetingMeetingTags
                    .IgnoreQueryFilters()
                    .Where(x => x.MeetingId == request.CurrentMeetingId.Value
                                && x.Meeting.OrganizationId == request.OrganizationId
                                && x.MeetingTag.OrganizationId == request.OrganizationId)
                    .Select(x => x.MeetingTagId)
                    .ToListAsync(cancellationToken);

                foreach (var tagId in currentMeetingTagIds)
                {
                    tagIds.Add(tagId);
                }
            }

            var requestedTagIds = request.PreferredTagIds?
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToArray() ?? Array.Empty<Guid>();

            if (requestedTagIds.Length > 0)
            {
                var validRequestedTagIds = await _dbContext.MeetingTags
                    .IgnoreQueryFilters()
                    .Where(x => x.OrganizationId == request.OrganizationId && requestedTagIds.Contains(x.Id))
                    .Select(x => x.Id)
                    .ToListAsync(cancellationToken);

                foreach (var tagId in validRequestedTagIds)
                {
                    tagIds.Add(tagId);
                }
            }

            return tagIds.OrderBy(id => id).ToArray();
        }

        private static IReadOnlyList<KnowledgeRetrievalTagResult> DeserializeTags(string tagsJson) =>
            JsonSerializer.Deserialize<List<KnowledgeRetrievalTagResult>>(tagsJson, JsonOptions)
            ?? [];

        private static void AddParameter(IDbCommand command, string name, object? value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
    }
}
