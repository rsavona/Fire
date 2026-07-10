using Fusion.Common;
using Fusion.Common.Contracts;

namespace Fusion.Element.Database.Suite.Tests;

/// <summary>
/// No-op message bus for exercising elements in isolation.
/// </summary>
internal sealed class StubMessageBus : IMessageBus
{
    public Task<bool> SubscribeAsync(string topic, Delegate handler) => Task.FromResult(true);

    public Task<bool> SubscribeAsync(string topic, Func<MessageEnvelope, CancellationToken, Task> handler) => Task.FromResult(true);

    public Task<bool> SubscribeAsync<TMessage>(string topic, Func<TMessage, Task> handler) => Task.FromResult(true);

    public Task<bool> SubscribeAsync<TRequest, TResponse>(string topic, Func<TRequest, Task<TResponse>> handler) => Task.FromResult(true);

    public void Unsubscribe(string topic, Delegate handler)
    {
    }

    public Task PublishAsync(string topic, MessageEnvelope messageEnvelope, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishAsync(MessageBusTopic topic, MessageEnvelope messageEnvelope, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishStatusAsync(IElementStatus snapshot, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public List<string> GetActiveTopics() => [];

    public List<string> GetSubscriptionList(MessageBusTopic messageBusTopic) => [];

    public List<string> GetSubscriptionList(string messageBusTopic) => [];
}
