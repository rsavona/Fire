using System.Timers;
using Serilog;

namespace Device.Virtual.Plc;
public class ConveyorTrackingPipeline
{
    private readonly ContainerInfo?[] _cells = new ContainerInfo?[100]; 
    private readonly System.Timers.Timer _pulseTimer;
    private readonly ILogger _logger;

    public event Action<int, ContainerInfo, string>? StationTriggered;

    public ConveyorTrackingPipeline(ILogger logger)
    {
        _logger = logger;
        _pulseTimer = new System.Timers.Timer(1000) { AutoReset = true };
        _pulseTimer.Elapsed += OnPulse;
    }

    public bool Started;
    public void Start()
    {
        Started = true;
        _pulseTimer.Start();
    }

    public void Stop() => _pulseTimer.Stop();

    public void LoadCarton(ContainerInfo carton)
    {
        _cells[1] = carton;
        _logger.Information("[Pipeline] GIN {Gin} loaded at Element 1", carton.Gin);
    }

    private void OnPulse(object? sender, ElapsedEventArgs e)
    {
        // Move backward through the array to shift elements forward
        for (int i = 98; i >= 1; i--)
        {
            if (_cells[i] == null) continue;

            var carton = _cells[i];
            _cells[i + 1] = carton;
            _cells[i] = null;

            CheckTriggers(i + 1, carton);
        }
        _cells[99] = null; // Carton leaves the tracking area
    }

    private void CheckTriggers(int index, ContainerInfo carton)
    {
        string? station = index switch
        {
            10 => "Induct",
            25 => "Labeler_1",
            35 => "Inserter_1",
            45 => "Labeler_2",
            55 => "Inserter_2",
            75 => "Verification",
            _ => null
        };

        if (station != null) 
            StationTriggered?.Invoke(index, carton, station);
    }
}