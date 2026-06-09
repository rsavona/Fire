# Print and Apply Product Flow

## Overview

The Print and Apply flow starts when a carton is inducted onto the line and ends when the newly printed label is verified. The PLC controls the physical movement and scanner events. The Print and Apply service on the server makes routing and print decisions, exchanges label messages with ActiveMQ, and validates that the label applied to the carton matches the expected barcode.

## Main Flow

1. A carton is conveyed to the Print and Apply induct scanner.
2. The induct scanner reads the existing side label on the carton.
3. The PLC receives the side-label scan and sends a decision request to the Print and Apply service.
4. When the server receives the induct decision request, two actions occur:
   - A label request is sent to ActiveMQ.
   - A response is sent back to the PLC identifying which printer should apply the new top label.
5. The server waits for both of these events:
   - ActiveMQ returns a label data message.
   - The carton reaches the selected print station.
6. The ActiveMQ label data message should normally arrive before the carton reaches the printer. It contains:
   - The label string to print.
   - The new barcode expected to be printed on the top label.
7. The server stores the label string and expected barcode in cache memory for that carton.
8. As the carton approaches the selected printer, the PLC sends a decision request for that print station.
9. The server does not need to send a response to the PLC for the print-station decision request. This request is used as the trigger for the server to send the cached label data to the selected printer.
10. After the label is printed and applied, the carton reaches the verification scanner.
11. The verification scanner reads the new top label.
12. The PLC sends the verification scan information to the server.
13. The server compares:
   - The expected barcode stored from the ActiveMQ label data message.
   - The barcode read by the verification scanner.

## Successful Verification

If the expected barcode and scanned barcode match:

1. The server responds to the PLC with a pass result.
2. The PLC diverts the carton toward shipping.
3. The server sends a message to ActiveMQ marking the carton as shipped.

## Failed Verification

If the expected barcode and scanned barcode do not match:

1. The server responds to the PLC with a fail result.
2. The PLC does not divert the carton toward shipping.
3. The carton continues around the loop toward exception handling.
4. The server sends a failure message to ActiveMQ.
5. The ActiveMQ failure message includes one of the configured failed conditions.

## Timing Notes

In real operation, cartons are not processed one at a time through the entire flow. Multiple cartons can be active between induction and verification at the same time. A realistic test should therefore allow several cartons to be in different stages of the Print and Apply flow simultaneously.

For the current scripted test model:

- Each conveyor line is treated as 12 feet long.
- Each carton is treated as 2 feet long.
- Conveyor speed is treated as 2 feet per second.
- A carton can complete one 12-foot segment in about 6 seconds.
- Realistic overlap testing should introduce cartons faster than the full cycle time so several cartons are active between induction, print, and verification.

## Test Validation

The scripted PLC test scenarios are defined in:

- `Fusion.Reaction.Simulation/TestCases/print-and-apply-real-world.json`

The expected results file used for log validation is:

- `Fusion.Reaction.Simulation/TestCases/print-and-apply-real-world-expected.csv`
