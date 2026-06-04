using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;
using Fusion.Common.Logging;

namespace Fusion.Element.ActiveMQ;

public class ActiveMqQueuePeekManager : ElementManagerBase<ActiveMqQueuePeekElement>
{
    public ActiveMqQueuePeekManager(
        IMessageBus bus,
        List<IElementBlueprint> config,
        IFireLogger<ActiveMqQueuePeekManager> logger,
        Func<IElementBlueprint, IFireLogger, ActiveMqQueuePeekElement> elementFactory,
        string managerName)
        : base(bus, config, logger, elementFactory, managerName)
    {
    }
}
