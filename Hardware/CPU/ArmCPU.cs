/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace OpenHardwareMonitor.Hardware.CPU {

  /// <summary>
  /// The CPUs of Windows on ARM64. Without CPUID and model specific
  /// registers, everything comes from Windows: load and clocks from the
  /// processor counters, and on Snapdragon the power of each cluster from
  /// the energy meters Windows exposes.
  /// </summary>
  internal class ArmCPUGroup : IGroup {

    private readonly List<ArmCPU> hardware = new List<ArmCPU>();

    public ArmCPUGroup(ISettings settings) {
      if (PdhQuery.CounterExists(ArmCPU.FrequencyPath))
        hardware.Add(new ArmCPU(0, settings));
    }

    public IHardware[] Hardware {
      get { return hardware.ToArray(); }
    }

    public string GetReport() {
      return null;
    }

    public void Close() {
      foreach (ArmCPU cpu in hardware)
        cpu.Close();
    }
  }

  internal class ArmCPU : Hardware {

    internal const string LoadPath =
      @"\Processor Information(*)\% Processor Time";
    // instantaneous, so it lists the cores on the first sample
    internal const string FrequencyPath =
      @"\Processor Information(*)\Processor Frequency";
    private const string PerformancePath =
      @"\Processor Information(*)\% Processor Performance";
    // Windows 11; the frequency each core actually ran at
    private const string ActualFrequencyPath =
      @"\Processor Information(*)\Actual Frequency";
    private const string PowerPath = @"\Energy Meter(*)\Power";
    private const string EnergyPath = @"\Energy Meter(*)\Energy";

    private static readonly Regex CoreInstance = new Regex(@"^(\d+),(\d+)$");
    private static readonly Regex ClusterChannel =
      new Regex(@"^cpu_cluster_(\d+)$", RegexOptions.IgnoreCase);

    private readonly PdhQuery query;
    private readonly PdhCounter loadCounter;
    private readonly PdhCounter frequencyCounter;
    private readonly PdhCounter performanceCounter;
    private readonly PdhCounter actualFrequencyCounter;
    private readonly PdhCounter powerCounter;

    private readonly string[] cores;     // "group,number", in order
    private readonly string[] clusters;  // energy meter channels

    private readonly Sensor totalLoad;
    private readonly Sensor[] coreLoads;
    private readonly Sensor[] coreClocks;
    private readonly Sensor coresPower;
    private readonly Sensor[] clusterPowers;

    public ArmCPU(int processorIndex, ISettings settings)
      : base(GetProcessorName(), new Identifier("armcpu",
        processorIndex.ToString(CultureInfo.InvariantCulture)), settings)
    {
      query = new PdhQuery();
      loadCounter = query.AddCounter(LoadPath);
      frequencyCounter = query.AddCounter(FrequencyPath);
      performanceCounter = query.AddCounter(PerformancePath);
      actualFrequencyCounter = query.AddCounter(ActualFrequencyPath);
      powerCounter = query.AddCounter(PowerPath);
      PdhCounter energyCounter = query.AddCounter(EnergyPath);
      query.Collect();

      cores = GetCores(frequencyCounter);
      clusters = GetClusters(energyCounter);

      coreLoads = new Sensor[cores.Length];
      coreClocks = new Sensor[cores.Length];
      for (int i = 0; i < cores.Length; i++) {
        coreLoads[i] = new Sensor(CoreString(i), i + 1, SensorType.Load,
          this, settings);
        coreClocks[i] = new Sensor(CoreString(i), i + 1, SensorType.Clock,
          this, settings);
      }
      totalLoad = new Sensor("CPU Total", 0, SensorType.Load, this, settings);

      if (loadCounter != null) {
        foreach (Sensor sensor in coreLoads)
          ActivateSensor(sensor);
        if (cores.Length > 1)
          ActivateSensor(totalLoad);
      }
      if (actualFrequencyCounter != null || frequencyCounter != null)
        foreach (Sensor sensor in coreClocks)
          ActivateSensor(sensor);

      clusterPowers = new Sensor[clusters.Length];
      for (int i = 0; i < clusters.Length; i++) {
        clusterPowers[i] = new Sensor("CPU Cluster #" + (i + 1), i + 1,
          SensorType.Power, this, settings);
        ActivateSensor(clusterPowers[i]);
      }
      coresPower = new Sensor("CPU Cores", 0, SensorType.Power, this,
        settings);
      if (clusters.Length > 0)
        ActivateSensor(coresPower);
    }

    private string CoreString(int i) {
      return cores.Length == 1 ? "CPU Core" : "CPU Core #" + (i + 1);
    }

    private static string GetProcessorName() {
      string name = null;
      try {
        using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
          @"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
        {
          if (key != null)
            name = key.GetValue("ProcessorNameString") as string;
        }
      } catch (System.Security.SecurityException) { }

      if (string.IsNullOrWhiteSpace(name))
        return "ARM Processor";

      name = name.Replace("(R)", "").Replace("(TM)", "").Replace("(tm)", "");
      name = Regex.Replace(name, @"\s+", " ").Trim();
      if (name.EndsWith(" CPU", StringComparison.Ordinal))
        name = name.Substring(0, name.Length - 4);
      return name;
    }

    private static string[] GetCores(PdhCounter counter) {
      List<KeyValuePair<long, string>> list =
        new List<KeyValuePair<long, string>>();
      if (counter != null) {
        foreach (string instance in counter.GetValues().Keys) {
          Match m = CoreInstance.Match(instance);
          if (m.Success) {
            long order = long.Parse(m.Groups[1].Value,
              CultureInfo.InvariantCulture) * 1000 + long.Parse(
              m.Groups[2].Value, CultureInfo.InvariantCulture);
            list.Add(new KeyValuePair<long, string>(order, instance));
          }
        }
      }
      if (list.Count == 0) {
        for (int i = 0; i < Environment.ProcessorCount; i++)
          list.Add(new KeyValuePair<long, string>(i,
            "0," + i.ToString(CultureInfo.InvariantCulture)));
      }
      list.Sort((a, b) => a.Key.CompareTo(b.Key));
      return list.ConvertAll(p => p.Value).ToArray();
    }

    private static string[] GetClusters(PdhCounter counter) {
      SortedDictionary<int, string> list = new SortedDictionary<int, string>();
      if (counter != null) {
        foreach (string channel in counter.GetValues().Keys) {
          Match m = ClusterChannel.Match(channel);
          if (m.Success)
            list[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)] =
              channel;
        }
      }
      return new List<string>(list.Values).ToArray();
    }

    public override HardwareType HardwareType {
      get { return HardwareType.CPU; }
    }

    public override void Update() {
      if (!query.Collect())
        return;

      if (loadCounter != null) {
        Dictionary<string, double> load = loadCounter.GetValues();
        for (int i = 0; i < cores.Length; i++)
          coreLoads[i].Value = Percent(load, cores[i]);
        totalLoad.Value = Percent(load, "_Total");
      }

      Dictionary<string, double> actual = actualFrequencyCounter != null ?
        actualFrequencyCounter.GetValues() : null;
      Dictionary<string, double> frequency = frequencyCounter != null ?
        frequencyCounter.GetValues() : null;
      Dictionary<string, double> performance = performanceCounter != null ?
        performanceCounter.GetValues() : null;
      for (int i = 0; i < cores.Length; i++) {
        double value;
        if (actual != null && actual.TryGetValue(cores[i], out value) &&
          value > 0)
        {
          coreClocks[i].Value = (float)value;
        } else if (frequency != null &&
          frequency.TryGetValue(cores[i], out value))
        {
          // nominal frequency scaled by how much of it the core delivered
          double scale;
          if (performance != null &&
            performance.TryGetValue(cores[i], out scale))
            value *= scale / 100;
          coreClocks[i].Value = (float)value;
        } else {
          coreClocks[i].Value = null;
        }
      }

      if (powerCounter != null && clusters.Length > 0) {
        Dictionary<string, double> power = powerCounter.GetValues();
        float total = 0;
        bool any = false;
        for (int i = 0; i < clusters.Length; i++) {
          double milliwatts;
          if (power.TryGetValue(clusters[i], out milliwatts)) {
            clusterPowers[i].Value = (float)(milliwatts / 1000);
            total += clusterPowers[i].Value.Value;
            any = true;
          } else {
            clusterPowers[i].Value = null;
          }
        }
        coresPower.Value = any ? total : (float?)null;
      }
    }

    private static float? Percent(Dictionary<string, double> values,
      string instance)
    {
      double value;
      if (!values.TryGetValue(instance, out value))
        return null;
      return (float)Math.Max(0, Math.Min(100, value));
    }

    public override string GetReport() {
      StringBuilder r = new StringBuilder();
      r.AppendLine("ARM Processor");
      r.AppendLine();
      r.AppendLine("Name: " + name);
      r.AppendLine("Logical Processors: " +
        cores.Length.ToString(CultureInfo.InvariantCulture));
      r.AppendLine("Processor Counters: " + string.Join(", ", cores));
      r.AppendLine("Load Counter: " + (loadCounter != null ? "Yes" : "No"));
      r.AppendLine("Actual Frequency Counter: " +
        (actualFrequencyCounter != null ? "Yes" : "No"));
      r.AppendLine("Energy Meter Clusters: " + (clusters.Length > 0 ?
        string.Join(", ", clusters) : "None"));
      r.AppendLine();
      return r.ToString();
    }

    public override void Close() {
      query.Dispose();
      base.Close();
    }
  }
}
