using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Linq;
using Fusion.Common;
using Fusion.Common.Configurations;
using Fusion.Common.Contracts;
using Fusion.Common.Enums;
using Fusion.Common.Messaging;
using Microsoft.Extensions.Logging;

namespace FusionConsole;

public class ConsoleStatusMonitor
{
    private static readonly Lock ConsoleLock = new();

    private const int START_ERROR = 30;

    private readonly ConcurrentDictionary<string, int> _elementRowMap = new();
    private readonly ConcurrentDictionary<string, (int priority, string core)> _elementMetadata = new();
    private readonly ConcurrentDictionary<string, DateTime> _flowEvents = new(); // Key: "Src->Force->Dest"
    private readonly ConcurrentDictionary<int, string> _coreHeaders = new(); // Row -> Header Text
    private readonly ConcurrentDictionary<string, string> _statusLines = new(); // ElementName -> Formatted String
    private readonly ConcurrentDictionary<int, string> _lastDrawnLine = new(); // Row -> Last Formatted String drawn
    private int _nextAvailableRow = 0;
    private int _lines;
    private int _errorLine;
    private bool _needsFullRedraw = false;
    private bool _showHeaders = true;
    private SystemTopologyMessage? _lastTopology;

    private string _lastMainHeader = string.Empty;
    private string _lastSimHeader = string.Empty;

    public void ToggleHeaders()
    {
        _showHeaders = !_showHeaders;
        if (_lastTopology != null)
        {
            _ = HandleTopologyMessageAsync(new MessageEnvelope(MessageBusTopic.SystemTopology, _lastTopology), CancellationToken.None);
        }
        else
        {
            _needsFullRedraw = true;
        }
    }

    private int GetElementPriority(IElementStatus msg)
    {
        var scope = msg.ElementId.ScopeName.ToUpperInvariant();
        var name = msg.ElementId.ElementName.ToUpperInvariant();

        if (scope == "SYSTEM") return 10; // Reactions (Reactions) first
        if (name.Contains("PLC")) return 20;
        if (name.Contains("PRINTER") || name.Contains("JETMARK") || name.Contains("ZEBRA")) return 30;
        if (name.Contains("HOST") || name.Contains("TCP") || name.Contains("CLIENT")) return 40;
        if (name.Contains("MQ") || name.Contains("NATS") || name.Contains("BUS")) return 50;
        
        return 100; // Others last
    }

    private int GetRowIndex(IElementStatus msg)
    {
        var name = msg.ElementId.ElementName.ToUpper().Trim();
        if (_elementRowMap.TryGetValue(name, out int existingIndex)) return existingIndex;

        // New element discovered at runtime! Just assign the next row sequentially for simplicity.
        lock (_elementRowMap)
        {
            if (_elementRowMap.TryGetValue(name, out existingIndex)) return existingIndex;

            int newRow = _nextAvailableRow++;
            _elementRowMap[name] = newRow;
            return newRow;
        }
    }

    private void RebuildLayout()
    {
        // Subheaders/Sorting logic is currently handled in HandleTopologyMessageAsync
        _needsFullRedraw = true;
    }

