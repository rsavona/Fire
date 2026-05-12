# Device.HostComm

This library provides a suite of devices for communicating with external hosts via common protocols such as TCP/IP and File System. It includes specialized support for message parsing (JSON, XML, Fixed-Length, Delimited) and lifecycle management.

## Core Components

| Class | Description |
| :--- | :--- |
| **TcpMessageServerDevice** | A TCP Server that listens for incoming connections. Expects ETX-terminated messages and routes parsed payloads to the Message Bus. |
| **TcpMessageClientDevice** | A TCP Client that connects to a remote server. Supports heartbeats and automated reconnection. |
| **FileMessageDevice** | Monitors a local or network folder (Hot Folder) for new files, parses their content, and archives or errors them after processing. |

## Shared Features: Payload Parsing
All devices in `HostComm` use the `PayloadParserFactory`. By setting the `ParserType` in the configuration, you can automatically convert raw data into JSON objects for the internal Message Bus.

**Supported ParserTypes:**
*   `JSON`: Standard JSON parsing.
*   `XML`: Converts XML to JSON objects.
*   `FIXED`: Parses fixed-width positional strings.
*   `DELIMITED`: Parses CSV or custom-delimited strings.

---

## Device Usage & Settings

### 1. TCP Server Device
Listens for multiple clients and accepts data.

**Manager:** `TcpMessageServerManager`

| Setting | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| **Port** | int | (required) | The TCP port to listen on. |
| **MaxClients** | int | `1` | Maximum number of concurrent client connections. |
| **ParserType** | string | `JSON` | The parsing strategy to use (JSON, XML, etc.). |

```json
{
  "Name": "HOST_LISTENER",
  "Manager": "TcpMessageServerManager",
  "Properties": {
    "Port": 8000,
    "MaxClients": 5,
    "ParserType": "JSON"
  }
}
```

---

### 2. TCP Client Device
Connects to a remote host (e.g., a Host ERP or WMS).

**Manager:** `TcpMessageClientManager`

| Setting | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| **IPAddress** | string | (required) | The remote server IP. |
| **Port** | int | (required) | The remote server Port. |
| **HeartbeatMessage**| string | (empty) | String to send periodically to the server. |
| **HeartbeatAck** | string | `HB_ACK` | String to look for in responses to validate link health. |
| **ParserType** | string | `JSON` | The parsing strategy for received data. |

```json
{
  "Name": "WMS_CLIENT",
  "Manager": "TcpMessageClientManager",
  "Properties": {
    "IPAddress": "10.0.0.50",
    "Port": 5001,
    "HeartbeatIntervalMs": 10000,
    "HeartbeatMessage": "KEEP_ALIVE",
    "HeartbeatAck": "ALIVE_OK"
  }
}
```

---

### 3. File Message Device
Monitors a folder for data drops.

**Manager:** `FileMessageManager`

| Setting | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| **HotFolder** | string | `C:\HotFolder` | The directory to monitor for new files. |
| **ArchiveFolder** | string | `HotFolder\Archive`| Where processed files are moved on success. |
| **ErrorFolder** | string | `HotFolder\Error` | Where files are moved if parsing fails. |
| **FileFilter** | string | `*.*` | Glob pattern for files (e.g., `*.xml`). |
| **ParserType** | string | `JSON` | How to parse the file content. |

```json
{
  "Name": "ERP_FILE_DROP",
  "Manager": "FileMessageManager",
  "Properties": {
    "HotFolder": "C:\\Fire\\Orders",
    "FileFilter": "*.txt",
    "ParserType": "DELIMITED",
    "Delimiter": "|"
  }
}
```

---

## Routing Convention
*   **Inbound:** When a message is received (via TCP or File), it is published to the Message Bus using the topic segment derived from the device name and the parsed payload.
*   **Outbound:** To send data out via a TCP Client or Server, set the Route `Destination` to start with the device name (e.g., `WMS_CLIENT.Outbound.Orders`). The manager will send the payload to the external host.
