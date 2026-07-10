# Fusion Link Suite (`Fusion.Element.Link`)

Fusion Link federates the Message Bus across multiple Fusion instances. It lets an
element in one instance subscribe to a topic published in another instance as if it
were local — no broker (ActiveMQ/NATS/MQTT) required, just a direct TCP connection
between the two instances.

---

## 1. Elements

| Element | Manager | Role |
| :--- | :--- | :--- |
| `LinkServerElement` | `LinkServerElementManager` | Listens for incoming link connections. Remote peers subscribe to this instance's bus topics; matching envelopes are forwarded to them. |
| `LinkClientElement` | `LinkClientElementManager` | Connects out to a Link Server. Pulls `RemoteTopics` from the remote instance and pushes `PublishTopics` to it. |

A single connection is **bidirectional**: the client can both receive remote topics
and publish local topics over the same socket.

Both elements follow the standard suite conventions: auto-discovery via the
`Fusion.Element.*.dll` scan, `IElementRegistrar` DI registration, `StatusTracker`
health reporting, and the console status monitor.

---

## 2. How It Works

```
  Instance SITE_A (server)                     Instance SITE_B (client)
 ┌───────────────────────────┐               ┌───────────────────────────┐
 │  MessageBus               │               │  MessageBus               │
 │    ▲            │         │               │    ▲            │         │
 │    │ publish    │ matching│               │    │ republish  │ matching│
 │    │ frames     ▼ topics  │               │    │ frames     ▼ topics  │
 │  LinkServerElement ◄──────┼── TCP :7500 ──┼─► LinkClientElement       │
 └───────────────────────────┘               └───────────────────────────┘
```

1. The client connects and sends a `Subscribe` frame listing its `RemoteTopics`.
2. For each pattern, the server subscribes on its **local** bus (the MessageBus
   natively supports `*` = one segment and `#` = all remaining segments).
3. Every matching envelope is serialized into a `Publish` frame and sent to the
   subscribed client(s). The client republishes it onto its local bus, preserving
   the original topic, payload, GIN, priority, and correlation ID.
4. In the other direction, the client manager subscribes to each `PublishTopics`
   pattern locally and forwards matching envelopes to the server, which republishes
   them on its bus.
5. The client heartbeats every `HeartbeatIntervalMs` (default 5000 ms); the server
   answers with `HeartbeatAck` and uses the traffic to satisfy its watchdog. The
   client auto-reconnects and re-subscribes after any connection loss.

### Wire Protocol

Frames are compact JSON terminated by `\n` (compact JSON never contains a raw
newline, so the terminator is safe even for payloads with embedded newlines):

```json
{"Kind":"Subscribe","Origin":"SITE_B","Topics":["HOST_A.Inbound","PLC1.*"]}
{"Kind":"Publish","Origin":"SITE_A","Topic":"HOST_A.Inbound","Payload":"...","Gin":0,"HighPriority":true,"SourceElement":"HOST_A","CorrelationId":"..."}
{"Kind":"Heartbeat","Origin":"SITE_B"}
{"Kind":"HeartbeatAck","Origin":"SITE_A"}
```

Frame kinds: `Subscribe`, `Unsubscribe`, `Publish`, `Heartbeat`, `HeartbeatAck`.
Non-string payloads are JSON-serialized before transmission and arrive at the
remote side as string payloads.

---

## 3. Multiple Connections

**A Link Server accepts multiple simultaneous clients** (`MaxClients`, default 4).
Subscriptions are tracked **per client**: each connected instance only receives
the topics it asked for, and when a client disconnects, only its bus subscriptions
are removed — other clients are unaffected. One central instance can therefore
serve several satellites at once, each pulling a different slice of the bus.

**A Link Client holds one connection to one server**, but you can declare any
number of client elements in a blueprint, each targeting a different remote
instance:

```json
{ "Name": "LINK_TO_SITE_A", "Manager": "LinkClientElementManager",
  "Properties": { "IPAddress": "10.0.1.5", "Port": "7500", "RemoteTopics": "PLC1.*" } },
{ "Name": "LINK_TO_SITE_C", "Manager": "LinkClientElementManager",
  "Properties": { "IPAddress": "10.0.3.5", "Port": "7500", "RemoteTopics": "SORTER.#" } }
```

This supports hub-and-spoke (many clients → one server) and fully meshed
topologies (every instance runs a server and links to every peer it needs).

### Loop Guard — and Why Relaying Is Single-Hop

