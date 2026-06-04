 // For IElement, ElementStatus
using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Fusion.Common.Contracts;

namespace Fusion.Element.Printer.Suite;

/// <summary>
/// Defines the contract for printer elements, focusing on essential properties and operations.
/// </summary>
public interface ITcpPrintClientBase : IElement  
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
    Task PrintAsync(string labelData);

   
  
}

