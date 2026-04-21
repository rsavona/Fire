using System.Text.Json.Nodes;
using DeviceSpace.Common;
using DeviceSpace.Common.BaseClasses;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Enums;
using DeviceSpace.Common.PayloadParsers;
using Serilog.Core;

namespace Device.HostComm;

public class FileMessageDevice : DeviceBase<FileMessageDevice.State, FileMessageDevice.Event, DeviceMetric>, IMessageProvider
{
    public enum State { Offline, Monitoring, Processing, Faulted }
    public enum Event { Start, Stop, FileDetected, ProcessSuccess, ProcessError, Error }

    public event Func<object, object, Task>? MessageReceived;
    
    private readonly IPayloadParser _payloadParser;
    private readonly string _hotFolderPath;
    private readonly string _archivePath;
    private readonly string _errorPath;
    private readonly string _fileFilter;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _workerCts;

    public FileMessageDevice(IDeviceConfig config, IFireLogger logger, LoggingLevelSwitch swtch)
        : base(config, logger, swtch, State.Offline, Event.Start)
    {
        _payloadParser = PayloadParserFactory.Create(config);
        
        _hotFolderPath = config.Properties.TryGetValue("HotFolder", out var h) ? h.ToString() ?? "" : "C:\\HotFolder";
        _archivePath = config.Properties.TryGetValue("ArchiveFolder", out var a) ? a.ToString() ?? "" : Path.Combine(_hotFolderPath, "Archive");
        _errorPath = config.Properties.TryGetValue("ErrorFolder", out var e) ? e.ToString() ?? "" : Path.Combine(_hotFolderPath, "Error");
        _fileFilter = config.Properties.TryGetValue("FileFilter", out var f) ? f.ToString() ?? "*.*" : "*.*";

        ConfigureStateMachine();
    }

    protected override void ConfigureStateMachine()
    {
        Machine.Configure(State.Offline)
            .Permit(Event.Start, State.Monitoring);

        Machine.Configure(State.Monitoring)
            .OnEntry(StartWatcher)
            .OnExit(StopWatcher)
            .Permit(Event.FileDetected, State.Processing)
            .Permit(Event.Stop, State.Offline)
            .Permit(Event.Error, State.Faulted);

        Machine.Configure(State.Processing)
            .Permit(Event.ProcessSuccess, State.Monitoring)
            .Permit(Event.ProcessError, State.Monitoring)
            .Permit(Event.Error, State.Faulted)
            .Permit(Event.Stop, State.Offline);

        Machine.Configure(State.Faulted)
            .Permit(Event.Start, State.Monitoring)
            .Permit(Event.Stop, State.Offline);
    }

    private void StartWatcher()
    {
        if (!Directory.Exists(_hotFolderPath)) Directory.CreateDirectory(_hotFolderPath);
        if (!Directory.Exists(_archivePath)) Directory.CreateDirectory(_archivePath);
        if (!Directory.Exists(_errorPath)) Directory.CreateDirectory(_errorPath);

        _watcher = new FileSystemWatcher(_hotFolderPath, _fileFilter);
        _watcher.Created += OnFileCreated;
        _watcher.EnableRaisingEvents = true;
        
        Logger.Information("[{Dev}] Monitoring folder: {Folder}", Config.Name, _hotFolderPath);
    }

    private void StopWatcher()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnFileCreated;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        Logger.Information("[{Dev}] New file detected: {FileName}", Config.Name, e.Name);
        RegisterTask(ProcessFileAsync(e.FullPath));
    }

    private async Task ProcessFileAsync(string fullPath)
    {
        if (Machine.State != State.Monitoring) return;
        
        await Machine.FireAsync(Event.FileDetected);
        
        try
        {
            // Handle file locks (retry for up to 2 seconds)
            string content = await ReadFileWithRetryAsync(fullPath);
            
            Tracker.IncrementInbound();
            
            var parsedPayload = _payloadParser.Parse(content);
            
            var topic = new MessageBusTopic(Config.Name, "FileInbound", Path.GetFileName(fullPath));
            var envelope = new MessageEnvelope(topic, parsedPayload, 0, "File");

            if (MessageReceived != null)
            {
                await MessageReceived.Invoke(this, envelope);
            }

            MoveFile(fullPath, _archivePath);
            await Machine.FireAsync(Event.ProcessSuccess);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] Failed to process file {File}", Config.Name, fullPath);
            MoveFile(fullPath, _errorPath);
            Tracker.IncrementError(ex.Message);
            await Machine.FireAsync(Event.ProcessError);
        }
    }

    private async Task<string> ReadFileWithRetryAsync(string path, int retries = 5)
    {
        for (int i = 0; i < retries; i++)
        {
            try
            {
                return await File.ReadAllTextAsync(path);
            }
            catch (IOException)
            {
                if (i == retries - 1) throw;
                await Task.Delay(500);
            }
        }
        return string.Empty;
    }

    private void MoveFile(string sourcePath, string destFolder)
    {
        try
        {
            string fileName = Path.GetFileName(sourcePath);
            string destPath = Path.Combine(destFolder, fileName);
            
            if (File.Exists(destPath))
            {
                destPath = Path.Combine(destFolder, $"{Path.GetFileNameWithoutExtension(fileName)}_{DateTime.Now:yyyyMMddHHmmss}{Path.GetExtension(fileName)}");
            }
            
            File.Move(sourcePath, destPath);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] Failed to move file {File} to {Dest}", Config.Name, sourcePath, destFolder);
        }
    }

    public async Task<bool> WriteFileAsync(string content)
    {
        string outboundFolder = Config.Properties.TryGetValue("OutboundFolder", out var o) ? o.ToString() ?? "" : Path.Combine(_hotFolderPath, "Outbound");
        
        try
        {
            if (!Directory.Exists(outboundFolder)) Directory.CreateDirectory(outboundFolder);

            string fileName = $"OUT_{DateTime.Now:yyyyMMdd_HHmmssfff}.dat";
            string fullPath = Path.Combine(outboundFolder, fileName);
            
            string serializedContent = _payloadParser.Serialize(content);
            
            await File.WriteAllTextAsync(fullPath, serializedContent);
            Logger.Information("[{Dev}] Outbound file created: {File}", Config.Name, fileName);
            Tracker.IncrementOutbound();
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "[{Dev}] Failed to write outbound file", Config.Name);
            Tracker.IncrementError("File Write Failure");
            return false;
        }
    }

    public override async Task StartAsync(CancellationToken token) => await Machine.FireAsync(Event.Start);
    public override async Task StopAsync(CancellationToken token)
    {
        Logger.Information("[{Dev}] Shutting down gracefully...", Config.Name);
        await Machine.FireAsync(Event.Stop);
    }

    protected override DeviceHealth MapStateToHealth(State state) => state switch
    {
        State.Monitoring => DeviceHealth.Normal,
        State.Processing => DeviceHealth.Normal,
        State.Faulted => DeviceHealth.Critical,
        _ => DeviceHealth.Warning
    };
}