Every envelope that arrives over a link is republished with a client tag of
`LINK:<origin>` (e.g. `LINK:SITE_A`). Before forwarding anything over a link —
in either direction — the suite checks for this tag and skips the envelope.

This is deliberate: if SITE_A and SITE_B link to each other and both subscribe
to the same topic, an untagged design would bounce every message back and forth
forever (an echo storm). The tag guarantees a message crosses **at most one
link hop** and dies there.

The consequence: messages do **not** transit *through* an intermediate instance.
If SITE_C wants a topic that originates in SITE_A, SITE_C must link **directly**
to SITE_A — it cannot receive it relayed via SITE_B:

```
  Works:      SITE_A ◄── SITE_C            Does NOT work:  SITE_A ◄── SITE_B ◄── SITE_C
              (direct link)                                (B will not relay A's traffic to C)
```

If a true relay/hub topology is ever needed, the protocol would need a hop count
or an origin list in `LinkFrame` (so a message is dropped only when it would
revisit an instance it has already crossed) instead of the current single-hop tag.
That is a small, isolated change in `LinkConventions` — flag it before designing
a multi-hop deployment.

---

## 4. Configuration Reference

### Link Server

```json
{
  "Name": "LINK_SERVER",
  "Manager": "LinkServerElementManager",
  "Enable": true,
  "Properties": {
    "Port": "7500",
    "MaxClients": "4",
    "Origin": "SITE_A",
    "AllowedHosts": "10.0.1.20;10.0.3.20",
    "HeartbeatTimeoutMs": "30000"
  }
}
```

| Property | Default | Description |
| :--- | :--- | :--- |
| `Port` | — (required) | TCP listen port. |
| `MaxClients` | `4` | Maximum simultaneous link clients. |
| `Origin` | ServiceName → CustomerName → machine name | Identity announced in outgoing frames. |
| `AllowedHosts` | `*` | Semicolon-separated IP allowlist (from `TcpServerElementBase`). |
| `HeartbeatTimeoutMs` | `30000` | Watchdog: disconnect a client silent for this long (ping-checked first). |
| `MaxBufferSize` | `65535` | Maximum bytes buffered while waiting for a frame terminator. |

### Link Client

```json
{
  "Name": "LINK_TO_SITE_A",
  "Manager": "LinkClientElementManager",
  "Enable": true,
  "Properties": {
    "IPAddress": "10.0.1.5",
    "Port": "7500",
    "Origin": "SITE_B",
    "RemoteTopics": "HOST_A.Inbound;HOST_A.Outbound.*",
    "PublishTopics": "LOCAL_SORTER.#",
    "HeartbeatIntervalMs": "5000"
  }
}
```

| Property | Default | Description |
| :--- | :--- | :--- |
| `IPAddress` / `Port` | — (required) | Remote Link Server endpoint. |
| `Origin` | ServiceName → CustomerName → machine name | Identity announced in outgoing frames. |
| `RemoteTopics` | empty | Patterns to pull **from** the remote instance (`;` or `,` separated; `*`/`#` wildcards). |
| `PublishTopics` | empty | Local patterns to push **to** the remote instance. |
| `HeartbeatIntervalMs` | `5000` | Link heartbeat interval. |
| `MaxBufferSize` | `65535` | Maximum bytes buffered while waiting for a frame terminator. |

A working demo pair lives in `ProjectConfigurations/`:
`fbp_LinkDemoServer.json` and `fbp_LinkDemoClient.json` (run two consoles with
`--config=` pointing at each; inject a test line into HOST_A on port 6601 and
watch it appear on the client instance's bus).

---

## 5. Operational Notes

- **Topic case**: `MessageBusTopic` normalizes string topics to upper case;
  subscription matching is case-insensitive, so `HOST_A.Inbound` and
  `HOST_A.INBOUND` are the same topic on the wire and on the bus.
- **Payloads are strings on arrival**: object payloads are JSON-serialized for
  transport. Subscribers on the remote side should expect string payloads (the
  normal case for parser-driven reactions).
- **Delivery guarantees**: none beyond TCP. If the link is down, envelopes
  published during the outage are not queued or replayed — the client simply
  re-subscribes on reconnect and receives traffic from that point on. Use a
  durable broker element (ActiveMQ/NATS) where store-and-forward is required.
- **Status/health**: both elements report through the standard status pipeline,
  so link state (Connecting / Connected / Listening / Faulted) is visible in the
  console monitor and diag server like any other element.
