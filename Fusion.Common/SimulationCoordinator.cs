using System;
using System.Collections.Concurrent;

namespace Fusion.Common;

public enum SimulationPhase
{
    Standby,        // System initialized, waiting for induction
    SteadyState,    // Running normally
    FaultInjection, // Injecting hardware errors (triggered by GIN 325)
    Recovery,       // Errors cleared, system recovering
    Completed       // Simulation cycle finished
}

public static class SimulationCoordinator
{
    private static bool _gin325Reached = false;
    private static DateTime? _simulationStartTime;
    private static SimulationPhase _currentPhase = SimulationPhase.Standby;
    private static readonly ConcurrentDictionary<string, int> _barcodeToGin = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<int, string> _ginToBarcode = new();
    private static readonly object _lock = new();

    public static event Action<SimulationPhase>? PhaseChanged;
    public static event Action<int>? GinReached;

    public static SimulationPhase CurrentPhase
    {
        get { lock (_lock) return _currentPhase; }
        private set
        {
            lock (_lock)
            {
                if (_currentPhase != value)
                {
                    _currentPhase = value;
                    PhaseChanged?.Invoke(_currentPhase);
                }
            }
        }
    }

    public static bool Gin325Reached
    {
        get { lock (_lock) return _gin325Reached; }
        set
        {
            lock (_lock)
            {
                if (!_gin325Reached && value)
                {
                    _gin325Reached = true;
                    _simulationStartTime = DateTime.UtcNow;
                    CurrentPhase = SimulationPhase.FaultInjection;
                    GinReached?.Invoke(325);
                }
            }
        }
    }

    public static void UpdateGin(int gin)
    {
        if (gin >= 1 && CurrentPhase == SimulationPhase.Standby)
        {
            CurrentPhase = SimulationPhase.SteadyState;
        }
        
        if (gin == 325)
        {
            Gin325Reached = true;
        }

        GinReached?.Invoke(gin);

        // Terminate simulation after 1500 labels (or some other target)
        if (gin >= 1500 && CurrentPhase != SimulationPhase.Completed)
        {
            CurrentPhase = SimulationPhase.Completed;
        }
    }

    public static void RecordBarcode(int gin, string? barcode)
    {
        if (gin <= 0 || string.IsNullOrWhiteSpace(barcode))
        {
            return;
        }

        var cleanBarcode = barcode.Trim();
        _ginToBarcode[gin] = cleanBarcode;
        _barcodeToGin[cleanBarcode] = gin;
    }

    public static bool TryGetGinForBarcode(string? barcode, out int gin)
    {
        gin = 0;
        if (string.IsNullOrWhiteSpace(barcode))
        {
            return false;
        }

        var cleanBarcode = barcode.Trim();
        if (_barcodeToGin.TryGetValue(cleanBarcode, out gin))
        {
            return true;
        }

        const string simulatorPrintedSuffix = "123";
        if (cleanBarcode.EndsWith(simulatorPrintedSuffix, StringComparison.OrdinalIgnoreCase) &&
            cleanBarcode.Length > simulatorPrintedSuffix.Length)
        {
            var sideBarcode = cleanBarcode[..^simulatorPrintedSuffix.Length];
            return _barcodeToGin.TryGetValue(sideBarcode, out gin);
        }

        return false;
    }

    public static double? ElapsedSeconds
    {
        get
        {
            lock (_lock)
            {
                if (_simulationStartTime == null) return null;
                return (DateTime.UtcNow - _simulationStartTime.Value).TotalSeconds;
            }
        }
    }

    public static void Reset()
    {
        lock (_lock)
        {
            _gin325Reached = false;
            _simulationStartTime = null;
            _currentPhase = SimulationPhase.Standby;
            _barcodeToGin.Clear();
            _ginToBarcode.Clear();
        }
    }

    /// <summary>
    /// Checks if a specific element should be in a faulted state based on the global simulation clock.
    /// Centralizes the "Error Script" here.
    /// </summary>
    public static (bool isFaulted, string reason) GetSimulatedHardwareStatus(string elementName, double? elapsed)
    {
        if (elapsed == null || elapsed >= 60) return (false, "ready");

        bool isPrinter2 = elementName.EndsWith("152") || elementName.EndsWith("2");
        
        if (elapsed < 10) // 0-10s: P1 Paper Out
        {
            if (!isPrinter2) return (true, "out of paper");
        }
        else if (elapsed < 20) // 10-20s: P2 Paper Out
        {
            if (isPrinter2) return (true, "out of paper");
        }
        else if (elapsed < 30) // 20-30s: P1 Head Open
        {
            if (!isPrinter2) return (true, "head open");
        }
        else if (elapsed < 40) // 30-40s: P2 Head Open
        {
            if (isPrinter2) return (true, "head open");
        }
        else if (elapsed < 50) // 40-50s: P1 Paused
        {
            if (!isPrinter2) return (true, "paused");
        }
        else if (elapsed < 60) // 50-60s: P2 Paused
        {
            if (isPrinter2) return (true, "paused");
        }

        return (false, "ready");
    }
}
