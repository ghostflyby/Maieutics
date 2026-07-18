using Maieutics.Agent;
using Maieutics.Jupyter;

namespace Maieutics.Configuration;

internal interface IMaieuticsRuntimeConfiguration : IAgentRunProfileProvider
{
    string ConnectionFile { get; }

    long Version { get; }

    MaieuticsAgentKernelOptions GetKernelOptions();
}