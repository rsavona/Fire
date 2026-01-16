using System.Threading;
using System.Threading.Tasks;

namespace DeviceSpace.Common.Contracts;

public interface IWorkflowAdapter
{
    Task StartAsync(CancellationToken token);
    Task StopAsync(CancellationToken token);
    
    Task HandleMessageAsync(MessageEnvelope envelope); 
}