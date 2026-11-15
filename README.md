# Open Hardware Monitor for .NET 11 and Windows on ARM

A fork of [Open Hardware Monitor](https://github.com/openhardwaremonitor/openhardwaremonitor), whose last change was in 2020. This fork:

- runs on **.NET 11** (`net11.0-windows`), natively on x64 and ARM64
- reads **Windows on ARM64** laptops such as Snapdragon X, where the original can't load its x86 kernel driver or run its x86 CPUID code
- reads **NVMe drives, batteries and ACPI thermal zones** on any Windows PC

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

Not there yet:

- **CPU and GPU temperatures** aren't labelled: the firmware's thermal zones have no names saying what they measure, so they show under the mainboard as TZ0, TZ1...
- **GPU and NPU readings haven't been seen on working hardware**: the test laptop's Adreno and Hexagon drivers weren't loading. Whether their drivers report temperature and clock through DXCore is untested. Microsoft's software adapters (Basic Render Driver) are left out.

## Other changes

- **NVMe drives** used to show only "Used Space" (they don't answer ATA SMART). They keep their identifier and that sensor.
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

## License

Mozilla Public License 2.0, as the original (see `Licenses/`).
