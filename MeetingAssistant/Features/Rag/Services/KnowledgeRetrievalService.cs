using System.Data;
using MeetingAssistant.Features.Rag.Models;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;

namespace MeetingAssistant.Features.Rag.Services
{
    public sealed class KnowledgeRetrievalService(ApplicationDbContext dbContext) : IKnowledgeRetrievalService
    {
        private const int MinTopK = 1;
        private const int MaxTopK = 20;
        private const double SharedTagBoost = 0.05d;
        private readonly ApplicationDbContext _dbContext = dbContext;

        public async Task<IReadOnlyList<KnowledgeRetrievalResult>> RetrieveAsync(
            KnowledgeRetrievalRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            var dimension = request.EmbeddingDimension ?? request.QueryEmbedding.Count;
            if (dimension <= 0 || dimension != request.QueryEmbedding.Count)
            {
                throw new ArgumentException("EmbeddingDimension must match the query embedding length.", nameof(request));
            }

            var topK = Math.Clamp(request.TopK, MinTopK, MaxTopK);
            var vectorLiteral = KnowledgeVectorSql.ToVectorLiteral(request.QueryEmbedding);
            var preferredTagIds = request.PreferredTagIds?.Where(id => id != Guid.Empty).Distinct().ToArray()
                ?? Array.Empty<Guid>();

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
                    ), tag_matches AS (
                        SELECT
                            "KnowledgeChunkId",
                            COUNT(*)::integer AS shared_tag_count
                        FROM "KnowledgeChunkTags"
                        WHERE "OrganizationId" = @organizationId
                            AND @hasPreferredTags = true
                            AND "MeetingTagId" = ANY(CAST(@preferredTagIds AS uuid[]))
                        GROUP BY "KnowledgeChunkId"
                    )
                    SELECT
                        c."Id",
                        c."DocumentId",
                        c."MeetingId",
                        c."ArtifactType",
                        c."Text",
                        c."MetadataJson",
                        d."Title",
                        (c."Embedding" <=> (SELECT embedding FROM query))::double precision AS distance,
                        COALESCE(tm.shared_tag_count, 0) AS shared_tag_count,
                        ((c."Embedding" <=> (SELECT embedding FROM query))::double precision
                            - (COALESCE(tm.shared_tag_count, 0)::double precision * @sharedTagBoost)) AS ranking_score
                    FROM "KnowledgeChunks" c
                    INNER JOIN "KnowledgeDocuments" d ON d."Id" = c."DocumentId"
                    LEFT JOIN tag_matches tm ON tm."KnowledgeChunkId" = c."Id"
                    WHERE c."OrganizationId" = @organizationId
                        AND d."OrganizationId" = @organizationId
                        AND c."Visibility" = @publishedVisibility
                        AND d."Visibility" = @publishedVisibility
                        AND c."IsCurrent" = true
                        AND d."IsCurrent" = true
                        AND c."EmbeddingDimension" = @embeddingDimension
                        AND (CAST(@embeddingModel AS text) IS NULL OR c."EmbeddingModel" = CAST(@embeddingModel AS text))
                        AND (CAST(@currentMeetingId AS uuid) IS NULL OR c."MeetingId" IS NULL OR c."MeetingId" <> CAST(@currentMeetingId AS uuid))
                    ORDER BY ranking_score ASC, distance ASC, c."GeneratedAtUtc" DESC, c."Id" ASC
                    LIMIT @topK;
                    """;

                AddParameter(command, "organizationId", request.OrganizationId);
                AddParameter(command, "queryEmbedding", vectorLiteral);
                AddParameter(command, "publishedVisibility", (int)KnowledgeVisibility.Published);
                AddParameter(command, "embeddingDimension", dimension);
                AddParameter(command, "embeddingModel", string.IsNullOrWhiteSpace(request.EmbeddingModel) ? DBNull.Value : request.EmbeddingModel);
                AddParameter(command, "currentMeetingId", request.CurrentMeetingId.HasValue ? request.CurrentMeetingId.Value : DBNull.Value);
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
                        (KnowledgeArtifactType)reader.GetInt32(3),
                        reader.GetString(4),
                        reader.GetString(6),
                        reader.GetString(5),
                        reader.GetDouble(7),
                        reader.GetInt32(8),
                        reader.GetDouble(9)));
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

        private static void AddParameter(IDbCommand command, string name, object? value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
    }
}
