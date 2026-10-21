using System;
using System.Collections.Generic;
using System.Management;

namespace ThinkBookFanControl
{
    /// <summary>
    /// Lenovo BIOS WMI interface (root\WMI, LENOVO_OTHER_METHOD).
    ///   0x0403_00XX  fan XX speed: read = current RPM, write = target RPM (0 = hand back to EC auto control)
    ///   0x0504_0000  CPU temperature (°C, read only)
    /// Fan list and rated limits come from LENOVO_FAN_TEST_DATA. Verified on ThinkBook 14+ 2025; other models with
    /// the same classes (ThinkBook+ 2024, ...) likely work too.
    /// Not thread-safe: use from a single thread.
    /// </summary>
    public class LenovoWmi : IFanBackend
    {
        public const uint FanSpeedBase = 0x04030000;
        public const uint CpuTemperature = 0x05040000;

        /// <summary>Below this the fan cannot hold its speed and stalls (measured on ThinkBook 14 G7+).</summary>
        public const int LowestStableRpm = 1000;
        /// <summary>Written to stop a fan: the EC takes small values literally (0 would mean auto).</summary>
        public const int StopValue = 1;

        private ManagementObject _other;
        private List<FanInfo> _fans = new List<FanInfo>();

        private LenovoWmi()
        {
            using (var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM LENOVO_OTHER_METHOD"))
            {
                foreach (ManagementObject o in searcher.Get()) { _other = o; break; }
            }
            if (_other == null)
                throw new NotSupportedException("未找到 LENOVO_OTHER_METHOD（root\\WMI）");
        }

        /// <summary>Just the method object, without fan discovery (for power limit features). Null when absent.</summary>
        public static LenovoWmi OpenRaw()
        {
            try { return new LenovoWmi(); }
            catch (Exception) { return null; }
        }

        /// <summary>Fan control backend; throws NotSupportedException when this machine has no usable WMI fans.</summary>
        public static LenovoWmi Open()
        {
            var w = new LenovoWmi();
            try
            {
                w.ReadCpuTemp();
                w._fans = DiscoverFans(w);
                if (w._fans.Count == 0) throw new NotSupportedException("没有找到可设定转速的风扇");
                return w;
            }
            catch (ManagementException ex)
            {
                w.Dispose();
                throw new NotSupportedException(ex.Message.Trim(), ex);
            }
            catch (Exception)
            {
                w.Dispose();
                throw;
            }
        }

        public string Name { get { return "联想 BIOS WMI（LENOVO_OTHER_METHOD）"; } }
        public List<FanInfo> Fans { get { return _fans; } }
        public bool CanSetSpeed { get { return true; } }
        public int ReassertSeconds { get { return 15; } }

        public uint GetFeature(uint id)
        {
            using (ManagementBaseObject inp = _other.GetMethodParameters("GetFeatureValue"))
            {
                inp["IDs"] = id;
                using (ManagementBaseObject outp = _other.InvokeMethod("GetFeatureValue", inp, null))
                    return Convert.ToUInt32(outp["value"]);
            }
        }

        public void SetFeature(uint id, uint value)
        {
            using (ManagementBaseObject inp = _other.GetMethodParameters("SetFeatureValue"))
            {
                inp["IDs"] = id;
                inp["value"] = value;
                using (_other.InvokeMethod("SetFeatureValue", inp, null)) { }
            }
        }

        public int ReadCpuTemp() { return (int)GetFeature(CpuTemperature); }
        public int ReadFanRpm(FanInfo fan) { return (int)GetFeature(FanSpeedBase | (uint)fan.Id); }

        public void WriteFan(FanInfo fan, int target)
        {
            SetFeature(FanSpeedBase | (uint)fan.Id, (uint)(target == FanTarget.Stop ? StopValue : Math.Max(0, target)));
        }

        public void RestoreAuto()
        {
            foreach (var f in _fans)
            {
                try { SetFeature(FanSpeedBase | (uint)f.Id, 0); } catch (Exception) { }
            }
        }

        /// <summary>
        /// LENOVO_CAPABILITY_DATA_00 support bits of a feature ID (1 = valid, 2 = get, 4 = set), or -1 when the
        /// class is missing or does not list the ID.
        /// </summary>
        public static int Capability(uint id)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT IDs, Capability FROM LENOVO_CAPABILITY_DATA_00"))
                    foreach (ManagementObject o in searcher.Get())
                        using (o)
                            if (Convert.ToUInt32(o["IDs"]) == id) return Convert.ToInt32(o["Capability"]);
            }
            catch (Exception) { }
            return -1;
        }

        public static List<FanInfo> DiscoverFans(LenovoWmi wmi)
        {
            var fans = new List<FanInfo>();
            try
            {
                using (var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM LENOVO_FAN_TEST_DATA"))
                {
                    foreach (ManagementObject o in searcher.Get())
                    {
                        var ids = (uint[])o["FanId"];
                        var max = (uint[])o["FanMaxSpeed"];
                        var min = (uint[])o["FanMinSpeed"];
                        for (int i = 0; i < ids.Length; i++)
                            fans.Add(new FanInfo { Id = (int)ids[i], MinRpm = (int)min[i], MaxRpm = (int)max[i] });
                        break;
                    }
                }
            }
            catch (ManagementException) { }

            if (fans.Count == 0)
            {
                // Fallback: probe fan IDs 1..4 for a readable non-zero speed.
                for (int id = 1; id <= 4; id++)
                {
                    try { if (wmi.GetFeature(FanSpeedBase | (uint)id) > 0) fans.Add(new FanInfo { Id = id, MinRpm = 1800, MaxRpm = 5000 }); }
                    catch (ManagementException) { }
                }
            }
            // Machines that list the fan feature but without the "set" bit (e.g. read-only speed on Legion) cannot be driven.
            fans.RemoveAll(f => { int cap = Capability(FanSpeedBase | (uint)f.Id); return cap >= 0 && (cap & 4) == 0; });
            for (int i = 0; i < fans.Count; i++)
            {
                // Round limits to the EC's 100 RPM step.
                fans[i].MinRpm = (fans[i].MinRpm + 99) / 100 * 100;
                fans[i].MaxRpm = fans[i].MaxRpm / 100 * 100;
                fans[i].Step = 100;
                fans[i].LowestStable = LowestStableRpm;
                fans[i].Name = "风扇" + (i + 1);
            }
            return fans;
        }

        public void Dispose()
        {
            if (_other != null) { _other.Dispose(); _other = null; }
        }
    }
}
