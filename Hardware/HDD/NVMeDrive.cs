/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenHardwareMonitor.Hardware.HDD {

  /// <summary>
  /// An NVMe drive, read through Windows' storage driver: the NVMe health
  /// log (SMART / Health Information, log page 02h) gives temperatures, wear
  /// and data written. NVMe drives don't answer the ATA SMART commands the
  /// other drives here use.
  /// </summary>
  internal class NVMeDrive : Hardware {

    private const int UPDATE_DIVIDER = 5; // the health log every 5 s

    private readonly int index;
    private readonly string firmwareRevision;
    private readonly DriveInfo[] driveInfos;
    private int count;

    private readonly Sensor temperature;
    private readonly Sensor[] temperatures;
    private readonly Sensor usedSpace;
    private readonly Sensor remainingLife;
    private readonly Sensor availableSpare;
    private readonly Sensor dataRead;
    private readonly Sensor dataWritten;

    private NVMeNative.HealthLog health;

    public static NVMeDrive Create(int driveIndex, ISmart smart,
      ISettings settings)
    {
      using (SafeFileHandle handle = NVMeNative.OpenDrive(driveIndex)) {
        if (handle == null)
          return null;
        string model, firmware;
        if (!NVMeNative.IsNVMe(handle, out model, out firmware))
          return null;
        NVMeNative.HealthLog log;
        if (!NVMeNative.ReadHealthLog(handle, out log))
          return null;
        return new NVMeDrive(driveIndex, model, firmware,
          smart.GetLogicalDrives(driveIndex), settings);
      }
    }

    private NVMeDrive(int index, string model, string firmwareRevision,
      string[] logicalDrives, ISettings settings)
      // same identifier as the drive had as a generic hard disk
      : base(string.IsNullOrEmpty(model) ? "NVMe Drive" : model,
        new Identifier("hdd", index.ToString(CultureInfo.InvariantCulture)),
        settings)
    {
      this.index = index;
      this.firmwareRevision = firmwareRevision;

      List<DriveInfo> infos = new List<DriveInfo>();
      foreach (string drive in logicalDrives) {
        try {
          DriveInfo di = new DriveInfo(drive);
          if (di.TotalSize > 0)
            infos.Add(di);
        } catch (ArgumentException) {
        } catch (IOException) {
        } catch (UnauthorizedAccessException) {
        }
      }
      driveInfos = infos.ToArray();

      temperature = new Sensor("Temperature", 0, SensorType.Temperature, this,
        settings);
      temperatures = new Sensor[8];
      for (int i = 0; i < temperatures.Length; i++)
        temperatures[i] = new Sensor("Temperature #" + (i + 1), i + 1,
          SensorType.Temperature, this, settings);
      // "Used Space" keeps the index it had as a generic hard disk
      usedSpace = new Sensor("Used Space", 0, SensorType.Load, this, settings);
      remainingLife = new Sensor("Remaining Life", 0, SensorType.Level, this,
        settings);
      availableSpare = new Sensor("Available Spare", 1, SensorType.Level, this,
        settings);
      dataRead = new Sensor("Data Read", 0, SensorType.Data, this, settings);
      dataWritten = new Sensor("Data Written", 1, SensorType.Data, this,
        settings);

      if (driveInfos.Length > 0)
        ActivateSensor(usedSpace);
      ActivateSensor(remainingLife);
      ActivateSensor(availableSpare);
      ActivateSensor(dataRead);
      ActivateSensor(dataWritten);

      Update();
    }

    public override HardwareType HardwareType {
      get { return HardwareType.HDD; }
    }

    public override void Update() {
      if (count == 0) {
        using (SafeFileHandle handle = NVMeNative.OpenDrive(index)) {
          if (handle != null && NVMeNative.ReadHealthLog(handle, out health))
            UpdateHealthSensors();
        }
        UpdateUsedSpace();
      }
      count = (count + 1) % UPDATE_DIVIDER;
    }

    private void UpdateHealthSensors() {
      if (health.CompositeTemperature > 0) {
        temperature.Value = health.CompositeTemperature - 273.15f;
        ActivateSensor(temperature);
      }
      for (int i = 0; i < temperatures.Length; i++) {
        ushort kelvin = health.TemperatureSensors[i];
        // a drive with a single sensor usually repeats the composite in
        // sensor 1, which then isn't shown twice
        if (kelvin == 0 || (i == 0 && kelvin == health.CompositeTemperature &&
          health.TemperatureSensors[1] == 0))
          continue;
        temperatures[i].Value = kelvin - 273.15f;
        ActivateSensor(temperatures[i]);
      }

      // percentage used is an estimate that may pass 100
      remainingLife.Value = Math.Max(0, 100 - health.PercentageUsed);
      availableSpare.Value = health.AvailableSpare;

      // data units are thousands of 512-byte blocks; Data sensors are GB
      // (2^30 bytes)
      dataRead.Value = (float)(health.DataUnitsRead * 512000.0 / (1 << 30));
      dataWritten.Value = (float)(health.DataUnitsWritten * 512000.0 /
        (1 << 30));
    }

    private void UpdateUsedSpace() {
      if (driveInfos.Length == 0)
        return;
      long totalSize = 0;
      long totalFreeSpace = 0;
      foreach (DriveInfo di in driveInfos) {
        if (!di.IsReady)
          continue;
        try {
          totalSize += di.TotalSize;
          totalFreeSpace += di.TotalFreeSpace;
        } catch (IOException) {
        } catch (UnauthorizedAccessException) {
        }
      }
      usedSpace.Value = totalSize > 0 ?
        100.0f - (100.0f * totalFreeSpace) / totalSize : (float?)null;
    }

    public override string GetReport() {
      StringBuilder r = new StringBuilder();
      r.AppendLine("NVMeDrive");
      r.AppendLine();
      r.AppendLine("Drive name: " + name);
      r.AppendLine("Firmware version: " + firmwareRevision);
      r.AppendLine();
      r.AppendLine("Health Information (log page 02h)");
      r.AppendLine(" Critical Warning: 0x" +
        health.CriticalWarning.ToString("X2", CultureInfo.InvariantCulture));
      r.AppendLine(" Composite Temperature: " +
        (health.CompositeTemperature - 273).ToString(
        CultureInfo.InvariantCulture) + " C");
      r.AppendLine(" Available Spare: " + health.AvailableSpare + " %");
      r.AppendLine(" Available Spare Threshold: " +
        health.AvailableSpareThreshold + " %");
      r.AppendLine(" Percentage Used: " + health.PercentageUsed + " %");
      r.AppendLine(" Data Units Read: " +
        health.DataUnitsRead.ToString(CultureInfo.InvariantCulture));
      r.AppendLine(" Data Units Written: " +
        health.DataUnitsWritten.ToString(CultureInfo.InvariantCulture));
      r.AppendLine(" Power Cycles: " +
        health.PowerCycles.ToString(CultureInfo.InvariantCulture));
      r.AppendLine(" Power On Hours: " +
        health.PowerOnHours.ToString(CultureInfo.InvariantCulture));
      r.AppendLine(" Unsafe Shutdowns: " +
        health.UnsafeShutdowns.ToString(CultureInfo.InvariantCulture));
      r.AppendLine(" Media Errors: " +
        health.MediaErrors.ToString(CultureInfo.InvariantCulture));
      r.AppendLine();
      foreach (DriveInfo di in driveInfos) {
        if (!di.IsReady)
          continue;
        try {
          r.AppendLine("Logical drive name: " + di.Name);
          r.AppendLine("Format: " + di.DriveFormat);
          r.AppendLine("Total size: " + di.TotalSize);
          r.AppendLine("Total free space: " + di.TotalFreeSpace);
          r.AppendLine();
        } catch (IOException) {
        } catch (UnauthorizedAccessException) {
        }
      }
      return r.ToString();
    }
  }

  internal static class NVMeNative {

    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400;
    private const int StorageDeviceProperty = 0;
    private const int StorageDeviceProtocolSpecificProperty = 50;
    private const int PropertyStandardQuery = 0;
    private const int ProtocolTypeNvme = 3;
    private const int NVMeDataTypeLogPage = 2;
    private const int NVME_LOG_PAGE_HEALTH_INFO = 2;
    private const int BusTypeNvme = 17;

    private const int QUERY_HEADER = 8;          // PropertyId, QueryType
    private const int PROTOCOL_DATA_SIZE = 40;   // STORAGE_PROTOCOL_SPECIFIC_DATA
    private const int HEALTH_LOG_SIZE = 512;

    internal struct HealthLog {
      public byte CriticalWarning;
      public ushort CompositeTemperature; // kelvin
      public byte AvailableSpare;
      public byte AvailableSpareThreshold;
      public byte PercentageUsed;
      public ulong DataUnitsRead;
      public ulong DataUnitsWritten;
      public ulong PowerCycles;
      public ulong PowerOnHours;
      public ulong UnsafeShutdowns;
      public ulong MediaErrors;
      public ushort[] TemperatureSensors; // kelvin, 0 when absent
    }

    /// <summary>
    /// Opens a drive for queries only (no read or write access), which
    /// needs no admin rights.
    /// </summary>
    public static SafeFileHandle OpenDrive(int index) {
      SafeFileHandle handle = CreateFile(@"\\.\PhysicalDrive" +
        index.ToString(CultureInfo.InvariantCulture), 0, 3, IntPtr.Zero, 3, 0,
        IntPtr.Zero);
      if (handle.IsInvalid) {
        handle.Dispose();
        return null;
      }
      return handle;
    }

    public static bool IsNVMe(SafeFileHandle handle, out string model,
      out string firmware)
    {
      model = firmware = null;
      byte[] query = new byte[12];
      BitConverter.GetBytes(StorageDeviceProperty).CopyTo(query, 0);
      BitConverter.GetBytes(PropertyStandardQuery).CopyTo(query, 4);
      byte[] buffer = new byte[1024];
      int returned;
      if (!DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, query,
        query.Length, buffer, buffer.Length, out returned, IntPtr.Zero) ||
        returned < 36)
        return false;

      // STORAGE_DEVICE_DESCRIPTOR
      int busType = BitConverter.ToInt32(buffer, 28);
      if (busType != BusTypeNvme)
        return false;
      model = ReadString(buffer, BitConverter.ToInt32(buffer, 16), returned);
      firmware = ReadString(buffer, BitConverter.ToInt32(buffer, 20), returned);
      return true;
    }

    private static string ReadString(byte[] buffer, int offset, int length) {
      if (offset <= 0 || offset >= length)
        return null;
      int end = offset;
      while (end < length && buffer[end] != 0)
        end++;
      return Encoding.ASCII.GetString(buffer, offset, end - offset).Trim();
    }

    public static bool ReadHealthLog(SafeFileHandle handle, out HealthLog log) {
      log = new HealthLog { TemperatureSensors = new ushort[8] };

      // STORAGE_PROPERTY_QUERY with STORAGE_PROTOCOL_SPECIFIC_DATA as its
      // additional parameters; the log page comes back after the same
      // header in a STORAGE_PROTOCOL_DATA_DESCRIPTOR
      byte[] buffer = new byte[QUERY_HEADER + PROTOCOL_DATA_SIZE +
        HEALTH_LOG_SIZE];
      BitConverter.GetBytes(StorageDeviceProtocolSpecificProperty)
        .CopyTo(buffer, 0);
      BitConverter.GetBytes(PropertyStandardQuery).CopyTo(buffer, 4);
      BitConverter.GetBytes(ProtocolTypeNvme).CopyTo(buffer, 8);
      BitConverter.GetBytes(NVMeDataTypeLogPage).CopyTo(buffer, 12);
      BitConverter.GetBytes(NVME_LOG_PAGE_HEALTH_INFO).CopyTo(buffer, 16);
      BitConverter.GetBytes(PROTOCOL_DATA_SIZE).CopyTo(buffer, 24);
      BitConverter.GetBytes(HEALTH_LOG_SIZE).CopyTo(buffer, 28);

      int returned;
      if (!DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, buffer,
        buffer.Length, buffer, buffer.Length, out returned, IntPtr.Zero) ||
        returned < QUERY_HEADER + PROTOCOL_DATA_SIZE + 216)
        return false;

      int d = QUERY_HEADER + PROTOCOL_DATA_SIZE;
      log.CriticalWarning = buffer[d];
      log.CompositeTemperature = BitConverter.ToUInt16(buffer, d + 1);
      log.AvailableSpare = buffer[d + 3];
      log.AvailableSpareThreshold = buffer[d + 4];
      log.PercentageUsed = buffer[d + 5];
      // 128-bit counters; the low 64 bits are plenty
      log.DataUnitsRead = BitConverter.ToUInt64(buffer, d + 32);
      log.DataUnitsWritten = BitConverter.ToUInt64(buffer, d + 48);
      log.PowerCycles = BitConverter.ToUInt64(buffer, d + 112);
      log.PowerOnHours = BitConverter.ToUInt64(buffer, d + 128);
      log.UnsafeShutdowns = BitConverter.ToUInt64(buffer, d + 144);
      log.MediaErrors = BitConverter.ToUInt64(buffer, d + 160);
      for (int i = 0; i < 8; i++)
        log.TemperatureSensors[i] = BitConverter.ToUInt16(buffer, d + 200 + 2 * i);
      return log.CompositeTemperature != 0 || log.DataUnitsWritten != 0;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string fileName,
      uint access, uint share, IntPtr security, uint creation, uint flags,
      IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle,
      uint code, byte[] input, int inputSize, byte[] output, int outputSize,
      out int returned, IntPtr overlapped);
  }
}
