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

namespace OpenHardwareMonitor.Hardware.Mainboard {

  /// <summary>
  /// The ACPI thermal zones Windows reports through its performance counters.
  /// Shown when no Super I/O chip was found: on laptops, and on Windows on
  /// ARM64, where there is no Super I/O at all. The firmware names zones
  /// (TZ0, TZ1...) without saying what they measure, so they keep those names.
  /// </summary>
  internal class ThermalZones : Hardware {

    // tenths of a kelvin
    private const string HighPrecisionPath =
      @"\Thermal Zone Information(*)\High Precision Temperature";
    // whole kelvin, on older Windows
    private const string Path = @"\Thermal Zone Information(*)\Temperature";

    private static readonly Regex NamePattern =
      new Regex(@"^(.*?)(\d*)$");

    private readonly IHardware parent;
    private readonly PdhQuery query;
    private readonly PdhCounter counter;
    private readonly bool highPrecision;
    private readonly string[] zones;
    private readonly Sensor[] sensors;

    public static ThermalZones Create(IHardware parent, ISettings settings) {
      PdhQuery query = new PdhQuery();
      bool highPrecision = true;
      PdhCounter counter = query.AddCounter(HighPrecisionPath);
      if (counter == null) {
        highPrecision = false;
        counter = query.AddCounter(Path);
      }
      if (counter == null || !query.Collect()) {
        query.Dispose();
        return null;
      }
      List<string> zones = new List<string>(counter.GetValues().Keys);
      if (zones.Count == 0) {
        query.Dispose();
        return null;
      }
      return new ThermalZones(parent, query, counter, highPrecision, zones,
        settings);
    }

    private ThermalZones(IHardware parent, PdhQuery query, PdhCounter counter,
      bool highPrecision, List<string> zones, ISettings settings)
      : base("ACPI Thermal Zones", new Identifier("acpi", "thermalzones"),
        settings)
    {
      this.parent = parent;
      this.query = query;
      this.counter = counter;
      this.highPrecision = highPrecision;

      // natural order (TZ2 before TZ10), which also fixes the indices
      zones.Sort(CompareZones);
      this.zones = zones.ToArray();

      Dictionary<string, int> shortNames = new Dictionary<string, int>();
      foreach (string zone in this.zones) {
        string s = ShortName(zone);
        int count;
        shortNames.TryGetValue(s, out count);
        shortNames[s] = count + 1;
      }

      sensors = new Sensor[this.zones.Length];
      for (int i = 0; i < this.zones.Length; i++) {
        string s = ShortName(this.zones[i]);
        // keep the ACPI scope when the short name isn't unique
        string sensorName = shortNames[s] > 1 ?
          this.zones[i].TrimStart('\\').ToUpperInvariant() : s;
        sensors[i] = new Sensor(sensorName, i, SensorType.Temperature, this,
          settings);
      }

      Update();
    }

    // "\_sb.tz0" -> "TZ0"
    private static string ShortName(string zone) {
      int dot = zone.LastIndexOf('.');
      string s = dot >= 0 ? zone.Substring(dot + 1) : zone.TrimStart('\\');
      return s.ToUpperInvariant();
    }

    private static int CompareZones(string a, string b) {
      Match ma = NamePattern.Match(ShortName(a));
      Match mb = NamePattern.Match(ShortName(b));
      int c = string.CompareOrdinal(ma.Groups[1].Value, mb.Groups[1].Value);
      if (c != 0)
        return c;
      long na, nb;
      long.TryParse(ma.Groups[2].Value, NumberStyles.None,
        CultureInfo.InvariantCulture, out na);
      long.TryParse(mb.Groups[2].Value, NumberStyles.None,
        CultureInfo.InvariantCulture, out nb);
      c = na.CompareTo(nb);
      return c != 0 ? c : string.CompareOrdinal(a, b);
    }

    public override IHardware Parent {
      get { return parent; }
    }

    public override HardwareType HardwareType {
      get { return HardwareType.ThermalZones; }
    }

    public override void Update() {
      if (!query.Collect())
        return;

      Dictionary<string, double> values = counter.GetValues();
      for (int i = 0; i < zones.Length; i++) {
        double kelvin;
        // a zone reading 0 K is switched off; it shows once it reads
        if (values.TryGetValue(zones[i], out kelvin) && kelvin > 0) {
          if (highPrecision)
            kelvin /= 10;
          sensors[i].Value = (float)(kelvin - 273.15);
          ActivateSensor(sensors[i]);
        } else {
          sensors[i].Value = null;
        }
      }
    }

    public override string GetReport() {
      StringBuilder r = new StringBuilder();
      r.AppendLine("ACPI Thermal Zones");
      r.AppendLine();
      r.AppendLine("Counter: " + (highPrecision ? HighPrecisionPath : Path));
      foreach (string zone in zones)
        r.AppendLine(" " + zone);
      r.AppendLine();
      return r.ToString();
    }

    public override void Close() {
      query.Dispose();
      base.Close();
    }
  }
}
