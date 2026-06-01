using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Livekit.Server.Sdk.Dotnet;
using MeetingAssistant.Features.LiveSession.Contracts.Responses;
using MeetingAssistant.Features.LiveSession.Infrastructure;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MeetingAssistant.Features.LiveSession.Services
{
    public class AiAssistantDispatchService : IAiAssistantDispatchService
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly ApplicationDbContext _dbContext;
        private readonly HttpClient _httpClient;
        private readonly LiveKitOptions _options;
        private readonly ILogger<AiAssistantDispatchService> _logger;

        public AiAssistantDispatchService(
            ApplicationDbContext dbContext,
            IHttpClientFactory httpClientFactory,
            IOptions<LiveKitOptions> options,
            ILogger<AiAssistantDispatchService> logger)
        {
            _dbContext = dbContext;
            _httpClient = httpClientFactory.CreateClient("livekit-agent-dispatch");
            _options = options.Value;
            _logger = logger;
        }

        public async Task<Result<AiAssistantStatusResponse>> GetStatusAsync(
            Guid organizationId,
            Guid meetingId,
            Guid callerUserId,
            CancellationToken cancellationToken = default)
        {
            var access = await ValidateOrganizationAdminMeetingAsync(
                organizationId,
                meetingId,
                callerUserId,
                requireJoinableMeeting: false,
                cancellationToken);

            if (access.IsFailure)
            {
                return Result.Failure<AiAssistantStatusResponse>(access.Error);
            }

            if (!access.Value.AiAssistantEnabled)
            {
                return Result.Success(new AiAssistantStatusResponse(false, false, null));
            }

            var dispatchesResult = await ListDispatchesAsync(
                RoomName(meetingId),
                treatUnavailableRoomAsEmpty: true,
                cancellationToken);
            return dispatchesResult.IsSuccess
                ? Result.Success(ToStatus(enabled: true, dispatchesResult.Value))
                : Result.Failure<AiAssistantStatusResponse>(dispatchesResult.Error);
        }

        public async Task<Result<AiAssistantStatusResponse>> EnableAsync(
            Guid organizationId,
            Guid meetingId,
            Guid callerUserId,
            CancellationToken cancellationToken = default)
        {
            var access = await ValidateOrganizationAdminMeetingAsync(
                organizationId,
                meetingId,
                callerUserId,
                requireJoinableMeeting: true,
                cancellationToken);

            if (access.IsFailure)
            {
                return Result.Failure<AiAssistantStatusResponse>(access.Error);
            }

            var dispatchResult = await DispatchEnabledMeetingAsync(
                organizationId,
                meetingId,
                callerUserId,
                cancellationToken);
            if (dispatchResult.IsFailure)
            {
                return Result.Failure<AiAssistantStatusResponse>(dispatchResult.Error);
            }

            access.Value.AiAssistantEnabled = true;
            await _dbContext.SaveChangesAsync(cancellationToken);

            return dispatchResult;
        }

        public async Task<Result<AiAssistantStatusResponse>> DisableAsync(
            Guid organizationId,
            Guid meetingId,
            Guid callerUserId,
            CancellationToken cancellationToken = default)
        {
            var access = await ValidateOrganizationAdminMeetingAsync(
                organizationId,
                meetingId,
                callerUserId,
                requireJoinableMeeting: false,
                cancellationToken);

            if (access.IsFailure)
            {
                return Result.Failure<AiAssistantStatusResponse>(access.Error);
            }

            var roomName = RoomName(meetingId);
            var existingResult = await ListDispatchesAsync(
                roomName,
                treatUnavailableRoomAsEmpty: true,
                cancellationToken);
            if (existingResult.IsFailure)
            {
                return Result.Failure<AiAssistantStatusResponse>(existingResult.Error);
            }

            foreach (var dispatch in ActiveAssistantDispatches(existingResult.Value))
            {
                var deleteResult = await SendDispatchRequestAsync(
                    "DeleteDispatch",
                    new DeleteDispatchRequest(roomName, dispatch.DispatchId),
                    _ => dispatch,
                    cancellationToken);

                if (deleteResult.IsFailure)
                {
                    return Result.Failure<AiAssistantStatusResponse>(deleteResult.Error);
                }
            }

            access.Value.AiAssistantEnabled = false;
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Deleted LiveKit AI assistant dispatches. OrganizationId={OrganizationId} MeetingId={MeetingId} RoomName={RoomName} AgentName={AgentName}",
                organizationId,
                meetingId,
                roomName,
                AgentName);

            return Result.Success(new AiAssistantStatusResponse(false, false, null));
        }

        public async Task<Result<AiAssistantStatusResponse>> EnsureDispatchedForMeetingAsync(
            Guid organizationId,
            Guid meetingId,
            Guid? requestedByUserId = null,
            CancellationToken cancellationToken = default)
        {
            var meeting = await _dbContext.Meetings
                .IgnoreQueryFilters()
                .Where(m => m.Id == meetingId && m.OrganizationId == organizationId)
                .Select(m => new
                {
                    m.AiAssistantEnabled,
                    m.Status
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (meeting is null)
            {
                return Result.Failure<AiAssistantStatusResponse>(LiveSessionErrors.MeetingNotFound);
            }

            if (!meeting.AiAssistantEnabled)
            {
                return Result.Success(new AiAssistantStatusResponse(false, false, null));
            }

            if (meeting.Status is MeetingStatus.Cancelled or MeetingStatus.Completed or MeetingStatus.Failed)
            {
                return Result.Failure<AiAssistantStatusResponse>(LiveSessionErrors.MeetingNotJoinable);
            }

            return await DispatchEnabledMeetingAsync(
                organizationId,
                meetingId,
                requestedByUserId,
                cancellationToken);
        }

        private async Task<Result<Meeting>> ValidateOrganizationAdminMeetingAsync(
            Guid organizationId,
            Guid meetingId,
            Guid callerUserId,
            bool requireJoinableMeeting,
            CancellationToken cancellationToken)
        {
            var access = await _dbContext.Meetings
                .IgnoreQueryFilters()
                .Where(m => m.Id == meetingId && m.OrganizationId == organizationId)
                .Select(m => new
                {
                    Meeting = m,
                    m.Status,
                    IsOrganizationAdmin = _dbContext.UserOrgMemberships
                        .IgnoreQueryFilters()
                        .Any(x => x.OrganizationId == organizationId
                                  && x.UserId == callerUserId
                                  && x.IsEnabled
                                  && x.OrgRole == OrganizationRole.Admin)
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (access is null)
            {
                return Result.Failure<Meeting>(LiveSessionErrors.MeetingNotFound);
            }

            if (!access.IsOrganizationAdmin)
            {
                return Result.Failure<Meeting>(OrganizationErrors.Unauthorized);
            }

            if (requireJoinableMeeting
                && access.Status is MeetingStatus.Cancelled or MeetingStatus.Completed or MeetingStatus.Failed)
            {
                return Result.Failure<Meeting>(LiveSessionErrors.MeetingNotJoinable);
            }

            return Result.Success(access.Meeting);
        }

        private async Task<Result<IReadOnlyList<DispatchSummary>>> ListDispatchesAsync(
            string roomName,
            bool treatUnavailableRoomAsEmpty,
            CancellationToken cancellationToken)
        {
            var result = await SendDispatchRequestAsync(
                "ListDispatch",
                new ListDispatchRequest(roomName),
                ParseDispatchList,
                cancellationToken);

            if (result.IsFailure
                && treatUnavailableRoomAsEmpty
                && result.Error == LiveSessionErrors.LiveKitRoomUnavailable)
            {
                return Result.Success<IReadOnlyList<DispatchSummary>>([]);
            }

            return result;
        }

        private async Task<Result<AiAssistantStatusResponse>> DispatchEnabledMeetingAsync(
            Guid organizationId,
            Guid meetingId,
            Guid? requestedByUserId,
            CancellationToken cancellationToken)
        {
            var roomName = RoomName(meetingId);
            var roomResult = await EnsureRoomExistsAsync(roomName, cancellationToken);
            if (roomResult.IsFailure)
            {
                return Result.Failure<AiAssistantStatusResponse>(roomResult.Error);
            }

            var existingResult = await ListDispatchesAsync(
                roomName,
                treatUnavailableRoomAsEmpty: false,
                cancellationToken);
            if (existingResult.IsFailure)
            {
                return Result.Failure<AiAssistantStatusResponse>(existingResult.Error);
            }

            var existing = ActiveAssistantDispatches(existingResult.Value).FirstOrDefault();
            if (existing is not null)
            {
                return Result.Success(ToStatus(enabled: true, existingResult.Value));
            }

            var metadata = JsonSerializer.Serialize(new
            {
                organizationId,
                meetingId,
                requestedByUserId
            }, JsonOptions);

            var created = await SendDispatchRequestAsync(
                "CreateDispatch",
                new CreateDispatchRequest(roomName, AgentName, metadata),
                ParseDispatch,
                cancellationToken);

            if (created.IsFailure)
            {
                return Result.Failure<AiAssistantStatusResponse>(created.Error);
            }

            _logger.LogInformation(
                "Dispatched LiveKit AI assistant. OrganizationId={OrganizationId} MeetingId={MeetingId} RoomName={RoomName} DispatchId={DispatchId} AgentName={AgentName}",
                organizationId,
                meetingId,
                roomName,
                created.Value.DispatchId,
                AgentName);

            return Result.Success(ToStatus(enabled: true, [created.Value]));
        }

        private async Task<Result> EnsureRoomExistsAsync(string roomName, CancellationToken cancellationToken)
        {
            var hostResult = ResolveLiveKitHttpHost();
            if (hostResult.IsFailure)
            {
                return Result.Failure(hostResult.Error);
            }

            if (string.IsNullOrWhiteSpace(_options.ApiKey) || string.IsNullOrWhiteSpace(_options.ApiSecret))
            {
                return Result.Failure(LiveSessionErrors.LiveKitCallFailed);
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{hostResult.Value}/twirp/livekit.RoomService/CreateRoom");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateRoomCreateToken());
            request.Content = JsonContent.Create(new CreateRoomRequest(roomName), options: JsonOptions);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return Result.Success();
            }

            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "LiveKit room creation API call failed. RoomName={RoomName} StatusCode={StatusCode} Body={Body}",
                roomName,
                (int)response.StatusCode,
                errorBody);
            return Result.Failure(LiveSessionErrors.LiveKitCallFailed);
        }

        private async Task<Result<T>> SendDispatchRequestAsync<T>(
            string method,
            object body,
            Func<JsonDocument, T> parse,
            CancellationToken cancellationToken)
        {
            var hostResult = ResolveLiveKitHttpHost();
            if (hostResult.IsFailure)
            {
                return Result.Failure<T>(hostResult.Error);
            }

            if (string.IsNullOrWhiteSpace(_options.ApiKey) || string.IsNullOrWhiteSpace(_options.ApiSecret))
            {
                return Result.Failure<T>(LiveSessionErrors.LiveKitCallFailed);
            }

            var roomName = body switch
            {
                CreateDispatchRequest create => create.Room,
                DeleteDispatchRequest delete => delete.Room,
                ListDispatchRequest list => list.Room,
                _ => string.Empty
            };

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{hostResult.Value}/twirp/livekit.AgentDispatchService/{method}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateRoomAdminToken(roomName));
            request.Content = JsonContent.Create(body, options: JsonOptions);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning(
                    "LiveKit agent dispatch API call failed. Method={Method} StatusCode={StatusCode} Body={Body}",
                    method,
                    (int)response.StatusCode,
                    errorBody);
                if (method == "ListDispatch" && IsLiveKitRoomUnavailable(errorBody))
                {
                    return Result.Failure<T>(LiveSessionErrors.LiveKitRoomUnavailable);
                }

                return Result.Failure<T>(LiveSessionErrors.LiveKitCallFailed);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return Result.Success(parse(document));
        }

        private string CreateRoomAdminToken(string roomName)
        {
            return new AccessToken(_options.ApiKey, _options.ApiSecret)
                .WithIdentity("meetingassistant-api")
                .WithName("Meeting Assistant API")
                .WithGrants(new VideoGrants
                {
                    RoomAdmin = true,
                    Room = roomName
                })
                .WithTtl(TimeSpan.FromMinutes(5))
                .ToJwt();
        }

        private string CreateRoomCreateToken()
        {
            return new AccessToken(_options.ApiKey, _options.ApiSecret)
                .WithIdentity("meetingassistant-api")
                .WithName("Meeting Assistant API")
                .WithGrants(new VideoGrants
                {
                    RoomCreate = true,
                    RoomList = true,
                    RoomAdmin = true
                })
                .WithTtl(TimeSpan.FromMinutes(5))
                .ToJwt();
        }

        private Result<string> ResolveLiveKitHttpHost()
        {
            var host = !string.IsNullOrWhiteSpace(_options.EgressHost)
                ? _options.EgressHost
                : _options.ServerUrl;

            if (string.IsNullOrWhiteSpace(host))
            {
                return Result.Failure<string>(LiveSessionErrors.LiveKitCallFailed);
            }

            host = host.TrimEnd('/');
            if (host.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            {
                host = $"https://{host[6..]}";
            }
            else if (host.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
            {
                host = $"http://{host[5..]}";
            }

            return host.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                   || host.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? Result.Success(host)
                : Result.Failure<string>(LiveSessionErrors.LiveKitCallFailed);
        }

        private static bool IsLiveKitRoomUnavailable(string errorBody)
        {
            return errorBody.Contains("\"code\":\"unavailable\"", StringComparison.OrdinalIgnoreCase)
                   && errorBody.Contains("no response from servers", StringComparison.OrdinalIgnoreCase);
        }

        private AiAssistantStatusResponse ToStatus(bool enabled, IReadOnlyList<DispatchSummary> dispatches)
        {
            var active = ActiveAssistantDispatches(dispatches).ToList();
            var first = active.FirstOrDefault();
            return new AiAssistantStatusResponse(
                enabled,
                active.Any(dispatch => dispatch.AgentIsConnected),
                first?.DispatchId);
        }

        private IEnumerable<DispatchSummary> ActiveAssistantDispatches(IReadOnlyList<DispatchSummary> dispatches)
        {
            return dispatches.Where(dispatch =>
                string.Equals(dispatch.AgentName, AgentName, StringComparison.Ordinal)
                && !dispatch.Deleted);
        }

        private string AgentName => string.IsNullOrWhiteSpace(_options.AgentName)
            ? "meeting-assistant"
            : _options.AgentName;

        private static string RoomName(Guid meetingId) => $"mtg:{meetingId}";

        private static IReadOnlyList<DispatchSummary> ParseDispatchList(JsonDocument document)
        {
            if (!TryGetProperty(document.RootElement, "agent_dispatches", "agentDispatches", out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return items.EnumerateArray().Select(ParseDispatch).ToList();
        }

        private static DispatchSummary ParseDispatch(JsonDocument document) => ParseDispatch(document.RootElement);

        private static DispatchSummary ParseDispatch(JsonElement element)
        {
            var dispatchId = GetString(element, "id") ?? string.Empty;
            var agentName = GetString(element, "agent_name", "agentName") ?? string.Empty;
            var deleted = false;
            var agentIsConnected = false;

            if (TryGetProperty(element, "state", out var state))
            {
                var deletedAt = GetInt64(state, "deleted_at", "deletedAt");
                deleted = deletedAt.GetValueOrDefault() > 0;

                if (TryGetProperty(state, "jobs", out var jobs) && jobs.ValueKind == JsonValueKind.Array)
                {
                    agentIsConnected = jobs.EnumerateArray().Any(IsRunningJob);
                }
            }

            return new DispatchSummary(dispatchId, agentName, deleted, agentIsConnected);
        }

        private static bool IsRunningJob(JsonElement job)
        {
            var status = default(string);
            var participantIdentity = default(string);

            if (TryGetProperty(job, "state", out var state))
            {
                status = GetString(state, "status");
                participantIdentity = GetString(state, "participant_identity", "participantIdentity");
            }

            return string.Equals(status, "JS_RUNNING", StringComparison.OrdinalIgnoreCase)
                   || !string.IsNullOrWhiteSpace(participantIdentity);
        }

        private static string? GetString(JsonElement element, params string[] names)
        {
            return TryGetProperty(element, names, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        private static long? GetInt64(JsonElement element, params string[] names)
        {
            if (!TryGetProperty(element, names, out var value))
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt64(out var number) => number,
                JsonValueKind.String when long.TryParse(value.GetString(), out var number) => number,
                _ => null
            };
        }

        private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
            => TryGetProperty(element, [name], out value);

        private static bool TryGetProperty(JsonElement element, string name, string alternateName, out JsonElement value)
            => TryGetProperty(element, [name, alternateName], out value);

        private static bool TryGetProperty(JsonElement element, string[] names, out JsonElement value)
        {
            foreach (var name in names)
            {
                if (element.TryGetProperty(name, out value))
                {
                    return true;
                }
            }

            value = default;
            return false;
        }

        private sealed record CreateDispatchRequest(
            [property: JsonPropertyName("room")] string Room,
            [property: JsonPropertyName("agent_name")] string AgentName,
            [property: JsonPropertyName("metadata")] string Metadata);

        private sealed record DeleteDispatchRequest(
            [property: JsonPropertyName("room")] string Room,
            [property: JsonPropertyName("dispatch_id")] string DispatchId);

        private sealed record ListDispatchRequest([property: JsonPropertyName("room")] string Room);

        private sealed record CreateRoomRequest([property: JsonPropertyName("name")] string Name);

        private sealed record DispatchSummary(
            string DispatchId,
            string AgentName,
            bool Deleted,
            bool AgentIsConnected);
    }
}
