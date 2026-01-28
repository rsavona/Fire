# Device.Plc.dll

The PlcDevice is a gateway for communication between Fusion and PLCs using the `WES-Routing Interface Message Specification`.

## Workload Developement
The PlcDeviceManager publishees Decision Requests and Decision Updates:
* Decision Request Payload 
```json
    { 
        "DecisionPoint":"PNA_Induct",
        "GIN":123,
        "Barcodes":[
            "BC01234","NOREAD"
        ],
        "Length":5000,
        "Width":6001,
        "Height":12980,
        "Weight":142,
        "Timestamp":"02-25-2022",
        "Header":"DReqM"
    }
```

* Decision Update Payload 
 

* Decision Response Payload
```json
 { 
    "DecisionPoint":"descPoint", 
    "GIN": 123, 
    "Actions": [ "Printer1", "Printer2" ] 
  }
```








## Objects
````
├── PlcDeviceManager - There is 1 manager that handles creation of all PlcDevices. 
    ├── PlcDevice - 1 device per server port needed.
        └── TcpServer from DeviceSpace.Utilities 
           └── MessageProcessor
              └── MessageParser


````

`[STX] [PLC_ID] [GS] [SequenceNumber] [GS] [Header] [GS] [Payload] [ETX]`

| Component | ASCII | Hex | Description |
| :--- | :--- | :--- | :--- |
| **STX** | 02 | `0x02` | Start of Text |
| **PLC_ID** | - | - | Unique Connection ID string |
| **GS** | 29 | `0x1D` | Group Separator |
| **SequenceNumber** | - | - | `1` to `99999999`. Used for ACK matching. |
| **Header** | - | - | Defines message type (e.g., `DReqM`, `HB`) |
| **Payload** | - | - | JSON formatted data string |
| **ETX** | 03 | `0x03` | End of Text |

## 2. Message Definitions

### A. Decision Request (`DReqM`)

**Direction:** PLC $\rightarrow$ WCS

**Ack Required:** No

**Description:** Sent when an item arrives at a decision point (scanner/divert).

| JSON Field | JSON Type | C# Type | Description |
| :--- | :--- | :--- | :--- |
| `DecisionPoint` | String | `string` | ID of the scanner/zone (e.g., `0001_UNL01_001`). |
| `GIN` | Integer | `int` | Global Identification Number (PLC assigned, recycled). |
| `Barcodes` | Array | `List<string>` | List of barcodes read. Use `["NOREAD"]` if empty. |
| `Length` | Integer | `int` | Item length (mm). |
| `Width` | Integer | `int` | Item width (mm). |
| `Height` | Integer | `int` | Item height (mm). |
| `Weight` | Number | `double` / `int` | Item weight (grams/lbs per config). |
| `Metadata` | Object | `Dictionary<string, object>` | Optional project-specific data (e.g., `{"DockDoor": 6}`). |
| `Timestamp` | String | `DateTime` | Format: `yyyy-MM-dd HH:mm:ss.fff` |

**Example Payload:**

```json
{
  "DecisionPoint": "0001_IDZ1_001",
  "GIN": 123,
  "Barcodes": ["01234", "NO_READ"],
  "Length": 500,
  "Width": 600,
  "Height": 1200,
  "Weight": 1420,
  "Metadata": {"DockDoor": 6},
  "Timestamp": "2022-11-07 09:45:12.413"
}