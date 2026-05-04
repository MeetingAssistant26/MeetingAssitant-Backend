using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using MeetingAssistant.Features.ActionItems.Models.Enums;
using Xunit;

namespace MeetingAssistant.Tests.Integration.ActionItems
{
    public class ExtractActionItemsJobTests : IClassFixture<TestWebApplicationFactory>
    {
        private readonly HttpClient _client;
        private readonly TestWebApplicationFactory _factory;

        public ExtractActionItemsJobTests(TestWebApplicationFactory factory)
        {
            _factory = factory;
            _client = factory.CreateClient();
        }

        [Fact(Skip = "Requires LLM service mocking")]
        public async Task ExtractActionItems_ShouldCreateActionItems_WhenTranscriptReady()
        {
            // Arrange: Create meeting with transcript
            // Act: Trigger extraction job
            // Assert: Action items exist in database
        }
    }
}
