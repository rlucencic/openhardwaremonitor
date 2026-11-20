# Open Hardware Monitor for .NET 11 and Windows on ARM

A fork of [Open Hardware Monitor](https://github.com/openhardwaremonitor/openhardwaremonitor), whose last change was in 2020. This fork:

- runs on **.NET 11** (`net11.0-windows`), natively on x64 and ARM64
- reads **Windows on ARM64** laptops such as Snapdragon X, where the original can't load its x86 kernel driver or run its x86 CPUID code
- reads **NVMe drives and batteries** on any Windows PC, and **ACPI thermal zones** on any PC without a Super I/O chip (laptops, and always on ARM64)

## Windows on ARM64

Everything comes from documented Windows interfaces, with no kernel driver and no admin rights needed:

| Hardware | Readings | Source |
|---|---|---|
| CPU | load and clock per core, power per cluster (Snapdragon) | Processor Information and Energy Meter counters |
| Mainboard | ACPI thermal zones (TZ0, TZ1...), system and USB-C power (Snapdragon) | Thermal Zone Information and Energy Meter counters |
| GPU (e.g. Adreno) | load per engine type, shared memory, power (Snapdragon), and temperature and clock where the driver reports them | GPU Engine, GPU Adapter Memory and Energy Meter counters, DXGI for names, DXCore adapter state for temperature and clock |
| NPU (e.g. Hexagon) | load, shared memory, and temperature, clock and power where reported | the same counters as GPUs, DXCore to find NPUs |
| NVMe drive | temperatures, remaining life, available spare, data read and written | NVMe health log (`IOCTL_STORAGE_QUERY_PROPERTY`) |
| Battery | charge, wear, voltage, charge or discharge rate, temperature | battery IOCTLs |
| RAM | as before | |

Tested on an ASUS Zenbook A14 (Snapdragon X Elite X1E-78-100), both as a native ARM64 process and as x64 under emulation.

**The Adreno X1-85 GPU** on that laptop (driver 31.0.178.0) shows its load per engine type, shared memory, power from the energy meter's GPU channel (about 10.5 W under full load) and its clock through DXCore (300 MHz idle, 1250 MHz under load). Its driver doesn't report temperature through DXCore; the GPU's temperature is in the thermal zones instead (below). Microsoft's software adapters (Basic Render Driver) are left out, since their load is CPU load.

**The Hexagon NPU** on that laptop shows its load (from its compute engine) and shared memory. Its driver doesn't report temperature or clock through DXCore, and the laptop's energy meters have no NPU channel, so those don't show.

**Thermal zone names.** The firmware calls its zones TZ0, TZ1... without saying what they measure. On the Zenbook A14 (board UX3407RA), the CPU's and GPU's zones are labelled from measurements: one CPU cluster at a time was loaded, then the GPU, and the zones that warmed up were recorded. They show as, for example, "CPU Cluster #1 (TZ0)" or "GPU (TZ6)". Clusters 2 and 3 heat the same zones, so those share one label. TZ44, TZ98 and TZ99 always read the hottest of the CPU and GPU zones, so they show as "SoC Hotspot". Other models keep the plain names, since they may number their zones differently. Their table is in `Hardware/Mainboard/ThermalZones.cs`.

To add another model:
1. Make sure the CPU and GPU are idle, with no builds, videos or other heavy work running.
2. Load one cluster's cores at a time (the cores sharing an L2 cache), switching the load on and off a few times, and note which zones warm up.
3. Check each cluster's energy meter rose most while that cluster was loaded. If not, something else was running: measure again.
4. Load the GPU the same way (a web page drawing a heavy WebGL shader will do), and check the GPU's energy meter rose while the CPU clusters' didn't.
5. Add the board's SMBIOS product name and its zones to the table.

## Other changes

- **NVMe drives** used to show only "Used Space" (they don't answer ATA SMART). They keep their identifier and that sensor.
- **Batteries** have their own switch: Hardware > Batteries.
- **Removed: WMI publishing** (`root\OpenHardwareMonitor`). The API it used (`System.Management.Instrumentation`) doesn't exist in modern .NET. The remote web server and its `data.json` still work.
- The old Windows Forms menus (`MainMenu`, `MenuItem`, `ContextMenu`) became `MenuStrip`, `ToolStripMenuItem` and `ContextMenuStrip`.
- The Intel and AMD code is ported to .NET 11 but hasn't been re-tested on x86 hardware.

## Building

Needs the .NET 11 SDK. To run, the .NET 11 Desktop Runtime.

```bash
dotnet build OpenHardwareMonitor.sln -c Release
```

To publish for one architecture:

```bash
dotnet publish OpenHardwareMonitor.csproj -c Release -r win-arm64 --self-contained false -o publish/win-arm64
```

```bash
dotnet publish OpenHardwareMonitor.csproj -c Release -r win-x64 --self-contained false -o publish/win-x64
```

Keep the published files together: the app checks for its DLLs next to the executable, so a single-file publish won't start.

## Running

The app asks for administrator rights when it starts, as the original did for its x86 kernel driver. On Windows on ARM64 no reading needs them, only the remote web server, to listen on its port. To start it without the prompt, from PowerShell in the app's folder:

```bash
$env:__COMPAT_LAYER = 'RunAsInvoker'; .\OpenHardwareMonitor.exe
```

## License

Mozilla Public License 2.0, as the original (see `Licenses/`).
