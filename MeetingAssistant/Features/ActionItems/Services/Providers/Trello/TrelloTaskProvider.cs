using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using MeetingAssistant.Features.ActionItems.Models.Entities;
using MeetingAssistant.Features.ActionItems.Services.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;

namespace MeetingAssistant.Features.ActionItems.Services.Providers.Trello
{
    public class TrelloTaskProvider : ITaskProvider
    {
        private readonly HttpClient _httpClient;
        private readonly IMemoryCache _cache;
        private readonly IDataProtector _dataProtector;
        private readonly ILogger<TrelloTaskProvider> _logger;

        public string ProviderName => "Trello";

        public TrelloTaskProvider(
            HttpClient httpClient,
            IMemoryCache cache,
            IDataProtectionProvider dataProtectionProvider,
            ILogger<TrelloTaskProvider> logger)
        {
            _httpClient = httpClient;
            _cache = cache;
            _dataProtector = dataProtectionProvider.CreateProtector("ActionItemsIntegration");
            _logger = logger;
        }

        public async Task<ProviderHealthResult> ValidateCredentialsAsync(
            OrganizationIntegrationConfig config,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var (key, token) = ExtractCredentials(config);
                var url = $"https://api.trello.com/1/members/me?key={key}&token={token}";

                var response = await _httpClient.GetAsync(url, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    var member = await response.Content.ReadFromJsonAsync<TrelloMemberDto>(cancellationToken);
                    return new ProviderHealthResult
                    {
                        IsHealthy = true,
                        ExternalUserId = member?.Id,
                        ExternalUsername = member?.Username
                    };
                }

                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                return new ProviderHealthResult { IsHealthy = false, ErrorMessage = error };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Trello credential validation failed");
                return new ProviderHealthResult { IsHealthy = false, ErrorMessage = ex.Message };
            }
        }

        public async Task<IReadOnlyList<ProviderProject>> ListProjectsAsync(
            OrganizationIntegrationConfig config,
            CancellationToken cancellationToken = default)
        {
            var (key, token) = ExtractCredentials(config);
            var url = $"https://api.trello.com/1/members/me/boards?key={key}&token={token}&fields=id,name";

            var response = await _httpClient.GetFromJsonAsync<List<TrelloBoardDto>>(url, cancellationToken);

            return response?
                .Select(b => new ProviderProject { Id = b.Id, Name = b.Name })
                .ToList()
                ?? new List<ProviderProject>();
        }

        public async Task<IReadOnlyList<ProviderList>> ListListsAsync(
            OrganizationIntegrationConfig config,
            string projectId,
            CancellationToken cancellationToken = default)
        {
            var (key, token) = ExtractCredentials(config);
            var url = $"https://api.trello.com/1/boards/{projectId}/lists?key={key}&token={token}&fields=id,name";

            var response = await _httpClient.GetFromJsonAsync<List<TrelloListDto>>(url, cancellationToken);

            return response?
                .Select(l => new ProviderList { Id = l.Id, Name = l.Name })
                .ToList()
                ?? new List<ProviderList>();
        }

        public async Task<ProviderTaskResult> CreateTaskAsync(
            OrganizationIntegrationConfig config,
            ProviderTaskRequest request,
            CancellationToken cancellationToken = default)
        {
            var (key, token) = ExtractCredentials(config);
            var url = "https://api.trello.com/1/cards";

            var queryParams = new Dictionary<string, string>
            {
                ["key"] = key,
                ["token"] = token,
                ["idList"] = config.SelectedListId,
                ["name"] = request.Title
            };

            if (!string.IsNullOrEmpty(request.Description))
                queryParams["desc"] = request.Description;

            if (request.DueDateUtc.HasValue)
                queryParams["due"] = request.DueDateUtc.Value.ToString("O");

            if (!string.IsNullOrEmpty(request.AssigneeExternalId))
                queryParams["idMembers"] = request.AssigneeExternalId;

            var queryString = string.Join("&", queryParams.Select(p => $"{p.Key}={HttpUtility.UrlEncode(p.Value)}"));
            var fullUrl = $"{url}?{queryString}";

            var response = await _httpClient.PostAsync(fullUrl, null, cancellationToken);
            response.EnsureSuccessStatusCode();

            var card = await response.Content.ReadFromJsonAsync<TrelloCardDto>(cancellationToken)
                ?? throw new InvalidOperationException("Trello returned null card response.");

            return new ProviderTaskResult
            {
                TaskId = card.Id,
                TaskUrl = card.Url,
                HasAssignee = !string.IsNullOrEmpty(request.AssigneeExternalId) &&
                    (card.IdMembers?.Contains(request.AssigneeExternalId) ?? false)
            };
        }

        public async Task<bool> ValidateAssigneeAsync(
            OrganizationIntegrationConfig config,
            string projectId,
            string assigneeExternalId,
            CancellationToken cancellationToken = default)
        {
            var cacheKey = $"trello_board_members_{projectId}";

            if (!_cache.TryGetValue(cacheKey, out List<TrelloMemberDto>? members) || members == null)
            {
                var (key, token) = ExtractCredentials(config);
                var url = $"https://api.trello.com/1/boards/{projectId}/members?key={key}&token={token}&fields=id,username,fullName";

                members = await _httpClient.GetFromJsonAsync<List<TrelloMemberDto>>(url, cancellationToken)
                    ?? new List<TrelloMemberDto>();

                _cache.Set(cacheKey, members, TimeSpan.FromMinutes(5));
            }

            return members.Any(m => m.Id == assigneeExternalId);
        }

        private (string Key, string Token) ExtractCredentials(OrganizationIntegrationConfig config)
        {
            var payload = _dataProtector.Unprotect(config.EncryptedProviderPayload);
            var doc = JsonDocument.Parse(payload);
            var key = doc.RootElement.GetProperty("apiKey").GetString()!;
            var token = doc.RootElement.GetProperty("apiToken").GetString()!;
            return (key, token);
        }
    }

    public class TrelloBoardDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    public class TrelloListDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    public class TrelloCardDto
    {
        public string Id { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public List<string> IdMembers { get; set; } = new();
    }

    public class TrelloMemberDto
    {
        public string Id { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
    }
}
