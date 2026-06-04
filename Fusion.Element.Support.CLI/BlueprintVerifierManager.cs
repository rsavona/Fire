using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;
using Microsoft.Extensions.Logging;

namespace Fusion.Element.Support.CLI;

public class BlueprintVerifierManager : ElementManagerBase<BlueprintVerifierElement>
{
    public BlueprintVerifierManager(
        IMessageBus bus,
        List<IElementBlueprint> configs,
        IFireLogger<BlueprintVerifierManager> logger,
        Func<IElementBlueprint, IFireLogger, BlueprintVerifierElement> elementFactory,
        string managerName)
        : base(bus, configs, logger, elementFactory, managerName)
    {
    }
}