    private async Task HandleTopologyMessageAsync(MessageEnvelope? message, CancellationToken ct)
    {
        if (message?.Payload is not SystemTopologyMessage topology) return;
        _lastTopology = topology;

        await Task.Run(() =>
        {
            lock (_elementRowMap)
            {
                // Clear existing layout
                _elementMetadata.Clear();
                _elementRowMap.Clear();
                _coreHeaders.Clear();
                _lastDrawnLine.Clear(); // Force redraw on layout change
                // We DON'T clear _statusLines here, so we can realign existing lines immediately
                _nextAvailableRow = 0;

                // 1. Identify Bonds and group Elements by their relationships
                var elementToGroupName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var elementToGroupPriority = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                
                foreach (var rx in topology.Reactions)
                {
                    foreach (var bond in rx.Bonds)
                    {
                        var srcName = new MessageBusTopic(bond.Source).ElementName;
                        var dstName = new MessageBusTopic(bond.Destination).ElementName;
                        
                        if (string.IsNullOrEmpty(srcName) || string.IsNullOrEmpty(dstName)) continue;

                        // Get types instead of names
                        string srcType = GetElementTypeFromTopology(topology, srcName);
                        string dstType = GetElementTypeFromTopology(topology, dstName);

                        string groupName = $"{srcType} -> {dstType}";
                        
                        // Use only Comment, ignore bond.Name fallback
                        if (!string.IsNullOrEmpty(bond.Comment))
                        {
                            groupName = $"{srcType} -> [{bond.Comment.ToLower()}] -> {dstType}";
                        }
                        
                        // Assign both elements to this bond group
                        elementToGroupName[srcName] = groupName;
                        elementToGroupName[dstName] = groupName;

                        // Bond groups get a priority based on their highest priority member
                        int p1 = CalculateBasePriority(srcName);
                        int p2 = CalculateBasePriority(dstName);
                        int groupPriority = Math.Min(p1, p2);

                        elementToGroupPriority[srcName] = groupPriority;
                        elementToGroupPriority[dstName] = groupPriority;
                    }
                }

                // 2. Add Reactions (highest priority)
                foreach (var rx in topology.Reactions.OrderBy(r => r.Name))
                {
                    var name = rx.Name.ToUpper().Trim();
                    string label = "reactions";
                    if (!string.IsNullOrEmpty(rx.Comment))
                    {
                        label += $" ({rx.Comment.ToLower()})";
                    }
                    _elementMetadata[name] = (10, label);
                }

                // 3. Add Elements (Bonded or fallback to Type)
                foreach (var el in topology.Elements.OrderBy(e => e.Name))
                {
                    var name = el.Name.ToUpper().Trim();
                    int priority;
                    string label;

                    if (elementToGroupName.TryGetValue(name, out label))
                    {
                        priority = elementToGroupPriority[name];
                    }
                    else
                    {
                        // Fallback to Manager Type
                        string type = el.Manager.Replace("Manager", "").Replace("Element", "").ToLower();
                        label = $"{type}";
                        if (!string.IsNullOrEmpty(el.Comment))
                        {
                            label += $" ({el.Comment.ToLower()})";
                        }
                        priority = CalculateBasePriority(name);
                    }

                    _elementMetadata[name] = (priority, label);
                }

                // 4. Build the final row map with interleaved headers
                var sortedElements = _elementMetadata.ToList()
                    .OrderBy(kvp => kvp.Value.priority)
                    .ThenBy(kvp => kvp.Value.core) // Group Name
                    .ThenBy(kvp => kvp.Key)        // Element Name
                    .ToList();

                int currentRow = 0;
                string lastGroup = "";

                foreach (var entry in sortedElements)
                {
                    string currentGroup = entry.Value.core;
                    if (currentGroup != lastGroup)
                    {
                        if (_showHeaders)
                        {
                            _coreHeaders[currentRow] = currentGroup;
                            currentRow++;
                        }
                        lastGroup = currentGroup;
                    }
                    _elementRowMap[entry.Key] = currentRow++;
                }

                _nextAvailableRow = currentRow;
                _needsFullRedraw = true;
            }
        }, ct);
    }

    private string GetElementTypeFromTopology(SystemTopologyMessage topology, string elementName)
    {
        var element = topology.Elements.FirstOrDefault(e => e.Name.Equals(elementName, StringComparison.OrdinalIgnoreCase));
        if (element != null)
        {
            string type = element.Manager.Replace("Manager", "").Replace("Element", "").ToLower();
            if (!string.IsNullOrEmpty(element.Comment))
            {
                type += $" ({element.Comment.ToLower()})";
            }
            return type;
        }
        return "element";
    }

