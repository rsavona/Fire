# Fusion.Element.Scanner.Suite

## Purpose
Manages barcode scanning operations, including physical scanner connections and simulated barcode generation.

## Messages In
| Source | Type | Description |
| :--- | :--- | :--- |
| **Physical Scanner** | TCP / Serial | Raw barcode data from the field. |
| **Internal Bus** | (Various) | Control commands or configuration updates. |

## Messages Out
| Destination | Type | Description |
| :--- | :--- | :--- |
| **Internal Bus** | `ElementName.Scan.*` | Discovered barcodes published for reaction logic. |

## Configuration Properties
The following properties can be configured in the `Properties` dictionary of the Element Blueprint:

### ScannerSimulatorManager
| Property | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| **Port** | `int` | `5000` | The TCP port the simulator server listens on. |
| **BarcodeSource** | `string` | `Random` | Source of barcodes (`Random`, `File`, `Sql`). |
| **BarcodeLengthRange** | `string` | `10-20` | Range for random barcode lengths (e.g. "12-15"). |
| **ScanIntervalRangeMs**| `string` | `1000-5000`| Range for interval between scans in milliseconds. |
| **Prefix** | `string` | `""` | Prefix to prepend to generated barcodes. |
| **Suffix** | `string` | `""` | Suffix to append to generated barcodes. |
| **TerminationChar** | `string` | `\r` | Character to terminate barcode messages with. |
| **SourceFile** | `string` | `null` | Path to a text file containing line-separated barcodes (used if `BarcodeSource` is `File`). |

## Mermaid Chart
```mermaid
graph LR
    SS[Scanner Simulator] -- Barcode --> SC[Scanner Client]
    SC -- Publish --> IB[Internal Message Bus]
```
