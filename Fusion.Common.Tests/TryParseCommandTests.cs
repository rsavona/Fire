using Fusion.Common;
using Fusion.Common.BaseClasses;
using Fusion.Common.Contracts;
using Fusion.Common.Logging;
using Serilog;
using Xunit;

namespace Fusion.Common.Tests;

/// <summary>
/// Verifies ElementManagerBase.TryParseCommand: success binds the command,
/// failure logs AND publishes a BusErrorMessage instead of silently dropping.
/// </summary>
public class TryParseCommandTests
{
    private record TestCommand : ElementMessageBase
    {
        public string? Sql { get; init; }
    }

    [Fact]
    public void ValidPayload_Parses_WithoutErrorPublication()
    {
        var (manager, bus) = CreateManager();
        var envelope = new MessageEnvelope("DB1.Execute", """{"Sql":"SELECT 1"}""");

        bool ok = manager.InvokeTryParse<TestCommand>(envelope, out var command);

        Assert.True(ok);
        Assert.Equal("SELECT 1", command!.Sql);
        Assert.Empty(bus.Published);
    }

    [Fact]
    public void MalformedPayload_Fails_AndPublishesBusError()
    {
        var (manager, bus) = CreateManager();
        var envelope = new MessageEnvelope("DB1.Execute", "this is not json");

        bool ok = manager.InvokeTryParse<TestCommand>(envelope, out var command);

        Assert.False(ok);
        Assert.Null(command);

        var (topic, published) = Assert.Single(bus.Published);
        Assert.Equal(MessageBusTopic.InternalError.ToString(), topic);
        var error = Assert.IsType<BusErrorMessage>(published.Payload);
        Assert.Contains(nameof(TestCommand), error.ExceptionMessage);
        Assert.Equal("DB1.EXECUTE", error.OriginalTopic);
    }

    [Fact]
    public void NullPayload_Fails_AndPublishesBusError()
    {
        var (manager, bus) = CreateManager();
        var envelope = new MessageEnvelope("DB1.Execute", null!);

        bool ok = manager.InvokeTryParse<TestCommand>(envelope, out _);

        Assert.False(ok);
        Assert.Single(bus.Published);
    }

    // ------------------------------------------------------------------
    //                           Test doubles
    // ------------------------------------------------------------------

    private static (TestManager, RecordingMessageBus) CreateManager()
    {
        var bus = new RecordingMessageBus();
        var logger = new FireLogger<ElementManagerBase<StubElement>>(
            new LoggerConfiguration().CreateLogger());
        var manager = new TestManager(bus, [], logger, (_, _) => throw new NotSupportedException(), "TestManager");
        return (manager, bus);
    }

    private sealed class TestManager : ElementManagerBase<StubElement>
    {
        public TestManager(IMessageBus bus, List<IElementBlueprint> configs,
            IFireLogger<ElementManagerBase<StubElement>> logger,
            Func<IElementBlueprint, IFireLogger, StubElement> elementFactory,
            string managerName)
            : base(bus, configs, logger, elementFactory, managerName)
        {
        }

        public bool InvokeTryParse<T>(MessageEnvelope envelope, out T? command) where T : class =>
            TryParseCommand(envelope, out command);
    }

    private sealed class RecordingMessageBus : IMessageBus
    {
        public List<(string Topic, MessageEnvelope Envelope)> Published { get; } = [];

        public Task PublishAsync(string topic, MessageEnvelope messageEnvelope, CancellationToken cancellationToken = default)
        {
            Published.Add((topic, messageEnvelope));
            return Task.CompletedTask;
        }

        public Task PublishAsync(MessageBusTopic topic, MessageEnvelope messageEnvelope, CancellationToken cancellationToken = default) =>
            PublishAsync(topic.ToString(), messageEnvelope, cancellationToken);

        public Task PublishStatusAsync(IElementStatus snapshot, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> SubscribeAsync(string topic, Delegate handler) => Task.FromResult(true);
        public Task<bool> SubscribeAsync(string topic, Func<MessageEnvelope, CancellationToken, Task> handler) => Task.FromResult(true);
        public Task<bool> SubscribeAsync<TMessage>(string topic, Func<TMessage, Task> handler) => Task.FromResult(true);
        public Task<bool> SubscribeAsync<TRequest, TResponse>(string topic, Func<TRequest, Task<TResponse>> handler) => Task.FromResult(true);
        public void Unsubscribe(string topic, Delegate handler) { }
        public List<string> GetActiveTopics() => [];
        public List<string> GetSubscriptionList(MessageBusTopic messageBusTopic) => [];
        public List<string> GetSubscriptionList(string messageBusTopic) => [];
    }

    private sealed class StubElement : IElement
    {
        public ElementKey Key => throw new NotSupportedException();
        public IElementBlueprint Config => throw new NotSupportedException();
        public string? TestCounterpart => null;
        public bool NeedsHeartbeat { get; set; }
        public event Action<IElement, IElementStatus> StatusUpdated { add { } remove { } }
        public event Action<IElement>? ElementReady { add { } remove { } }
        public IElementStatus CreateStatusSnapshot(string comment = "") => throw new NotSupportedException();
        public IFireLogger GetLogger() => throw new NotSupportedException();
        public string ExportToGraphviz() => throw new NotSupportedException();
        public string GetElementVersion() => throw new NotSupportedException();
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task StopAsync(CancellationToken token) => Task.CompletedTask;
        public IEnumerable<DiagCommand> GetAvailableCommands() => [];
        public void OnError(string context, Exception? ex = null) { }
        public void RefreshStatus() { }
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
