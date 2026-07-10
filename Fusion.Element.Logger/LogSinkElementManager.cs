using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;

namespace Fusion.Element.Logger;

/// <summary>
/// Manager for <see cref="SerilogSinkElement"/> instances. Discovered automatically by
/// CoreServicesExtension (Fusion.Element.*.dll scan) and instantiated when a blueprint
/// declares an element with "Manager": "LogSinkElementManager".
///
/// The elements consume the dedicated LoggingBus (raw firehose) directly; the manager
/// only wires lifecycle, status and control topics via the standard base-class pipeline.
/// </summary>
public class LogSinkElementManager : ElementManagerBase<SerilogSinkElement>
{
    public LogSinkElementManager(IMessageBus bus, List<IElementBlueprint> configs,
        IFireLogger<ElementManagerBase<SerilogSinkElement>> logger,
        Func<IElementBlueprint, IFireLogger, SerilogSinkElement> elementFactory,
        string managerName)
        : base(bus, configs, logger, elementFactory, managerName)
    {
    }
}
