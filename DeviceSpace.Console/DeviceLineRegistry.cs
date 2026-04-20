public class DeviceLineRegistry
{
    // Fast lookup: Device Name -> Line Number
    private readonly Dictionary<string, int> _deviceToLine = new(StringComparer.OrdinalIgnoreCase);
    
    // Fast reverse lookup: Line Number -> Device Name (Replaces the HashSet)
    private readonly Dictionary<int, string> _lineToDevice = new();

    // Tracks the highest line number currently in use to avoid scanning
    private int _maxLineNumber = 0;

    public int Count => _deviceToLine.Count;

    /// <summary>
    /// Adds a device. If the line is taken, relocates the existing device to the highest line + 1.
    /// </summary>
    public bool AddOrRelocate(string deviceName, int requestedLine)
    {
        // 1. Safety check: Don't allow the exact same device name to be added twice
        if (_deviceToLine.ContainsKey(deviceName))
        {
            return false; 
        }

        // 2. Check for a line collision
        if (_lineToDevice.TryGetValue(requestedLine, out string existingDeviceName))
        {
            // Collision detected! Calculate the new line for the existing device
            int newLineForExistingDevice = _maxLineNumber + 1;

            // Relocate the existing device
            _deviceToLine[existingDeviceName] = newLineForExistingDevice;
            _lineToDevice[newLineForExistingDevice] = existingDeviceName;
            
            // Update our max tracker
            _maxLineNumber = newLineForExistingDevice;
            
            // Note: We don't need to .Remove() the old line from _lineToDevice 
            // because we are about to overwrite it in Step 3.
        }

        // 3. Assign the NEW device to the requested line
        _deviceToLine[deviceName] = requestedLine;
        _lineToDevice[requestedLine] = deviceName;

        // 4. Keep max tracker accurate in case the requested line is the highest we've ever seen
        if (requestedLine > _maxLineNumber)
        {
            _maxLineNumber = requestedLine;
        }

        return true;
    }

    public int? GetLineNumber(string deviceName)
    {
        if (_deviceToLine.TryGetValue(deviceName, out int line))
        {
            return line;
        }
        return null;
    }
}