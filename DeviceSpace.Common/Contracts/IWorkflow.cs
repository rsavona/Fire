using System.Threading.Tasks;

namespace DeviceSpace.Common.Contracts;

public interface IWorkflow
{
    Task SubscribeToTopics();
}