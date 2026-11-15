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
using Microsoft.Win32.SafeHandles;

namespace OpenHardwareMonitor.Hardware.Battery {

  /// <summary>
  /// Batteries, read from the battery driver with the documented battery
  /// IOCTLs (as Windows' own battery meter does). No admin rights needed.
  /// </summary>
  internal class BatteryGroup : IGroup {

    private readonly List<Battery> hardware = new List<Battery>();

    public BatteryGroup(ISettings settings) {
      if (OperatingSystem.IsUnix)
        return;
      int index = 0;
      foreach (string path in BatteryNative.GetBatteryDevicePaths()) {
        Battery battery = Battery.Create(path, index, settings);
        if (battery != null) {
          hardware.Add(battery);
          index++;
        }
      }
    }

    public IHardware[] Hardware {
      get { return hardware.ToArray(); }
    }

    public string GetReport() {
      return null;
    }

    public void Close() {
      foreach (Battery battery in hardware)
        battery.Close();
    }
  }

  internal class Battery : Hardware {

    private const uint BATTERY_POWER_ON_LINE = 0x00000001;
    private const uint BATTERY_DISCHARGING = 0x00000002;
    private const uint BATTERY_CHARGING = 0x00000004;
    private const uint BATTERY_CAPACITY_RELATIVE = 0x40000000;
    private const uint BATTERY_UNKNOWN = 0xFFFFFFFF;
    private const int BATTERY_UNKNOWN_RATE = unchecked((int)0x80000000);

    private readonly SafeFileHandle handle;
    private readonly uint tag;
    private readonly BatteryNative.BatteryInformation info;
    private readonly string manufacturer;
    private readonly string chemistry;

    private readonly Sensor chargeLevel;
    private readonly Sensor wearLevel;
    private readonly Sensor voltage;
    private readonly Sensor chargeRate;
    private readonly Sensor dischargeRate;
    private readonly Sensor temperature;

    private BatteryNative.BatteryStatus status;

    public static Battery Create(string path, int index, ISettings settings) {
      SafeFileHandle handle = BatteryNative.Open(path);
      if (handle == null)
        return null;
      uint tag;
      BatteryNative.BatteryInformation info;
      if (!BatteryNative.QueryTag(handle, out tag) ||
        !BatteryNative.QueryInformation(handle, tag, out info))
      {
        handle.Dispose();
        return null;
      }
      string name = BatteryNative.QueryString(handle, tag,
        BatteryNative.BatteryDeviceName);
      if (string.IsNullOrWhiteSpace(name))
        name = "Battery";
      return new Battery(name, index, handle, tag, info, settings);
    }

    private Battery(string name, int index, SafeFileHandle handle, uint tag,
      BatteryNative.BatteryInformation info, ISettings settings)
      : base(name, new Identifier("battery",
        index.ToString(CultureInfo.InvariantCulture)), settings)
    {
      this.handle = handle;
      this.tag = tag;
      this.info = info;
      this.manufacturer = BatteryNative.QueryString(handle, tag,
        BatteryNative.BatteryManufactureName);
      this.chemistry = Encoding.ASCII.GetString(info.Chemistry).TrimEnd('\0');

      chargeLevel = new Sensor("Charge Level", 0, SensorType.Level, this,
        settings);
      wearLevel = new Sensor("Wear Level", 1, SensorType.Level, this,
        settings);
      voltage = new Sensor("Voltage", 0, SensorType.Voltage, this, settings);
      chargeRate = new Sensor("Charge Rate", 0, SensorType.Power, this,
        settings);
      dischargeRate = new Sensor("Discharge Rate", 1, SensorType.Power, this,
        settings);
      temperature = new Sensor("Temperature", 0, SensorType.Temperature, this,
        settings);

      ActivateSensor(chargeLevel);
      if (IsAbsolute && info.DesignedCapacity > 0 &&
        info.FullChargedCapacity > 0)
      {
        wearLevel.Value = Math.Max(0, 100f - 100f * info.FullChargedCapacity /
          info.DesignedCapacity);
        ActivateSensor(wearLevel);
      }

      Update();
    }

    private bool IsAbsolute {
      get { return (info.Capabilities & BATTERY_CAPACITY_RELATIVE) == 0; }
    }

    public override HardwareType HardwareType {
      get { return HardwareType.Battery; }
    }

