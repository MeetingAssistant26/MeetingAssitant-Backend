using FluentAssertions;
using Xunit;

namespace MeetingAssistant.Tests.Integration.ActionItems
{
    public class ProviderSyncTests : IClassFixture<TestWebApplicationFactory>
    {
        private readonly HttpClient _client;
        private readonly TestWebApplicationFactory _factory;

        public ProviderSyncTests(TestWebApplicationFactory factory)
        {
            _factory = factory;
            _client = factory.CreateClient();
        }

        [Fact(Skip = "Requires mocked Trello API")]
        public async Task SyncActionItem_ShouldCreateExternalTask()
        {
        }
    }
}
