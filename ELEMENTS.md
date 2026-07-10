# Fortna Fusion: System Elements Summary

This document provides a comprehensive overview of the **Elements** available in the Fortna Fusion system. Elements represent hardware interfaces, communication protocols, or infrastructure components.

---

## 1. PLC Suite (`Fusion.Element.Plc.Suite`)
Manages communication with Programmable Logic Controllers.

| Element Type | Manager Name | Description |
| :--- | :--- | :--- |
| **AB CIP PLC** | `AbCipPlcManager` | Interface for Allen-Bradley PLCs using the Common Industrial Protocol (CIP). Supports read/write tags and message sequences. |
| **PLC Server** | `PlcServerManager` | Acting as a PLC server for external systems to connect to. |
| **Virtual PLC** | `VirtualPlcManager` | A simulated PLC used for testing and simulation environments. Supports virtual conveyor logic and tote injection. |

---

## 2. Printer Suite (`Fusion.Element.Printer.Suite`)
Handles integration with industrial label printers.

| Element Type | Manager Name | Description |
| :--- | :--- | :--- |
| **Zebra Printer** | `PrintClientManager` | Standard interface for Zebra thermal printers using ZPL (Zebra Programming Language). |
| **JetMark Printer** | `PrintClientManager` | Interface for JetMark printers, handling high-speed inkjet marking. |
| **Virtual Printer** | `VirtualPrinterManager` | A simulated printer that captures ZPL data and logs it or displays it on the dashboard without physical hardware. |

---

## 3. Messaging Suite
Facilitates communication across various message brokers.

| Element Type | Manager Name | Description |
| :--- | :--- | :--- |
| **ActiveMQ** | `ActiveMqManager` | Connects to Apache ActiveMQ brokers. Supports full producer/consumer patterns and session management. |
| **ActiveMQ Peek** | `ActiveMqQueuePeekManager` | Specialized element for monitoring queue depths and message counts without consuming data. |
| **NATS** | `NatsManager` | High-performance interface for NATS.io messaging clusters. |
| **MQTT** | `MqttManager` | Lightweight messaging interface for IoT-style communications. |

---

## 4. Host Communication (`Fusion.Element.HostComm`)
Direct communication layers for external host systems.

| Element Type | Manager Name | Description |
| :--- | :--- | :--- |
| **TCP Client** | `TcpMessageClientElementManager` | Generic TCP client for connecting to external host servers. |
| **TCP Server** | `TcpMessageServerElementManager` | Generic TCP server for accepting connections from external clients. |
| **File Comm** | `FileMessageElementManager` | Watches directories for incoming flat files and writes outgoing files for integration with legacy systems. |

---

## 5. Database Suite (`Fusion.Element.Database.Suite`)
Persistence layers for system data and logging.

| Element Type | Manager Name | Description |
| :--- | :--- | :--- |
| **MS SQL** | `DatabaseElementManager` | Microsoft SQL Server integration for structured data storage. |
| **MySQL** | `DatabaseElementManager` | MySQL/MariaDB database interface. |
| **PostgreSQL** | `DatabaseElementManager` | PostgreSQL database interface. |
| **DuckDB** | `DatabaseElementManager` | Embedded in-process DuckDB interface supporting file-backed (.duckdb) and in-memory (:memory:) databases. |
| **DB Pruning** | `DatabaseElementManager` | Specialized element for maintaining database health by automatically removing aged records. |

---

## 6. Enterprise Suite (`Fusion.Element.Enterprise.Suite`)
Advanced business and operational support elements.

| Element Type | Manager Name | Description |
| :--- | :--- | :--- |
| **Inventory** | `InventoryManager` | Manages real-time tracking of items, totes, and containers within the system. |
| **Telemetry** | `TelemetryManager` | Captures and exports system-wide metrics and performance data. |
| **Fire Logging** | `FireLogManager` | Centralized high-performance logging element for system-wide auditing. |

---

## 7. Notification Suite (`Fusion.Element.Notification.Suite`)
Outbound alert and notification services.

