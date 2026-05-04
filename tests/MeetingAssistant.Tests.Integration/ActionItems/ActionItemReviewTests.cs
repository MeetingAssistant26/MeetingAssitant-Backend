using System.Net;
using FluentAssertions;
using Xunit;

namespace MeetingAssistant.Tests.Integration.ActionItems
{
    public class ActionItemReviewTests : IClassFixture<TestWebApplicationFactory>
    {
        private readonly HttpClient _client;
        private readonly TestWebApplicationFactory _factory;

        public ActionItemReviewTests(TestWebApplicationFactory factory)
        {
            _factory = factory;
            _client = factory.CreateClient();
        }

        [Fact(Skip = "Requires test data setup")]
        public async Task ApproveActionItem_ShouldChangeStatusToApproved()
        {
        }

        [Fact(Skip = "Requires test data setup")]
        public async Task RejectActionItem_ShouldChangeStatusToRejected()
        {
        }

        [Fact(Skip = "Requires test data setup")]
        public async Task UpdateActionItem_ShouldApplyChanges()
        {
        }

        [Fact(Skip = "Requires test data setup")]
        public async Task DeleteActionItem_ShouldRemoveItem()
        {
        }

        [Fact(Skip = "Requires test data setup")]
        public async Task BulkSync_ShouldReturn207MultiStatus()
        {
        }
    }
}
