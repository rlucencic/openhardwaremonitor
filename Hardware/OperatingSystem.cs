/*
 
  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.
 
  Copyright (C) 2012-2020 Michael Möller <mmoeller@openhardwaremonitor.org>
	
*/

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenHardwareMonitor.Hardware {
  public static class OperatingSystem {

    static OperatingSystem() {
      int p = (int)Environment.OSVersion.Platform;
      IsUnix = (p == 4) || (p == 6) || (p == 128);

      Is64BitOperatingSystem = GetIs64BitOperatingSystem();
      IsArm64 = !IsUnix && GetIsArm64();
    }

    public static bool IsUnix { get; }

    public static bool Is64BitOperatingSystem { get; }

    /// <summary>
    /// Windows on ARM64, whatever the process runs as (native ARM64 or x64
    /// emulated). There, the x86 kernel driver can't load and the x86 CPUID
    /// code can't run, so hardware is read through Windows' own interfaces.
    /// </summary>
    public static bool IsArm64 { get; }

    private const ushort IMAGE_FILE_MACHINE_ARM64 = 0xAA64;

    private static bool GetIsArm64() {
      try {
        // IsWow64Process2 reports the native machine even to an x64 process
        // emulated on ARM64, where RuntimeInformation may say X64.
        ushort processMachine, nativeMachine;
        if (IsWow64Process2(Process.GetCurrentProcess().Handle,
          out processMachine, out nativeMachine))
          return nativeMachine == IMAGE_FILE_MACHINE_ARM64;
      } catch (EntryPointNotFoundException) { }
      return RuntimeInformation.OSArchitecture == Architecture.Arm64;
    }

    public static string ProcessArchitectureName {
      get { return RuntimeInformation.ProcessArchitecture.ToString(); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(IntPtr hProcess,
      out ushort processMachine, out ushort nativeMachine);

    private static bool GetIs64BitOperatingSystem() {
      if (IntPtr.Size == 8)
        return true;

      try {
        bool wow64Process;
        bool result = IsWow64Process(
          Process.GetCurrentProcess().Handle, out wow64Process);

        return result && wow64Process;
      } catch (EntryPointNotFoundException) {
        return false;
      }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process(IntPtr hProcess,
      out bool wow64Process);
  }
}
