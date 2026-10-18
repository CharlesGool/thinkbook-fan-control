using System;
using System.Collections.Generic;
using System.Management;

namespace ThinkBookFanControl
{
    public class FanInfo
    {
        public int Id;       // EC fan ID (low byte of the feature ID)
        public int MinRpm;
        public int MaxRpm;
        public string Name;
    }

    /// <summary>
    /// Lenovo BIOS WMI interface (root\WMI, LENOVO_OTHER_METHOD).
    ///   0x0403_00XX  fan XX speed: read = current RPM, write = target RPM (0 = hand back to EC auto control)
    ///   0x0504_0000  CPU temperature (°C, read only)
    /// Not thread-safe: use from a single thread.
    /// </summary>
    public class LenovoWmi : IDisposable
    {
        public const uint FanSpeedBase = 0x04030000;
        public const uint CpuTemperature = 0x05040000;

        /// <summary>Below this the fan cannot hold its speed and stalls (measured on ThinkBook 14 G7+).</summary>
        public const int LowestStableRpm = 1000;
        /// <summary>Written to stop a fan: the EC takes small values literally (0 would mean auto).</summary>
        public const int StopValue = 1;

        private ManagementObject _other;

        public LenovoWmi()
        {
            using (var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM LENOVO_OTHER_METHOD"))
            {
                foreach (ManagementObject o in searcher.Get()) { _other = o; break; }
            }
            if (_other == null)
                throw new NotSupportedException("未找到 LENOVO_OTHER_METHOD（root\\WMI）。本机型可能不支持。");
        }

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
        public int ReadFanRpm(int fanId) { return (int)GetFeature(FanSpeedBase | (uint)fanId); }

        /// <summary>rpm = 0 restores EC automatic control.</summary>
        public void WriteFanRpm(int fanId, int rpm) { SetFeature(FanSpeedBase | (uint)fanId, (uint)rpm); }

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
                    try { if (wmi.ReadFanRpm(id) > 0) fans.Add(new FanInfo { Id = id, MinRpm = 1800, MaxRpm = 5000 }); }
                    catch (ManagementException) { }
                }
            }
            for (int i = 0; i < fans.Count; i++)
            {
                // Round limits to the EC's 100 RPM step.
                fans[i].MinRpm = (fans[i].MinRpm + 99) / 100 * 100;
                fans[i].MaxRpm = fans[i].MaxRpm / 100 * 100;
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