    private int CalculateBasePriority(string name)
    {
        name = name.ToUpperInvariant();
        if (name.Contains("PLC")) return 20;
        if (name.Contains("PRINTER") || name.Contains("ZEBRA") || name.Contains("JETMARK")) return 30;
        if (name.Contains("HOST") || name.Contains("TCP") || name.Contains("CLIENT")) return 40;
        if (name.Contains("MQ") || name.Contains("NATS") || name.Contains("BUS")) return 50;
        return 100;
    }

    private void ClearStatusArea()
    {
        lock (ConsoleLock)
        {
            _lastDrawnLine.Clear();
            _lastMainHeader = string.Empty;
            _lastSimHeader = string.Empty;
            int startRow = _startingLineOffset + _headerHeight;
            int endRow = Math.Min(Console.BufferHeight, startRow + 100); // Clear up to 100 rows of status
            string emptyLine = new string(' ', Console.WindowWidth - 1);

            for (int i = startRow; i < endRow; i++)
            {
                if (i >= Console.BufferHeight) break;
                Console.SetCursorPosition(0, i);
                Console.Write(emptyLine);
            }
        }
    }

    private int _startingLineOffset = 0;
    private DateTime _startTime;
    private readonly IFireLogger _logger;
    private bool _isPaused = false;
    private bool _isActive = true;
    private bool _isLocked = true;
    private readonly bool _useColor;
    private readonly int _headerHeight;

