# Virtual PLC Test Cases

This document outlines the testing scenarios and simulation behaviors implemented within the Virtual PLC of the Fortna Fusion system. The Virtual PLC is designed to simulate a physical Programmable Logic Controller (PLC) for local development and testing environments, allowing the Warehouse Control System (WCS) to be verified without requiring physical hardware.

## Overview of Virtual PLC Operations

The Virtual PLC (`VirtualPlcElement`) simulates the lifecycle of a tote moving along a conveyor system. It relies on a configured "decision chain" (Decision Points) to simulate physical movement and routing:

1.  **Induction (Phase 0):** A tote is introduced into the system at a configurable frequency (`InductionFreq`, typically set to **2000ms** for stable testing) and assigned a Goods Identification Number (GIN) and a barcode (from `BarcodeList` or auto-generated). The Virtual PLC sends a `DecisionRequestMessage` (DReqM) to the WCS.
2.  **Routing/Diverting (Phase 1):** The Virtual PLC waits for a defined travel time (`DistanceMs`). It expects a routing decision from the WCS (stored internally via `DecisionResponsePayload`). Based on the routing decision, it sends another `DecisionRequestMessage` for the specific target destination.
3.  **Completion (Phase 2+):** The tote reaches its final destination in the simulated chain, triggering a final `DecisionRequestMessage`.
4.  **Verification Handling:** If a decision point involves "Verify", the Virtual PLC simulates a verification scan by appending "123" to the effective barcode.

### Global Overrides
*   **GIN 360 Override:** At GIN 360, the system explicitly forces both printers to active (`"1"`) to verify restoration or specific routing logic.

## Automated Simulation Edge Cases (GIN-Based)

The Virtual PLC is designed to test various system edge cases based on the assigned GIN. These scenarios primarily apply to decision points associated with Print and Apply (PNA) operations. The behaviors validate the WCS's routing logic, failover mechanisms, and error handling.

| GIN Range | Simulation Behavior | Test Purpose |
| :--- | :--- | :--- |
| **1 - 99** | Normal operation. Both primary devices are active. | **Baseline:** Establish system stability and verify normal routing. |
| **100 - 324** | Cycles through the **60-GIN PNA cycle** (6 phases of 10 GINs each). | **Cyclic Testing:** Verify failover, recovery, and full failure scenarios in a continuous loop. |
| **325+** | **Synchronized HW Cycle:** Cycles Paper Out, Head Open, and Paused states between Printer 1 and 2 (reported via Zebra `~HS`). | **Coordinated Error:** Verify system handles synchronized hardware errors after the GIN cycle. |
| **300** | Simulated Scanner Error (Barcodes: `["????", "*****"]`). | **Scanner Error:** Test the WCS's handling of multiple unreadable barcodes and subsequent reject logic. |
| **360** | Both devices (PNA2_151/152) explicitly active. | **Override:** Explicit restoration test for GIN 360. |

*Note: The hardware status cycle triggered at GIN 325 is reported in response to Zebra **`~HS`** commands, and is synchronized between both printers.*
