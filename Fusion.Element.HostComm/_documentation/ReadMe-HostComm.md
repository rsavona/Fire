# Fusion.Element.HostComm

## Purpose
Provides low-level communication interfaces for external hosts via TCP/IP sockets (Client/Server) or File System drops (Hot Folders). It includes robust parsing for JSON, XML, Fixed-Width, and Delimited payloads.

## Messages In
| Source | Type | Description |
| :--- | :--- | :--- |
| **External Host** | TCP Stream / File | Raw data received from an external system. |
| **Internal Bus** | `ElementName.*` | Messages to be sent out to the external host. |

## Messages Out
| Destination | Type | Description |
| :--- | :--- | :--- |
| **Internal Bus** | `ElementName.Inbound.*` | Parsed data from the host published for internal consumption. |
| **External Host** | TCP Stream / File | Formatted data sent to the external system. |

## Configuration Properties
The following properties can be configured in the `Properties` dictionary of the Element Blueprint:

### TcpMessageServerElementManager
| Property | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| **Port** | `int` | **Required** | The TCP port the server listens on. |
| **MaxClients** | `int` | `1` | Maximum number of concurrent client connections. |
| **TerminationType** | `string` | `DELIMITED` | `DELIMITED` or `FIXED`. |
| **Delimiter** | `string` | `\n` | The message terminator (e.g. `\r`, `\n`, `\r\n`). |
| **FixedLength** | `int` | `0` | Message length if `TerminationType` is `FIXED`. |
| **ParserType** | `string` | `RAW` | Data parser (`JSON`, `XML`, `FIXED`, `DELIMITED`, `RAW`). |

### TcpMessageClientElementManager
| Property | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| **IPAddress** | `string` | **Required** | The remote host IP address. |
| **Port** | `int` | **Required** | The remote host TCP port. |
| **HeartbeatIntervalMs** | `int` | `0` | If > 0, enables periodic heartbeats. |
| **HeartbeatMessage**| `string`| `""` | The string to send for heartbeats. |
| **HeartbeatAck** | `string`| `HB_ACK` | Expected response to a heartbeat. |

### FileMessageElementManager
| Property | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| **HotFolder** | `string` | `C:\HotFolder` | Directory to monitor for new files. |
| **ArchiveFolder**| `string` | `[HotFolder]\Archive` | Where to move processed files. |
| **ErrorFolder** | `string` | `[HotFolder]\Error` | Where to move failed files. |
| **FileFilter** | `string` | `*.*` | Filter for files to monitor (e.g. `*.json`). |
| **OutboundFolder**| `string`| `[HotFolder]\Outbound` | Directory where outbound files are written. |

## Mermaid Chart
```mermaid
graph LR
    Host[External Host] -- TCP/File --> HC[HostComm Element]
    HC -- Parser --> IB[Internal Message Bus]
    IB -- Topic: ElementName.Out --> HC
    HC -- Formatter --> Host
```
