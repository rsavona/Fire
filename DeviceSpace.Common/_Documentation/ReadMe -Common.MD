# DeviceSpace.Common

`DeviceSpace.Common` is the foundational framework for the **Fortna Flexible Integration Routing Engine (FIRE)**. It provides the core contracts, base classes, and messaging infrastructure required to build, manage, and scale hardware and system interfaces.

## Architecture Overview

The library enforces a decoupled architecture where hardware interfaces remain independent of business logic:

* **`IDevice`**: An interface representing an external system or physical hardware. No device possesses knowledge of another device in the system.
* **`IDeviceManager`**: A singleton service acting as a registry and factory. It manages device lifecycles, loads configurations, and provides other services access to device instances.
* **`IMessageBus`**: A decoupled communication layer using a publish/subscribe model to move data between devices and orchestration services.
* **`IWorkflow`**: Every message leaving a device goes through the workflow class. Workflow changes the data as needed and sends it to a new destination.
## Core Components

### 1. Base Engines
* **`DeviceManagerBase<TDevice>`**: The primary engine that handles heavy lifting, including asynchronous startup/shutdown, automatic event wiring for message providers, and scanning global configurations for workflow subscriptions.
* **`DeviceBase`**: A foundational class for implementing specific hardware logic while maintaining compliance with the system's management requirements.
* **`WorkflowBase`**: Automatically wires up all the defined routes. 

### 2. Messaging Infrastructure
* **`IMessageBus`**: The contract for decoupled communication, supporting asynchronous subscriptions, type-safe payloads, and request/response patterns.
* **`MessageEnvelope`**: The standard data structure for all bus traffic. It contains the payload along with critical metadata like the Global Identification Number (GIN), client identifiers, and priority flags.
* **`MessageBusTopic`**: A structured naming convention (`Device.MessageType.Discriminator`) used to route messages accurately across the system.

### 3. Contracts and Interfaces
* **`IMessageProvider`**: A standard contract for any device capable of sending data (e.g., a scanner), allowing the `DeviceManagerBase` to automatically hook into its data stream.
* **`IDiagnosticProvider`**: Enables devices to announce their presence, version, and available commands to diagnostic and monitoring tools.

## Key Features

* **Lifecycle Automation**: Automatically manages the complex startup and shutdown sequences of multiple hardware interfaces.
* **Auto-Wiring**: Detects if a device implements `IMessageProvider` or `IDiagnosticProvider` and automatically connects the necessary event handlers.
* **Workflow-Driven Routing**: Automatically subscribes devices to specific bus topics based on the global workflow configuration, removing the need for hard-coded routing logic.
* **Unified Status Tracking**: Provides a centralized mechanism for devices to report state changes (e.g., Connected, Error) and health metrics to the rest of the system.

## Implementation Guide

To implement a new device module using this framework:

1. **Create Class Library** Create a new project that is a C# class library. Reference The DeviceSpace.Common project.   
2. **Define the Device**: Create a class inheriting from one of the intermediate Device base classes like
    *`ClientDeviceBase`. If none of the intermediates fit the device you are creating, inherit from DeviceBase, define your state machine rules. If the device sends data to the system, implement `IMessageProvider`.
3. **Define the DeviceManager**
4. **Define the DeviceRegistar**
4**Handle Inbound Data**: Override `OnDeviceMessageReceivedAsync` to define how raw data from the hardware is packaged into a `MessageEnvelope` and published to the bus.
5**Handle Outbound Commands**: Override `HandleBusMessageAsync` to define how the device reacts when the WCS sends a command or decision via the bus.
6**Status Reporting**: Use the built-in `StatusUpdated` events to ensure the system can monitor the health of the new interface.
7. **Configuration**: The All devices habe a DeviceName and a  