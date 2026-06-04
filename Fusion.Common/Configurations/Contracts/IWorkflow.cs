using System.Threading.Tasks;

namespace Fusion.Common.Contracts;

public interface IWorkflow
{
    Task SubscribeToTopics();
}