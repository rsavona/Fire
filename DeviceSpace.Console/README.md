# DeviceSpace Console

The primary entry point and interactive dashboard for the FortnaFire system.

## Interactive Controls

The console provides a real-time dashboard with several interactive commands. Most commands require the console to be **UNLOCKED** before they will be accepted.

### Security
*   **Unlock Sequence**: Type `fortna` anywhere in the console to unlock.
*   **Auto-Lock**: The console will automatically re-lock itself after **30 seconds** of inactivity.
*   **Visual Status**: The top-right corner of the dashboard displays a red `LOCKED` or green `UNLOCKED` indicator.

### View Management
*   **Toggle Dashboard/Logs (L)**: Press `L` to switch between the graphical dashboard and a live scrolling log view. 
    *   Toggling back to the dashboard forces a global status refresh for all devices.
*   **Pause/Resume (P / Space / Pause)**: Temporarily freeze the dashboard redraw loop.

### Logging Control
*   **Level 1**: Set global log level to `Information`.
*   **Level 2**: Set global log level to `Debug`.
*   **Level 3**: Set global log level to `Verbose` (Full Trace).

### Simulation Commands
*   **Release Tote (T)**: Sends a `RELEASE_TOTE` command to the message bus. If a Virtual PLC is active, it will respond by injecting a new tote into the simulation.

## Dynamic Layout
The dashboard automatically organizes devices by priority:
1.  **Workflows**: Business logic and orchestrators.
2.  **PLCs**: Hardware controllers.
3.  **Printers**: Zebra and JetMark devices.
4.  **Host Comm**: Communication layers.
5.  **Infrastructure**: Messaging and Bus components.

## Diagnostic Server (Telnet)
The system includes a built-in diagnostic server (accessible via PuTTY/Telnet on Port 9999). It provides the same interactive dashboard and supports the same command set for remote troubleshooting:

*   **Security**: Must type `fortna` to unlock remote commands.
*   **Commands**: `1`, `2`, `3` (Logging), `T` (Tote Release), and `L` (Refresh).
*   **Additional Remote Commands**: `TRACE`, `PUB`, `DESC`, `RESTART`, `ONLINE`, `OFFLINE`. Type `HELP` in the telnet session for full details.
