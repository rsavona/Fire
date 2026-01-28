🟢 Information (The "What Happened")
Purpose: High-level milestones for business logic and auditing.

Structure: Must contain an Object (the entity), Event (the action), and Subject (the identifier).

Example: InventoryAdjustment (Event) for SKU 100148 (Object) sent to Aurora (Subject).

Best Practice: Always use Structured Logging (passing objects, not just strings). This allows you to query your SQL event tables by SKU or DeviceID without parsing text.

Storage: Sent to permanent storage (SQL Table, Syslog, or long-term Audit logs).

🟡 Debug (The "Why It Happened")
Purpose: Contextual data that explains the decision-making process.

Analogy: Think of these as "Live Comments."

Example: Checking cycle count threshold: Current=5, Max=10. Proceeding with adjustment.

Best Practice: Use these to log the state of variables before a major if/else block or a switch expression.

Storage: Usually kept in daily rolling files; deleted after 7–30 days.

🔴 Trace (The "Where It Happened")
Purpose: Flow control and execution tracking.

Structure: Method entry/exit and step numbering.

Example: [1.0] Entering CalculateAdjustments, [1.1] Start Loop, [1.2] End Loop, [1.3] Exiting CalculateAdjustments.

Best Practice: In C#, use [CallerMemberName] to automatically inject the method name so you don't have to hardcode it.

Performance Warning: Trace logging is expensive. Use if (Log.IsEnabled(LogEventLevel.Verbose)) to ensure the string isn't even built unless the level is turned on.

Level	Guideline for GIN usage
* Information	Log major state changes (e.g., GIN 12345: Arrived at Scanner_01). These are your "Milestones."
* Debug	Log internal logic decisions using the GIN context (e.g., GIN 12345: Routing to Lane 3 based on weight).
* Trace	Log high-frequency timing data. Since all logs share the GIN, you can calculate the exact delta between 
* Trace: Entering Method and Trace: Exiting Method for a specific item
