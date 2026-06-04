using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;
using Microsoft.Extensions.Logging;
using Serilog.Core;

namespace Fusion.Element.Support.CLI;

public class SimulationValidationManager : ElementManagerBase<SimulationValidationElement>
{
    public SimulationValidationManager(
        IMessageBus bus, 
        List<IElementBlueprint> configs, 
        IFireLogger<ElementManagerBase<SimulationValidationElement>> logger, 
        Func<IElementBlueprint, IFireLogger, SimulationValidationElement> elementFactory,
        string managerName)
        : base(bus, configs, logger, elementFactory, managerName)
    {
    }
}
