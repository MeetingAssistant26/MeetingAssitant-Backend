using MeetingAssistant.Features.ActionItems.Models.Entities;
using MeetingAssistant.Features.ActionItems.Services.Abstractions;
using MeetingAssistant.Tests.Integration.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeetingAssistant.Tests.Integration.ActionItems;

public class TestWebApplicationFactory : MeetingAssistantWebFactory
{
    public FakeTaskProviderState ProviderState { get; } = new();

    public TestWebApplicationFactory()
    {
        ProviderState.Reset();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ITaskProviderFactory>();
            services.RemoveAll<ITaskProvider>();

            services.AddSingleton(ProviderState);
            services.AddSingleton<FakeTaskProvider>();
            services.AddSingleton<ITaskProvider>(sp => sp.GetRequiredService<FakeTaskProvider>());
            services.AddSingleton<ITaskProviderFactory>(sp => new FakeTaskProviderFactory(sp.GetRequiredService<FakeTaskProvider>()));
        });
    }

    public void ResetProviderState() => ProviderState.Reset();

    private sealed class FakeTaskProviderFactory : ITaskProviderFactory
    {
        private readonly FakeTaskProvider _provider;

        public FakeTaskProviderFactory(FakeTaskProvider provider)
        {
            _provider = provider;
        }

        public ITaskProvider GetProvider(string providerName) => _provider;
    }

    private sealed class FakeTaskProvider : ITaskProvider
    {
        private readonly FakeTaskProviderState _state;

        public FakeTaskProvider(FakeTaskProviderState state)
        {
            _state = state;
        }

        public string ProviderName => "Trello";

        public Task<ProviderHealthResult> ValidateCredentialsAsync(
            OrganizationIntegrationConfig config,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_state.CredentialsAreValid
                ? new ProviderHealthResult
                {
                    IsHealthy = true,
                    ExternalUserId = "trello-admin-member",
                    ExternalUsername = "admin.trello"
                }
                : new ProviderHealthResult
                {
                    IsHealthy = false,
                    ErrorMessage = "provider rejected token"
                });
        }

        public Task<IReadOnlyList<ProviderProject>> ListProjectsAsync(
            OrganizationIntegrationConfig config,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ProviderProject>>(
                _state.Boards
                    .Where(board => !board.Closed)
                    .Select(board => new ProviderProject { Id = board.Id, Name = board.Name })
                    .ToList());

        public Task<IReadOnlyList<ProviderWorkspace>> ListWorkspacesAsync(
            OrganizationIntegrationConfig config,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ProviderWorkspace>>(_state.Workspaces.ToList());

        public Task<IReadOnlyList<ProviderBoard>> ListBoardsAsync(
            OrganizationIntegrationConfig config,
            string workspaceId,
            bool openOnly,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ProviderBoard>>(
                _state.Boards
                    .Where(board => board.WorkspaceId == workspaceId)
                    .Where(board => !openOnly || !board.Closed)
                    .ToList());

        public Task<IReadOnlyList<ProviderMember>> ListBoardMembersAsync(
            OrganizationIntegrationConfig config,
            string boardId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ProviderMember>>(
                _state.BoardMembersByBoard.TryGetValue(boardId, out var members)
                    ? members.ToList()
                    : new List<ProviderMember>());

        public Task<IReadOnlyList<ProviderList>> ListListsAsync(
            OrganizationIntegrationConfig config,
            string projectId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ProviderList>>(
                _state.ListsByBoard.TryGetValue(projectId, out var lists)
                    ? lists.ToList()
                    : new List<ProviderList>());

        public Task<ProviderTaskResult> CreateTaskAsync(
            OrganizationIntegrationConfig config,
            ProviderTaskRequest request,
            CancellationToken cancellationToken = default)
        {
            _state.CreatedTasks.Add(request);
            var taskId = $"trello-card-{_state.CreatedTasks.Count}";

            return Task.FromResult(new ProviderTaskResult
            {
                TaskId = taskId,
                TaskUrl = $"https://trello.example/cards/{taskId}",
                HasAssignee = !string.IsNullOrWhiteSpace(request.AssigneeExternalId)
            });
        }

        public Task<bool> ValidateAssigneeAsync(
            OrganizationIntegrationConfig config,
            string projectId,
            string assigneeExternalId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(
                _state.BoardMembersByBoard.TryGetValue(projectId, out var members)
                && members.Any(member => member.Id == assigneeExternalId));
    }
}

public sealed class FakeTaskProviderState
{
    public const string WorkspaceId = "trello-workspace-test";
    public const string BoardId = "trello-board-open";
    public const string ClosedBoardId = "trello-board-closed";
    public const string ListId = "trello-list-action-items";
    public const string MappedMemberId = "trello-member-mapped";
    public const string ConnectedMemberId = "trello-member-connected";

    public bool CredentialsAreValid { get; set; } = true;
    public List<ProviderWorkspace> Workspaces { get; } = new();
    public List<ProviderBoard> Boards { get; } = new();
    public Dictionary<string, List<ProviderList>> ListsByBoard { get; } = new();
    public Dictionary<string, List<ProviderMember>> BoardMembersByBoard { get; } = new();
    public List<ProviderTaskRequest> CreatedTasks { get; } = new();

    public void Reset()
    {
        CredentialsAreValid = true;
        Workspaces.Clear();
        Boards.Clear();
        ListsByBoard.Clear();
        BoardMembersByBoard.Clear();
        CreatedTasks.Clear();

        Workspaces.Add(new ProviderWorkspace
        {
            Id = WorkspaceId,
            Name = "qa-workspace",
            DisplayName = "QA Workspace",
            Url = "https://trello.example/w/qa-workspace"
        });
        Boards.Add(new ProviderBoard
        {
            Id = BoardId,
            Name = "QA Action Board",
            Url = "https://trello.example/b/qa-action-board",
            WorkspaceId = WorkspaceId,
            Closed = false
        });
        Boards.Add(new ProviderBoard
        {
            Id = ClosedBoardId,
            Name = "Closed Board",
            WorkspaceId = WorkspaceId,
            Closed = true
        });
        ListsByBoard[BoardId] =
        [
            new ProviderList
            {
                Id = ListId,
                Name = "Action Items"
            }
        ];
        BoardMembersByBoard[BoardId] =
        [
            new ProviderMember
            {
                Id = MappedMemberId,
                Username = "mapped.trello",
                FullName = "Mapped Trello"
            },
            new ProviderMember
            {
                Id = ConnectedMemberId,
                Username = "connected.trello",
                FullName = "Connected Trello"
            }
        ];
    }
}
