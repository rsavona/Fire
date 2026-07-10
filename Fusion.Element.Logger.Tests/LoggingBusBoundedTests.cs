using System.Collections.Concurrent;
using Fusion.Common.Logging;
using Fusion.Core;
using Serilog.Events;

namespace Fusion.Element.Logger.Tests;

public class LoggingBusBoundedTests
{
    private static LogMessage Msg(int index) => new()
    {
        Level = LogEventLevel.Information,
        Context = "TEST",
        MessageTemplate = "message {Index}",
        Args = [index],
        FormattedMessage = index.ToString()
    };

    [Fact]
    public async Task Publish_PastCapacity_DropsOldestAndCountsDrops()
    {
        var bus = new LoggingBus(capacity: 5);

        // Not started: nothing drains the channel, so writes 6..10 evict 1..5.
        for (int i = 0; i < 10; i++)
        {
            await bus.PublishAsync(Msg(i));
        }

        Assert.Equal(5, bus.DroppedMessageCount);
        Assert.Equal(5, bus.Capacity);
    }

    [Fact]
    public async Task Publish_PastCapacity_SurvivorsAreTheNewestMessages()
    {
        var bus = new LoggingBus(capacity: 5);
        var received = new ConcurrentBag<string>();
        await bus.SubscribeAsync("#", (msg, _) =>
        {
            received.Add(msg.FormattedMessage);
            return Task.CompletedTask;
        });

        for (int i = 0; i < 10; i++)
        {
            await bus.PublishAsync(Msg(i));
        }

        await bus.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (received.Count < 5 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(25);
            }
        }
        finally
        {
            await bus.StopAsync(CancellationToken.None);
        }

        // Oldest (0..4) were dropped; newest (5..9) survived.
        Assert.Equal(new[] { "5", "6", "7", "8", "9" },
            received.Select(int.Parse).OrderBy(i => i).Select(i => i.ToString()).ToArray());
        Assert.Equal(5, bus.DroppedMessageCount);
    }

    [Fact]
    public async Task Publish_WithinCapacity_DropsNothing()
    {
        var bus = new LoggingBus(capacity: 100);
        for (int i = 0; i < 50; i++)
        {
            await bus.PublishAsync(Msg(i));
        }

        Assert.Equal(0, bus.DroppedMessageCount);
    }

    [Fact]
    public void InvalidCapacity_FallsBackToDefault()
    {
        Assert.Equal(LoggingBus.DefaultCapacity, new LoggingBus(0).Capacity);
        Assert.Equal(LoggingBus.DefaultCapacity, new LoggingBus(-3).Capacity);
    }
}
