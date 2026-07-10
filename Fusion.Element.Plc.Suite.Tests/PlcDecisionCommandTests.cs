using Fusion.Common;
using Fusion.Element.Plc.Suite.Messages;
using Xunit;

namespace Fusion.Element.Plc.Suite.Tests;

/// <summary>
/// Verifies PlcDecisionCommand binds the payload shapes reactions actually publish,
/// matching the behavior of the JsonNode probing it replaced.
/// </summary>
public class PlcDecisionCommandTests
{
    private static MessageEnvelope Envelope(object payload) => new("PLC1.DRespM.DP1", payload);

    [Fact]
    public void PascalCasePayload_Binds()
    {
        var envelope = Envelope("""{"DecisionPoint":"DP1","GIN":42,"Actions":["LANE_2"]}""");

        Assert.True(envelope.TryGetPayload<PlcDecisionCommand>(out var command, out _));
        Assert.Equal("DP1", command!.DecisionPoint);
        Assert.Equal(42, command.Gin);
        Assert.Equal(["LANE_2"], command.Actions);
    }

    [Fact]
    public void CamelCasePayload_WithGinAsString_Binds()
    {
        var envelope = Envelope("""{"decisionPoint":"DP7","gin":"17","decisionPoints":["LANE_3","LANE_4"]}""");

        Assert.True(envelope.TryGetPayload<PlcDecisionCommand>(out var command, out _));
        Assert.Equal("DP7", command!.DecisionPoint);
        Assert.Equal(17, command.Gin);
        Assert.Null(command.Actions);
        Assert.Equal(["LANE_3", "LANE_4"], command.DecisionPoints);
    }

    [Fact]
    public void ActionsFallbackToDecisionPoints_MatchesManagerLogic()
    {
        var envelope = Envelope("""{"DecisionPoint":"DP1","GIN":1,"DecisionPoints":["A"]}""");
        envelope.TryGetPayload<PlcDecisionCommand>(out var command, out _);

        // Mirrors PlcElementManager: Actions ?? DecisionPoints ?? []
        var actions = command!.Actions ?? command.DecisionPoints ?? [];
        Assert.Equal(["A"], actions);
    }

    [Fact]
    public void MissingRequiredFields_BindAsNull_SoManagerCanReject()
    {
        var envelope = Envelope("""{"SomethingElse":true}""");

        Assert.True(envelope.TryGetPayload<PlcDecisionCommand>(out var command, out _));
        Assert.Null(command!.DecisionPoint);
        Assert.Null(command.Gin);
    }

    [Fact]
    public void TypedPayload_PassesThrough()
    {
        var typed = new PlcDecisionCommand { DecisionPoint = "DP2", Gin = 5, Actions = ["X"] };

        Assert.True(Envelope(typed).TryGetPayload<PlcDecisionCommand>(out var command, out _));
        Assert.Same(typed, command);
    }
}
