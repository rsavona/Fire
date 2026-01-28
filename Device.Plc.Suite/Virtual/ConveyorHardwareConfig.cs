using System.Text.Json.Serialization;

namespace Device.Plc.Suite.Virtual;

public record ConveyorHardwareConfig(
    [property: JsonPropertyName("conveyorConfiguration")] ConveyorDetails Configuration
);

public record ConveyorDetails(
    string Id,
    string Description,
    ConveyorParams Parameters,
    List<ConveyorNode> Nodes
);

public record ConveyorParams(
    int TotalLengthFeet,
    int InchesPerCell,
    int TotalCells,
    int ToteLengthFeet,
    int ToteCellOccupancy
);

public record ConveyorNode(
    string Id,
    string Type,
    string Description,
    double PositionFeet,
    int CellIndex,
    List<DivertOutput>? Outputs, // Optional, only for Diverts
    List<ConveyorNode>? Nodes    // Optional, for nested nodes like PrinterBanks
);

public record DivertOutput(
    string Id,
    string Logic,
    string Description
);