using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using MeetingAssistant.Features.AgentApi.Models.Responses;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Features.Rag.Models;
using MeetingAssistant.Features.Rag.Services;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Tests.Integration.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace MeetingAssistant.Tests.Integration.AgentApi;

public sealed class AgentMeetingContextQueryTests : IClassFixture<AgentMeetingContextQueryTests.ContextQueryWebFactory>, IAsyncLifetime
{
    private readonly ContextQueryWebFactory _factory;
    private HttpClient _client = null!;

    public AgentMeetingContextQueryTests(ContextQueryWebFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        _factory.Retrieval.Reset();

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task QueryMeetingContext_WithAgentToken_ShouldReturnSnippetsAndScopeRetrievalToTokenOrganization()
    {
        // Arrange
        var organizationId = Guid.NewGuid();
        var meetingId = await SeedMeetingAsync(organizationId, "Current roadmap sync");
        var preferredTagId = Guid.NewGuid();
        var sourceMeetingId = Guid.NewGuid();
        var chunkId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var scheduledStartUtc = DateTime.UtcNow.AddDays(-3);

        _factory.Retrieval.Results =
        [
            new KnowledgeRetrievalResult(
                chunkId,
                documentId,
                sourceMeetingId,
                "Past roadmap planning",
                scheduledStartUtc,
                KnowledgeArtifactType.Summary,
                "Roadmap decisions from the prior planning meeting.",
                "Past roadmap summary",
                "{}",
                [new KnowledgeRetrievalTagResult(preferredTagId, "Roadmap", "#336699")],
                0.12d,
                1,
                0.05d,
                0.07d,
                true)
        ];
        SetAgentAuthorization(organizationId, meetingId);

        var request = new
        {
            question = "What did we decide about the launch?",
            transcript = "Speaker 1: We should revisit the roadmap.",
            topK = 3,
            preferredTagIds = new[] { preferredTagId },
            sourceTypes = new[] { "Summary" }
        };

        // Act
        var response = await _client.PostAsJsonAsync($"/api/agent/meetings/{meetingId}/context/query", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AgentMeetingContextQueryResponse>();
        body.Should().NotBeNull();
        body!.MeetingId.Should().Be(meetingId);
        body.TopK.Should().Be(3);
        body.Snippets.Should().ContainSingle();
        var snippet = body.Snippets.Single();
        snippet.ChunkId.Should().Be(chunkId);
        snippet.DocumentId.Should().Be(documentId);
        snippet.Source.MeetingId.Should().Be(sourceMeetingId);
        snippet.Source.MeetingTitle.Should().Be("Past roadmap planning");
        snippet.Source.MeetingScheduledStartUtc.Should().BeCloseTo(scheduledStartUtc, TimeSpan.FromSeconds(1));
        snippet.Source.Type.Should().Be("Summary");
        snippet.DocumentTitle.Should().Be("Past roadmap summary");
        snippet.ChunkText.Should().Contain("Roadmap decisions");
        snippet.Tags.Should().ContainSingle(tag => tag.Id == preferredTagId && tag.Name == "Roadmap" && tag.Color == "#336699");
        snippet.Score.SharedTagCount.Should().Be(1);
        snippet.Score.TagBoost.Should().Be(0.05d);
        snippet.Score.RankingScore.Should().Be(0.07d);
        snippet.Score.IsTagPreferredResult.Should().BeTrue();

        _factory.Retrieval.Calls.Should().ContainSingle();
        var call = _factory.Retrieval.Calls.Single();
        call.OrganizationId.Should().Be(organizationId);
        call.CurrentMeetingId.Should().Be(meetingId);
        call.TopK.Should().Be(20, "source filtering asks the retrieval layer for overflow candidates before trimming to TopK");
        call.PreferredTagIds.Should().ContainSingle().Which.Should().Be(preferredTagId);
        call.QueryText.Should().Contain("What did we decide about the launch?");
        call.QueryText.Should().Contain("Speaker 1: We should revisit the roadmap.");
    }

    [Fact]
    public async Task QueryMeetingContext_WithoutToken_ShouldReturnUnauthorized()
    {
        // Arrange
        var organizationId = Guid.NewGuid();
        var meetingId = await SeedMeetingAsync(organizationId, "Unauthorized query");

        // Act
        var response = await _client.PostAsJsonAsync($"/api/agent/meetings/{meetingId}/context/query", new { question = "Context?" });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.Retrieval.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryMeetingContext_WithUserBrowserToken_ShouldReturnUnauthorized()
    {
        // Arrange
        var organizationId = Guid.NewGuid();
        var meetingId = await SeedMeetingAsync(organizationId, "Browser token query");
        var userToken = TestJwtTokenHelper.GenerateToken(Guid.NewGuid(), organizationId);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken);

        // Act
        var response = await _client.PostAsJsonAsync($"/api/agent/meetings/{meetingId}/context/query", new { question = "Context?" });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.Retrieval.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryMeetingContext_WithMismatchedMeetingClaim_ShouldReturnForbidden()
    {
        // Arrange
        var organizationId = Guid.NewGuid();
        var tokenMeetingId = await SeedMeetingAsync(organizationId, "Token meeting");
        var routeMeetingId = await SeedMeetingAsync(organizationId, "Route meeting");
        SetAgentAuthorization(organizationId, tokenMeetingId);

        // Act
        var response = await _client.PostAsJsonAsync($"/api/agent/meetings/{routeMeetingId}/context/query", new { question = "Context?" });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Retrieval.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryMeetingContext_WithTokenMeetingInAnotherOrganization_ShouldReturnNotFoundWithoutRetrieval()
    {
        // Arrange
        var tokenOrganizationId = Guid.NewGuid();
        await SeedOrganizationAsync(tokenOrganizationId, "token-org");
        var foreignOrganizationId = Guid.NewGuid();
        var foreignMeetingId = await SeedMeetingAsync(foreignOrganizationId, "Foreign meeting");
        SetAgentAuthorization(tokenOrganizationId, foreignMeetingId);

        // Act
        var response = await _client.PostAsJsonAsync($"/api/agent/meetings/{foreignMeetingId}/context/query", new { question = "Context?" });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _factory.Retrieval.Calls.Should().BeEmpty("meeting existence is checked inside the token organization before retrieval");
    }

    [Fact]
    public async Task QueryMeetingContext_ShouldReturnSharedTagPreferredResultsBeforeFallbackMetadata()
    {
        // Arrange
        var organizationId = Guid.NewGuid();
        var meetingId = await SeedMeetingAsync(organizationId, "Tag-aware query");
        SetAgentAuthorization(organizationId, meetingId);

        _factory.Retrieval.Results =
        [
            Result("shared", KnowledgeArtifactType.Summary, sharedTagCount: 1, isPreferred: true, rankingScore: 0.05d),
            Result("fallback", KnowledgeArtifactType.Transcript, sharedTagCount: 0, isPreferred: false, rankingScore: 0.20d)
        ];

        // Act
        var response = await _client.PostAsJsonAsync($"/api/agent/meetings/{meetingId}/context/query", new { question = "roadmap", topK = 2 });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AgentMeetingContextQueryResponse>();
        body.Should().NotBeNull();
        body!.Snippets.Select(snippet => snippet.ChunkText).Should().Equal("shared", "fallback");
        body.Snippets[0].Score.SharedTagCount.Should().Be(1);
        body.Snippets[0].Score.IsTagPreferredResult.Should().BeTrue();
        body.Snippets[1].Score.SharedTagCount.Should().Be(0);
        body.Snippets[1].Score.IsTagPreferredResult.Should().BeFalse();
    }

    [Fact]
    public async Task QueryMeetingContext_ShouldReturnBroaderFallbackResultsWhenNoSharedTagsExist()
    {
        // Arrange
        var organizationId = Guid.NewGuid();
        var meetingId = await SeedMeetingAsync(organizationId, "Fallback query");
        SetAgentAuthorization(organizationId, meetingId);

        _factory.Retrieval.Results =
        [
            Result("general org context", KnowledgeArtifactType.Note, sharedTagCount: 0, isPreferred: false, rankingScore: 0.11d),
            Result("second fallback", KnowledgeArtifactType.ActionItem, sharedTagCount: 0, isPreferred: false, rankingScore: 0.12d)
        ];

        // Act
        var response = await _client.PostAsJsonAsync($"/api/agent/meetings/{meetingId}/context/query", new { question = "roadmap", topK = 2 });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AgentMeetingContextQueryResponse>();
        body.Should().NotBeNull();
        body!.Snippets.Should().HaveCount(2);
        body.Snippets.Should().OnlyContain(snippet => snippet.Score.SharedTagCount == 0 && snippet.Score.IsTagPreferredResult == false);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public async Task QueryMeetingContext_WithOutOfBoundsTopK_ShouldReturnBadRequest(int topK)
    {
        // Arrange
        var organizationId = Guid.NewGuid();
        var meetingId = await SeedMeetingAsync(organizationId, "TopK query");
        SetAgentAuthorization(organizationId, meetingId);

        // Act
        var response = await _client.PostAsJsonAsync($"/api/agent/meetings/{meetingId}/context/query", new { question = "Context?", topK });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.Retrieval.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryMeetingContext_WithInvalidFilters_ShouldReturnBadRequest()
    {
        // Arrange
        var organizationId = Guid.NewGuid();
        var meetingId = await SeedMeetingAsync(organizationId, "Invalid filters query");
        SetAgentAuthorization(organizationId, meetingId);

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/agent/meetings/{meetingId}/context/query",
            new { question = "Context?", sourceTypes = new[] { "UnknownArtifact" } });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.Retrieval.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryMeetingContext_WithEmptyQuestion_ShouldReturnBadRequest()
    {
        // Arrange
        var organizationId = Guid.NewGuid();
        var meetingId = await SeedMeetingAsync(organizationId, "Empty question query");
        SetAgentAuthorization(organizationId, meetingId);

        // Act
        var response = await _client.PostAsJsonAsync($"/api/agent/meetings/{meetingId}/context/query", new { question = "" });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.Retrieval.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryMeetingContext_WithSimpleQuestionOnly_ShouldPreserveBackwardCompatibleQueryText()
    {
        // Arrange
        var organizationId = Guid.NewGuid();
        var meetingId = await SeedMeetingAsync(organizationId, "Simple question query");
        SetAgentAuthorization(organizationId, meetingId);
        _factory.Retrieval.Results = [Result("simple context", KnowledgeArtifactType.Summary, 0, false, 0.10d)];

        const string question = "What did we decide about the launch?";

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/agent/meetings/{meetingId}/context/query",
            new { question, topK = 2 });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Retrieval.Calls.Should().ContainSingle();
        _factory.Retrieval.Calls.Single().QueryText.Should().Be(question);
        _factory.Retrieval.Calls.Single().TopK.Should().Be(2);
    }

    [Fact]
    public async Task QueryMeetingContext_WithConversationHistory_ShouldIncludePriorTurnsInQueryText()
    {
        // Arrange
        var organizationId = Guid.NewGuid();
        var meetingId = await SeedMeetingAsync(organizationId, "Conversation query");
        SetAgentAuthorization(organizationId, meetingId);
        _factory.Retrieval.Results = [Result("roadmap context", KnowledgeArtifactType.Summary, 0, false, 0.10d)];

        const string question = "What about the Q3 launch date?";
        var request = new
        {
            question,
            conversationTurns = new[]
            {
                new { role = "user", text = "Can you recap the roadmap discussion?" },
                new { role = "assistant", text = "The team aligned on a phased rollout for the platform." },
                new { role = "user", text = "Did anyone mention the Acme partnership?" },
                new { role = "assistant", text = "Yes, Acme integration was flagged as a Q3 dependency." }
            }
        };

        // Act
        var response = await _client.PostAsJsonAsync($"/api/agent/meetings/{meetingId}/context/query", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Retrieval.Calls.Should().ContainSingle();
        var queryText = _factory.Retrieval.Calls.Single().QueryText;
        queryText.Should().Contain("Question:");
        queryText.Should().Contain(question);
        queryText.Should().Contain("Recent conversation:");
        queryText.Should().Contain("User: Can you recap the roadmap discussion?");
        queryText.Should().Contain("Assistant: The team aligned on a phased rollout for the platform.");
        queryText.Should().Contain("User: Did anyone mention the Acme partnership?");
        queryText.Should().Contain("Assistant: Yes, Acme integration was flagged as a Q3 dependency.");
        queryText.Should().NotContain("Current transcript/context:");
    }

    [Fact]
    public async Task QueryMeetingContext_WithConversationAndTranscript_ShouldIncludeAllSections()
    {
        // Arrange
        var organizationId = Guid.NewGuid();
        var meetingId = await SeedMeetingAsync(organizationId, "Conversation and transcript query");
        SetAgentAuthorization(organizationId, meetingId);
        _factory.Retrieval.Results = [Result("combined context", KnowledgeArtifactType.Transcript, 0, false, 0.10d)];

        const string question = "Summarize the blocker we just discussed.";
        const string transcript = "Speaker 1: The API migration is still blocked.";
        var request = new
        {
            question,
            transcript,
            conversationTurns = new[]
            {
                new { role = "user", text = "What blockers came up earlier?" },
                new { role = "assistant", text = "Infrastructure capacity was raised in the prior sync." }
            }
        };

        // Act
        var response = await _client.PostAsJsonAsync($"/api/agent/meetings/{meetingId}/context/query", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var queryText = _factory.Retrieval.Calls.Single().QueryText;
        queryText.Should().Contain("Question:");
        queryText.Should().Contain(question);
        queryText.Should().Contain("Recent conversation:");
        queryText.Should().Contain("User: What blockers came up earlier?");
        queryText.Should().Contain("Assistant: Infrastructure capacity was raised in the prior sync.");
        queryText.Should().Contain("Current transcript/context:");
        queryText.Should().Contain(transcript);
    }

    [Fact]
    public async Task QueryMeetingContext_WithDuplicateLastUserTurn_ShouldDeduplicateQuestionFromConversation()
    {
        // Arrange
        var organizationId = Guid.NewGuid();
        var meetingId = await SeedMeetingAsync(organizationId, "Conversation dedupe query");
        SetAgentAuthorization(organizationId, meetingId);
        _factory.Retrieval.Results = [Result("dedupe context", KnowledgeArtifactType.Note, 0, false, 0.10d)];

        const string question = "What about the Q3 launch date?";
        var request = new
        {
            question,
            conversationTurns = new[]
            {
                new { role = "assistant", text = "The roadmap targets a phased rollout." },
                new { role = "user", text = question }
            }
        };

        // Act
        var response = await _client.PostAsJsonAsync($"/api/agent/meetings/{meetingId}/context/query", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var queryText = _factory.Retrieval.Calls.Single().QueryText;
        queryText.Should().Contain("Question:");
        queryText.Should().Contain(question);
        queryText.Should().Contain("Assistant: The roadmap targets a phased rollout.");
        queryText.Should().NotContain($"User: {question}");
    }

    [Fact]
    public async Task QueryMeetingContext_WithTooManyConversationTurns_ShouldReturnBadRequest()
    {
        // Arrange
        var organizationId = Guid.NewGuid();
        var meetingId = await SeedMeetingAsync(organizationId, "Too many turns query");
        SetAgentAuthorization(organizationId, meetingId);

        var conversationTurns = Enumerable.Range(1, 9)
            .Select(index => new { role = index % 2 == 0 ? "assistant" : "user", text = $"Turn {index}" })
            .ToArray();

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/agent/meetings/{meetingId}/context/query",
            new { question = "Context?", conversationTurns });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.Retrieval.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryMeetingContext_WithOversizedConversationTurn_ShouldReturnBadRequest()
    {
        // Arrange
        var organizationId = Guid.NewGuid();
        var meetingId = await SeedMeetingAsync(organizationId, "Oversized turn query");
        SetAgentAuthorization(organizationId, meetingId);

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/agent/meetings/{meetingId}/context/query",
            new
            {
                question = "Context?",
                conversationTurns = new[] { new { role = "user", text = new string('x', 2_001) } }
            });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.Retrieval.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryMeetingContext_WithOversizedTotalConversation_ShouldReturnBadRequest()
    {
        // Arrange
        var organizationId = Guid.NewGuid();
        var meetingId = await SeedMeetingAsync(organizationId, "Oversized total conversation query");
        SetAgentAuthorization(organizationId, meetingId);

        var conversationTurns = Enumerable.Range(1, 5)
            .Select(_ => new { role = "user", text = new string('x', 1_601) })
            .ToArray();

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/agent/meetings/{meetingId}/context/query",
            new { question = "Context?", conversationTurns });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.Retrieval.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryMeetingContext_WithNullConversationTurn_ShouldReturnBadRequest()
    {
        // Arrange
        var organizationId = Guid.NewGuid();
        var meetingId = await SeedMeetingAsync(organizationId, "Null conversation turn query");
        SetAgentAuthorization(organizationId, meetingId);

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/agent/meetings/{meetingId}/context/query",
            new
            {
                question = "Context?",
                conversationTurns = new object?[] { null }
            });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.Retrieval.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("system")]
    [InlineData("tool")]
    [InlineData("")]
    public async Task QueryMeetingContext_WithInvalidConversationRole_ShouldReturnBadRequest(string role)
    {
        // Arrange
        var organizationId = Guid.NewGuid();
        var meetingId = await SeedMeetingAsync(organizationId, "Invalid role query");
        SetAgentAuthorization(organizationId, meetingId);

        // Act
        var response = await _client.PostAsJsonAsync(
            $"/api/agent/meetings/{meetingId}/context/query",
            new
            {
                question = "Context?",
                conversationTurns = new[] { new { role, text = "Prior context" } }
            });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.Retrieval.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryMeetingContext_WithConversationAndSourceFiltering_ShouldPreserveTopKAndSourceBehavior()
    {
        // Arrange
        var organizationId = Guid.NewGuid();
        var meetingId = await SeedMeetingAsync(organizationId, "Conversation source filter query");
        SetAgentAuthorization(organizationId, meetingId);

        _factory.Retrieval.Results =
        [
            Result("summary hit", KnowledgeArtifactType.Summary, 0, false, 0.05d),
            Result("transcript hit", KnowledgeArtifactType.Transcript, 0, false, 0.20d)
        ];

        var request = new
        {
            question = "What did we decide?",
            topK = 1,
            sourceTypes = new[] { "Summary" },
            conversationTurns = new[]
            {
                new { role = "user", text = "Remind me about the roadmap." },
                new { role = "assistant", text = "The roadmap targets a phased rollout." }
            }
        };

        // Act
        var response = await _client.PostAsJsonAsync($"/api/agent/meetings/{meetingId}/context/query", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AgentMeetingContextQueryResponse>();
        body.Should().NotBeNull();
        body!.TopK.Should().Be(1);
        body.Snippets.Should().ContainSingle();
        body.Snippets.Single().ChunkText.Should().Be("summary hit");

        var call = _factory.Retrieval.Calls.Single();
        call.TopK.Should().Be(20, "source filtering still asks the retrieval layer for overflow candidates");
        call.QueryText.Should().Contain("Recent conversation:");
        call.QueryText.Should().Contain("User: Remind me about the roadmap.");
    }

    private void SetAgentAuthorization(Guid organizationId, Guid meetingId)
    {
        var token = TestJwtTokenHelper.GenerateAgentToken(organizationId, meetingId);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private async Task<Guid> SeedMeetingAsync(Guid organizationId, string title)
    {
        await SeedOrganizationAsync(organizationId, $"org-{organizationId:N}");

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var meetingId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.Meetings.Add(new Meeting
        {
            Id = meetingId,
            OrganizationId = organizationId,
            Title = title,
            ScheduledStartUtc = now.AddMinutes(-10),
            ScheduledEndUtc = now.AddMinutes(50),
            Status = MeetingStatus.InProgress
        });

        await db.SaveChangesAsync();
        return meetingId;
    }

    private async Task SeedOrganizationAsync(Guid organizationId, string slug)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var exists = await db.Organizations.AnyAsync(org => org.Id == organizationId);
        if (exists)
        {
            return;
        }

        db.Organizations.Add(new Organization
        {
            Id = organizationId,
            Name = slug,
            Slug = slug
        });

        await db.SaveChangesAsync();
    }

    private static KnowledgeRetrievalResult Result(
        string text,
        KnowledgeArtifactType sourceType,
        int sharedTagCount,
        bool isPreferred,
        double rankingScore)
    {
        return new KnowledgeRetrievalResult(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            $"Source for {text}",
            DateTime.UtcNow.AddDays(-1),
            sourceType,
            text,
            $"Document for {text}",
            "{}",
            [],
            rankingScore + 0.01d,
            sharedTagCount,
            sharedTagCount > 0 ? 0.05d : 0d,
            rankingScore,
            isPreferred);
    }

    public sealed class ContextQueryWebFactory : MeetingAssistantWebFactory
    {
        public CaptureKnowledgeRetrievalService Retrieval { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IKnowledgeRetrievalService>();
                services.AddSingleton<IKnowledgeRetrievalService>(Retrieval);
            });
        }
    }

    public sealed class CaptureKnowledgeRetrievalService : IKnowledgeRetrievalService
    {
        public List<KnowledgeRetrievalRequest> Calls { get; } = [];

        public IReadOnlyList<KnowledgeRetrievalResult> Results { get; set; } = [];

        public Task<IReadOnlyList<KnowledgeRetrievalResult>> RetrieveAsync(
            KnowledgeRetrievalRequest request,
            CancellationToken cancellationToken)
        {
            Calls.Add(request);
            return Task.FromResult(Results);
        }

        public void Reset()
        {
            Calls.Clear();
            Results = [];
        }
    }
}
