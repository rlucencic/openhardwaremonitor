/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

*/

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace OpenHardwareMonitor.Hardware {

  /// <summary>
  /// A Windows performance counter query (PDH). Counters are added by their
  /// English path, so it works on any display language, and may use a
  /// wildcard instance ("\Processor Information(*)\Actual Frequency"), which
  /// PDH expands again on every collection as instances come and go.
  /// </summary>
  internal sealed class PdhQuery : IDisposable {

    private IntPtr query;

    public PdhQuery() {
      if (PdhNative.PdhOpenQuery(null, IntPtr.Zero, out query) != 0)
        query = IntPtr.Zero;
    }

    public bool IsOpen {
      get { return query != IntPtr.Zero; }
    }

    /// <summary>Adds a counter, or returns null if it doesn't exist.</summary>
    public PdhCounter AddCounter(string englishPath) {
      if (query == IntPtr.Zero)
        return null;
      IntPtr counter;
      if (PdhNative.PdhAddEnglishCounter(query, englishPath, IntPtr.Zero,
        out counter) != 0)
        return null;
      return new PdhCounter(counter);
    }

    /// <summary>
    /// Takes a sample. Rate and average counters need two samples before
    /// they have values.
    /// </summary>
    public bool Collect() {
      if (query == IntPtr.Zero)
        return false;
      return PdhNative.PdhCollectQueryData(query) == 0;
    }

    public void Dispose() {
      if (query != IntPtr.Zero) {
        PdhNative.PdhCloseQuery(query);
        query = IntPtr.Zero;
      }
    }

    /// <summary>
    /// Whether Windows has the counter set at all, for deciding whether to
    /// create hardware that reads it.
    /// </summary>
    public static bool CounterExists(string englishPath) {
      using (PdhQuery q = new PdhQuery()) {
        return q.AddCounter(englishPath) != null;
      }
    }
  }

  internal sealed class PdhCounter {

    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_FMT_NOCAP100 = 0x00008000;
    private const uint PDH_MORE_DATA = 0x800007D2;
    private const uint PDH_CSTATUS_VALID_DATA = 0;
    private const uint PDH_CSTATUS_NEW_DATA = 1;

    // PDH_FMT_COUNTERVALUE_ITEM_W: a name pointer, then PDH_FMT_COUNTERVALUE
    // (a DWORD status and a union holding the double), 8-byte aligned
    private const int ITEM_SIZE = 24;
    private const int STATUS_OFFSET = 8;
    private const int VALUE_OFFSET = 16;

    private readonly IntPtr counter;

    internal PdhCounter(IntPtr counter) {
      this.counter = counter;
    }

    /// <summary>
    /// The latest values by instance name (lower case, as PDH gives them).
    /// Instances without valid data yet are left out.
    /// </summary>
    public Dictionary<string, double> GetValues() {
      Dictionary<string, double> values = new Dictionary<string, double>(
        StringComparer.OrdinalIgnoreCase);

      uint bufferSize = 0;
      uint itemCount;
      uint status = PdhNative.PdhGetFormattedCounterArray(counter,
        PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref bufferSize, out itemCount,
        IntPtr.Zero);
      if (status != PDH_MORE_DATA || bufferSize == 0)
        return values;

      IntPtr buffer = Marshal.AllocHGlobal((int)bufferSize);
      try {
        status = PdhNative.PdhGetFormattedCounterArray(counter,
          PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref bufferSize, out itemCount,
          buffer);
        if (status != 0)
          return values;

        for (int i = 0; i < itemCount; i++) {
          IntPtr item = buffer + i * ITEM_SIZE;
          uint cStatus = (uint)Marshal.ReadInt32(item, STATUS_OFFSET);
          if (cStatus != PDH_CSTATUS_VALID_DATA &&
            cStatus != PDH_CSTATUS_NEW_DATA)
            continue;
          string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item));
          if (name == null)
            continue;
          double value = BitConverter.Int64BitsToDouble(
            Marshal.ReadInt64(item, VALUE_OFFSET));
          // the same instance name can repeat (several processes with one
          // name); keep the sum, which is what totals want
          double previous;
          if (values.TryGetValue(name, out previous))
            values[name] = previous + value;
          else
            values.Add(name, value);
        }
      } finally {
        Marshal.FreeHGlobal(buffer);
      }
      return values;
    }
  }

  internal static class PdhNative {
    private const string PDH = "pdh.dll";

    [DllImport(PDH, CharSet = CharSet.Unicode)]
    public static extern uint PdhOpenQuery(string dataSource, IntPtr userData,
      out IntPtr query);

    [DllImport(PDH, CharSet = CharSet.Unicode)]
    public static extern uint PdhAddEnglishCounter(IntPtr query,
      string counterPath, IntPtr userData, out IntPtr counter);

    [DllImport(PDH)]
    public static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport(PDH, CharSet = CharSet.Unicode,
      EntryPoint = "PdhGetFormattedCounterArrayW")]
    public static extern uint PdhGetFormattedCounterArray(IntPtr counter,
      uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [DllImport(PDH)]
    public static extern uint PdhCloseQuery(IntPtr query);
  }
}
