
namespace DeviceSpace.Common.Contracts;

public interface IDiagnosticProvider
{
    // Returns a list of available actions (e.g., "Reset Tracker", "Simulate Paper Out")
    IEnumerable<DiagCommand> GetAvailableCommands();
    
    // Executes a command by name
    Task<DiagResult> ExecuteCommandAsync(string commandName, Dictionary<string, string> parameters);
}

