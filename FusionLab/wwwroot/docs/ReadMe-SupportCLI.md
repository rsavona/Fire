# Fusion.Element.Support.CLI

## Purpose
Provides support and diagnostic utilities for the FortnaFire system. This includes a Telnet-based diagnostic server for real-time monitoring and blueprint verification tools.

## Messages In
| Source | Type | Description |
| :--- | :--- | :--- |
| **Admin Console** | Telnet / CLI | Manual commands for system inspection or verification. |
| **Internal Bus** | (Various) | Diagnostic listener can observe bus traffic for debugging. |

## Messages Out
| Destination | Type | Description |
| :--- | :--- | :--- |
| **Admin Console** | Text Output | Real-time status and diagnostic information. |
| **Internal Bus** | `Diagnostic.*` | Broadcasts findings or status reports. |

## Configuration Properties
The following properties can be configured in the `Properties` dictionary of the Element Blueprint:

### DiagnosticElementManager
| Property | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| **Port** | `int` | `9999` | The TCP port for the Telnet diagnostic server. |

## Mermaid Chart
```mermaid
graph LR
    Admin[Admin/Engineer] -- Telnet --> DE[Diagnostic Element]
    DE -- Query --> IB[Internal Message Bus]
    IB -- Events --> DE
    DE -- Logs/Status --> Admin
```
