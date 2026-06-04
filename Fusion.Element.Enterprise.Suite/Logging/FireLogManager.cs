using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;
using Microsoft.Extensions.Logging;

namespace Fusion.Element.Enterprise.Suite.Logging;

public class FireLogManager : ElementManagerBase<FireLogElement>
{
    public FireLogManager(IMessageBus bus, List<IElementBlueprint> configs,
        IFireLogger<FireLogManager> logger,
        Func<IElementBlueprint, IFireLogger, FireLogElement> elementFactory,
        string managerName)
        : base(bus, configs, logger, elementFactory, managerName)
    {
    }
}
