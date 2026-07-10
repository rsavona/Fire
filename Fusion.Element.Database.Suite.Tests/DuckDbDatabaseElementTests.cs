using Fusion.Common.Blueprints;
using Fusion.Common.Logging;
using Serilog;

namespace Fusion.Element.Database.Suite.Tests;

/// <summary>
/// In-process integration tests for the DuckDB element. DuckDB is embedded,
/// so these tests exercise the real element end-to-end without a live server.
/// </summary>
public class DuckDbDatabaseElementTests
{
    private static DuckDbDatabaseElement CreateElement(Dictionary<string, object>? properties = null)
    {
        var blueprint = new ElementBlueprint
        {
            Name = "DuckDbTest",
            Manager = "DatabaseElementManager",
            Enable = true,
            CoreName = "TestCore",
            Properties = properties ?? new Dictionary<string, object>()
        };

        var logger = new FireLogger(new LoggerConfiguration().CreateLogger());
        return new DuckDbDatabaseElement(new StubMessageBus(), blueprint, logger);
    }

    [Fact]
    public async Task InMemory_CreateInsertQuery_RoundTrips()
    {
        using var element = CreateElement(new Dictionary<string, object>
        {
            ["Database"] = ":memory:"
        });

        await element.ExecuteAsync("CREATE TABLE cartons (id INTEGER, barcode VARCHAR)");
        var inserted = await element.ExecuteAsync(
            "INSERT INTO cartons VALUES ($id, $barcode)",
            new { id = 1, barcode = "BC-001" });

        Assert.Equal(1, inserted);

        var rows = (await element.QueryAsync<CartonRow>("SELECT id, barcode FROM cartons")).ToList();

        Assert.Single(rows);
        Assert.Equal(1, rows[0].Id);
        Assert.Equal("BC-001", rows[0].Barcode);
    }

    [Fact]
    public async Task InMemory_StatePersistsAcrossOperations_OnSameElement()
    {
        // In-memory DuckDB databases are scoped to a connection; the element must
        // keep a single connection alive so data survives between operations.
        using var element = CreateElement();

        await element.ExecuteAsync("CREATE TABLE t (v INTEGER)");
        await element.ExecuteAsync("INSERT INTO t VALUES (41)");
        await element.ExecuteAsync("UPDATE t SET v = v + 1");

        var value = (await element.QueryAsync<int>("SELECT v FROM t")).Single();
        Assert.Equal(42, value);
    }

    [Fact]
    public async Task DefaultsToInMemory_WhenNothingConfigured()
    {
        using var element = CreateElement();

        var result = (await element.QueryAsync<int>("SELECT 7")).Single();
        Assert.Equal(7, result);
    }

    [Fact]
    public async Task QueryWithParameters_FiltersRows()
    {
        using var element = CreateElement();

        await element.ExecuteAsync("CREATE TABLE items (id INTEGER, name VARCHAR)");
        await element.ExecuteAsync("INSERT INTO items VALUES (1, 'apple'), (2, 'banana'), (3, 'cherry')");

        var rows = (await element.QueryAsync<string>(
            "SELECT name FROM items WHERE id > $minId ORDER BY id",
            new { minId = 1 })).ToList();

        Assert.Equal(new[] { "banana", "cherry" }, rows);
    }

    [Fact]
    public async Task FileBacked_DataPersistsAcrossElements()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"fusion_duckdb_test_{Guid.NewGuid():N}.duckdb");
        try
        {
            using (var writer = CreateElement(new Dictionary<string, object> { ["Database"] = dbPath }))
            {
                await writer.ExecuteAsync("CREATE TABLE persisted (v VARCHAR)");
                await writer.ExecuteAsync("INSERT INTO persisted VALUES ($v)", new { v = "hello" });
            }

            using var reader = CreateElement(new Dictionary<string, object> { ["Database"] = dbPath });
            var value = (await reader.QueryAsync<string>("SELECT v FROM persisted")).Single();

            Assert.Equal("hello", value);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task ConnectionStringProperty_IsHonored()
    {
        using var element = CreateElement(new Dictionary<string, object>
        {
            ["ConnectionString"] = "DataSource=:memory:"
        });

        var result = (await element.QueryAsync<long>("SELECT 40 + 2")).Single();
        Assert.Equal(42, result);
    }

    private sealed class CartonRow
    {
        public int Id { get; set; }
        public string Barcode { get; set; } = string.Empty;
    }
}