| Element Type | Manager Name | Description |
| :--- | :--- | :--- |
| **Email** | `NotificationElementManager` | Sends SMTP-based alerts and reports. |
| **SMS** | `NotificationElementManager` | Sends text message alerts via integrated SMS gateways. |

---

## 8. Support & CLI (`Fusion.Element.Support.CLI`)
System management and diagnostic tools.

| Element Type | Manager Name | Description |
| :--- | :--- | :--- |
| **Diagnostic Server**| `DiagnosticElementManager` | Provides a Telnet-accessible (Port 9999) dashboard for remote system monitoring and control. |
| **Verifier** | `BlueprintVerifierManager` | Validates the integrity of `.fusion` blueprints and identifies logical errors or missing dependencies. |

---

## 9. Link Suite (`Fusion.Element.Link`)
Federates the Message Bus across multiple Fusion instances, letting an element in one instance subscribe to topics published in another.

| Element Type | Manager Name | Description |
| :--- | :--- | :--- |
| **Link Server** | `LinkServerElementManager` | Listens for Link Client connections. Remote peers subscribe to local bus topics (wildcards `*`/`#` supported); matching envelopes are forwarded over the wire. Publishes received frames onto the local bus. |
| **Link Client** | `LinkClientElementManager` | Connects to a remote Link Server. `RemoteTopics` are pulled from the remote instance and republished locally; `PublishTopics` are pushed to the remote instance. Heartbeat + auto-reconnect built in. |

**Wire protocol:** newline-terminated compact JSON frames (`Subscribe`, `Unsubscribe`, `Publish`, `Heartbeat`, `HeartbeatAck`). Envelopes republished from a remote instance carry a `LINK:<origin>` client tag, which acts as a loop guard: anything that arrived over a link is never forwarded back out, preventing echo storms when two instances link to each other.

**Key properties:** `IPAddress`/`Port` (client), `Port`/`MaxClients` (server), `Origin` (instance identity), `RemoteTopics`, `PublishTopics` (semicolon-separated patterns), `HeartbeatIntervalMs`. See `fbp_LinkDemoServer.json` / `fbp_LinkDemoClient.json` for a working pair, and `Fusion.Element.Link/_documentation/ReadMe-Link.md` for the full protocol, multi-connection topology, and configuration reference.

---

## 10. Sparkplug Suite (`Fusion.Element.Sparkplug`)
Presents a Fusion instance as a Sparkplug B 3.0 Edge Node on an MQTT broker, so SCADA hosts like Ignition MQTT Engine see Fusion elements as devices with live metrics in their tag browser.

| Element Type | Manager Name | Description |
| :--- | :--- | :--- |
| **Sparkplug Edge Node** | `SparkplugElementManager` | Maps Group = site, Edge Node = this instance, Device = a Fusion element. Publishes the full lifecycle (NBIRTH/NDEATH with bdSeq will, DBIRTH per discovered element, batched report-by-exception DDATA, DDEATH on Critical health) and honors NCMD `Node Control/Rebirth`. Inbound NCMD/DCMD metric writes are republished on the local bus at `{EdgeNodeId}.SparkplugCmd.{MetricName}`. |

**Metrics:** element status fields (`State`, `Health`, `Counts/*`, `Rates/*`, `Resources/*`, custom `Metrics/*`) plus payloads of any blueprint-selected bus topics (`MetricTopics`, wildcards `*`/`#` supported; metric name = full topic string). The Sparkplug B protobuf payload is implemented in-repo; the transport reuses MQTTnet.

**Key properties:** `BrokerHost`/`BrokerPort`, `Username`/`Password`, `UseTls`, `GroupId` (default: space CustomerName), `EdgeNodeId` (default: instance Origin), `MetricTopics` (semicolon-separated patterns), `PublishStatusMetrics`, `ReportIntervalMs`. See `Fusion.Element.Sparkplug/_documentation/ReadMe-Sparkplug.md` for the lifecycle detail, an example blueprint, and how to browse the node in Ignition.

---
