using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;
using Fusion.Common.Logging;

namespace Fusion.Element.ActiveMQ;

public class ActiveMqBrowserManager : ElementManagerBase<ActiveMqBrowserElement>
{
    public ActiveMqBrowserManager(
        IMessageBus bus,
        List<IElementBlueprint> config,
        IFireLogger<ActiveMqBrowserManager> logger,
        Func<IElementBlueprint, IFireLogger, ActiveMqBrowserElement> elementFactory,
        string managerName)
        : base(bus, config, logger, elementFactory, managerName)
    {
    }
}
