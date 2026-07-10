using System.Text.Json;
using Fusion.Common;
using Xunit;

namespace Fusion.Element.Database.Suite.Tests;

/// <summary>
/// Verifies DatabaseCommand binds the legacy JSON payload shapes the JsonNode
/// probing used to accept, and that parameter conversion matches Dapper's needs.
/// </summary>
public class DatabaseCommandTests
{
    private static MessageEnvelope Envelope(object payload) => new("DB1.Execute", payload);

    [Fact]
    public void LegacyPayload_MixedCase_Binds()
    {
        const string json = """
            {"sql":"SELECT * FROM Cartons WHERE Id = @Id",
             "Operation":"QUERY",
             "commandType":"StoredProcedure",
             "decisionPoint":"DP1",
             "GIN":42}
            """;

        Assert.True(Envelope(json).TryGetPayload<DatabaseCommand>(out var command, out _));
        Assert.Equal("SELECT * FROM Cartons WHERE Id = @Id", command!.Sql);
        Assert.Equal("QUERY", command.Operation);
        Assert.Equal("StoredProcedure", command.CommandType);
        Assert.Equal("DP1", command.DecisionPoint);
        Assert.Equal(42, command.Gin);
    }

    [Fact]
    public void MissingSql_BindsAsNull_SoManagerCanReject()
    {
        Assert.True(Envelope("""{"Operation":"QUERY"}""").TryGetPayload<DatabaseCommand>(out var command, out _));
        Assert.Null(command!.Sql);
    }

    [Fact]
    public void Parameters_ConvertToClrTypes()
    {
        const string json = """
            {"Sql":"x","Parameters":{
                "Id":5,
                "Price":19.95,
                "Name":"tote",
                "Active":true,
                "Missing":null,
                "Tags":["a","b"],
                "Extra":{"k":1}}}
            """;

        Envelope(json).TryGetPayload<DatabaseCommand>(out var command, out _);
        var converted = DatabaseElementManager.ConvertParameters(command!.Parameters);

        Assert.NotNull(converted);
        Assert.Equal(5L, converted!["Id"]);
        Assert.Equal(19.95, converted["Price"]);
        Assert.Equal("tote", converted["Name"]);
        Assert.Equal(true, converted["Active"]);
        Assert.Null(converted["Missing"]);
        Assert.Equal("""["a","b"]""", converted["Tags"]);
        Assert.Equal("""{"k":1}""", converted["Extra"]);
    }

    [Fact]
    public void NoParameters_ConvertsToNull()
    {
        Assert.Null(DatabaseElementManager.ConvertParameters(null));
        Assert.Null(DatabaseElementManager.ConvertParameters(new Dictionary<string, JsonElement>()));
    }

    [Fact]
    public void TypedCommand_PassesThrough()
    {
        var typed = new DatabaseCommand { Sql = "SELECT 1", Operation = "EXECUTE" };

        Assert.True(Envelope(typed).TryGetPayload<DatabaseCommand>(out var command, out _));
        Assert.Same(typed, command);
    }
}
