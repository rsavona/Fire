using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;

namespace Fusion.Element.Link;

/// <summary>
/// Manages LinkServerElement instances. The server element wires itself to the
/// bus per remote subscription, so this manager only handles lifecycle.
/// </summary>
public class LinkServerElementManager : ElementManagerBase<LinkServerElement>
{
    public LinkServerElementManager(IMessageBus bus, List<IElementBlueprint> configs,
        IFireLogger<ElementManagerBase<LinkServerElement>> logger,
        Func<IElementBlueprint, IFireLogger, LinkServerElement> elementFactory,
        string managerName)
        : base(bus, configs, logger, elementFactory, managerName)
    {
    }
}
