/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

*/

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace OpenHardwareMonitor.Hardware.Gpu {

  /// <summary>
  /// DXCore (dxcore.dll), Windows' adapter API for GPUs and for compute-only
  /// adapters such as NPUs, which DXGI doesn't list. Newer Windows also
  /// answers adapter state queries through it: temperature and engine
  /// clocks, where the driver reports them. Definitions follow the Windows
  /// SDK's dxcore_interface.h.
  /// </summary>
  internal static class DxCore {

    private static readonly Guid IID_IDXCoreAdapterFactory =
      new Guid("78ee5945-c36e-4b13-a669-005dd11c0f06");
    private static readonly Guid IID_IDXCoreAdapterList =
      new Guid("526c7776-40e9-459b-b711-f32ad76dfc28");
    private static readonly Guid IID_IDXCoreAdapter =
      new Guid("f0db4c7f-fe5a-42a2-bd62-f2a6cf6fc83e");

    public static readonly Guid HardwareTypeNpu =
      new Guid("d46140c4-add7-451b-9e56-06fe8c3b58ed");
    public static readonly Guid AttributeGenericML =
      new Guid("b71b0d41-1088-422f-a27c-0250b7d3a988");
    public static readonly Guid AttributeD3D12Graphics =
      new Guid("0c9ece4d-2f6e-4f01-8c96-e89e331b47b1");
    public static readonly Guid AttributeD3D11Graphics =
      new Guid("8c47866b-7583-450d-f0f0-6bada895af4b");

    // vtable slots after IUnknown (0 QueryInterface, 1 AddRef, 2 Release)
    private const int RELEASE = 2;
    private const int FACTORY_CREATE_ADAPTER_LIST = 3;
    private const int FACTORY_GET_ADAPTER_BY_LUID = 4;
    private const int LIST_GET_ADAPTER = 3;
    private const int LIST_GET_ADAPTER_COUNT = 4;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateAdapterListFn(IntPtr self, uint count,
      ref Guid attribute, ref Guid riid, out IntPtr list);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetAdapterByLuidFn(IntPtr self, ref long luid,
      ref Guid riid, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetAdapterFn(IntPtr self, uint index, ref Guid riid,
      out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint GetAdapterCountFn(IntPtr self);

    [DllImport("dxcore.dll")]
    private static extern int DXCoreCreateAdapterFactory(ref Guid riid,
      out IntPtr factory);

    internal static T Method<T>(IntPtr obj, int slot) where T : Delegate {
      IntPtr vtable = Marshal.ReadIntPtr(obj);
      return Marshal.GetDelegateForFunctionPointer<T>(
        Marshal.ReadIntPtr(vtable, slot * IntPtr.Size));
    }

    internal static void Release(IntPtr obj) {
      if (obj != IntPtr.Zero)
        Method<ReleaseFn>(obj, RELEASE)(obj);
    }

    private static IntPtr CreateFactory() {
      try {
        Guid iid = IID_IDXCoreAdapterFactory;
        IntPtr factory;
        if (DXCoreCreateAdapterFactory(ref iid, out factory) == 0)
          return factory;
      } catch (DllNotFoundException) {
      } catch (EntryPointNotFoundException) {
      }
      return IntPtr.Zero;
    }

    /// <summary>
    /// The adapters with an attribute (a DXCORE_ADAPTER_ATTRIBUTE_* or
    /// DXCORE_HARDWARE_TYPE_ATTRIBUTE_* GUID). The caller disposes them.
    /// </summary>
    public static List<DxCoreAdapter> GetAdapters(Guid attribute) {
      List<DxCoreAdapter> adapters = new List<DxCoreAdapter>();
      IntPtr factory = CreateFactory();
      if (factory == IntPtr.Zero)
        return adapters;
      IntPtr list = IntPtr.Zero;
      try {
        Guid listIid = IID_IDXCoreAdapterList;
        if (Method<CreateAdapterListFn>(factory, FACTORY_CREATE_ADAPTER_LIST)(
          factory, 1, ref attribute, ref listIid, out list) != 0 ||
          list == IntPtr.Zero)
          return adapters;
        uint count = Method<GetAdapterCountFn>(list, LIST_GET_ADAPTER_COUNT)(
          list);
        for (uint i = 0; i < count; i++) {
          Guid adapterIid = IID_IDXCoreAdapter;
          IntPtr adapter;
          if (Method<GetAdapterFn>(list, LIST_GET_ADAPTER)(list, i,
            ref adapterIid, out adapter) == 0 && adapter != IntPtr.Zero)
            adapters.Add(new DxCoreAdapter(adapter));
        }
      } finally {
        Release(list);
        Release(factory);
      }
      return adapters;
    }

    /// <summary>The adapter with this LUID, or null.</summary>
    public static DxCoreAdapter GetAdapterByLuid(uint luidLow, int luidHigh) {
      IntPtr factory = CreateFactory();
      if (factory == IntPtr.Zero)
        return null;
      try {
        long luid = ((long)luidHigh << 32) | luidLow;
        Guid adapterIid = IID_IDXCoreAdapter;
        IntPtr adapter;
        if (Method<GetAdapterByLuidFn>(factory, FACTORY_GET_ADAPTER_BY_LUID)(
          factory, ref luid, ref adapterIid, out adapter) != 0 ||
          adapter == IntPtr.Zero)
          return null;
        return new DxCoreAdapter(adapter);
      } finally {
        Release(factory);
      }
    }
  }

  /// <summary>An IDXCoreAdapter.</summary>
  internal sealed class DxCoreAdapter : IDisposable {

    // IDXCoreAdapter vtable slots
    private const int IS_ATTRIBUTE_SUPPORTED = 4;
    private const int IS_PROPERTY_SUPPORTED = 5;
    private const int GET_PROPERTY = 6;
    private const int GET_PROPERTY_SIZE = 7;
    private const int IS_QUERY_STATE_SUPPORTED = 8;
    private const int QUERY_STATE = 9;

    // DXCoreAdapterProperty
    private const uint InstanceLuid = 0;
    private const uint DriverDescription = 2;
    private const uint HardwareID = 3;
    private const uint DedicatedAdapterMemory = 7;
    private const uint IsHardwareProperty = 11;
    private const uint AdapterEngineCount = 16;

    // DXCoreAdapterState
    private const uint AdapterTemperatureCelsius = 6;
    private const uint AdapterEngineFrequencyHertz = 9;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate byte IsAttributeSupportedFn(IntPtr self, ref Guid guid);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate byte IsSupportedFn(IntPtr self, uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPropertyFn(IntPtr self, uint property,
      UIntPtr size, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPropertySizeFn(IntPtr self, uint property,
      out UIntPtr size);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryStateFn(IntPtr self, uint state, UIntPtr inSize,
      IntPtr input, UIntPtr outSize, IntPtr output);

    private IntPtr adapter;
    // the documentation doesn't give the temperature's type: 0 untried,
    // 4 a 32-bit integer, 8 a double, -1 not available
    private int temperatureSize;
    private int temperatureFailures;

    internal DxCoreAdapter(IntPtr adapter) {
      this.adapter = adapter;

      Description = ReadString(DriverDescription) ?? "";
      byte[] luid = ReadBytes(InstanceLuid, 8);
      if (luid != null) {
        LuidLow = BitConverter.ToUInt32(luid, 0);
        LuidHigh = BitConverter.ToInt32(luid, 4);
      }
      byte[] hardwareId = ReadBytes(HardwareID, 16);
      if (hardwareId != null) {
        VendorId = BitConverter.ToUInt32(hardwareId, 0);
        DeviceId = BitConverter.ToUInt32(hardwareId, 4);
      }
      byte[] hardware = ReadBytes(IsHardwareProperty, 1);
      IsHardware = hardware != null && hardware[0] != 0;
      byte[] dedicated = ReadBytes(DedicatedAdapterMemory, 8);
      if (dedicated != null)
        DedicatedMemory = BitConverter.ToUInt64(dedicated, 0);
      byte[] engines = ReadBytes(AdapterEngineCount, 4);
      if (engines != null)
        EngineCount = BitConverter.ToUInt32(engines, 0);
    }

    public string Description { get; private set; }
    public uint LuidLow { get; private set; }
    public int LuidHigh { get; private set; }
    public uint VendorId { get; private set; }
    public uint DeviceId { get; private set; }
    public bool IsHardware { get; private set; }
    public ulong DedicatedMemory { get; private set; }
    public uint EngineCount { get; private set; }

    public bool IsAttributeSupported(Guid attribute) {
      if (adapter == IntPtr.Zero)
        return false;
      return DxCore.Method<IsAttributeSupportedFn>(adapter,
        IS_ATTRIBUTE_SUPPORTED)(adapter, ref attribute) != 0;
    }

    private bool IsPropertySupported(uint property) {
      return DxCore.Method<IsSupportedFn>(adapter, IS_PROPERTY_SUPPORTED)(
        adapter, property) != 0;
    }

    private bool IsQueryStateSupported(uint state) {
      return DxCore.Method<IsSupportedFn>(adapter, IS_QUERY_STATE_SUPPORTED)(
        adapter, state) != 0;
    }

    private byte[] ReadBytes(uint property, int size) {
      if (adapter == IntPtr.Zero || !IsPropertySupported(property))
        return null;
      IntPtr buffer = Marshal.AllocHGlobal(size);
      try {
        if (DxCore.Method<GetPropertyFn>(adapter, GET_PROPERTY)(adapter,
          property, (UIntPtr)size, buffer) != 0)
          return null;
        byte[] data = new byte[size];
        Marshal.Copy(buffer, data, 0, size);
        return data;
      } finally {
        Marshal.FreeHGlobal(buffer);
      }
    }

    private string ReadString(uint property) {
      if (adapter == IntPtr.Zero || !IsPropertySupported(property))
        return null;
      UIntPtr size;
      if (DxCore.Method<GetPropertySizeFn>(adapter, GET_PROPERTY_SIZE)(
        adapter, property, out size) != 0 || size == UIntPtr.Zero)
        return null;
      IntPtr buffer = Marshal.AllocHGlobal((int)size);
      try {
        if (DxCore.Method<GetPropertyFn>(adapter, GET_PROPERTY)(adapter,
          property, size, buffer) != 0)
          return null;
        // a null-terminated char string
        return Marshal.PtrToStringUTF8(buffer);
      } finally {
        Marshal.FreeHGlobal(buffer);
      }
    }

    private int QueryState(uint state, IntPtr input, int inputSize,
      IntPtr output, int outputSize)
    {
      return DxCore.Method<QueryStateFn>(adapter, QUERY_STATE)(adapter, state,
        (UIntPtr)inputSize, input, (UIntPtr)outputSize, output);
    }

    public bool SupportsTemperature {
      get {
        return adapter != IntPtr.Zero && temperatureSize >= 0 &&
          IsQueryStateSupported(AdapterTemperatureCelsius);
      }
    }

    /// <summary>
    /// Degrees Celsius of the first physical adapter, or null when the
    /// driver doesn't report it.
    /// </summary>
    public float? GetTemperature() {
      if (!SupportsTemperature)
        return null;
      IntPtr input = Marshal.AllocHGlobal(4);
      IntPtr output = Marshal.AllocHGlobal(8);
      try {
        Marshal.WriteInt32(input, 0); // physical adapter index
        // The output type isn't documented: try a double, then a 32-bit
        // integer, and keep whichever the call accepts with a plausible
        // value.
        if (temperatureSize == 0 || temperatureSize == 8) {
          Marshal.WriteInt64(output, 0);
          if (QueryState(AdapterTemperatureCelsius, input, 4, output, 8) == 0) {
            double value = BitConverter.Int64BitsToDouble(
              Marshal.ReadInt64(output));
            if (value > -50 && value < 200 && value != 0) {
              temperatureSize = 8;
              return (float)value;
            }
          }
          if (temperatureSize == 8)
            return null;
        }
        Marshal.WriteInt64(output, 0);
        if (QueryState(AdapterTemperatureCelsius, input, 4, output, 4) == 0) {
          int value = Marshal.ReadInt32(output);
          if (value > -50 && value < 200) {
            temperatureSize = 4;
            return value;
          }
        }
        // neither works; a sleeping GPU may refuse once, so only give up
        // after repeated failures without any success
        if (temperatureSize == 0 && ++temperatureFailures >= 10)
          temperatureSize = -1;
        return null;
      } finally {
        Marshal.FreeHGlobal(input);
        Marshal.FreeHGlobal(output);
      }
    }

    public bool SupportsEngineFrequency {
      get {
        return adapter != IntPtr.Zero &&
          IsQueryStateSupported(AdapterEngineFrequencyHertz);
      }
    }

    /// <summary>An engine's clock in MHz, or null.</summary>
    public float? GetEngineFrequency(uint engineIndex) {
      if (!SupportsEngineFrequency)
        return null;
      // DXCoreAdapterEngineIndex in, DXCoreFrequencyQueryOutput out
      IntPtr input = Marshal.AllocHGlobal(8);
      IntPtr output = Marshal.AllocHGlobal(24);
      try {
        Marshal.WriteInt32(input, 0, 0);
        Marshal.WriteInt32(input, 4, (int)engineIndex);
        if (QueryState(AdapterEngineFrequencyHertz, input, 8, output, 24) != 0)
          return null;
        long hertz = Marshal.ReadInt64(output, 0);
        return hertz > 0 ? hertz / 1e6f : (float?)null;
      } finally {
        Marshal.FreeHGlobal(input);
        Marshal.FreeHGlobal(output);
      }
    }

    public void Dispose() {
      DxCore.Release(adapter);
      adapter = IntPtr.Zero;
    }
  }
}