    public bool IsLocked
    {
        get => _isLocked;
        set => _isLocked = value;
    }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            if (!_isActive)
            {
                lock (ConsoleLock)
                {
                    Console.Clear();
                }
            }
        }
    }

    public void TogglePause()
    {
        _isPaused = !_isPaused;
        if (_isPaused)
        {
            lock (ConsoleLock)
            {
                Console.SetCursorPosition(0, _startingLineOffset);
                string msg = "  DASHBOARD PAUSED - PRESS ANY KEY TO RESUME  ";
                if (_useColor)
                {
                    msg = $"\e[41m\e[37m{msg}\e[0m";
                }
                Console.WriteLine(msg.PadRight(Console.WindowWidth));
            }
        }
    }

    public void RequestRedraw()
    {
        _needsFullRedraw = true;
    }

    public ConsoleStatusMonitor(IMessageBus bus, IFireLogger<ConsoleStatusMonitor> logger)
    {
        _startTime = DateTime.Now;
        var messageBus = bus ?? throw new ArgumentNullException(nameof(bus));
        _lines = 0;
        _errorLine = 30;
        _logger = logger;

        var config = ConfigurationLoader.GetSpaceConfig();
        _useColor = config?.ColorConsole ?? true;
        _headerHeight = (_useColor ? 1 : 2) + (config is { IsTestEnvironment: true } ? 1 : 0);

        // Subscribe to all status messages.
        messageBus.SubscribeAsync(MessageBusTopic.ElementStatus.ToString(), HandleStatusMessageAsync);
        messageBus.SubscribeAsync(MessageBusTopic.SystemTopology.ToString(), HandleTopologyMessageAsync);
        messageBus.SubscribeAsync(MessageBusTopic.DataFlow.ToString(), async (envelope, ct) =>
        {
            if (envelope.Payload is FlowEvent flow)
            {
                string key = $"{flow.Source}->{flow.Force}->{flow.Destination}";
                _flowEvents[key] = DateTime.Now;
                
                // Also track simplified segments for highlighting
                _flowEvents[$"ELEMENT:{flow.Source}"] = DateTime.Now;
                _flowEvents[$"FORCE:{flow.Force}"] = DateTime.Now;
                _flowEvents[$"ELEMENT:{flow.Destination}"] = DateTime.Now;
            }
        });
        if (OperatingSystem.IsWindows())
        {
            try
            {
                // 140 columns gives us plenty of room for the 99 cells + labels
                int targetWidth = 250;
                int targetHeight = 60;

                // Cap to what the screen/OS actually supports
                targetWidth = Math.Min(targetWidth, Console.LargestWindowWidth);
                targetHeight = Math.Min(targetHeight, Console.LargestWindowHeight);

                // Set buffer first if we are expanding, to avoid window being larger than buffer.
                // Then set window. Finally set buffer to match window exactly if desired.
                if (targetWidth > 0 && targetHeight > 0)
                {
                    if (Console.BufferWidth < targetWidth) Console.BufferWidth = targetWidth;
                    if (Console.BufferHeight < targetHeight) Console.BufferHeight = targetHeight;

                    Console.WindowWidth = targetWidth;
                    Console.WindowHeight = targetHeight;

                    Console.BufferWidth = targetWidth;
                    Console.BufferHeight = targetHeight;
                }
            }
            catch
            {
                // Silently ignore failures to resize the console window.
                // This can happen in many terminal environments (VS Code, Windows Terminal, CI).
            }
        }
    }

    public static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= maxLength ? value : value.Substring(0, maxLength);
    }

    private string C(string ansiCode) => _useColor ? ansiCode : string.Empty;
    private string S(string unicode, string ascii) => _useColor ? unicode : ascii;

    private async Task HandleStatusMessageAsync(MessageEnvelope? message, CancellationToken ct)
    {
        if (_isPaused || !_isActive) return;
        await Task.Run(() =>
        {
            if (message == null || !_isActive) return;
            
            if (message.Payload is not IElementStatus msg) return;
            var name = msg.ElementId.ElementName.ToUpper();
            var color = _useColor ? ElementHealthExtension.ToAnsiColor(msg.Health) : string.Empty;
            var reset = _useColor ? ElementHealthExtension.ToAnsiColor(null) : string.Empty;

            // Prepare the comment
            string raw = string.IsNullOrEmpty(msg.Comment) ? "" : $" - {msg.Comment}";
            string commentSuffix = raw.Length > 44 ? $"{raw.Substring(0, 43)}" : raw;
            string hbString = C("\e[30m") + S("♥ ", "  ") + C("\e[0m"); 
            if (msg.HbVisual != ' ')
            {
                // Active heartbeat -> Toggle between Bright Red and Blue based on the even/odd second
                hbString = msg.HbVisual == 'H' ? C("\e[34m") + S("♥ ", "  ") + C("\e[0m") : C("\e[31m") + S("♥ ", "* ") + C("\e[0m");
            }

            if (name.Contains("MANAGER", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var nameColor = string.Empty;
            if (_useColor)
            {
                // 1. Reactions (SYSTEM scope) -> White
                if (msg.ElementId.ScopeName.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase))
                {
                    nameColor = "\e[97m"; // White
                }
                // 2. Hardware Elements -> Light Blue
                else if (name.Contains("PLC") || name.Contains("PRINTER") || name.Contains("ZEBRA") || name.Contains("JETMARK"))
                {
                    nameColor = "\e[94m"; // Light Blue
                }
                // 3. Infrastructure / Others -> Gray
                else
                {
                    nameColor = "\e[90m"; // Gray
                }
            }

            name = name.PadRight(12, ' ');
            string formattedLine = $"{nameColor}{Truncate(name, 12),-12}{reset}{hbString}{color}{Truncate(msg.State, 12),-12}";
            
            string div = C("\e[90m") + S("│", "|") + C("\e[0m"); // Dark gray vertical divider
              
                // Connections / Disconnects
                string cdString = $"{msg.CountConnections % 100,2}{C("\e[90m")}/{C("\e[0m")}{msg.CountDisconnects % 100,-2}";

                // Process Time
                var apt = Math.Round(msg.AvgProcessTime, 1);

                var aptColor = apt < 30 ? C("\e[92m") : (apt < 100 ? C("\e[93m") : C("\e[91m")); // Green/Yellow/Red

                var str = apt.ToString("000.0");
                string aptString = $"{aptColor}{S("⏱ ", "T ")}{str}{C("\e[0m")}";
                // I/O Messages 
                string ioString =
                    $"{C("\e[96m")}{S("↓", "v")}{C("\e[0m")}{msg.CountInbound % 10000,4}{C("\e[90m")}{S("│", "|")}{C("\e[0m")}{C("\e[36m")}{S("↑", "^")}{C("\e[0m")}{msg.CountOutbound % 10000,4}";

                // Errors (Green checkmark if 0, Red warning if > 0)
                string errString = msg.CountError > 0
                    ? $"{C("\e[91m")}{S("✖ ", "X ")}{msg.CountError,-2}{C("\e[0m")}"
                    : $"{C("\e[92m")}{S("✓ ", "O ")}0 {C("\e[0m")}";

                // Time Formatting
                var displayTime = msg.Timestamp.ToLocalTime().ToString("d/hh:mm:ss");
                // Build the final appended line (Assuming formattedLine already contains ElementName and Status)
                formattedLine +=
                    $"{div}⇄ {cdString}{div}{aptString}{div}{ioString}{div}{errString} {div}{S("⌚", "T")}{displayTime,-11}{div}{commentSuffix,-43}{reset}";

            int rowIndex = GetRowIndex(msg);
            int lineIndex = rowIndex + _startingLineOffset + _headerHeight;

            _statusLines[name.Trim()] = formattedLine;

            lock (ConsoleLock)
            {
                WriteStatusLines(lineIndex, formattedLine);
                if (msg.Health != ElementHealth.Critical) return;
                lock (ConsoleLock)
                {
                    WriteStatusLines(_errorLine, $"{DateTime.Now} CRITICAL [{name}]: {msg.Comment}");
                    _errorLine = _errorLine + 2;
                }

                if (_errorLine > START_ERROR + 10)
                    _errorLine = START_ERROR;
            }
        }, ct);
    }

    public void Start(CancellationToken cancellationToken, int startingLine)
    {
        _startingLineOffset = startingLine;
        if (_isPaused)
        {
            lock (ConsoleLock)
            {
                if (startingLine >= 0 && startingLine < Console.BufferHeight)
                {
                    Console.SetCursorPosition(0, startingLine);
                    string msg = "  DASHBOARD PAUSED - PRESS ANY KEY TO RESUME  ";
                    if (_useColor)
                    {
                        msg = $"\e[41m\e[37m{msg}\e[0m";
                    }
                    Console.WriteLine(msg.PadRight(Console.WindowWidth));
                }
            }
        }

        Task.Run(async () =>
        {
            Console.CursorVisible = false;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        if (_isPaused || !_isActive) 
                        {
                            await Task.Delay(500, cancellationToken);
                            continue;
                        }

                        lock (ConsoleLock)
                        {
                            if (_needsFullRedraw)
                            {
                                ClearStatusArea();
                                _needsFullRedraw = false;
                            }

                            // Ensure startingLine is within buffer bounds
                            if (startingLine >= 0 && startingLine < Console.BufferHeight)
                            {
                                string mainHeader;
                                // main header
                                if (_useColor)
                                {
                                    mainHeader = 
                                        $"\e[48;2;220;220;220m\e[38;2;0;90;190m{"Element",-12}" +
                                        $"HB" +
                                        $"{"Status",-12}\e[38;2;0;0;0m│\e[38;2;0;90;190m" +
                                        $"{"Conn",-7}\e[38;2;0;0;0m│\e[38;2;0;90;190m" +
                                        $"{"APT",-7}\e[38;2;0;0;0m│\e[38;2;0;90;190m" +
                                        $"{"↓IN/↑OUT",-11}\e[38;2;0;0;0m│\e[38;2;0;90;190m" +
                                        $"{"Err",-5}\e[38;2;0;0;0m│\e[38;2;0;90;190m" +
                                        $"{"Last Active",-12}\e[38;2;0;0;0m│\e[38;2;0;90;190m" +
                                        $" Now:{DateTime.Now:HH:mm:ss}    {(_isLocked ? "\e[91mLOCKED" : "\e[92mUNLOCKED")}\e[0m ";
                                }
                                else
                                {
                                    mainHeader = 
                                        $"{"Element",-12}" +
                                        $"HB" +
                                        $"{"Status",-12}|" +
                                        $"{"Conn",-7}|" +
                                        $"{"APT",-7}|" +
                                        $"{"vIN/^OUT",-11}|" +
                                        $"{"Err",-5}|" +
                                        $"{"Last Active",-12}|" +
                                        $" Now:{DateTime.Now:HH:mm:ss}    {(_isLocked ? "LOCKED" : "UNLOCKED")} ";
                                }

                                if (mainHeader != _lastMainHeader)
                                {
                                    Console.SetCursorPosition(0, startingLine);
                                    Console.WriteLine(mainHeader.PadRight(Console.WindowWidth - 1));
                                    if (!_useColor) Console.WriteLine(new string('-', 100));
                                    _lastMainHeader = mainHeader;
                                }

                                // Simulation Header
                                var spaceConfig = ConfigurationLoader.GetSpaceConfig();
                                if (spaceConfig is { IsTestEnvironment: true })
                                {
                                    string simHeader = $" [SIMULATION MODE]  Runtime: {spaceConfig.SimulationRuntime:hh\\:mm\\:ss}  |  Stability: {spaceConfig.StabilityDuration:hh\\:mm\\:ss} ";
                                    if (_useColor) simHeader = $"\e[48;2;200;50;50m\e[37m\e[1m{simHeader}\e[0m";
                                    
                                    if (simHeader != _lastSimHeader)
                                    {
                                        Console.SetCursorPosition(0, startingLine + (_useColor ? 1 : 2));
                                        Console.WriteLine(simHeader.PadRight(Console.WindowWidth - 1));
                                        _lastSimHeader = simHeader;
                                    }
                                }

                                // Draw Cached Headers
                                foreach (var kvp in _coreHeaders)
                                {
                                    int rowIndex = kvp.Key;
                                    string label = kvp.Value.ToLower(); // Strictly lowercase
                                    int row = startingLine + _headerHeight + rowIndex;

                                    if (row < Console.BufferHeight)
                                    {
                                        string flowLine = string.Empty;
                                        if (label.StartsWith("bond:") || label.Contains("->"))
                                        {
                                            flowLine = BuildFlowLineForLabel(label);
                                        }

                                        string headerText = $"  {label}  {flowLine}";
                                        if (_useColor) headerText = $"\e[48;2;0;60;120m\e[37m\e[1m{headerText}\e[0m";
                                        
                                        string finalHeaderLine = headerText.PadRight(Console.WindowWidth - 1);
                                        if (!_lastDrawnLine.TryGetValue(row, out var last) || last != finalHeaderLine)
                                        {
                                            Console.SetCursorPosition(0, row);
                                            Console.Write(finalHeaderLine);
                                            _lastDrawnLine[row] = finalHeaderLine;
                                        }
                                    }
                                }

                                // Redraw all cached status lines (only if changed)
                                foreach (var kvp in _statusLines)
                                {
                                    if (_elementRowMap.TryGetValue(kvp.Key, out int rowIndex))
                                    {
                                        int lineIndex = rowIndex + startingLine + _headerHeight;
                                        WriteStatusLines(lineIndex, kvp.Value);
                                    }
                                }
                            }
                            if (_lines == 0)
                                _lines = startingLine + (_useColor ? 1 : 2);
                        }
                    }
                    catch
                    {
                        // Ignore console I/O errors
                    }

                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                }
            }
            catch (TaskCanceledException)
            {
            }
            catch (Exception)
            {
                _logger.Error("Status monitor task failed: {ex.Message}");
            }
            finally
            {
                Console.CursorVisible = true;
            }
        }, cancellationToken);
    }

    private string BuildFlowLineForLabel(string label)
    {
        PruneFlowEvents();
        
        // Extract src and dst from label "bond: src -> dst (comment)"
        try 
        {
            if (!label.StartsWith("bond:")) return string.Empty;

            var bondPart = label.Replace("bond: ", "");
            // Split by " -> "
            var arrowIndex = bondPart.IndexOf("->");
            if (arrowIndex == -1) return string.Empty;

            string src = bondPart.Substring(0, arrowIndex).Trim().ToUpper();
            string rest = bondPart.Substring(arrowIndex + 2).Trim();
            
            // Handle optional comment at the end
            string dst = rest;
            var parenIndex = rest.IndexOf('(');
            if (parenIndex != -1)
            {
                dst = rest.Substring(0, parenIndex).Trim().ToUpper();
            }
            else
            {
                dst = rest.Trim().ToUpper();
            }

            bool isSrcActive = _flowEvents.ContainsKey($"ELEMENT:{src}");
            bool isDstActive = _flowEvents.ContainsKey($"ELEMENT:{dst}");

            string srcPart = isSrcActive ? $"\e[92m({src.ToLower()})\e[0m" : $"({src.ToLower()})";
            string dstPart = isDstActive ? $"\e[92m({dst.ToLower()})\e[0m" : $"({dst.ToLower()})";

            return $"  {srcPart} ─> {dstPart}";
        }
        catch { return string.Empty; }
    }

    private void PruneFlowEvents()
    {
        var now = DateTime.Now;
        foreach (var kvp in _flowEvents)
        {
            if ((now - kvp.Value).TotalMilliseconds > 800)
            {
                _flowEvents.TryRemove(kvp.Key, out _);
            }
        }
    }

    private string BuildFlowLine(ICompoundBlueprint core)
    {
        PruneFlowEvents();
        if (!core.Reactions.Any()) return "  (No Reactions Defined)";

        var segments = new List<string>();
        var seenBonds = new HashSet<string>();

        foreach (var force in core.Reactions)
        {
            foreach (var bond in force.Bonds)
            {
                var src = new MessageBusTopic(bond.Source).ElementName;
                var dst = new MessageBusTopic(bond.Destination).ElementName;
                
                string bondKey = $"{src}->{force.Comment}->{dst}";
                if (seenBonds.Contains(bondKey)) continue;
                seenBonds.Add(bondKey);

                bool isSrcActive = _flowEvents.ContainsKey($"ELEMENT:{src}");
                bool isForceActive = _flowEvents.ContainsKey($"FORCE:{force.Name}");
                bool isDstActive = _flowEvents.ContainsKey($"ELEMENT:{dst}");

                string srcPart = isSrcActive ? $"\e[92m({src})\e[0m" : $"({src})";
                string forcePart = isForceActive ? $"\e[92m[{force.Comment}]\e[0m" : $"[{force.Comment}]";
                string dstPart = isDstActive ? $"\e[92m({dst})\e[0m" : $"({dst})";

                segments.Add($"{srcPart} ─> {forcePart} ─> {dstPart}");
            }
        }

        return "  FLOW: " + string.Join("  |  ", segments);
    }

    private void WriteStatusLines(int index, string status)
    {
        try
        {
            lock (ConsoleLock)
            {
                int width = Console.WindowWidth > 0 ? Console.WindowWidth : 85;

                // Ensure index is within buffer bounds to prevent ArgumentOutOfRangeException
                if (index >= 0 && index < Console.BufferHeight)
                {
                    // Only write if the content has changed for this row
                    string paddedStatus = status.PadRight(Math.Max(0, width - 1));
                    if (!_lastDrawnLine.TryGetValue(index, out var last) || last != paddedStatus)
                    {
                        Console.SetCursorPosition(0, index);
                        Console.Write(paddedStatus);
                        _lastDrawnLine[index] = paddedStatus;
                    }

                    // Park the cursor at the bottom of the window to keep it out of the status area
                    int parkingRow = Math.Min(Console.WindowHeight - 1, Console.BufferHeight - 1);
                    Console.SetCursorPosition(0, parkingRow);
                }
            }
        }
        catch
        {
            // Ignore console I/O errors
        }
    }
}