/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OpenHardwareMonitor.Hardware.Mainboard {

  /// <summary>
  /// The power meters Windows exposes through its Energy Meter counters, as
  /// Snapdragon platforms have: the whole system, the USB-C power input...
  /// The CPU clusters' and the GPU's channels show on the CPU and GPU
  /// instead.
  /// </summary>
  internal class EnergyMeters : Hardware {

    private const string PowerPath = @"\Energy Meter(*)\Power"; // milliwatts
    private const string EnergyPath = @"\Energy Meter(*)\Energy";

    private static readonly string[] ShownElsewhere =
      { "cpu_cluster", "gpu", "npu", "nsp" };

    private static readonly Dictionary<string, string> Names =
      new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        { "sys", "System" },
        { "psu_usb", "Power Supply (USB)" },
        { "usbc_total", "USB-C Total" },
        { "soc", "SoC" },
        { "dram", "DRAM" },
        { "display", "Display" },
        { "battery", "Battery" }
      };

    private readonly IHardware parent;
    private readonly PdhQuery query;
    private readonly PdhCounter powerCounter;
    private readonly string[] channels;
    private readonly Sensor[] sensors;

    public static EnergyMeters Create(IHardware parent, ISettings settings) {
      PdhQuery query = new PdhQuery();
      PdhCounter power = query.AddCounter(PowerPath);
      // Energy is a plain count, so it lists the channels on the first sample
      PdhCounter energy = query.AddCounter(EnergyPath);
      if (power == null || energy == null || !query.Collect()) {
        query.Dispose();
        return null;
      }
      List<string> channels = new List<string>();
      foreach (string channel in energy.GetValues().Keys) {
        if (channel.StartsWith("_", StringComparison.Ordinal))
          continue; // _total
        bool elsewhere = false;
        foreach (string prefix in ShownElsewhere)
          if (channel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            elsewhere = true;
        if (!elsewhere)
          channels.Add(channel);
      }
      if (channels.Count == 0) {
        query.Dispose();
        return null;
      }
      channels.Sort(StringComparer.OrdinalIgnoreCase);
      return new EnergyMeters(parent, query, power, channels.ToArray(),
        settings);
    }

    private EnergyMeters(IHardware parent, PdhQuery query,
      PdhCounter powerCounter, string[] channels, ISettings settings)
      : base("Energy Meters", new Identifier("energymeters"), settings)
    {
      this.parent = parent;
      this.query = query;
      this.powerCounter = powerCounter;
      this.channels = channels;

      sensors = new Sensor[channels.Length];
      for (int i = 0; i < channels.Length; i++) {
        sensors[i] = new Sensor(DisplayName(channels[i]), i, SensorType.Power,
          this, settings);
        ActivateSensor(sensors[i]);
      }
    }

    private static string DisplayName(string channel) {
      string name;
      if (Names.TryGetValue(channel, out name))
        return name;
      // "some_channel" -> "Some Channel"
      return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
        channel.Replace('_', ' '));
    }

    public override IHardware Parent {
      get { return parent; }
    }

    public override HardwareType HardwareType {
      get { return HardwareType.EnergyMeters; }
    }

    public override void Update() {
      if (!query.Collect())
        return;

      Dictionary<string, double> power = powerCounter.GetValues();
      for (int i = 0; i < channels.Length; i++) {
        double milliwatts;
        if (power.TryGetValue(channels[i], out milliwatts))
          sensors[i].Value = (float)(milliwatts / 1000);
        else
          sensors[i].Value = null;
      }
    }

    public override string GetReport() {
      StringBuilder r = new StringBuilder();
      r.AppendLine("Energy Meters");
      r.AppendLine();
      r.AppendLine("Channels: " + string.Join(", ", channels));
      r.AppendLine();
      return r.ToString();
    }

    public override void Close() {
      query.Dispose();
      base.Close();
    }
  }
}
