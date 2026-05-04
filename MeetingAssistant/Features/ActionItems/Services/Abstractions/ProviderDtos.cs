namespace MeetingAssistant.Features.ActionItems.Services.Abstractions
{
    public class ProviderProject
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    public class ProviderList
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    public class ProviderTaskRequest
    {
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }
        public DateTime? DueDateUtc { get; set; }
        public string? AssigneeExternalId { get; set; }
    }

    public class ProviderTaskResult
    {
        public string TaskId { get; set; } = string.Empty;
        public string? TaskUrl { get; set; }
        public bool HasAssignee { get; set; }
    }

    public class ProviderHealthResult
    {
        public bool IsHealthy { get; set; }
        public string? ErrorMessage { get; set; }
        public string? ExternalUserId { get; set; }
        public string? ExternalUsername { get; set; }
    }
}
