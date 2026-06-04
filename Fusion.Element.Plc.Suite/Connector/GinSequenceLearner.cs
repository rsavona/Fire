using System.Collections.Concurrent;
using System.Text.Json;
using Fusion.Common.Contracts;

namespace Fusion.Element.Plc.Suite.Connector;

public enum GinSequencePattern
{
    Learning,
    Sequential,     // 1, 2, 3...
    Skips,          // 1, 3, 5... or 2, 4, 6...
    Random
}

public class DecisionPointStats
{
    public string DecisionPoint { get; set; } = string.Empty;
    public GinSequencePattern Pattern { get; set; } = GinSequencePattern.Learning;
    public int LastGin { get; set; }
    public int MaxGinDetected { get; set; }
    public int RolloverValue { get; set; }
    public int DetectedIncrement { get; set; } = 0;
    public int SampleCount { get; set; }
    public List<int> RecentGins { get; set; } = new();
}

public class GinLearnedData
{
    public Dictionary<string, DecisionPointStats> Stats { get; set; } = new();
}

public class GinSequenceLearner
{
    private readonly string _elementName;
    private readonly string _filePath;
    private readonly IFireLogger _logger;
    private readonly ConcurrentDictionary<string, DecisionPointStats> _dpStats = new();
    private const int MIN_SAMPLES_FOR_PATTERN = 10;
    private const int MAX_RECENT_GINS = 20;

    public GinSequenceLearner(string elementName, IFireLogger logger)
    {
        _elementName = elementName;
        _logger = logger;
        _filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PersistentMemory", $"{_elementName}.dat");
        
        LoadData();
    }

    public void ProcessGin(string decisionPoint, int gin)
    {
        var stats = _dpStats.GetOrAdd(decisionPoint, (dp) => new DecisionPointStats { DecisionPoint = dp });

        lock (stats)
        {
            if (stats.SampleCount > 0)
            {
                AnalyzeSequence(stats, gin);
            }

            stats.LastGin = gin;
            if (gin > stats.MaxGinDetected) stats.MaxGinDetected = gin;
            
            stats.RecentGins.Add(gin);
            if (stats.RecentGins.Count > MAX_RECENT_GINS) stats.RecentGins.RemoveAt(0);
            
            stats.SampleCount++;

            if (stats.SampleCount == MIN_SAMPLES_FOR_PATTERN || stats.SampleCount % 100 == 0)
            {
                SaveData();
            }
        }
    }

    private void AnalyzeSequence(DecisionPointStats stats, int currentGin)
    {
        int diff = currentGin - stats.LastGin;

        // Potential Rollover detected
        if (currentGin < stats.LastGin)
        {
            if (stats.LastGin > stats.RolloverValue)
            {
                stats.RolloverValue = stats.LastGin;
                _logger.Information("[{Dev}] DP:{DP} Detected Rollover at {Rollover}. New GIN is {Gin}", 
                    _elementName, stats.DecisionPoint, stats.RolloverValue, currentGin);
            }
            return;
        }

        if (stats.Pattern == GinSequencePattern.Learning)
        {
            if (stats.DetectedIncrement == 0)
            {
                stats.DetectedIncrement = diff;
            }
            else if (stats.DetectedIncrement != diff)
            {
                stats.Pattern = GinSequencePattern.Random;
                _logger.Information("[{Dev}] DP:{DP} Sequence seems Random.", _elementName, stats.DecisionPoint);
            }

            if (stats.SampleCount >= MIN_SAMPLES_FOR_PATTERN && stats.Pattern == GinSequencePattern.Learning)
            {
                stats.Pattern = stats.DetectedIncrement == 1 ? GinSequencePattern.Sequential : GinSequencePattern.Skips;
                _logger.Information("[{Dev}] DP:{DP} Learned Pattern: {Pattern} (Increment: {Inc})", 
                    _elementName, stats.DecisionPoint, stats.Pattern, stats.DetectedIncrement);
            }
        }
        else if (stats.Pattern == GinSequencePattern.Sequential || stats.Pattern == GinSequencePattern.Skips)
        {
            // Verify the current GIN matches the expected increment
            if (diff != stats.DetectedIncrement)
            {
                _logger.Error("[{Dev}] DP:{DP} GIN Sequence Violation! Expected +{Expected}, but got +{Actual} (Last:{Last}, Current:{Current})",
                    _elementName, stats.DecisionPoint, stats.DetectedIncrement, diff, stats.LastGin, currentGin);
            }
        }
    }

    private void LoadData()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                string json = File.ReadAllText(_filePath);
                var data = JsonSerializer.Deserialize<GinLearnedData>(json);
                if (data != null)
                {
                    foreach (var kvp in data.Stats)
                    {
                        _dpStats.TryAdd(kvp.Key, kvp.Value);
                    }
                    _logger.Information("[{Dev}] Loaded learned GIN data from {File}. DPs: {Count}", 
                        _elementName, _filePath, _dpStats.Count);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[{Dev}] Failed to load learned GIN data.", _elementName);
        }
    }

    public void SaveData()
    {
        try
        {
            string dir = Path.GetDirectoryName(_filePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var data = new GinLearnedData { Stats = _dpStats.ToDictionary(kvp => kvp.Key, kvp => kvp.Value) };
            string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[{Dev}] Failed to save learned GIN data.", _elementName);
        }
    }

    public DecisionPointStats? GetStats(string dp)
    {
        return _dpStats.TryGetValue(dp, out var stats) ? stats : null;
    }
}
