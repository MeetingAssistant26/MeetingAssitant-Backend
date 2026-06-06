using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
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
                var url = BuildUrl("members/me", key, token, "fields=id,username");

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

        public async Task<IReadOnlyList<ProviderWorkspace>> ListWorkspacesAsync(
            OrganizationIntegrationConfig config,
            CancellationToken cancellationToken = default)
        {
            var (key, token) = ExtractCredentials(config);
            var url = BuildUrl("members/me/organizations", key, token, "fields=id,name,displayName,url");

            var response = await _httpClient.GetFromJsonAsync<List<TrelloOrganizationDto>>(url, cancellationToken);

            return response?
                .Select(o => new ProviderWorkspace
                {
                    Id = o.Id,
                    Name = o.Name,
                    DisplayName = o.DisplayName,
                    Url = o.Url
                })
                .ToList()
                ?? new List<ProviderWorkspace>();
        }

        public async Task<IReadOnlyList<ProviderBoard>> ListBoardsAsync(
            OrganizationIntegrationConfig config,
            string workspaceId,
            bool openOnly,
            CancellationToken cancellationToken = default)
        {
            var (key, token) = ExtractCredentials(config);
            var url = BuildUrl($"organizations/{workspaceId}/boards", key, token, "fields=id,name,url,idOrganization,closed");

            var response = await _httpClient.GetFromJsonAsync<List<TrelloBoardDto>>(url, cancellationToken);
            var boards = response ?? new List<TrelloBoardDto>();

            if (openOnly)
                boards = boards.Where(b => !b.Closed).ToList();

            return boards
                .Select(b => new ProviderBoard
                {
                    Id = b.Id,
                    Name = b.Name,
                    Url = b.Url,
                    WorkspaceId = b.IdOrganization,
                    Closed = b.Closed
                })
                .ToList();
        }

        public async Task<IReadOnlyList<ProviderMember>> ListBoardMembersAsync(
            OrganizationIntegrationConfig config,
            string boardId,
            CancellationToken cancellationToken = default)
        {
            var cacheKey = $"trello_board_members_{boardId}";

            if (!_cache.TryGetValue(cacheKey, out List<TrelloMemberDto>? members) || members == null)
            {
                members = await FetchBoardMembersFromApiAsync(config, boardId, cancellationToken);
                _cache.Set(cacheKey, members, TimeSpan.FromMinutes(5));
            }

            return MapMembers(members);
        }

        public async Task<IReadOnlyList<ProviderProject>> ListProjectsAsync(
            OrganizationIntegrationConfig config,
            CancellationToken cancellationToken = default)
        {
            var (key, token) = ExtractCredentials(config);
            var url = BuildUrl("members/me/boards", key, token, "fields=id,name,url,idOrganization,closed");

            var response = await _httpClient.GetFromJsonAsync<List<TrelloBoardDto>>(url, cancellationToken);

            return response?
                .Where(b => !b.Closed)
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
            var url = BuildUrl($"boards/{projectId}/lists", key, token, "fields=id,name", "filter=open");

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
            var members = await FetchBoardMembersFromApiAsync(config, projectId, cancellationToken);
            return members.Any(m => m.Id == assigneeExternalId);
        }

        private async Task<List<TrelloMemberDto>> FetchBoardMembersFromApiAsync(
            OrganizationIntegrationConfig config,
            string boardId,
            CancellationToken cancellationToken)
        {
            var (key, token) = ExtractCredentials(config);
            var url = BuildUrl($"boards/{boardId}/members", key, token, "fields=id,username,fullName,avatarUrl");

            return await _httpClient.GetFromJsonAsync<List<TrelloMemberDto>>(url, cancellationToken)
                ?? new List<TrelloMemberDto>();
        }

        private static IReadOnlyList<ProviderMember> MapMembers(IEnumerable<TrelloMemberDto> members)
            => members
                .Select(m => new ProviderMember
                {
                    Id = m.Id,
                    Username = m.Username,
                    FullName = m.FullName,
                    AvatarUrl = m.AvatarUrl
                })
                .ToList();

        private (string Key, string Token) ExtractCredentials(OrganizationIntegrationConfig config)
        {
            var payload = _dataProtector.Unprotect(config.EncryptedProviderPayload);
            var doc = JsonDocument.Parse(payload);
            var key = doc.RootElement.GetProperty("apiKey").GetString()!;
            var token = doc.RootElement.GetProperty("apiToken").GetString()!;
            return (key, token);
        }

        private static string BuildUrl(string path, string key, string token, string fields, string? extraQuery = null)
        {
            var query = $"key={HttpUtility.UrlEncode(key)}&token={HttpUtility.UrlEncode(token)}&{fields}";
            if (!string.IsNullOrEmpty(extraQuery))
                query += $"&{extraQuery}";

            return $"https://api.trello.com/1/{path}?{query}";
        }
    }

    public class TrelloOrganizationDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
    }

    public class TrelloBoardDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("idOrganization")]
        public string? IdOrganization { get; set; }

        public bool Closed { get; set; }
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
        public string? AvatarUrl { get; set; }
    }
}
