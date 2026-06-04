# Fortna Fusion: Simulation Reference Guide

This document defines the automated edge cases and hardware states triggered during system simulation. These scenarios are strictly applied to all decision points containing **"PNA"** (Print and Apply).

---

## 1. GIN-Based Scenarios (PLC Reporting)

The PLC dynamically injects metadata and special payloads based on the current **GIN** (Goods Identification Number). This "simulated reality" is automatically synchronized with the Reaction logic.

| GIN Range | Primary Logic | Metadata State (PNA2_151/152) | Scenario / Purpose |
| :--- | :--- | :--- | :--- |
| **1 - 99** | **Baseline** | `"1"`, `"1"` | Normal operations. |
| **100 - 324** | **PNA Cycle** | *Dynamic 60-GIN Cycle* | Cycles through 6 printer availability states. |
| **325+** | **Baseline** | `"1"`, `"1"` | Baseline metadata (Status moves to ~HS). |
| **360** | **Override** | `"1"`, `"1"` | Explicit restoration test for GIN 360. |
| **300** | **Scanner Err**| `["????", "*****"]` | **No-Read** and **Side-by-Side** barcodes. |

### Synchronized Hardware Status Cycle (Triggered at GIN 325)
When the PLC induces **GIN 325**, a synchronized 60-second hardware status cycle begins for the PNA printers. This status is reported in response to Zebra **`~HS`** (Host Status) commands:

* **0-10s**: PNA2_151: `"out of paper"`, PNA2_152: `"ready"`
* **10-20s**: PNA2_151: `"ready"`, PNA2_152: `"out of paper"`
* **20-30s**: PNA2_151: `"head open"`, PNA2_152: `"ready"`
* **30-40s**: PNA2_151: `"ready"`, PNA2_152: `"head open"`
* **40-50s**: PNA2_151: `"paused"`, PNA2_152: `"ready"`
* **50-60s**: PNA2_151: `"ready"`, PNA2_152: `"paused"`
* **60s+**: Both return to `"ready"` for the remainder of the test.

---

### PNA 60-GIN Cycle Detail (GIN 100-324)
The PNA simulation cycles through 6 phases (10 GINs each) in the PLC metadata:
1. **100-109**: PNA2_151 Offline (`"0"`, `"1"`)
2. **110-119**: Both Active (`"1"`, `"1"`)
3. **120-129**: PNA2_152 Offline (`"1"`, `"0"`)
4. **130-139**: Both Active (`"1"`, `"1"`)
5. **140-149**: Both Offline (`"0"`, `"0"`)
6. **150-159**: Both Active (`"1"`, `"1"`)
*Note: The cycle restarts at phase 1 for GIN 160.*

---

## 2. Sorter Simulation Mode (Non-PNA)

If a decision point does **not** contain "PNA", the Virtual PLC operates in **Sorter Simulation Mode**.

*   **Divert Confirmation**: After receiving a routing decision, the Virtual PLC sends a Decision Update Message (DUM).
*   **Mis-Divert Probability**: There is a **5% random chance** that the PLC will simulate a mis-divert. In this case, the `ActionTaken` will be reported as `"REJECT"` with a `ReasonCode` of `99`, regardless of the WCS decision.

---

## 3. Special Barcode Handling (GIN 300)

At exactly **GIN 300**, the simulation bypasses standard LPN generation and provides a multi-barcode array to test reject logic:

*   **`????`**: Represents a **No-Read** (unreadable label).
*   **`*****`**: Represents a **Side-by-Side** (two labels detected simultaneously).

---

## 3. Time-Based Hardware Simulation (Virtual Printer)

Independent of GIN, the **Virtual Printer** element runs a deterministic error cycle whenever a client (the System) connects:

1.  **0 - 30s**: **Paper Out** (Status: Warning)
2.  **31 - 60s**: **Ready** (Status: Normal)
3.  **61 - 90s**: **Paused** (Status: Warning)
4.  **91 - 120s**: **Ready** (Status: Normal)
5.  **121 - 420s**: **Head Open** (Status: Warning)
6.  **421s+**: **Ready** (Indefinite)

---
*Note: GIN ranges above 325 (excluding 300 and 360) default to both printers active ("1", "1").*
