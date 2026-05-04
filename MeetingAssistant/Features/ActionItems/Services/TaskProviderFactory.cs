using MeetingAssistant.Features.ActionItems.Services.Abstractions;

namespace MeetingAssistant.Features.ActionItems.Services
{
    public class TaskProviderFactory : ITaskProviderFactory
    {
        private readonly IEnumerable<Abstractions.ITaskProvider> _providers;

        public TaskProviderFactory(IEnumerable<Abstractions.ITaskProvider> providers)
        {
            _providers = providers;
        }

        public Abstractions.ITaskProvider GetProvider(string providerName)
        {
            var provider = _providers.FirstOrDefault(p =>
                p.ProviderName.Equals(providerName, StringComparison.OrdinalIgnoreCase));

            if (provider == null)
                throw new NotSupportedException($"Provider '{providerName}' is not supported.");

            return provider;
        }
    }
}
