# Device.Plc.Suite

The `Device.Plc.Suite` serves as a high-performance communication gateway between the **Fire** warehouse control system and various **PLCs**. It implements the `WES-Routing Interface Message Specification` to facilitate seamless data exchange for warehouse automation tasks.

## Core Components

The suite is built on a modular architecture to handle device management, message parsing, and simulation:
This library holds 2 Devices
* **PlcServerDevice**: a Tcp Socket Server intended to receive connections from a PLC thst sends Decision Request, Decision Updates and Receives Decision Responses t to/from the WCS.
* **VirtualPlcDevice**: A simulation of the PLC client that sends Decision Request, Decision Updates and Receives Decision Responses to/from the WCS.
* #### other conponents
* **PlcDeviceManager & VirtualPlcManager**: A central manager responsible for the lifecycle and creation of all `PlcServerDevice` instances. It coordinates inbound messages from PLCs to the system message bus and routes outbound bus responses back to the correct PLC client.
* **PlcMessageProcessor**: Manages the low-level processing of network streams. It extracts framed messages using protocol delimiters, handles heartbeats automatically, and prepares business data for the application layer.
* **PlcMessageParser**: Provides the logic for framing JSON payloads into the required protocol format and parsing raw strings into C# objects.



### Supported Message Types

* **Decision Request (`DReqM`)**: Sent from the PLC when an item reaches a decision point (e.g., a scanner). It includes data like `GIN`, `Barcodes`, and dimensions.
* **Decision Update (`DUM`)**: Used to provide status updates for items within the system.
* **Decision Response**: Sent from the WCS to the PLC, providing routing instructions or "Actions" based on a previous request.
* **Heartbeat (`HB`)**: A bidirectional message used to monitor connection health; the suite automatically responds with a Heartbeat ACK upon receipt.

## Key Features

* **Asynchronous Processing**: Uses `Task`-based operations for processing messages and handling network I/O, ensuring high throughput.
* **Simulation Support**: Includes a full virtual PLC implementation for development and testing environments.
* **Structured Logging**: Integrated with **Serilog**, providing context-aware logs that include device names and client keys for easier troubleshooting.
* **State Management**: Utilizes the **Stateless** library for managing device connection states.
* **Modern .NET Foundation**: Built on **.NET 10** using C# 14 features.

## Usage

### Initialization
The system is typically initialized by providing a list of device configurations to the `PlcDeviceManager`, which then manages the lifecycle of each server device.

### Message Flow
1.  **Inbound**: The `PlcMessageProcessor` receives a byte array from a `NetworkStream`, extracts frames between `STX` and `ETX`, and passes them to the parser.
2.  **Bus Integration**: Validated business messages are wrapped in a `MessageEnvelope` and published to the `IMessageBus` on specific topics based on their decision point.
3.  **Outbound**: The `PlcDeviceManager` subscribes to response topics. When a decision is published to the bus, the manager correlates it with the pending request and sends the framed response to the PLC.