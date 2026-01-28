 // For IDevice, DeviceStatus
using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using DeviceSpace.Common.Contracts;

namespace Device.Printer.Suite;

/// <summary>
/// Defines the contract for printer devices, focusing on essential properties and operations.
/// </summary>
public interface ITcpPrintClientBase : IDevice  
{
    // --- Properties specified by user ---
    string Brand { get; init; }
    PrintDestination DestinationType { get; init; }
    bool PrintError { get; init; }
    ZplString ErrorLabel { get; init; }
    
     

    // --- Core Operations ---
    /// <summary>
    /// Asynchronously sends label data (e.g., ZPL) to the printer.
    /// </summary>
    /// <param name="labelData">The label data to print.</param>
    /// <returns>A task representing the asynchronous print operation.</returns>
    Task PrintAsync(LabelToPrintMessage labelData);

   
  
}

