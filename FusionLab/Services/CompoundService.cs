using System.Text.Json;
using Fusion.Common.Blueprints;

namespace FusionLab.Services;

public class CompoundService
{
    private readonly IWebHostEnvironment _env;
    private readonly List<CompoundBlueprint> _discoveredCompounds = new();

    public CompoundService(IWebHostEnvironment env)
    {
        _env = env;
        LoadCompounds();
    }

    public IEnumerable<CompoundBlueprint> GetDiscoveredCompounds() => _discoveredCompounds;

    private void LoadCompounds()
    {
        _discoveredCompounds.Clear();
        var compoundsPath = Path.Combine(_env.WebRootPath, "compounds");
        
        if (!Directory.Exists(compoundsPath)) return;

        var files = Directory.GetFiles(compoundsPath, "*.json");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                var compound = JsonSerializer.Deserialize<CompoundBlueprint>(json, options);
                if (compound != null)
                {
                    // Ensure the name is set if missing
                    if (string.IsNullOrEmpty(compound.Name)) compound.Name = Path.GetFileNameWithoutExtension(file);
                    _discoveredCompounds.Add(compound);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading compound {file}: {ex.Message}");
            }
        }
    }

    public async Task SaveCompoundAsync(CompoundBlueprint compound)
    {
        var compoundsPath = Path.Combine(_env.WebRootPath, "compounds");
        if (!Directory.Exists(compoundsPath)) Directory.CreateDirectory(compoundsPath);

        var fileName = $"{compound.Name.Replace(" ", "_")}.json";
        var filePath = Path.Combine(compoundsPath, fileName);
        
        var json = JsonSerializer.Serialize(compound, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(filePath, json);
        
        // Refresh local cache
        if (!_discoveredCompounds.Any(c => c.Name == compound.Name))
        {
            _discoveredCompounds.Add(compound);
        }
    }
}
