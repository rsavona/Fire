using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;

namespace Fusion.Element.Support.CLI;

/// <summary>
/// Manages the Diagnostic telnet server.
/// </summary>
public class DiagnosticElementManager : ElementManagerBase<DiagnosticElement>, IElementManager
{
    public DiagnosticElementManager(IMessageBus bus, List<IElementBlueprint> configs,
        IFireLogger<ElementManagerBase<DiagnosticElement>> logger,
        Func<IElementBlueprint, IFireLogger, DiagnosticElement> elementFactory,
        string managerName)
        : base(bus, configs, logger, elementFactory, managerName)
    {
    }

    protected override async Task OnElementMessageToMessageBusAsync(object? dev, object messEnv)
    {
        if (dev is not DiagnosticElement element || messEnv is not MessageEnvelope env) return;
        await MessageBus.PublishAsync(env.Destination.ToString(), env);
    }

    protected override async Task HandleBusMessageAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        // Handle custom diagnostic commands from the bus if needed
        await Task.CompletedTask;
    }
}
