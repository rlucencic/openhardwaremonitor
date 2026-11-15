/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenHardwareMonitor.Hardware.Gpu {

  /// <summary>
  /// GPUs without a vendor library, such as Qualcomm's Adreno on Windows on
  /// ARM64, and NPUs such as the Hexagon, read through what Windows itself
  /// reports: engine load from the GPU Engine counters, memory from the GPU
  /// Adapter Memory counters, temperature and clock from DXCore where the
  /// driver reports them, and on Snapdragon power from the energy meters.
  /// </summary>
  internal class WindowsGpuGroup : IGroup {

    // Software adapters (Microsoft Basic Render Driver) run on the CPU; their
    // "GPU" load is CPU load. Tests switch this on to exercise the code on a
    // machine whose GPU driver isn't loaded.
    internal static bool IncludeSoftwareAdapters = false;

    private readonly List<WindowsGpu> hardware = new List<WindowsGpu>();
    private readonly StringBuilder report = new StringBuilder();

    public WindowsGpuGroup(ISettings settings) {
      report.AppendLine("Windows GPUs (DXGI)");
      report.AppendLine();

      List<Dxgi.Adapter> adapters = Dxgi.GetAdapters();
      foreach (Dxgi.Adapter adapter in adapters)
        report.AppendLine(string.Format(CultureInfo.InvariantCulture,
          " {0} vendor 0x{1:X4} device 0x{2:X4} luid {3} {4}",
          adapter.Description, adapter.VendorId, adapter.DeviceId,
          adapter.LuidString, adapter.IsSoftware ? "(software)" : ""));
      report.AppendLine();

      if (!PdhQuery.CounterExists(WindowsGpu.EnginePath))
        return;

      int index = 0;
      HashSet<string> seen = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase);
      foreach (Dxgi.Adapter adapter in adapters) {
        if (adapter.IsSoftware && !IncludeSoftwareAdapters)
          continue;
        // NVIDIA and AMD have their own, richer readings
        if (adapter.VendorId == 0x10DE || adapter.VendorId == 0x1002)
          continue;
        // DXGI lists an adapter once per output; keep one
        if (!seen.Add(adapter.LuidString))
          continue;
        hardware.Add(new WindowsGpu(adapter, index++, false, settings));
      }

      // NPUs are compute-only adapters, which DXGI doesn't list; DXCore does
      report.AppendLine("Windows NPUs (DXCore)");
      report.AppendLine();
      int npuIndex = 0;
      foreach (DxCoreAdapter npu in GetNpus()) {
        Dxgi.Adapter info = new Dxgi.Adapter {
          Description = npu.Description, VendorId = npu.VendorId,
          DeviceId = npu.DeviceId, LuidLow = npu.LuidLow,
          LuidHigh = npu.LuidHigh, DedicatedVideoMemory = npu.DedicatedMemory,
          Software = !npu.IsHardware
        };
        npu.Dispose();
        report.AppendLine(string.Format(CultureInfo.InvariantCulture,
          " {0} vendor 0x{1:X4} device 0x{2:X4} luid {3} {4}",
          info.Description, info.VendorId, info.DeviceId, info.LuidString,
          info.IsSoftware ? "(software)" : ""));
        if (info.IsSoftware && !IncludeSoftwareAdapters)
          continue;
        if (!seen.Add(info.LuidString))
          continue;
        hardware.Add(new WindowsGpu(info, npuIndex++, true, settings));
      }
      report.AppendLine();
    }

    private static List<DxCoreAdapter> GetNpus() {
      List<DxCoreAdapter> npus = DxCore.GetAdapters(DxCore.HardwareTypeNpu);
      if (npus.Count > 0)
        return npus;
      // Windows without the NPU hardware type: machine learning adapters
      // that can't do graphics
      foreach (DxCoreAdapter adapter in
        DxCore.GetAdapters(DxCore.AttributeGenericML))
      {
        if (adapter.IsAttributeSupported(DxCore.AttributeD3D12Graphics) ||
          adapter.IsAttributeSupported(DxCore.AttributeD3D11Graphics))
          adapter.Dispose();
        else
          npus.Add(adapter);
      }
      return npus;
    }

    public IHardware[] Hardware {
      get { return hardware.ToArray(); }
    }

    public string GetReport() {
      return report.ToString();
    }

    public void Close() {
      foreach (WindowsGpu gpu in hardware)
        gpu.Close();
    }
  }

  internal class WindowsGpu : Hardware {

    internal const string EnginePath =
      @"\GPU Engine(*)\Utilization Percentage";
    private const string SharedMemoryPath =
      @"\GPU Adapter Memory(*)\Shared Usage";
    private const string DedicatedMemoryPath =
      @"\GPU Adapter Memory(*)\Dedicated Usage";
    private const string PowerPath = @"\Energy Meter(*)\Power";

    // pid_1234_luid_0x00000000_0x0000ED51_phys_0_eng_3_engtype_VideoDecode
    private static readonly Regex EngineInstance = new Regex(
      @"luid_(0x[0-9a-f]+_0x[0-9a-f]+)_phys_(\d+)_eng_(\d+)_engtype_(.*)$",
      RegexOptions.IgnoreCase);

    // engine type (as Windows names it) -> sensor
    private static readonly string[][] Engines = {
      new[] { "3D", "GPU Core" },
      new[] { "Compute", "GPU Compute" },
      new[] { "VideoDecode", "GPU Video Decode" },
      new[] { "VideoEncode", "GPU Video Encode" },
      new[] { "Copy", "GPU Copy" }
    };

    private readonly Dxgi.Adapter adapter;
    private readonly bool npu;
    private readonly string prefix; // "GPU" or "NPU"
    private readonly PdhQuery query;
    private readonly PdhCounter engineCounter;
    private readonly PdhCounter sharedMemoryCounter;
    private readonly PdhCounter dedicatedMemoryCounter;
    private readonly PdhCounter powerCounter;
    private DxCoreAdapter dxcore;
    private uint coreEngine; // the engine whose clock shows: a GPU's 3D engine

    private readonly Sensor[] engineLoads;
    private readonly Sensor sharedMemory;
    private readonly Sensor dedicatedMemory;
    private readonly Sensor power;
    private readonly Sensor temperature;
    private readonly Sensor clock;

    public WindowsGpu(Dxgi.Adapter adapter, int index, bool npu,
      ISettings settings)
      : base(adapter.Description, new Identifier(npu ? "npu" : "gpu",
        index.ToString(CultureInfo.InvariantCulture)), settings)
    {
      this.adapter = adapter;
      this.npu = npu;
      this.prefix = npu ? "NPU" : "GPU";

      query = new PdhQuery();
      engineCounter = query.AddCounter(EnginePath);
      sharedMemoryCounter = query.AddCounter(SharedMemoryPath);
      dedicatedMemoryCounter = query.AddCounter(DedicatedMemoryPath);
      // the Snapdragon energy meter has one GPU channel, so it can only be
      // told apart when there is a single GPU (and likewise NPU)
      if (index == 0)
        powerCounter = query.AddCounter(PowerPath);
      query.Collect();

      // temperature and engine clocks, where Windows and the driver have them
      try {
        dxcore = DxCore.GetAdapterByLuid(adapter.LuidLow, adapter.LuidHigh);
      } catch (Exception) {
        dxcore = null;
      }

      if (npu) {
        // an NPU's engines aren't of the GPU types: show the busiest
        engineLoads = new[] {
          new Sensor("NPU", 0, SensorType.Load, this, settings) };
      } else {
        engineLoads = new Sensor[Engines.Length];
        for (int i = 0; i < Engines.Length; i++)
          engineLoads[i] = new Sensor(Engines[i][1], i, SensorType.Load, this,
            settings);
      }

      sharedMemory = new Sensor(prefix + " Shared Memory Used", 0,
        SensorType.SmallData, this, settings);
      dedicatedMemory = new Sensor(prefix + " Dedicated Memory Used", 1,
        SensorType.SmallData, this, settings);
      power = new Sensor(prefix + " Power", 0, SensorType.Power, this,
        settings);
      temperature = new Sensor(npu ? "NPU" : "GPU Core", 0,
        SensorType.Temperature, this, settings);
      clock = new Sensor(npu ? "NPU" : "GPU Core", 0, SensorType.Clock, this,
        settings);
    }

    public override HardwareType HardwareType {
      get { return npu ? HardwareType.Npu : HardwareType.Gpu; }
    }

    private bool IsPowerChannel(string channel) {
      if (npu)
        return channel.IndexOf("npu", StringComparison.OrdinalIgnoreCase) >= 0
          || channel.IndexOf("nsp", StringComparison.OrdinalIgnoreCase) >= 0;
      return string.Equals(channel, "gpu", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsThisAdapter(string luid) {
      return string.Equals(luid, adapter.LuidString,
        StringComparison.OrdinalIgnoreCase);
    }

    public override void Update() {
      if (!query.Collect())
        return;

      if (engineCounter != null) {
        // Windows reports each engine once per process using it: add up the
        // processes per engine, then show the busiest engine of each type
        Dictionary<string, double> perEngine =
          new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> engineType =
          new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, double> v in engineCounter.GetValues()) {
          Match m = EngineInstance.Match(v.Key);
          if (!m.Success || !IsThisAdapter(m.Groups[1].Value))
            continue;
          string engine = m.Groups[2].Value + "/" + m.Groups[3].Value;
          double sum;
          perEngine.TryGetValue(engine, out sum);
          perEngine[engine] = sum + v.Value;
          engineType[engine] = m.Groups[4].Value;

          // the clock shown is the 3D engine's (the first one on the first
          // physical adapter); an NPU's is its first engine's
          uint engineIndex;
          if (!npu && m.Groups[2].Value == "0" && string.Equals(
            m.Groups[4].Value, "3D", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(m.Groups[3].Value, NumberStyles.None,
            CultureInfo.InvariantCulture, out engineIndex) &&
            (coreEngine == 0 || engineIndex < coreEngine))
            coreEngine = engineIndex;
        }

        for (int i = 0; i < engineLoads.Length; i++) {
          double max = -1;
          foreach (KeyValuePair<string, double> e in perEngine) {
            if (npu || string.Equals(engineType[e.Key], Engines[i][0],
              StringComparison.OrdinalIgnoreCase))
              max = Math.Max(max, e.Value);
          }
          if (max >= 0) {
            engineLoads[i].Value = (float)Math.Min(100, max);
            // an engine type shows once some process has used it
            ActivateSensor(engineLoads[i]);
          } else {
            engineLoads[i].Value = active.Contains(engineLoads[i]) ?
              0 : (float?)null;
          }
        }
      }

      UpdateMemory(sharedMemoryCounter, sharedMemory);
      UpdateMemory(dedicatedMemoryCounter, dedicatedMemory);

      if (powerCounter != null) {
        foreach (KeyValuePair<string, double> v in powerCounter.GetValues()) {
          if (IsPowerChannel(v.Key)) {
            power.Value = (float)(v.Value / 1000); // milliwatts
            ActivateSensor(power);
            break;
          }
        }
      }

      if (dxcore != null) {
        try {
          float? celsius = dxcore.GetTemperature();
          if (celsius.HasValue) {
            temperature.Value = celsius;
            ActivateSensor(temperature);
          }
          float? megahertz = dxcore.GetEngineFrequency(coreEngine);
          if (megahertz.HasValue) {
            clock.Value = megahertz;
            ActivateSensor(clock);
          }
        } catch (Exception) {
          // a driver that misreports shouldn't stop the other readings
          dxcore.Dispose();
          dxcore = null;
        }
      }
    }

    private void UpdateMemory(PdhCounter counter, Sensor sensor) {
      if (counter == null)
        return;
      double bytes = 0;
      bool found = false;
      foreach (KeyValuePair<string, double> v in counter.GetValues()) {
        // luid_0x00000000_0x0000ED51_phys_0
        if (v.Key.IndexOf(adapter.LuidString,
          StringComparison.OrdinalIgnoreCase) >= 0)
        {
          bytes += v.Value;
          found = true;
        }
      }
      if (!found)
        return;
      sensor.Value = (float)(bytes / (1024 * 1024));
      // dedicated memory only means something on GPUs that have some
      if (sensor == sharedMemory || adapter.DedicatedVideoMemory > 0)
        ActivateSensor(sensor);
    }

    public override string GetReport() {
      StringBuilder r = new StringBuilder();
      r.AppendLine(npu ? "Windows NPU" : "Windows GPU");
      r.AppendLine();
      r.AppendLine("Name: " + adapter.Description);
      r.AppendLine("DXCore: " + (dxcore == null ? "not available" :
        string.Format(CultureInfo.InvariantCulture,
        "{0} engines, temperature {1}, engine clocks {2}", dxcore.EngineCount,
        dxcore.SupportsTemperature ? "reported" : "not reported",
        dxcore.SupportsEngineFrequency ? "reported" : "not reported")));
      r.AppendLine(string.Format(CultureInfo.InvariantCulture,
        "Vendor: 0x{0:X4}, Device: 0x{1:X4}, LUID: {2}", adapter.VendorId,
        adapter.DeviceId, adapter.LuidString));
      r.AppendLine("Dedicated Video Memory: " + (adapter.DedicatedVideoMemory
        / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MB");
      r.AppendLine("Shared System Memory: " + (adapter.SharedSystemMemory
        / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MB");
      r.AppendLine();
      return r.ToString();
    }

    public override void Close() {
      query.Dispose();
      if (dxcore != null) {
        dxcore.Dispose();
        dxcore = null;
      }
      base.Close();
    }
  }

  /// <summary>
  /// Lists the graphics adapters through DXGI, which gives each one's name,
  /// vendor and LUID (the id the GPU counters use) and says which are
  /// software adapters.
  /// </summary>
  internal static class Dxgi {

    internal class Adapter {
      public string Description;
      public uint VendorId;
      public uint DeviceId;
      public uint LuidLow;
      public int LuidHigh;
      public ulong DedicatedVideoMemory;
      public ulong SharedSystemMemory;
      public bool Software;

      // Microsoft's own adapters (Basic Render, Basic Display, Hyper-V) draw
      // on the CPU or a host; DXGI doesn't always flag them as software,
      // for instance when they stand in for a GPU whose driver failed.
      public bool IsSoftware {
        get { return Software || VendorId == 0x1414; }
      }

      // as the GPU counters write it: luid_0x00000000_0x0000ED51
      public string LuidString {
        get {
          return string.Format(CultureInfo.InvariantCulture,
            "0x{0:X8}_0x{1:X8}", LuidHigh, LuidLow);
        }
      }
    }

    private static readonly Guid IID_IDXGIFactory1 =
      new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
    private const int DXGI_ADAPTER_FLAG_SOFTWARE = 2;

    // vtable slots: IUnknown (0-2), IDXGIObject (3-6), IDXGIFactory (7-11),
    // IDXGIFactory1.EnumAdapters1 (12); IDXGIAdapter (7-9),
    // IDXGIAdapter1.GetDesc1 (10)
    private const int RELEASE = 2;
    private const int ENUM_ADAPTERS1 = 12;
    private const int GET_DESC1 = 10;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1Fn(IntPtr self, uint index,
      out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1Fn(IntPtr self, IntPtr desc);

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid,
      out IntPtr factory);

    private static T Method<T>(IntPtr obj, int slot) where T : Delegate {
      IntPtr vtable = Marshal.ReadIntPtr(obj);
      return Marshal.GetDelegateForFunctionPointer<T>(
        Marshal.ReadIntPtr(vtable, slot * IntPtr.Size));
    }

    public static List<Adapter> GetAdapters() {
      List<Adapter> list = new List<Adapter>();
      IntPtr factory;
      try {
        Guid iid = IID_IDXGIFactory1;
        if (CreateDXGIFactory1(ref iid, out factory) != 0 ||
          factory == IntPtr.Zero)
          return list;
      } catch (DllNotFoundException) {
        return list;
      } catch (EntryPointNotFoundException) {
        return list;
      }

      // DXGI_ADAPTER_DESC1: WCHAR[128], 4 UINTs, 3 SIZE_Ts, LUID, UINT flags
      int sizeT = IntPtr.Size;
      int memoryOffset = 256 + 16;
      int luidOffset = memoryOffset + 3 * sizeT;
      int flagsOffset = luidOffset + 8;
      IntPtr desc = Marshal.AllocHGlobal(flagsOffset + 8);
      try {
        EnumAdapters1Fn enumAdapters =
          Method<EnumAdapters1Fn>(factory, ENUM_ADAPTERS1);
        for (uint i = 0; ; i++) {
          IntPtr adapter;
          if (enumAdapters(factory, i, out adapter) != 0 ||
            adapter == IntPtr.Zero)
            break; // DXGI_ERROR_NOT_FOUND after the last one
          try {
            if (Method<GetDesc1Fn>(adapter, GET_DESC1)(adapter, desc) != 0)
              continue;
            list.Add(new Adapter {
              Description = Marshal.PtrToStringUni(desc, 128).TrimEnd('\0'),
              VendorId = (uint)Marshal.ReadInt32(desc, 256),
              DeviceId = (uint)Marshal.ReadInt32(desc, 260),
              DedicatedVideoMemory = ReadSizeT(desc, memoryOffset),
              SharedSystemMemory = ReadSizeT(desc, memoryOffset + 2 * sizeT),
              LuidLow = (uint)Marshal.ReadInt32(desc, luidOffset),
              LuidHigh = Marshal.ReadInt32(desc, luidOffset + 4),
              Software = (Marshal.ReadInt32(desc, flagsOffset) &
                DXGI_ADAPTER_FLAG_SOFTWARE) != 0
            });
          } finally {
            Method<ReleaseFn>(adapter, RELEASE)(adapter);
          }
        }
      } finally {
        Marshal.FreeHGlobal(desc);
        Method<ReleaseFn>(factory, RELEASE)(factory);
      }
      return list;
    }

    private static ulong ReadSizeT(IntPtr p, int offset) {
      return IntPtr.Size == 8 ? (ulong)Marshal.ReadInt64(p, offset) :
        (uint)Marshal.ReadInt32(p, offset);
    }
  }
}
