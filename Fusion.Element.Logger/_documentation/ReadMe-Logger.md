# Fusion.Element.Logger — The Logger as an Element

This suite turns log persistence into a swappable Element instead of a hard-wired Serilog
pipeline. Call sites do not change: `IFireLogger` remains the only logging API.

## Architecture: Two Tiers

```
                         TIER 1 (raw firehose)                TIER 2 (curated)
 IFireLogger ──────► LoggingBus (bounded channel) ──────► SerilogSinkElement ──► Serilog sinks
   (call sites)        in-memory, drop-oldest                    │                (CLEF file, console, ...)
        │                                                        │ Level >= RepublishMinimumLevel
        └──► direct Serilog (dual-write, default on)             ▼
                                                          Main MessageBus
                                                          SYS.LOG.{Element}.{Level}
                                                          (LogEventMessage payload)
```

- **Tier 1 — raw firehose.** Every log event flows over the dedicated `LoggingBus`
  (`Fusion.Core/LoggingBus.cs`), a `System.Threading.Channels`-based bus completely
  separate from the main `MessageBus`. The raw stream is high-volume (Verbose method
  enter/exit tracing included) and must never touch `MessageBus.PublishAsync` — it would
  drown the operational bus and the audit log.
- **Tier 2 — curated republish.** The `SerilogSinkElement` filters the firehose and
  republishes only significant events (default: Warning and above) on the main MessageBus
  as `LogEventMessage` payloads on topics `SYS.LOG.{ElementName}.{Level}`. Reactions,
  notification elements, or Link peers can subscribe to these without ever seeing the
  firehose.

## Why not durable?

The log channel is intentionally in-memory. Durability (files, rolling retention,
shipping to a log store) is a **sink** concern — the Serilog pipeline that the
`SerilogSinkElement` owns — not a **bus** concern. Making the bus durable would couple
every logging call site to disk I/O latency and failure modes, exactly what the channel
design exists to avoid. If the process dies, the direct Serilog emergency path (below)
has already persisted everything at Warning and above.

## Why bounded?

The previous LoggingBus channel was unbounded: a slow sink (disk stall, huge burst)
would grow the queue without limit and eventually take the process down with it. The
channel is now **bounded** (default capacity 10,000, configurable via
`Fusion:LoggingBusCapacity`) with `FullMode.DropOldest`:

- Publishing never blocks an element's hot path.
- Memory is capped.
- Under back-pressure, the oldest (least relevant) chatter is sacrificed first.
- Drops are counted (`LoggingBus.DroppedMessageCount`) and reported at most every 30 s
  through a **direct** Serilog warning — never through the bus itself, which would just
  add pressure to a full channel.

## Message shape: `LogEventMessage`

`Fusion.Common/Logging/LogEventMessage.cs` — a fully structured event: UTC `Timestamp`,
`Level`, `MessageTemplate` (named holes preserved), `RenderedMessage`, `Properties`
(name → value), `Exception` (string), `ElementName`. It serializes to/from CLEF
(Serilog Compact Log Event Format: `@t`, `@l`, `@mt`, `@m`, `@x` + properties) via
`ToClef()` / `FromClef()`, so events survive process/wire boundaries without losing
structure. `FireLogger` attaches a pre-built `LogEventMessage` to every `LogMessage` it
publishes (the `LogMessage.Event` property), keeping the `ILoggingBus` contract intact.

## FireLogger dual-write and the exclusive flag

By default nothing changes for deployments without this element: `FireLogger` **dual
writes** — directly to Serilog (as always) *and* to the LoggingBus. Setting

```json
{ "Fusion": { "LoggerElementExclusive": true } }
```

stops the direct Serilog write for sub-Warning events, making the Logger element the
sole persistence path for routine logs. **Emergency path:** Warning, Error, and Fatal
events are ALWAYS also written directly to Serilog regardless of the flag, so a
misconfigured or dead sink element can never hide serious problems. The
`LogConveyableEvent` tracking path is deliberately untouched (always direct): carton
tracking logs are operationally critical.

## Blueprint properties

| Property | Default | Description |
| :--- | :--- | :--- |
| `TopicPattern` | `#` | LoggingBus pattern to consume (`LOG.{LEVEL}.{CONTEXT}`; `*` = one segment, `#` = rest). |
| `LogFilePath` | `../logs/logger-element/{Name}_.clef` | CLEF output file (daily rolling, 14 retained). |
| `MinimumLevel` | `Verbose` | Sink pipeline minimum level. |
| `WriteToConsole` | `false` | Also write rendered events to the console. |
| `RepublishMinimumLevel` | `Warning` | Level floor for the curated MessageBus republish. |
| `RepublishTopicPattern` | `#` | Origin-topic filter for the republish. |

Example element entry:

```json
{
  "Name": "LOGSINK",
  "Manager": "LogSinkElementManager",
  "Enable": true,
  "Properties": {
    "RepublishMinimumLevel": "Warning",
    "WriteToConsole": false
  }
}
```

## Recursion guards (why this can't feed back)

Logging about logging is the classic feedback loop. Four guards break every cycle:

1. **The element never logs its own hot path through `IFireLogger`.** All internal
   diagnostics use a direct Serilog logger (`Log.Logger.ForContext(...)`), which cannot
   re-enter the LoggingBus.
2. **`SYS.LOG` topics are exempt from the BusAuditLogger** (`Fusion.Core/BusAuditLogger.cs`),
   so curated republishes are never audited back into the log stream.
3. **The element never republishes events that are already republishes**: events whose
   `ElementName` matches the element itself or carries a `SYS.LOG` topic are written to
   the sink but never re-published.
4. **MessageBus verbose dispatch tracing is safe**: it logs through `IFireLogger` into
   the LoggingBus, but Verbose never crosses the `RepublishMinimumLevel` floor, so it is
   persisted (Tier 1) without ever echoing onto the MessageBus (Tier 2).

## Known limitations

- `ILoggingBus` has no unsubscribe; if an element instance is reconciled away, its
  handler stays registered but goes inert (state-machine gate in the handler).
- In exclusive mode, sub-Warning events no longer appear in the legacy per-element log
  files built by `CoreServicesExtension` — they live in the element's CLEF output instead.
