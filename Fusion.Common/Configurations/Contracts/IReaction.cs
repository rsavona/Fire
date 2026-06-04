using System.Threading.Tasks;

namespace Fusion.Common.Contracts;

public interface IReaction
{
    Task SubscribeToTopics();
}