    public override void Update() {
      if (!BatteryNative.QueryStatus(handle, tag, out status)) {
        chargeLevel.Value = null;
        return;
      }

      if (status.Capacity != BATTERY_UNKNOWN) {
        if (!IsAbsolute)
          chargeLevel.Value = status.Capacity; // already a percentage
        else if (info.FullChargedCapacity > 0)
          chargeLevel.Value = Math.Min(100f,
            100f * status.Capacity / info.FullChargedCapacity);
      } else {
        chargeLevel.Value = null;
      }

      if (status.Voltage != BATTERY_UNKNOWN && status.Voltage > 0) {
        voltage.Value = status.Voltage / 1000f;
        ActivateSensor(voltage);
      }

      // milliwatts, negative while discharging; relative batteries report
      // a rate without a unit, which isn't shown
      if (IsAbsolute && status.Rate != BATTERY_UNKNOWN_RATE) {
        if ((status.PowerState & BATTERY_CHARGING) != 0 && status.Rate > 0) {
          chargeRate.Value = status.Rate / 1000f;
          ActivateSensor(chargeRate);
          DeactivateSensor(dischargeRate);
        } else if ((status.PowerState & BATTERY_DISCHARGING) != 0) {
          dischargeRate.Value = Math.Abs(status.Rate) / 1000f;
          ActivateSensor(dischargeRate);
          DeactivateSensor(chargeRate);
        } else {
          DeactivateSensor(chargeRate);
          DeactivateSensor(dischargeRate);
        }
      }

      // few batteries report it; tenths of a kelvin
      uint deciKelvin;
      if (BatteryNative.QueryTemperature(handle, tag, out deciKelvin) &&
        deciKelvin > 0)
      {
        temperature.Value = deciKelvin / 10f - 273.15f;
        ActivateSensor(temperature);
      }
    }

    private static string PowerState(uint state) {
      List<string> parts = new List<string>();
      if ((state & BATTERY_POWER_ON_LINE) != 0) parts.Add("on AC power");
      if ((state & BATTERY_CHARGING) != 0) parts.Add("charging");
      if ((state & BATTERY_DISCHARGING) != 0) parts.Add("discharging");
      return parts.Count > 0 ? string.Join(", ", parts) : "idle";
    }

    public override string GetReport() {
      StringBuilder r = new StringBuilder();
      string unit = IsAbsolute ? " mWh" : " (relative)";
      r.AppendLine("Battery");
      r.AppendLine();
      r.AppendLine("Name: " + name);
      if (!string.IsNullOrEmpty(manufacturer))
        r.AppendLine("Manufacturer: " + manufacturer);
      r.AppendLine("Chemistry: " + chemistry);
      r.AppendLine("Designed Capacity: " +
        info.DesignedCapacity.ToString(CultureInfo.InvariantCulture) + unit);
      r.AppendLine("Full Charged Capacity: " +
        info.FullChargedCapacity.ToString(CultureInfo.InvariantCulture) + unit);
      if (status.Capacity != BATTERY_UNKNOWN)
        r.AppendLine("Remaining Capacity: " +
          status.Capacity.ToString(CultureInfo.InvariantCulture) + unit);
      r.AppendLine("Cycle Count: " +
        info.CycleCount.ToString(CultureInfo.InvariantCulture));
      r.AppendLine("Power State: " + PowerState(status.PowerState));
      r.AppendLine();
      return r.ToString();
    }

    public override void Close() {
      handle.Dispose();
      base.Close();
    }
  }

  internal static class BatteryNative {

    // battery.h
    private static readonly Guid GUID_DEVCLASS_BATTERY =
      new Guid("72631e54-78a4-11d0-bcf7-00aa00b7b32a");
    private const uint IOCTL_BATTERY_QUERY_TAG = 0x294040;
    private const uint IOCTL_BATTERY_QUERY_INFORMATION = 0x294044;
    private const uint IOCTL_BATTERY_QUERY_STATUS = 0x29404C;

    // BATTERY_QUERY_INFORMATION_LEVEL
    private const int BatteryInformationLevel = 0;
    private const int BatteryTemperatureLevel = 2;
    internal const int BatteryDeviceName = 4;
    internal const int BatteryManufactureName = 6;

