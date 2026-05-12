# Device.Nats

This library provides a high-performance integration for NATS (Messaging System) into the FortnaFire automation framework. It utilizes the NATS.Net V2 client to manage subjects, subscriptions, and asynchronous message routing.

## Core Components

| Class | Description |
| :--- | :--- |
| **NatsManager** | Orchestrates NATS connectivity across the application. It maps NATS subjects to internal Message Bus topics. |
| **NatsDevice** | The logical representation of a NATS connection. Handles the low-level Pub/Sub operations and heartbeat logic. |
| **NatsRegistrar** | Handles the Dependency Injection (DI) registration and factory setup for NATS components. |

## Features

* **High Performance:** Built on NATS.Net V2 for minimal latency and memory overhead.
* **Dynamic Routing:** Automatically maps workflow routes to NATS subjects.
* **Automatic Reconnection:** Inherits robust state management and backoff logic from `ClientDeviceBase`.
* **Automated Heartbeats:** Sends periodic "HB" signals on `{DeviceName}.heartbeat` to monitor link health.

## Usage 

### 1. Add a NATS Device to Configuration
To enable NATS, add a device entry to your `.chamber` (or target configuration) under the `DeviceList`:

```json
{
  "Name": "NATS_BROKER",
  "Manager": "NatsManager",
  "Enable": true,
  "Properties": {
    "Url": "nats://127.0.0.1:4222",
    "DefaultWriteSubject": "NATS_BROKER.default.out",
    "SubscribedSubjects": "subject.one, subject.two"
  }
}
```

### 2. Configuration Settings (Properties)

| Setting | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| **Url** | string | `nats://127.0.0.1:4222` | The NATS server connection URL. |
| **DefaultWriteSubject** | string | `{Name}.out` | Fallback subject used for `SendAsync` if no destination is provided. |
| **SubscribedSubjects** | string | (empty) | A comma-separated list of subjects to subscribe to immediately on startup. |
| **HeartbeatIntervalMs** | int | `5000` | Frequency of heartbeat signals sent to the NATS server. |
| **HeartbeatTimeoutMs** | int | `15000` | Time to wait for activity before marking the device as "No Activity". |

### 3. Routing Logic
The `NatsManager` follows the standard FortnaFire routing convention:

*   **Inbound (NATS -> Bus):** When a workflow route `Source` starts with the NATS device name (e.g., `NATS_BROKER.MsgType.Orders`), the manager subscribes to the NATS subject `Orders`. Incoming messages are wrapped in a `MessageEnvelope` and published to the internal bus.
*   **Outbound (Bus -> NATS):** When a workflow route `Destination` starts with the NATS device name (e.g., `NATS_BROKER.MsgType.Updates`), any message published to that bus topic is automatically extracted from its envelope and published to the NATS subject `Updates`.

### 4. Topic Structure
Topics in FortnaFire are segmented as: `DeviceName.MessageType.Discriminator`.
For NATS routing, the **Discriminator** is typically mapped to the **NATS Subject**.

Example Route:
*   **Source:** `NATS_BROKER.OrderRequest.NEW_ORDERS`
*   **Result:** The system listens to NATS subject `NEW_ORDERS` and publishes to the internal bus topic `NATS_BROKER.OrderRequest.NEW_ORDERS`.
