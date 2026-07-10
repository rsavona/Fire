# Fortna Fusion — Product Roadmap

*Last updated: 2026-07-10*

This roadmap covers the next wave of Fusion capabilities. It is organized around a
simple thesis: **features get evaluated, demos get remembered.** Fusion's unfair
advantage is that the simulation fleet (virtual PLC / printer / scanner), the bus
audit log, FusionLab, and Tokamak AI already exist — most of the "notice me"
work is packaging and connecting what is already in the repo.

---

## Theme 1 — Credibility Integrations

### 1.1 Ignition / Sparkplug B Element (`Fusion.Element.Sparkplug`)
**Why:** Inductive Automation Ignition is the de facto plant SCADA. A first-class
integration is instant credibility with the engineers who evaluate platforms —
Fusion topics appearing live in an Ignition tag browser closes arguments.

**How:**
- Build a Sparkplug B layer on top of the existing `MqttManager` transport:
  NBIRTH/NDEATH/DBIRTH/DDEATH lifecycle certificates, typed metric encoding,
  sequence numbers, and rebirth handling.
- Map Fusion identity onto the Sparkplug model: Group = site, Edge Node =
  Fusion instance, Device = Element, Metrics = element status fields + selected
  topic payloads (blueprint-selected, wildcard patterns supported).
- Optional second phase: OPC UA client element (read/write tag groups, same
  manager shape as `AbCipPlcManager`) for shops that prefer OPC to MQTT.

**Bonus:** Sparkplug compliance is what "Unified Namespace (UNS)" architects ask
for by name. This one element makes Fusion a UNS citizen.

**Depends on:** existing MQTT element. **Size:** M (protocol layer + mapping).

---

## Theme 2 — Reliability Story (Fusion Link evolution)

### 2.1 Competing-Consumer Load Balancing over Fusion Link
**Why:** Today Link is pub/sub fan-out — every subscriber gets every message.
Distributing *work* (print jobs, host transactions, waves) across instances
needs exactly-one delivery.

**How:**
- New frame kinds: `SubscribeQueue`, `Ack`, `Redeliver` in `LinkFrame`.
- The Link Server round-robins each matching envelope to exactly one queue
  subscriber; unacked messages redeliver after a timeout.
- Health-weighted routing: the server already sees `ElementStatus` traffic —
  prefer peers with healthy/idle status, back off peers reporting Critical.

**Depends on:** Link suite (done). **Size:** M.

### 2.2 Hot-Standby Failover
**Why:** "Kill the primary mid-wave and nothing stops" is the demo operations
directors remember. Reliability sells to the people who sign.

**How:**
- Two instances linked bidirectionally; heartbeats already exist in the protocol.
- Standby holds warm subscriptions (received but not acted on); on link-loss
  past a grace window, standby activates its elements (reuse the existing
  SYS.CONTROL ONLINE/OFFLINE machinery).
- Requires the single-hop loop guard to become an origin-list (planned change
  documented in `Fusion.Element.Link/_documentation/ReadMe-Link.md`) so a
  fail-back does not echo.

**Depends on:** 2.1 ack machinery helps but is not required. **Size:** M–L.

---

## Theme 3 — The Demo Package (highest attention-per-effort)

### 3.1 Simulation-First Commissioning
**Why:** "Run the whole warehouse virtually, then point the same blueprint at
real hardware." Nobody in this space sells virtual commissioning well, and
Fusion is ~70% there (virtual PLC/printer/scanner, scripted scenarios,
`TestCounterpart` attribute already links real elements to their virtual twins).

**How:**
- A blueprint "simulation profile": one flag/overlay that swaps every element
  for its `TestCounterpart` without editing the element list.
- Scenario runner UI in FusionLab (the scripted TCP groups and PNA scenarios
  already exist — surface them, with pass/fail assertions).
- Publish timing/throughput metrics per scenario run for before/after tuning.

**Depends on:** existing simulation fleet. **Size:** M, mostly packaging.

### 3.2 Live Flow Visualization + Bus Replay
**Why:** Demos spectacularly; costs little. The `System.DataFlow` topic and the
`BusAuditLogger` already capture everything needed.

**How:**
- FusionLab view that animates conveyables moving element-to-element in real
  time from `FlowEvent`s (source → force → destination is already published).
- "Replay the last N minutes" — re-dispatch from the audit log at wall-clock or
  accelerated speed onto a replay-namespaced topic for incident forensics.
- Export a replay as a shareable scenario file (feeds 3.1).

**Depends on:** DataFlow events (done), audit log (done). **Size:** S–M.

### 3.3 Tokamak Ops Copilot
**Why:** The feature non-technical stakeholders repeat to each other. In-context
AI over live operational data is the 2026 differentiator.

**How:**
- Wire the existing `AiElement` to subscribe to `All_Elements.StatusMessage`,
  `All_Elements.Exceptions`, and `System.DataFlow`; maintain a rolling
  operational context window.
- Q&A grounded in live bus data: "why is lane 4 backing up?", "what changed in
  the last 10 minutes?", "which printer is slowest today?"
- Action suggestions gated behind the existing SYS.CONTROL commands (restart
  element, take offline) — suggest, human confirms.

**Depends on:** AI element (exists), status pipeline (exists). **Size:** M.

---

## Theme 4 — Domain Model (build when a site drives it)

### 4.1 Material Handler Element (`Fusion.Element.MaterialHandling`)
**Why:** Formalizes the conveyable lifecycle — induct → track → divert →
confirm — with zone occupancy and accumulation state, sitting above whichever
PLC element drives the hardware. Reactions bond to `MH1.Divert.DP4` instead of
raw PLC semantics.

**Caution:** the vocabulary already exists informally (GIN, decision points,
`LogConveyableEvent`). Design the abstraction against a *real site's*
requirements or it will be wrong. Prototype against the Virtual PLC first.

**Depends on:** PLC suite (done). **Size:** L.

### 4.2 Pick & Put Strategy Reactions
**Why:** Batch / zone / wave / cluster picking and put-wall support. Table
stakes for WES; the differentiator is **A/B-testing strategies in simulation**
(3.1) before touching the floor.

**How:** a `PickStrategyReaction` with pluggable `IPickStrategy` classes;
hardware underneath is scanners + lights + PLC already supported. Ship with
2–3 strategies and the swap-in-simulation workflow as the headline.

**Depends on:** 4.1 for clean bonding; 3.1 for the differentiator. **Size:** L.

---

## Suggested Sequence

| Order | Item | Rationale |
| :--- | :--- | :--- |
| 1 | 3.2 Flow viz + replay | Smallest effort, biggest demo delta; exercises data already captured |
| 2 | 1.1 Sparkplug/Ignition | Credibility with evaluators; unlocks UNS conversations |
| 3 | 2.1 + 2.2 Link balancing → failover | The reliability demo; Link is fresh and the protocol is ours |
| 4 | 3.1 Simulation commissioning | Packaging of existing fleet; feeds sales and testing alike |
| 5 | 3.3 Tokamak copilot | Rides on everything above; most memorable to non-engineers |
| 6 | 4.1 → 4.2 Material handling + pick/put | Wait for a driving site; largest and easiest to get wrong early |

---

*Related docs: `ELEMENTS.md` (current element catalog), `TODO.md` (engineering
debt), `Fusion.Element.Link/_documentation/ReadMe-Link.md` (Link protocol and
the multi-hop change 2.2 requires).*
