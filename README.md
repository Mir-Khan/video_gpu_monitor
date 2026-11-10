# GPU Monitor

GPU Monitor is a small background program for Windows that records what the PC is doing when it freezes or when a monitor flickers or goes black, and then builds a report that ranks the most likely causes. It was written for two specific problems on this PC, namely short total system freezes (where audio on Discord or a Twitch stream repeats like a stuck record) and brief black flickers on the monitors when protected video plays, such as Amazon Prime Video in the browser or a music video in the Spotify desktop app.

It is a single `GpuMonitor.exe` built with the C# compiler that already ships with Windows, so nothing needs to be installed. It sits in the system tray, uses roughly 0.01% of the CPU, and keeps its memory use flat over long sessions.

## Building

Run `build.cmd` from this folder. It calls the .NET Framework 4.8 compiler at `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe` and writes `GpuMonitor.exe` next to the script. Keep `GpuMonitor.exe.config` in the same folder as the program, since it holds a setting that lowers memory use.

## Using it

1. Start `GpuMonitor.exe`. A small green icon appears in the system tray, and a notification confirms that it is running.
2. Use the PC normally, mainly gaming, streaming and watching video, since that is when the problems happen.
3. Freezes are detected automatically, so nothing needs to be done when one happens.
4. When a monitor flickers or goes black, press **Ctrl+Alt+F** right afterwards. This marks the moment, and the 60 seconds before it are saved. The tray menu also has a "Mark flicker now" item that does the same thing.
5. When there are a few incidents, open the tray menu and choose "Build and open report", or double-click the tray icon.

The icon turns orange for 30 minutes after an incident is recorded, and hovering over it shows how many incidents were recorded today.

"Start with Windows" in the tray menu adds the program to the current user's startup list; clicking it again removes it. This is the only setting the program changes on the PC.

### Command line

- `GpuMonitor.exe` runs the monitor in the tray.
- `GpuMonitor.exe check` writes a one-time health check to `reports\check-<date>.txt` and opens it in Notepad.
- `GpuMonitor.exe report` builds `reports\report-<date>.html` and opens it in the browser.
- Adding `--no-open` to `check` or `report` writes the file without opening it.

## What it records

| Signal | How it is measured | Why it matters |
|---|---|---|
| System stalls | A high priority thread wakes every 50 ms and measures how late it woke up; a delay over 250 ms is a freeze | This measures the freeze itself, including its exact length |
| GPU telemetry | Read once per second from `nvml.dll`: temperature, power and power limit, core and memory clocks, P-state, PCIe link and replay errors, NVENC sessions, fan, and clock limit reasons | Shows GPU power or voltage limits, memory clock switching and PCIe errors |
| CPU and driver latency | PDH counters once per second: total CPU, busiest core, effective clock speed, and DPC and interrupt time on the busiest core | Shows CPU throttling and drivers that hold the CPU too long |
| Event logs | Subscriptions to the System and Application logs for `nvlddmkm`, `Display`, WHEA hardware errors, TPM, Kernel-Power 41 and app crashes | Driver timeouts (TDR), hardware errors and crashes appear here |
| Displays | Windows messages for display mode changes and monitors connecting or disconnecting | A black screen often shows up as a mode change or a reconnect |
| Foreground app | The app and window title in front at the moment of an incident | Shows whether DRM video (Prime Video, Spotify) was playing |

## Files

- `incidents\incident-<time>-<type>.json` holds one incident: the 60 seconds of samples before it and 10 seconds after it, nearby events, the foreground app, the running apps that matter, and the monitor modes.
- `logs\samples-<date>.csv` holds one row per second for the whole day. If the PC freezes completely and has to be reset, the last rows in this file show the state right before it, and the report shows them in the "Unexpected restarts" section. These files are deleted after 7 days.
- `logs\events-<date>.csv` holds every event the monitor saw.
- `logs\errors.txt` only appears if something inside the monitor failed.
- `reports\` holds the generated reports and health checks.

## Reading the report

The report starts with a summary that counts the incidents and lists the most likely cause for each of them, followed by the health check, any unexpected restarts, and then one card per incident. Each card lists the possible causes, labelled "likely", "possible" or "weak", together with what to try, and below them small charts that cover 60 seconds before the incident and 10 seconds after it. The red line marks the moment of the incident, and the shaded area is the length of the freeze.

The rules behind the causes are mainly these:

- **AMD fTPM stutter**: the whole system stalls for 0.3 to 5 seconds while there is no GPU driver error, no DPC spike and no hardware error. On a Ryzen system with a BIOS from before mid-2022, this is the most likely cause of a freeze where audio loops, and the fix is a BIOS update.
- **GPU driver timeout (TDR)**: the NVIDIA or display driver logged an error near the incident. Common causes are an overclock or undervolt, a driver problem, or power delivery to the card.
- **Driver latency**: the busiest core's DPC or interrupt time rose well above its normal level from earlier in the same incident. LatencyMon can then find the specific driver.
- **GPU power or voltage limit**: the GPU was at its power limit or reported hardware slowdown just before the freeze.
- **GPU memory clock switching**: the memory clock jumped (for example from 405 MHz to over 9000 MHz) around a flicker, which can blank the screen with two high refresh rate monitors.
- **Protected video (HDCP) or multiplane overlay**: a browser on a video site, or Spotify, was in front when the flicker happened.
- **PCIe link errors**: the GPU had to resend data over the PCIe slot.

## Findings from the first health check

The first health check on this PC, on 2026-11-09, found the following:

- The BIOS is F33i from April 2021, which predates AGESA 1.2.0.7, and the TPM is the AMD firmware TPM. This combination has the known fTPM stutter bug, which matches the freezes with looping audio closely, so a BIOS update for the X570 AORUS ULTRA is the first thing to try.
- The NVIDIA driver logged 7 errors or timeout resets in the last 90 days, the most recent on 2026-10-12.
- MSI Afterburner and Wallpaper Engine are running; both are worth turning off once while testing the flicker.
- The busiest CPU core normally shows 15 to 25% interrupt or DPC time even at idle, which is on the high side and worth a run of LatencyMon.

The program only observes and reports. It does not change the BIOS, drivers, registry or Afterburner profiles, apart from the optional startup entry.
