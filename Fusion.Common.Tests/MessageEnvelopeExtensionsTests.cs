using System.Text;
using Fusion.Common;
using Fusion.Common.BaseClasses;
using Xunit;

namespace Fusion.Common.Tests;

public class MessageEnvelopeExtensionsTests
{
    private record TestCommand : ElementMessageBase
    {
        public string? Sql { get; init; }
        public string? DecisionPoint { get; init; }
        public int? Gin { get; init; }
        public List<string>? Actions { get; init; }
    }

    private static MessageEnvelope Envelope(object payload) =>
        new("TEST.Command", payload);

    [Fact]
    public void TypedPayload_PassesThroughDirectly()
    {
        var command = new TestCommand { Sql = "SELECT 1", Gin = 42 };

        bool ok = Envelope(command).TryGetPayload<TestCommand>(out var result, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Same(command, result);
    }

    [Fact]
    public void JsonString_Deserializes_CaseInsensitively()
    {
        const string json = """{"sql":"SELECT 1","decisionPoint":"DP1","GIN":7}""";

        bool ok = Envelope(json).TryGetPayload<TestCommand>(out var result, out _);

        Assert.True(ok);
        Assert.Equal("SELECT 1", result!.Sql);
        Assert.Equal("DP1", result.DecisionPoint);
        Assert.Equal(7, result.Gin);
    }

    [Fact]
    public void NumberAsString_IsAccepted()
    {
        const string json = """{"Gin":"123"}""";

        bool ok = Envelope(json).TryGetPayload<TestCommand>(out var result, out _);

        Assert.True(ok);
        Assert.Equal(123, result!.Gin);
    }

    [Fact]
    public void Utf8Bytes_Deserialize()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("""{"Actions":["DIVERT_1","DIVERT_2"]}""");

        bool ok = Envelope(bytes).TryGetPayload<TestCommand>(out var result, out _);

        Assert.True(ok);
        Assert.Equal(["DIVERT_1", "DIVERT_2"], result!.Actions);
    }

    [Fact]
    public void AnonymousObjectPayload_RoundTripsThroughJson()
    {
        var payload = new { Sql = "SELECT 2", Gin = 9 };

        bool ok = Envelope(payload).TryGetPayload<TestCommand>(out var result, out _);

        Assert.True(ok);
        Assert.Equal("SELECT 2", result!.Sql);
        Assert.Equal(9, result.Gin);
    }

    [Fact]
    public void MalformedJson_FailsWithError_DoesNotThrow()
    {
        bool ok = Envelope("not json at all").TryGetPayload<TestCommand>(out var result, out var error);

        Assert.False(ok);
        Assert.Null(result);
        Assert.NotNull(error);
        Assert.Contains("TestCommand", error);
    }

    [Fact]
    public void EmptyString_FailsWithError()
    {
        bool ok = Envelope("  ").TryGetPayload<TestCommand>(out _, out var error);

        Assert.False(ok);
        Assert.Equal("Payload is empty.", error);
    }

    [Fact]
    public void GetPayloadText_HandlesAllThreeShapes()
    {
        Assert.Equal("hello", Envelope("hello").GetPayloadText());
        Assert.Equal("hello", Envelope(Encoding.UTF8.GetBytes("hello")).GetPayloadText());
        Assert.Contains("\"Sql\":\"SELECT 1\"", Envelope(new { Sql = "SELECT 1" }).GetPayloadText());
    }
}
