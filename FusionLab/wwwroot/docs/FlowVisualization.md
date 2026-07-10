# Live Flow Visualization + Bus Replay

FusionLab's **Flow** view (`/flow`, or the FLOW button in the designer header) renders the
Fusion system as a live topology graph and can replay recorded bus traffic from the audit log.

## The Flow view

- **Nodes** are elements. They are discovered two ways: from `System.Topology`
  (`SystemTopologyMessage` — the bond configuration, same derivation the console status
  monitor uses) and dynamically from `System.DataFlow` traffic itself.
- **Edges** are bonds/flows (`source element → destination element`), labeled with the
  reaction (`Force`) that moved the message and a per-edge rate counter
  (events per minute + lifetime total).
- **Pulses**: every `FlowEvent` fires an animated pulse along its edge.
- **Health coloring**: nodes are colored from `All_Elements.StatusMessage`
  (`ElementStatusMessage.Health`): Normal (green), Warning (amber), Error (orange),
  Critical (red). Health is never conveyed by color alone — each node also shows its
  state/health as text, and a labeled legend is always visible. Nodes that have not yet
  reported status show a hollow indicator ("no status").
- When nothing is flowing the view shows an explanatory empty state, not a blank page.

FusionLab hosts its own in-process message bus. Live mode therefore shows traffic on
*this process's* bus; a FusionLab instance that is not hosting a running core will mostly
use **Replay** mode to animate audit logs recorded by any Fusion host (console runs, etc.).

## Bus replay

`Fusion.Core.Replay.ReplayService` reads the CLEF audit files that `BusAuditLogger`
writes via Serilog to `{BaseDirectory}/../logs/audit/{config}_audit_{yyyyMMdd}.json`
(i.e. `Output/logs/audit`). It is a plain in-process service rather than an element
because the audit log is a local file shared through the `Output` folder — there is no
remote endpoint to adapt.

Capabilities:

- **List windows** — every audit file is presented as a replayable window with its
  first/last timestamps and record count.
- **Time-range replay** — replay the whole file or any `From`/`To` slice; the seek slider
  restarts playback from the chosen time.
- **Speed** — `1x` (recorded pace), `5x`, or `MAX` (no inter-message delay).
- **Start / Stop / Seek** controls live in the Flow view's replay bar.

### The REPLAY.* namespace guard

**Replayed messages are always re-published on `REPLAY.{originalTopic}` and never on the
original topic.** Replaying onto original topics would re-trigger live reactions and real
hardware (printers, PLCs, host connections). Consumers that want replayed traffic must
opt in by subscribing to `REPLAY.*` topics (the Flow view subscribes to
`REPLAY.System.DataFlow` and `REPLAY.All_Elements.StatusMessage`).

Two further guards close the loop:

- `BusAuditLogger` refuses to audit any `REPLAY.*` topic, so replays never pollute the
  historical record (and replay-of-replay loops are impossible). `SYS.LOG.*` topics remain
  audit-exempt as before.
- Records whose recorded topic already carries the `REPLAY.` prefix are skipped entirely.

Replay envelopes carry provenance in the header metadata: `ReplayOriginalTopic`,
`ReplayOriginalTimestamp`, and `ReplayOriginalCorrelationId`.

## Audit capture format & the `AuditCapturePayloads` flag

`BusAuditLogger` writes each bus message as a CLEF line with structured `Topic` and
`PayloadType` properties. By default the payload is captured as its `ToString()`
rendering — compact, but not always reconstructable.

Setting **`Fusion:AuditCapturePayloads = true`** (or `AppSettings:Fusion:AuditCapturePayloads`)
captures every payload as a fully structured object (`{@Payload}`), making replay lossless.
It is **off by default** because destructuring large payloads costs disk space.
`FlowEvent` payloads are always captured structured regardless of the flag (they are tiny
and are what drives the flow animation), so flow replay works out of the box.

Legacy audit files (written before the structured template) are still parseable: topic and
payload type are recovered from the rendered message, element status (id/state/health) is
recovered by pattern, and unreconstructable payloads are replayed as their recorded text.
