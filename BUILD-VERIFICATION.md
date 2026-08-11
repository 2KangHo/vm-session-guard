# Build verification

- Version: 1.4.1
- Verification date: 2026-08-03
- Build host: macOS arm64
- SDK: .NET SDK 8.0.423 (official `dotnet-install.sh`)
- Target: `net8.0-windows`, `win-x64`, self-contained, single file

## Results

1. `dotnet format --verify-no-changes --no-restore`: **PASS**
2. `dotnet publish` with `TreatWarningsAsErrors=true`: **PASS**, 0 warnings/errors
3. Output type inspection using `file`: **PASS** — `PE32+ executable (console) x86-64, for MS Windows`
4. External NuGet dependencies: **none**; project uses only the .NET shared framework and Windows `kernel32.dll`/`user32.dll` APIs.
5. Static review:
   - CPU calculation uses deltas from two successful `GetSystemTimes` samples.
   - Threshold, duration, interval, fallback list, and log path are range/format validated.
   - `Fallbacks` can run `CpuFloor` together with `MousePixel` or `ScrollLock`; legacy single `Fallback` remains supported.
   - CPU floor can start immediately, uses normal-priority workers, and enforces a minimum worker duty corresponding to the configured target.
   - Mouse and keyboard pulses use spaced `SendInput` calls with no-coalesce mouse movement and keyboard scan codes.
   - The distributed configuration uses `MousePixel` to refresh input-based VM/VDI idle policies; `None` remains available for environments where synthetic input is not approved.
   - Scroll Lock fallback sends two complete key down/up cycles.
   - Mouse fallback sends +1 then -1 relative motion.
   - `SetThreadExecutionState` and optional fallback results are logged independently.
   - `CpuFloor` is opt-in, can activate immediately, runs at normal priority, and targets the configured threshold with feedback control and a minimum duty cycle.
   - `CpuFloor` is limited to a 1–25% target, at most 32 workers, and a maximum 50% duty cycle per worker.
   - CPU floor workers are background threads and receive a stop signal and bounded join during shutdown.
   - Shutdown is available through `Ctrl+C`, console close, or normal process termination.

## Runtime-test limitation

The build host is not Windows, so Windows API behavior, CPU floor accuracy, and organization-specific VDI/session policy integration were not executed here. Before broad deployment, run the EXE in an approved non-production Windows VM, confirm CPU use and logs, verify the intended session policy response, and complete the organization's security/code-signing review.
