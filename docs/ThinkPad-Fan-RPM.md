# ThinkPad fan RPM support — experimental

LiteMonitor's existing **CPU Fan** indicator can now use a ThinkPad-specific
fallback without running another monitoring application.

## Design and selection order

1. **TPFanCtrl2 is already running:** consume its read-only mapping
   \`Global\TPFanControl_State\`. The EC is never accessed by LiteMonitor in this mode.
   Fan 1 feeds \`CPU.Fan\`; fan 2 feeds \`CASE.Fan\` only if distinctly reported.
2. **No TPFanCtrl2 mapping:** on a positively identified Lenovo ThinkPad, try
   the PawnIO signed \`LpcACPIEC.bin\` module to read the first tachometer at
   EC offsets 0x84/0x85. This uses \`Global\Access_EC\` to coordinate with
   software implementing the same named mutex and runs in a background timer.
   The EC read command requires control-port communication at 0x66/0x62,
   but **never sends EC WRITE 0x81, fan-mode writes to 0x2F, or fan-selector
   writes to 0x31**. Dual-fan standalone reads intentionally remain unsupported.
3. **No usable ThinkPad RPM:** fall back to LibreHardwareMonitor's existing
   sensors. A valid **0 RPM** is not filtered out.

If the user has explicitly selected an LHM fan in Hardware Settings, that
selection takes precedence. Other vendors retain existing hardware detection.

## Test

Use the Windows x64 artifact from the
[ThinkPad fan workflow](https://github.com/onecat/LiteMonitor/actions/workflows/thinkpad-fan-build.yml)
and extract *all files* together. The package must contain
\`LiteMonitor.exe\` and \`LpcACPIEC.bin\` in the same directory. The already
supported PawnIO driver must be installed and accessible for independent EC
reads. Enable **CPU Fan** in LiteMonitor's display settings. If running
TPFanCtrl2 as a fan controller, keep it running; LiteMonitor will consume its
shared telemetry rather than accessing its EC directly.

The native EC backend is opt-in via showing fan telemetry, checks BIOS
manufacturer and product family, and samples at most every **3 seconds**.
It does not need a second resident process and does not issue fan control
commands. Binary helper is taken from a pinned TPFanCtrl2 upstream commit for
build packaging; it is not self-downloaded by the application at runtime.

Debug output beginning \`[ThinkPadFan]\` identifies which data source is in
use and why EC access failed (e.g. missing helper, driver unavailable, mutex
busy, stale TPFanCtrl2 IPC, inconsistent EC reads).

## Limitations and safety

- This has **not yet been validated on the user's specific ThinkPad**. Do not
  assume every ThinkPad implements the 0x84/0x85 RPM registers.
- If the TPFanCtrl2 mapping is present but stale or inaccessible, LiteMonitor
  **does not** switch to direct EC reads, avoiding a second EC owner.
- Standalone Fan 2 is omitted to avoid writing 0x31 just to choose a fan.
- Kernel drivers, other hardware utilities and Windows ACPI can still access
  the EC independently. The named mutex is cooperative only. Stop testing if
  read errors, thermal anomalies or conflicts occur.
- The PawnIO driver is **not** installed, updated or started by this feature.
  Normal LiteMonitor's existing driver setup applies.
- During CI only decoding and compilation run; actual RPM/CPU/thermal behavior
  requires a real device test.
