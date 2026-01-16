using System.Net;
using System.Net.Sockets;
using DeviceSpace.Common.Configurations;
using DeviceSpace.Common.Contracts;
using DeviceSpace.Common.Enums;
using Stateless;
using ILogger = Serilog.ILogger;

namespace DeviceSpace.Common.BaseClasses;

public abstract class ClientDeviceBase : DeviceBase<ClientDeviceBase.State, ClientDeviceBase.Event>
{
    public enum State
    {
        Offline,
        Starting,
        Connecting,
        Connected,
        Processing,
        Faulted,
        Reconnect,
        Stopping
    }

    public enum Event
    {
        Start,
        Stop,
        ConnectSuccess,
        ConnectFailed,
        DataReceived,
        MessageSent,
        ConnectionLost,
        Error
    }

    /// <summary>
    /// Returns true if the TCP client is initialized and the underlying socket is connected.
    /// </summary>
    protected ClientDeviceBase(IDeviceConfig config, ILogger logger)
        : base(config, logger, State.Offline, Event.Start)
    {
        ConfigureStateMachine();
    }

    /// <summary>
    /// Starts the device asynchronously and transitions its state to the initial state.
    /// </summary>
    /// <param name="token">A cancellation token that can be used to cancel the start operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public override async Task StartAsync(CancellationToken token)
    {
        await Machine.FireAsync(Event.Start);
    }

    private int _connectionAttempts = 0; // Added counter

    protected sealed override void ConfigureStateMachine()
    {
        Machine.Configure(State.Offline)
            .Permit(Event.Start, State.Connecting);


        Machine.Configure(State.Connecting)
            .OnEntry(t =>
            {
                _connectionAttempts++; // Increment every time we enter Connecting
                Logger.Information("[{Dev}] Connection attempt #{Count} ",
                    Config.Name, _connectionAttempts); // Added attempt logging
                _ = ConnectAsync();
            })
            .Permit(Event.ConnectSuccess, State.Connected)
            .Permit(Event.ConnectFailed, State.Reconnect)
            .Permit(Event.Stop, State.Offline);

        Machine.Configure(State.Connected)
            .OnEntry(() =>
            {
                Tracker.IncrementConnections();
                Logger.Information("[{Dev}] Connected successfully after {Count} attempts.",
                    Config.Name, _connectionAttempts); // Log total attempts on success
                _connectionAttempts = 0; // Reset counter upon success
            })
            // ... existing permits ...
            .Permit(Event.ConnectionLost, State.Reconnect)
            .Ignore(Event.MessageSent)
            .Ignore(Event.DataReceived);

        Machine.Configure(State.Reconnect)
            .OnEntry(t =>
            {
                Logger.Warning("[{Dev}] Waiting 5s before attempt #{Next}",
                    Config.Name, _connectionAttempts + 1); // Informative warning
                _ = Task.Delay(5000).ContinueWith(_ => Machine.Fire(Event.Start));
            })
            .Permit(Event.Start, State.Connecting);


        Machine.Configure(State.Processing)
            .Permit(Event.MessageSent, State.Connected)
            .Permit(Event.ConnectionLost, State.Connecting)
            .Permit(Event.Error, State.Faulted);

        Machine.Configure(State.Faulted)
            .OnEntry(_ => DeviceFaultedAsync())
            .Permit(Event.Start, State.Starting)
            .Permit(Event.Stop, State.Offline);

        Machine.OnUnhandledTrigger((state, trigger) =>
        {
            var ex = new InvalidOperationException($"Invalid transition: {trigger} is not allowed in {state}");
            OnError("StateMachine_LogicGap", ex);
        });
    }

    protected abstract void DeviceFaultedAsync();

    protected abstract Task ConnectAsync();


   
    /// <summary>
    /// Maps the provided device state to a corresponding health status value.
    /// </summary>
    /// <param name="state">The current state of the device.</param>
    /// <returns>The corresponding <see cref="DeviceHealth"/> value based on the given state.</returns>
    protected override DeviceHealth MapStateToHealth(State state)
    {
        return state switch
        {
            State.Processing => DeviceHealth.Normal,
            State.Connected => DeviceHealth.Normal,
            State.Offline => DeviceHealth.Warning,
            State.Connecting => DeviceHealth.Warning,
            State.Reconnect => DeviceHealth.Warning,
            State.Faulted => DeviceHealth.Critical,
            _ => DeviceHealth.Warning
        };
    }

    protected abstract Task HandleReceivedDataAsync(byte[] buffer, int bytesRead, CancellationToken ct);

    /// <summary>
    /// Stops the device asynchronously by canceling operations, closing the client connection,
    /// and transitioning the state machine to the 'Stop' event.
    /// </summary>
    /// <param name="token">A cancellation token that can be used to cancel the stop operation.</param>
    /// <returns>A task that represents the asynchronous stop operation.</returns>
    public override async Task StopAsync(CancellationToken token)
    {
        await Machine.FireAsync(Event.Stop);
    }

    public override void OnError(string context, Exception ex)
    {
        Logger.Error(ex, "[{Dev}] Error in context: {Context}. Current State: {State}",
            Config.Name, context, Machine.State);
        Tracker.IncrementError(ex.Message);

        UpdateAndNotify();

        if (Machine.CanFire(Event.Error))
        {
            Machine.Fire(Event.Error);
        }
    }
}