# Fortna Fusion — Logging Standards

*Last updated: 2026-07-10. Applies to all Fusion code. The pipeline these rules
target is the two-tier design shipped with `Fusion.Element.Logger` — see
`Fusion.Element.Logger/_documentation/ReadMe-Logger.md` for the architecture.*

---

## 1. The One API

All library and element code logs through **`IFireLogger`** — never `Console.WriteLine`,
never raw `Serilog.Log`, never `Microsoft.Extensions.Logging.ILogger` directly.

- `Console.WriteLine` in library code is a defect (tracked in TODO.md); it bypasses
  structure, context, the LoggingBus, and every sink.
- Raw `Serilog.Log` is reserved for exactly two places: bootstrap code that runs
  before DI exists, and the logging pipeline's own internals (which must never log
  through themselves — see §6).

## 2. Message Templates, Not Strings

Log with **named template holes and arguments** — never interpolation, never
concatenation:

```csharp
// RIGHT — structure survives to CLEF/Seq/DB sinks and SYS.LOG topics
Logger.Information("[{Dev}] Sending scripted group {Group}/{Total}", Config.Name, i, total);

// WRONG — the values are baked into a flat string; nothing downstream can query them
Logger.Information($"[{Config.Name}] Sending scripted group {i}/{total}");
```

Rules:
- Hole names are PascalCase and stable — treat them as a schema. Reuse the
  established names: `{Dev}` (element name), `{Topic}`, `{Client}`, `{Gin}`,
  `{Payload}`, `{Count}`, `{Msg}`.
- Element-scoped messages start with the `[{Dev}]` prefix (codebase-wide
  convention; the console monitor and log filters rely on it).
- Never pre-render values into the template (`.ToString()`, string.Format) when
  they can be passed as arguments.

## 3. Context

- Element code gets its logger from the manager via `Logger.WithContext("ElementName", config.Name)`
  — every event from that element then carries the property automatically.
- Reactions and managers add correlation where they have it: GIN, DecisionPoint,
  barcode(s). For carton/conveyable tracking specifically, use
  **`LogConveyableEvent(element, message, gin, barcodes, decisionPoint)`** — it is
  the auditable trail for "where was this carton," publishes to the bus, and is
  exempt from element-exclusive mode (always direct-written). Do not reinvent it
  with ad-hoc Information calls.

## 4. Levels — What Goes Where

| Level | Meaning in Fusion | Examples | Republication |
| :--- | :--- | :--- | :--- |
| `Verbose` | Wire-level noise; only for live debugging | RX/TX raw bytes, bus dispatch entry/exit | Never leaves the LoggingBus |
| `Debug` | Developer flow tracing | state transitions, handler wiring, config resolution | Never leaves the LoggingBus |
| `Information` | Normal operations someone would read tomorrow | element started, connection established, scripted group sent, N rows published | Stays in sink files |
| `Warning` | Something abnormal that the system handled | discarded malformed command, reconnecting, watchdog timeout, dropped log messages, element-not-found | **Republished to `SYS.LOG.*`** (default floor) |
| `Error` | An operation failed; system continues | send failure, handler exception, DB operation failed | Republished + `BusErrorMessage` where a message was involved |
| `Fatal` | The process/host cannot continue | unhandled exception at host level | Republished; also always hits the direct emergency sink |

Level discipline:
- **Per-message work is Verbose/Debug.** If it fires per carton at line rate, it
  is not Information. (High-volume exceptions: the audited conveyable trail via
  `LogConveyableEvent`, and inbound/outbound message logs already established at
  Information in HostComm — keep those consistent rather than churn them.)
- **Warnings must be actionable or countable.** If nobody would ever act on it or
  chart it, it's Debug.
- **Never log-and-swallow silently.** A dropped/discarded message is at minimum a
  Warning, and if it carried a business payload, also publish a `BusErrorMessage`
  to `MessageBusTopic.InternalError` — use `ElementManagerBase.TryParseCommand<T>`,
  which does both for you.

## 5. Exceptions

- Pass the exception object: `Logger.Error(ex, "[{Dev}] Send failure", Config.Name)` —
  never `Logger.Error("failed: " + ex.Message)` (loses type + stack).
- Log an exception **once, where it is handled** — not at every level it bubbles
  through. Rethrows do not log.
- Expected connection-lifecycle noise (peer reset, operation aborted on shutdown)
  is Debug/Warning, not Error — see `TcpClientElementBase.ReadLoopAsync` for the
  pattern of catching specific `SocketError` codes.

## 6. Pipeline Rules (do not break these)

These invariants keep the logging system from feeding back into itself:

1. **The raw firehose rides the LoggingBus only** (bounded channel, drop-oldest).
   Never publish raw log events onto the main MessageBus.
2. **`SYS.LOG.*` topics are audit-exempt** (BusAuditLogger skips them) and carry
   only curated events (Warning+ by default). Don't lower a sink element's
   `RepublishMinimumLevel` below Warning in production.
3. **The logging pipeline never logs through itself.** LoggingBus, FireLogger
   internals, and sink elements report their own problems via direct Serilog only.
4. **Warning+ always dual-writes** to the direct emergency sink, even in
   `LoggerElementExclusive` mode. Never remove that path — it is the evidence
   trail when the bus itself is broken.
5. Wire format for structured events is **CLEF** (`LogEventMessage.ToClef()`).
   New sinks consume CLEF; don't invent parallel formats.

## 7. What Not to Log

- **Credentials, connection strings with passwords, API keys** — ever. Blueprint
  `Properties` may contain secrets; log property *names*, not full property bags.
- **Full payload dumps at Information+** — payloads go at Verbose/Debug, or as
  bounded excerpts (`payload[..Math.Min(payload.Length, 256)]`) when a Warning
  needs context.
- Anything inside a per-byte or per-poll loop without sampling — use
  `LogSampled(key, message, sampleRate)` for high-frequency observations.

## 8. Review Checklist

When reviewing a PR, logging is wrong if you see:
- [ ] `Console.WriteLine` / `$"..."` interpolation into a log call
- [ ] A catch block that neither logs nor rethrows (silent drop)
- [ ] `ex.Message` passed as a string instead of the exception object
- [ ] Per-message-rate logging at Information or above (outside the established trails)
- [ ] A new hole name for a concept that already has one (`{Element}` vs `{Dev}`)
- [ ] Log output written directly to the main MessageBus
