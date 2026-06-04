using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeetingAssistant.Features.ActionItems.Models.Entities;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Services;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Features.Rag.Models;
using MeetingAssistant.Infrastructure.AI;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;

namespace MeetingAssistant.Features.Rag.Services
{
    public sealed class ReindexMeetingKnowledgeService(
        ApplicationDbContext dbContext,
        IEmbeddingService embeddingService,
        ILogger<ReindexMeetingKnowledgeService> logger) : IReindexMeetingKnowledgeService
    {
        private const int MaxChunkCharacters = 2_000;
        private const int ChunkOverlapCharacters = 200;
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly ApplicationDbContext _dbContext = dbContext;
        private readonly IEmbeddingService _embeddingService = embeddingService;
        private readonly ILogger<ReindexMeetingKnowledgeService> _logger = logger;

        public async Task<ReindexMeetingKnowledgeResult> ReindexMeetingAsync(
            Guid organizationId,
            Guid meetingId,
            CancellationToken cancellationToken)
        {
            var meeting = await _dbContext.Meetings
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(x => x.OrganizationId == organizationId && x.Id == meetingId)
                .Select(x => new MeetingSnapshot(x.Id, x.OrganizationId, x.Title))
                .FirstOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException($"Meeting '{meetingId}' was not found for knowledge indexing.");

            var transcript = await _dbContext.MeetingTranscripts
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.MeetingId == meetingId, cancellationToken);

            if (transcript is null || string.IsNullOrWhiteSpace(transcript.FullText))
            {
                _logger.LogInformation(
                    "Knowledge reindex skipped because meeting transcript was unavailable. MeetingId={MeetingId} OrganizationId={OrganizationId}",
                    meetingId,
                    organizationId);

                return new ReindexMeetingKnowledgeResult(Guid.NewGuid(), 0, 0, Array.Empty<Guid>());
            }

            if (!MeetingTranscriptCompletenessGuard.IsCompleteForDownstream(transcript))
            {
                _logger.LogInformation(
                    "Knowledge reindex skipped because meeting transcript is incomplete. MeetingId={MeetingId} OrganizationId={OrganizationId} CompletenessStatus={CompletenessStatus}",
                    meetingId,
                    organizationId,
                    transcript.CompletenessStatus);

                return new ReindexMeetingKnowledgeResult(Guid.NewGuid(), 0, 0, Array.Empty<Guid>());
            }

            var confirmedTags = await LoadConfirmedTagsAsync(organizationId, meetingId, cancellationToken);
            var artifacts = await BuildArtifactsAsync(meeting, confirmedTags, cancellationToken);

            if (artifacts.Count == 0)
            {
                _logger.LogInformation(
                    "Knowledge reindex skipped because no finalized artifacts exist. MeetingId={MeetingId} OrganizationId={OrganizationId}",
                    meetingId,
                    organizationId);

                return new ReindexMeetingKnowledgeResult(Guid.NewGuid(), 0, 0, Array.Empty<Guid>());
            }

            var artifactsToPublish = await FilterChangedArtifactsAsync(
                organizationId,
                meetingId,
                artifacts,
                confirmedTags,
                cancellationToken);

            if (artifactsToPublish.Count == 0)
            {
                var currentDocumentIds = await _dbContext.KnowledgeDocuments
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(x => x.OrganizationId == organizationId
                                && x.MeetingId == meetingId
                                && x.Visibility == KnowledgeVisibility.Published
                                && x.IsCurrent)
                    .Select(x => x.Id)
                    .ToListAsync(cancellationToken);

                _logger.LogInformation(
                    "Knowledge reindex found no artifact changes. MeetingId={MeetingId} OrganizationId={OrganizationId}",
                    meetingId,
                    organizationId);

                return new ReindexMeetingKnowledgeResult(Guid.NewGuid(), 0, 0, currentDocumentIds);
            }

            var generationId = Guid.NewGuid();
            var generatedAtUtc = DateTime.UtcNow;
            var drafts = await CreateDraftDocumentsAsync(artifactsToPublish, confirmedTags, generationId, generatedAtUtc, cancellationToken);

            var executionStrategy = _dbContext.Database.CreateExecutionStrategy();
            await executionStrategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                try
                {
                    _dbContext.KnowledgeDocuments.AddRange(drafts);
                    await _dbContext.SaveChangesAsync(cancellationToken);

                    var replacementKeys = drafts
                        .Select(x => new ArtifactKey(x.ArtifactType, x.ArtifactId, x.ArtifactVersion))
                        .Distinct()
                        .ToArray();

                    await ArchivePreviousCurrentDocumentsAsync(
                        organizationId,
                        meetingId,
                        replacementKeys,
                        generationId,
                        cancellationToken);

                    foreach (var document in drafts)
                    {
                        document.Visibility = KnowledgeVisibility.Published;
                        document.IsCurrent = true;

                        foreach (var chunk in document.Chunks)
                        {
                            chunk.Visibility = KnowledgeVisibility.Published;
                            chunk.IsCurrent = true;
                        }
                    }

                    await _dbContext.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                }
                catch
                {
                    await transaction.RollbackAsync(cancellationToken);
                    throw;
                }
            });

            var documentIds = drafts.Select(x => x.Id).ToArray();
            var chunkCount = drafts.Sum(x => x.Chunks.Count);

            _logger.LogInformation(
                "Published meeting knowledge generation. MeetingId={MeetingId} OrganizationId={OrganizationId} GenerationId={GenerationId} Documents={DocumentCount} Chunks={ChunkCount}",
                meetingId,
                organizationId,
                generationId,
                drafts.Count,
                chunkCount);

            return new ReindexMeetingKnowledgeResult(generationId, drafts.Count, chunkCount, documentIds);
        }

        private async Task<IReadOnlyList<TagSnapshot>> LoadConfirmedTagsAsync(
            Guid organizationId,
            Guid meetingId,
            CancellationToken cancellationToken)
        {
            return await _dbContext.MeetingMeetingTags
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(x => x.MeetingId == meetingId && x.MeetingTag.OrganizationId == organizationId)
                .OrderBy(x => x.MeetingTag.Name)
                .Select(x => new TagSnapshot(x.MeetingTagId, x.MeetingTag.Name, x.MeetingTag.Color))
                .ToListAsync(cancellationToken);
        }

        private async Task<List<KnowledgeArtifactDraft>> BuildArtifactsAsync(
            MeetingSnapshot meeting,
            IReadOnlyList<TagSnapshot> confirmedTags,
            CancellationToken cancellationToken)
        {
            var artifacts = new List<KnowledgeArtifactDraft>();

            var transcript = await _dbContext.MeetingTranscripts
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(x => x.OrganizationId == meeting.OrganizationId && x.MeetingId == meeting.Id)
                .OrderByDescending(x => x.GeneratedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            if (transcript is not null && !string.IsNullOrWhiteSpace(transcript.FullText))
            {
                artifacts.Add(new KnowledgeArtifactDraft(
                    KnowledgeArtifactType.Transcript,
                    meeting.OrganizationId,
                    transcript.Id,
                    meeting.Id,
                    $"{meeting.Title} transcript",
                    transcript.FullText.Trim(),
                    new
                    {
                        source = "meeting_transcript",
                        meetingId = meeting.Id,
                        transcriptId = transcript.Id,
                        transcript.GeneratedAtUtc,
                        transcript.SttModel,
                        confirmedTags = confirmedTags.Select(TagMetadata).ToArray()
                    }));
            }

            var summary = await _dbContext.MeetingSummaries
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(x => x.OrganizationId == meeting.OrganizationId && x.MeetingId == meeting.Id)
                .OrderByDescending(x => x.GeneratedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            if (summary is not null && !string.IsNullOrWhiteSpace(summary.SummaryText))
            {
                artifacts.Add(new KnowledgeArtifactDraft(
                    KnowledgeArtifactType.Summary,
                    meeting.OrganizationId,
                    summary.Id,
                    meeting.Id,
                    $"{meeting.Title} summary",
                    summary.SummaryText.Trim(),
                    new
                    {
                        source = "meeting_summary",
                        meetingId = meeting.Id,
                        summaryId = summary.Id,
                        summary.GeneratedAtUtc,
                        summary.LlmModel,
                        confirmedTags = confirmedTags.Select(TagMetadata).ToArray()
                    }));
            }

            var actionItems = await _dbContext.ActionItems
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(x => x.OrganizationId == meeting.OrganizationId && x.MeetingId == meeting.Id)
                .OrderBy(x => x.CreatedAtUtc)
                .ThenBy(x => x.Id)
                .ToListAsync(cancellationToken);

            foreach (var actionItem in actionItems)
            {
                var content = FormatActionItem(actionItem);
                if (string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                artifacts.Add(new KnowledgeArtifactDraft(
                    KnowledgeArtifactType.ActionItem,
                    meeting.OrganizationId,
                    actionItem.Id,
                    meeting.Id,
                    $"{meeting.Title} action item: {actionItem.Title}",
                    content,
                    new
                    {
                        source = "action_item",
                        meetingId = meeting.Id,
                        actionItemId = actionItem.Id,
                        actionItem.Status,
                        actionItem.AssignedToUserId,
                        actionItem.AssignedToParticipantId,
                        actionItem.DueDateUtc,
                        actionItem.ExtractedAtUtc,
                        confirmedTags = confirmedTags.Select(TagMetadata).ToArray()
                    }));
            }

            if (confirmedTags.Count > 0 || artifacts.Count > 0)
            {
                var tagText = confirmedTags.Count == 0
                    ? "Confirmed meeting tags: none"
                    : "Confirmed meeting tags: " + string.Join(
                        ", ",
                        confirmedTags.Select(tag => string.IsNullOrWhiteSpace(tag.Color)
                            ? tag.Name
                            : $"{tag.Name} ({tag.Color})"));

                artifacts.Add(new KnowledgeArtifactDraft(
                    KnowledgeArtifactType.ConfirmedMeetingTags,
                    meeting.OrganizationId,
                    meeting.Id,
                    meeting.Id,
                    $"{meeting.Title} confirmed tags",
                    tagText,
                    new
                    {
                        source = "confirmed_meeting_tags",
                        meetingId = meeting.Id,
                        confirmedTags = confirmedTags.Select(TagMetadata).ToArray()
                    }));
            }

            return artifacts;
        }

        private async Task<List<KnowledgeDocument>> CreateDraftDocumentsAsync(
            IReadOnlyList<KnowledgeArtifactDraft> artifacts,
            IReadOnlyList<TagSnapshot> confirmedTags,
            Guid generationId,
            DateTime generatedAtUtc,
            CancellationToken cancellationToken)
        {
            var documents = new List<KnowledgeDocument>();
            var chunkInputs = new List<ChunkEmbeddingInput>();

            foreach (var artifact in artifacts)
            {
                var document = new KnowledgeDocument
                {
                    OrganizationId = artifact.OrganizationId,
                    MeetingId = artifact.MeetingId,
                    ArtifactType = artifact.ArtifactType,
                    ArtifactId = artifact.ArtifactId,
                    ArtifactVersion = artifact.ArtifactVersion,
                    Title = Truncate(artifact.Title, 300),
                    ContentHash = HashArtifact(artifact, confirmedTags),
                    IndexGenerationId = generationId,
                    Visibility = KnowledgeVisibility.Draft,
                    IsCurrent = false,
                    EmbeddingProvider = _embeddingService.Metadata.Provider,
                    EmbeddingModel = _embeddingService.Metadata.Model,
                    EmbeddingDimension = _embeddingService.Metadata.Dimension,
                    MetadataJson = JsonSerializer.Serialize(artifact.Metadata, JsonOptions),
                    GeneratedAtUtc = generatedAtUtc
                };

                var chunks = SplitIntoChunks(artifact.Content);
                for (var i = 0; i < chunks.Count; i++)
                {
                    var text = chunks[i];
                    var chunk = new KnowledgeChunk
                    {
                        OrganizationId = artifact.OrganizationId,
                        Document = document,
                        MeetingId = artifact.MeetingId,
                        ArtifactType = artifact.ArtifactType,
                        ArtifactId = artifact.ArtifactId,
                        ArtifactVersion = artifact.ArtifactVersion,
                        ChunkIndex = i,
                        Text = text,
                        CharacterCount = text.Length,
                        TokenCount = EstimateTokenCount(text),
                        ContentHash = HashText(text),
                        EmbeddingProvider = _embeddingService.Metadata.Provider,
                        EmbeddingModel = _embeddingService.Metadata.Model,
                        EmbeddingDimension = _embeddingService.Metadata.Dimension,
                        IndexGenerationId = generationId,
                        Visibility = KnowledgeVisibility.Draft,
                        IsCurrent = false,
                        MetadataJson = JsonSerializer.Serialize(new
                        {
                            artifact = artifact.Metadata,
                            chunkIndex = i,
                            characterCount = text.Length,
                            tokenCount = EstimateTokenCount(text)
                        }, JsonOptions),
                        GeneratedAtUtc = generatedAtUtc
                    };

                    foreach (var tag in confirmedTags)
                    {
                        chunk.Tags.Add(new KnowledgeChunkTag
                        {
                            OrganizationId = artifact.OrganizationId,
                            KnowledgeDocument = document,
                            KnowledgeChunk = chunk,
                            MeetingTagId = tag.Id,
                            TagNameSnapshot = tag.Name,
                            TagColorSnapshot = tag.Color
                        });
                    }

                    document.Chunks.Add(chunk);
                    chunkInputs.Add(new ChunkEmbeddingInput(chunk, text));
                }

                documents.Add(document);
            }

            var embeddings = await _embeddingService.EmbedBatchAsync(
                chunkInputs.Select(x => x.Text).ToArray(),
                cancellationToken);

            if (embeddings.Length != chunkInputs.Count)
            {
                throw new InvalidOperationException(
                    $"Embedding service returned {embeddings.Length} embeddings for {chunkInputs.Count} knowledge chunks.");
            }

            for (var i = 0; i < chunkInputs.Count; i++)
            {
                chunkInputs[i].Chunk.EmbeddingVectorText = KnowledgeVectorSql.ToVectorLiteral(embeddings[i]);
            }

            return documents;
        }

        private async Task<IReadOnlyList<KnowledgeArtifactDraft>> FilterChangedArtifactsAsync(
            Guid organizationId,
            Guid meetingId,
            IReadOnlyList<KnowledgeArtifactDraft> artifacts,
            IReadOnlyList<TagSnapshot> confirmedTags,
            CancellationToken cancellationToken)
        {
            var existingCurrent = await _dbContext.KnowledgeDocuments
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(x => x.OrganizationId == organizationId
                            && x.MeetingId == meetingId
                            && x.Visibility == KnowledgeVisibility.Published
                            && x.IsCurrent)
                .Select(x => new
                {
                    x.ArtifactType,
                    x.ArtifactId,
                    x.ArtifactVersion,
                    x.ContentHash
                })
                .ToListAsync(cancellationToken);

            var existingHashes = existingCurrent.ToDictionary(
                x => new ArtifactKey(x.ArtifactType, x.ArtifactId, x.ArtifactVersion),
                x => x.ContentHash);

            return artifacts
                .Where(artifact =>
                {
                    var key = new ArtifactKey(artifact.ArtifactType, artifact.ArtifactId, artifact.ArtifactVersion);
                    return !existingHashes.TryGetValue(key, out var existingHash)
                           || !string.Equals(existingHash, HashArtifact(artifact, confirmedTags), StringComparison.Ordinal);
                })
                .ToList();
        }

        private async Task ArchivePreviousCurrentDocumentsAsync(
            Guid organizationId,
            Guid meetingId,
            IReadOnlyCollection<ArtifactKey> replacementKeys,
            Guid generationId,
            CancellationToken cancellationToken)
        {
            if (replacementKeys.Count == 0)
            {
                return;
            }

            var updatedAtUtc = DateTime.UtcNow;

            foreach (var key in replacementKeys)
            {
                await _dbContext.KnowledgeDocuments
                    .IgnoreQueryFilters()
                    .Where(x => x.OrganizationId == organizationId
                                && x.MeetingId == meetingId
                                && x.ArtifactType == key.ArtifactType
                                && x.ArtifactId == key.ArtifactId
                                && x.ArtifactVersion == key.ArtifactVersion
                                && x.IsCurrent
                                && x.Visibility == KnowledgeVisibility.Published
                                && x.IndexGenerationId != generationId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.IsCurrent, false)
                        .SetProperty(x => x.Visibility, KnowledgeVisibility.Archived)
                        .SetProperty(x => x.UpdatedAtUtc, updatedAtUtc),
                        cancellationToken);

                await _dbContext.KnowledgeChunks
                    .IgnoreQueryFilters()
                    .Where(x => x.OrganizationId == organizationId
                                && x.MeetingId == meetingId
                                && x.ArtifactType == key.ArtifactType
                                && x.ArtifactId == key.ArtifactId
                                && x.ArtifactVersion == key.ArtifactVersion
                                && x.IsCurrent
                                && x.Visibility == KnowledgeVisibility.Published
                                && x.IndexGenerationId != generationId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.IsCurrent, false)
                        .SetProperty(x => x.Visibility, KnowledgeVisibility.Archived)
                        .SetProperty(x => x.UpdatedAtUtc, updatedAtUtc),
                        cancellationToken);
            }
        }

        private static IReadOnlyList<string> SplitIntoChunks(string content)
        {
            var normalized = NormalizeWhitespace(content);
            if (normalized.Length <= MaxChunkCharacters)
            {
                return [normalized];
            }

            var chunks = new List<string>();
            var start = 0;
            while (start < normalized.Length)
            {
                var length = Math.Min(MaxChunkCharacters, normalized.Length - start);
                var end = start + length;

                if (end < normalized.Length)
                {
                    var breakAt = normalized.LastIndexOf('\n', end - 1, length);
                    if (breakAt <= start + (MaxChunkCharacters / 2))
                    {
                        breakAt = normalized.LastIndexOf(' ', end - 1, length);
                    }

                    if (breakAt > start + (MaxChunkCharacters / 2))
                    {
                        end = breakAt + 1;
                    }
                }

                var chunk = normalized[start..end].Trim();
                if (!string.IsNullOrWhiteSpace(chunk))
                {
                    chunks.Add(chunk);
                }

                if (end >= normalized.Length)
                {
                    break;
                }

                start = Math.Max(end - ChunkOverlapCharacters, start + 1);
            }

            return chunks;
        }

        private static string FormatActionItem(ActionItem actionItem)
        {
            var lines = new List<string>
            {
                $"Action item: {actionItem.Title.Trim()}",
                $"Status: {actionItem.Status}"
            };

            if (!string.IsNullOrWhiteSpace(actionItem.Description))
            {
                lines.Add($"Description: {actionItem.Description.Trim()}");
            }

            if (actionItem.AssignedToUserId.HasValue)
            {
                lines.Add($"Assigned user id: {actionItem.AssignedToUserId.Value}");
            }
            if (actionItem.DueDateUtc.HasValue)
            {
                lines.Add($"Due date UTC: {actionItem.DueDateUtc.Value:O}");
            }
            if (!string.IsNullOrWhiteSpace(actionItem.SyncMissingAssigneeReason))
            {
                lines.Add($"Review reason: {actionItem.SyncMissingAssigneeReason}");
            }

            return string.Join(Environment.NewLine, lines);
        }

        private static string NormalizeWhitespace(string value)
        {
            return string.Join(
                Environment.NewLine,
                value.Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Replace('\r', '\n')
                    .Split('\n')
                    .Select(line => string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
                    .Where(line => !string.IsNullOrWhiteSpace(line)));
        }

        private static string HashText(string value)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        private static string HashArtifact(KnowledgeArtifactDraft artifact, IReadOnlyList<TagSnapshot> confirmedTags)
        {
            var tagFingerprint = string.Join(
                '|',
                confirmedTags.Select(tag => $"{tag.Id:N}:{tag.Name}:{tag.Color}"));

            return HashText($"{artifact.ArtifactType}:{artifact.ArtifactId:N}:{artifact.ArtifactVersion}\n{artifact.Content}\nconfirmed-tags:{tagFingerprint}");
        }

        private static int EstimateTokenCount(string text) => Math.Max(1, (int)Math.Ceiling(text.Length / 4d));

        private static string Truncate(string value, int maxLength) =>
            value.Length <= maxLength ? value : value[..maxLength];

        private static object TagMetadata(TagSnapshot tag) => new
        {
            id = tag.Id,
            name = tag.Name,
            color = tag.Color
        };

        private sealed record MeetingSnapshot(Guid Id, Guid OrganizationId, string Title);

        private sealed record TagSnapshot(Guid Id, string Name, string? Color);

        private sealed record KnowledgeArtifactDraft(
            KnowledgeArtifactType ArtifactType,
            Guid OrganizationId,
            Guid ArtifactId,
            Guid MeetingId,
            string Title,
            string Content,
            object Metadata,
            int ArtifactVersion = 1);

        private sealed record ArtifactKey(KnowledgeArtifactType ArtifactType, Guid ArtifactId, int ArtifactVersion);

        private sealed record ChunkEmbeddingInput(KnowledgeChunk Chunk, string Text);
    }
}
