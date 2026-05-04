namespace MeetingAssistant.Features.ActionItems.Services.Abstractions
{
    public interface ITaskProviderFactory
    {
        ITaskProvider GetProvider(string providerName);
    }
}
