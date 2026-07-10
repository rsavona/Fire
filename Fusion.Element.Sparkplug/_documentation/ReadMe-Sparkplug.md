# Fusion Sparkplug Suite (`Fusion.Element.Sparkplug`)

Makes a Fusion instance appear as a **Sparkplug B 3.0 Edge Node** on an MQTT
broker, so hosts like **Ignition MQTT Engine** see Fusion elements as devices
with live metrics in their tag browser — and can write commands back.

The Sparkplug B protobuf payload (org.eclipse.tahu `sparkplug_b.proto`) is
implemented in-repo (`Protocol/`) with no external protobuf dependency; the
MQTT transport reuses **MQTTnet**, the same library as `Fusion.Element.Mqtt`.

## Identity Mapping

| Sparkplug concept | Fusion concept | Default |
| :--- | :--- | :--- |
| Group ID | The site | Space `CustomerName` (else `Fusion`) |
| Edge Node ID | This Fusion instance | `Origin` property → space `ServiceName` → `CustomerName` → machine name (same precedent as the Link suite) |
| Device ID | A Fusion element | Discovered from `All_Elements.StatusMessage` traffic |
| Metrics | Element status fields + blueprint-selected topic payloads | `State`, `Health`, `Counts/*`, `Rates/*`, `Resources/*`, `Metrics/*`, plus one metric per matching `MetricTopics` envelope |

Resulting MQTT topics: `spBv1.0/{GroupId}/{NBIRTH|NDEATH|NDATA|NCMD}/{EdgeNodeId}`
and `spBv1.0/{GroupId}/{DBIRTH|DDEATH|DDATA|DCMD}/{EdgeNodeId}/{ElementName}`.

## Session Lifecycle

1. **CONNECT** — every MQTT connect opens a new Sparkplug session: `bdSeq`
   increments (wrapping at 255) and the CONNECT packet registers an **NDEATH
   will** carrying that `bdSeq`, so the broker announces the node's death if
   the connection drops uncleanly.
2. **NBIRTH** — published immediately on connect with `seq = 0`, the `bdSeq`
   metric (matching the will), the writable `Node Control/Rebirth` metric, and
   node info metrics (platform, version, machine).
3. **DBIRTH** — one per known Fusion element. Elements are discovered by
   subscribing to the bus status topic (`All_Elements.StatusMessage`); the
   first status snapshot from an element produces a DBIRTH with its full
   metric set. Cached devices are re-birthed automatically after a reconnect.
4. **DDATA / NDATA** — status changes are **report-by-exception**: only
   metrics whose values changed are queued, and queues are flushed in batches
   every `ReportIntervalMs`. Every payload carries the 0–255 wrapping `seq`.
5. **DDEATH** — published immediately when an element reports `Critical`
   health. When the element recovers, a fresh DBIRTH is published.
6. **NCMD `Node Control/Rebirth`** — resets `seq` to 0 and republishes NBIRTH
   plus all DBIRTHs (bdSeq unchanged; the MQTT session survives). This is how
   Ignition resynchronizes after an MQTT Engine restart.
7. **Inbound writes** — any other NCMD/DCMD metric write is republished on the
   local bus as a `SparkplugCommandMessage` envelope on
   `{EdgeNodeId}.SparkplugCmd.{MetricName}`, so reactions can act on tag
   writes made in Ignition.

If the broker is unreachable, the element does **not** fault permanently — it
follows the standard client element retry cycle (exponential backoff) and
re-runs the full birth sequence once the broker returns.

## Blueprint Properties

| Property | Default | Description |
| :--- | :--- | :--- |
| `BrokerHost` | `127.0.0.1` | MQTT broker host. |
| `BrokerPort` | `1883` | MQTT broker port. |
| `Username` / `Password` | *(none)* | Optional broker credentials. |
| `UseTls` | `false` | Enable TLS on the broker connection. |
| `GroupId` | space `CustomerName` | Sparkplug Group (the site). |
| `EdgeNodeId` | instance Origin | Sparkplug Edge Node (this instance). |
| `Origin` | *(none)* | Link-suite-style identity override used when `EdgeNodeId` is not set. |
| `MetricTopics` | *(empty)* | Semicolon-separated bus topic patterns (`*`/`#` wildcards) whose payloads become device metrics. Metric name = full topic string; numeric/boolean text is typed automatically. |
| `PublishStatusMetrics` | `true` | Mirror `All_Elements.StatusMessage` traffic as device metrics. |
| `ReportIntervalMs` | `5000` | Batch interval for DDATA/NDATA flushes (minimum 250). |

## Example Blueprint Element

```json
{
  "Name": "SPARKPLUG_EDGE",
  "Manager": "SparkplugElementManager",
  "Enable": true,
  "Comment": "Publish this instance to Ignition as a Sparkplug B Edge Node",
  "Properties": {
    "BrokerHost": "10.0.0.50",
    "BrokerPort": "1883",
    "GroupId": "Dekalb",
    "EdgeNodeId": "FUSION_LINE_1",
    "MetricTopics": "SORTER.DivertConfirm;PRINTER1.#",
    "PublishStatusMetrics": "true",
    "ReportIntervalMs": "2000"
  }
}
```

## Seeing It in Ignition

1. Install the **MQTT Engine** module (and a broker — MQTT Distributor or any
   Mosquitto/HiveMQ instance) in Ignition.
2. Point MQTT Engine at the same broker configured in `BrokerHost`/`BrokerPort`.
3. Start Fusion. In Ignition's Tag Browser open
   `MQTT Engine → Edge Nodes → {GroupId} → {EdgeNodeId}` — each Fusion element
   appears as a device folder with live `State`, `Health`, `Counts/…`,
   `Rates/…` tags updating every `ReportIntervalMs`.
4. Write `true` to the node's `Node Control/Rebirth` tag to force a full
   rebirth; write to any custom metric to see the value arrive on the Fusion
   bus at `{EdgeNodeId}.SparkplugCmd.{MetricName}`.

## Files

| File | Role |
| :--- | :--- |
| `Protocol/SparkplugPayload.cs` / `SparkplugMetric.cs` | Sparkplug B protobuf payload encode/decode (spec datatypes, aliases, seq). |
| `Protocol/ProtoCodec.cs` | Minimal protobuf wire-format reader/writer. |
| `Protocol/SparkplugTopics.cs` | spBv1.0 topic builders/parser. |
| `SparkplugSession.cs` | Broker-independent session state machine: bdSeq, seq wraparound, device cache, RBE, rebirth. |
| `SparkplugEdgeNodeElement.cs` | The client element: MQTT transport, lifecycle certificates, command handling. |
| `SparkplugElementManager.cs` / `SparkplugElementRegistrar.cs` | Standard manager/registrar trio wiring bus subscriptions. |
