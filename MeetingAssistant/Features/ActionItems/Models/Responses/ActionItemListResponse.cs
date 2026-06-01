namespace MeetingAssistant.Features.ActionItems.Models.Responses
{
    public class ActionItemListResponse
    {
        public List<ActionItemResponse> Items { get; set; } = new();
        public int TotalCount { get; set; }
        public int Page { get; set; } = 1;
        public int PageSize { get; set; }
    }
}