    [StructLayout(LayoutKind.Sequential)]
    internal struct BatteryInformation {
      public uint Capabilities;
      public byte Technology;
      [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
      public byte[] Reserved;
      [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
      public byte[] Chemistry;
      public uint DesignedCapacity;
      public uint FullChargedCapacity;
      public uint DefaultAlert1;
      public uint DefaultAlert2;
      public uint CriticalBias;
      public uint CycleCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BatteryStatus {
      public uint PowerState;
      public uint Capacity;
      public uint Voltage;
      public int Rate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryQueryInformation {
      public uint BatteryTag;
      public int InformationLevel;
      public int AtRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryWaitStatus {
      public uint BatteryTag;
      public uint Timeout;
      public uint PowerState;
      public uint LowCapacity;
      public uint HighCapacity;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData {
      public int Size;
      public Guid InterfaceClassGuid;
      public int Flags;
      public IntPtr Reserved;
    }

    private const int DIGCF_PRESENT = 0x02;
    private const int DIGCF_DEVICEINTERFACE = 0x10;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ_WRITE = 3;
    private const uint OPEN_EXISTING = 3;

    public static List<string> GetBatteryDevicePaths() {
      List<string> paths = new List<string>();
      Guid guid = GUID_DEVCLASS_BATTERY;
      IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero,
        DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
      if (set == IntPtr.Zero || set == new IntPtr(-1))
        return paths;
      try {
        for (int i = 0; i < 16; i++) {
          SpDeviceInterfaceData data = new SpDeviceInterfaceData();
          data.Size = Marshal.SizeOf(typeof(SpDeviceInterfaceData));
          if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i,
            ref data))
            break;

          int required;
          SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0,
            out required, IntPtr.Zero);
          if (required <= 0)
            continue;
          IntPtr detail = Marshal.AllocHGlobal(required);
          try {
            // SP_DEVICE_INTERFACE_DETAIL_DATA_W: a DWORD size, then the path;
            // cbSize is the fixed part, 8 on 64-bit and 6 on 32-bit
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            if (SetupDiGetDeviceInterfaceDetail(set, ref data, detail,
              required, out required, IntPtr.Zero))
              paths.Add(Marshal.PtrToStringUni(detail + 4));
          } finally {
            Marshal.FreeHGlobal(detail);
          }
        }
      } finally {
        SetupDiDestroyDeviceInfoList(set);
      }
      return paths;
    }

    public static SafeFileHandle Open(string path) {
      SafeFileHandle handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE,
        FILE_SHARE_READ_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
      if (handle.IsInvalid) {
        handle.Dispose();
        return null;
      }
      return handle;
    }

    public static bool QueryTag(SafeFileHandle handle, out uint tag) {
      uint wait = 0;
      int returned;
      tag = 0;
      return DeviceIoControl(handle, IOCTL_BATTERY_QUERY_TAG, ref wait, 4,
        out tag, 4, out returned, IntPtr.Zero) && tag != 0;
    }

    private static bool Query(SafeFileHandle handle, uint tag, int level,
      IntPtr output, int outputSize, out int returned)
    {
      BatteryQueryInformation query = new BatteryQueryInformation {
        BatteryTag = tag, InformationLevel = level
      };
      return DeviceIoControl(handle, IOCTL_BATTERY_QUERY_INFORMATION,
        ref query, Marshal.SizeOf(typeof(BatteryQueryInformation)), output,
        outputSize, out returned, IntPtr.Zero);
    }

    public static bool QueryInformation(SafeFileHandle handle, uint tag,
      out BatteryInformation info)
    {
      info = new BatteryInformation();
      int size = Marshal.SizeOf(typeof(BatteryInformation));
      IntPtr buffer = Marshal.AllocHGlobal(size);
      try {
        int returned;
        if (!Query(handle, tag, BatteryInformationLevel, buffer, size,
          out returned))
          return false;
        info = Marshal.PtrToStructure<BatteryInformation>(buffer);
        return true;
      } finally {
        Marshal.FreeHGlobal(buffer);
      }
    }

    public static string QueryString(SafeFileHandle handle, uint tag,
      int level)
    {
      const int size = 512;
      IntPtr buffer = Marshal.AllocHGlobal(size);
      try {
        int returned;
        if (!Query(handle, tag, level, buffer, size, out returned) ||
          returned <= 0)
          return null;
        return Marshal.PtrToStringUni(buffer, returned / 2).TrimEnd('\0');
      } finally {
        Marshal.FreeHGlobal(buffer);
      }
    }

    public static bool QueryTemperature(SafeFileHandle handle, uint tag,
      out uint deciKelvin)
    {
      deciKelvin = 0;
      IntPtr buffer = Marshal.AllocHGlobal(4);
      try {
        int returned;
        if (!Query(handle, tag, BatteryTemperatureLevel, buffer, 4,
          out returned) || returned < 4)
          return false;
        deciKelvin = (uint)Marshal.ReadInt32(buffer);
        return true;
      } finally {
        Marshal.FreeHGlobal(buffer);
      }
    }

    public static bool QueryStatus(SafeFileHandle handle, uint tag,
      out BatteryStatus status)
    {
      BatteryWaitStatus wait = new BatteryWaitStatus { BatteryTag = tag };
      int returned;
      return DeviceIoControl(handle, IOCTL_BATTERY_QUERY_STATUS, ref wait,
        Marshal.SizeOf(typeof(BatteryWaitStatus)), out status,
        Marshal.SizeOf(typeof(BatteryStatus)), out returned, IntPtr.Zero);
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid,
      IntPtr enumerator, IntPtr parent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set,
      IntPtr deviceInfo, ref Guid interfaceClassGuid, int memberIndex,
      ref SpDeviceInterfaceData data);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode,
      EntryPoint = "SetupDiGetDeviceInterfaceDetailW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set,
      ref SpDeviceInterfaceData data, IntPtr detail, int detailSize,
      out int requiredSize, IntPtr deviceInfo);

    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string fileName,
      uint access, uint share, IntPtr security, uint creation, uint flags,
      IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle,
      uint code, ref uint input, int inputSize, out uint output,
      int outputSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle,
      uint code, ref BatteryQueryInformation input, int inputSize,
      IntPtr output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle,
      uint code, ref BatteryWaitStatus input, int inputSize,
      out BatteryStatus output, int outputSize, out int returned,
      IntPtr overlapped);
  }
}
