using Fusion.Common;
using Fusion.Common.Contracts;
using Fusion.Common.Logging;

namespace Fusion.Element.Logger.Tests;

/// <summary>Records every publish; subscriptions are accepted and ignored.</summary>
public class FakeMessageBus : IMessageBus
{
    public List<(string Topic, MessageEnvelope Envelope)> Published { get; } = new();

    public Task<bool> SubscribeAsync(string topic, Delegate handler) => Task.FromResult(true);

    public Task<bool> SubscribeAsync(string topic, Func<MessageEnvelope, CancellationToken, Task> handler) =>
        Task.FromResult(true);

    public Task<bool> SubscribeAsync<TMessage>(string topic, Func<TMessage, Task> handler) => Task.FromResult(true);

    public Task<bool> SubscribeAsync<TRequest, TResponse>(string topic, Func<TRequest, Task<TResponse>> handler) =>
        Task.FromResult(true);

    public void Unsubscribe(string topic, Delegate handler)
    {
    }

    public Task PublishAsync(string topic, MessageEnvelope messageEnvelope,
        CancellationToken cancellationToken = default)
    {
        lock (Published)
        {
            Published.Add((topic, messageEnvelope));
        }

        return Task.CompletedTask;
    }

    public Task PublishAsync(MessageBusTopic topic, MessageEnvelope messageEnvelope,
        CancellationToken cancellationToken = default) =>
        PublishAsync(topic.ToString(), messageEnvelope, cancellationToken);

    public Task PublishStatusAsync(IElementStatus snapshot, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public List<string> GetActiveTopics() => new();
    public List<string> GetSubscriptionList(MessageBusTopic messageBusTopic) => new();
    public List<string> GetSubscriptionList(string messageBusTopic) => new();
}

/// <summary>Captures subscriptions so tests can push messages straight to handlers.</summary>
public class FakeLoggingBus : ILoggingBus
{
    public List<(string Pattern, Func<LogMessage, CancellationToken, Task> Handler)> Subscriptions { get; } = new();
    public List<LogMessage> Published { get; } = new();

    public Task PublishAsync(LogMessage logMessage, CancellationToken ct = default)
    {
        Published.Add(logMessage);
        return Task.CompletedTask;
    }

    public Task SubscribeAsync(string topicPattern, Func<LogMessage, CancellationToken, Task> handler)
    {
        Subscriptions.Add((topicPattern, handler));
        return Task.CompletedTask;
    }
}
