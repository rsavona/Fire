using System;
using Fusion.Common;
using Fusion.Common.BaseClasses;


namespace Fusion.Element.Printer.Suite;

public record LabelToPrintMessage : ElementMessageBase
{
    public LabelToPrintMessage(string data, object messageObj, bool print = true) 
        // We must call the base constructor. 
        // If you don't have a specific topic yet, pass a default or null if allowed.
        : base() 
    {
        if (string.IsNullOrWhiteSpace(data))
            throw new ArgumentNullException(nameof(data), "ZPL Data cannot be null or empty.");

        LaneId = ""; 
        ZplData = data;
        MessageObj = messageObj;
        Printable = print;
        SessionId = Guid.NewGuid().ToString();
    }

    public bool Printable { get; set; }
    public string SessionId { get; set; }
    public string LaneId { get; set; }
    public string ZplData { get; set; } 
    public object MessageObj { get; }


}