# Fusion Console (Fusion Dashboard)

The primary entry point and interactive dashboard for the Fortna Fusion system. It provides real-time visibility into the health and activity of all Elements and Reactions.

## Interactive Controls

The console provides a real-time dashboard with several interactive commands. Most commands require the console to be **unlocked** before they will be accepted.

### Security
*   **Unlock Sequence**: Type `fortna` anywhere in the console to unlock.
*   **Auto-Lock**: The console will automatically re-lock itself after **30 seconds** of inactivity.
*   **Visual Status**: The top-right corner displays a red `locked` or green `unlocked` indicator.

### View Management
*   **Toggle Dashboard/Logs (L)**: Press `l` to switch between the graphical dashboard and a live scrolling log view. 
    *   Toggling back to the dashboard triggers a global status refresh for all components.
*   **Toggle Headers (H)**: Press `h` to show or hide the logical group headers.
*   **Pause/Resume (P / Space / Pause)**: Temporarily freeze the dashboard redraw loop.

### Logging Control
*   **Level 1**: Set global log level to `information`.
*   **Level 2**: Set global log level to `debug`.
*   **Level 3**: Set global log level to `verbose` (full trace).

### Specialized Commands
*   **Release Tote (T)**: Sends a `release_tote` command to the message bus. If a Virtual PLC is active, it will respond by injecting a new tote into the simulation.

## Dynamic Layout & Grouping
The dashboard automatically pre-initializes its layout upon startup by receiving a system topology message from the core. It uses a minimalist, strictly **lowercase** aesthetic.

### Logical Grouping
Elements and Reactions are grouped based on their relationships:
1.  **Bonded Groups**: Components linked via a bond are grouped under a header describing the connection: `type -> "comment" -> type`.
2.  **Functional Type**: Unbonded elements are grouped by their manager type (e.g., `activemq`, `nats`).
3.  **Reactions**: Orchestrators and business logic are grouped at the top.

### Color Coding
*   **White**: Reactions (high-level orchestration).
*   **Light Blue**: Primary Hardware (PLCs, Printers, Zebra, JetMark).
*   **Gray**: Infrastructure (Brokers, TCP Servers, Communication Clients).

## Diagnostic Server (Telnet)
The system includes a built-in diagnostic server (accessible via PuTTY/Telnet on Port 9999). It provides the same interactive dashboard and supports the same command set for remote troubleshooting:

*   **Security**: Must type `fortna` to unlock remote commands.
*   **Commands**: `1`, `2`, `3` (Logging), `T` (Tote Release), and `L` (Refresh).
*   **Additional Remote Commands**: `trace`, `pub`, `desc`, `restart`, `online`, `offline`. Type `help` in the telnet session for full details.

## Tokamak AI Blueprint Builder (`tools/ai`)
A standalone tool is provided to help build new **Blueprints** (`.fusion` files) and provision hardware onsite.
*   **Location**: `tools/ai/Tokamak.AI.exe`
*   **Usage**: 
    *   **Mode 1 (Online)**: Describe the system elements and Reactions you want, and the AI will generate a `.fusion` blueprint.
    *   **Mode 2 (Offline/Onsite)**: Scan a subnet to discover physical hardware and automatically update your blueprint with the correct IP addresses.
*   **Configuration**: Requires a Google Gemini API Key in `tools/ai/appsettings.json` for AI mode.
