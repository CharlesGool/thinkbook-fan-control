using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Win32;

namespace ThinkBookFanControl
{
    /// <summary>
    /// Publishes values to HWiNFO64 "custom user sensors":
    ///   HKCU\Software\HWiNFO64\Sensors\Custom\&lt;DeviceName&gt;\&lt;Type&gt;&lt;N&gt;  Name / Value / Unit
    /// HWiNFO reads the sensor list (names) only when it starts and polls values about every 2 s,
    /// so keys are kept stable across runs and never deleted on exit.
    /// Set() only stores a value; Flush() writes the ones that changed. Registry errors are swallowed.
    /// </summary>
    public sealed class HwInfoSensors : IDisposable
    {
        public const string DeviceName = "ThinkBook Fan Control";
        private const string CustomPath = @"Software\HWiNFO64\Sensors\Custom";

        private static readonly string[] Types = { "Temp", "Fan", "Power", "Clock", "Usage", "Other" };

        private sealed class Sensor
        {
            public string Type;
            public string KeyName;     // e.g. "Fan1"
            public string Name;
            public string Unit;
            public RegistryKey Key;    // kept open between flushes
            public double Pending = double.NaN;
            public double Written = double.NaN;
        }

        private readonly object _sync = new object();
        private readonly List<Sensor> _sensors = new List<Sensor>();
        private readonly Dictionary<string, int> _typeCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private RegistryKey _device;
        private bool _cleaned;
        private bool _disposed;

        public HwInfoSensors() : this(DeviceName) { }

        /// <summary>Separate device name, for testing without touching the real device key.</summary>
        internal HwInfoSensors(string deviceName)
        {
            try { _device = Registry.CurrentUser.CreateSubKey(CustomPath + @"\" + deviceName); }
            catch { _device = null; }
        }

        /// <summary>
        /// Declares a sensor; N is assigned per type in declaration order (Fan0, Fan1, ...).
        /// type: "Temp","Fan","Power","Clock","Usage","Other" (unit is only used for "Other").
        /// </summary>
        public int Declare(string type, string name, string unit = null)
        {
            string t = null;
            foreach (string known in Types)
                if (string.Equals(known, type, StringComparison.OrdinalIgnoreCase)) { t = known; break; }
            if (t == null) throw new ArgumentException("未知的传感器类型：" + type, "type");

            lock (_sync)
            {
                int n;
                _typeCount.TryGetValue(t, out n);
                _typeCount[t] = n + 1;

                var s = new Sensor { Type = t, KeyName = t + n.ToString(CultureInfo.InvariantCulture) };
                s.Name = name ?? s.KeyName;
                s.Unit = unit ?? "";
                _sensors.Add(s);
                if (!_disposed) OpenSensorKey(s);   // writes Name (and Unit) right away
                return _sensors.Count - 1;
            }
        }

        /// <summary>Stores a value for the next Flush(). NaN means unknown: the last written value stays.</summary>
        public void Set(int id, double value)
        {
            lock (_sync)
            {
                if (id < 0 || id >= _sensors.Count) return;
                _sensors[id].Pending = value;
            }
        }

        /// <summary>Writes values changed since the last flush. Call about once per second.</summary>
        public void Flush()
        {
            lock (_sync)
            {
                if (_disposed || _device == null) return;
                if (!_cleaned) { _cleaned = true; RemoveStaleKeys(); }

                foreach (Sensor s in _sensors)
                {
                    double v = s.Pending;
                    if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                    if (v == s.Written) continue;
                    RegistryKey k = OpenSensorKey(s);
                    if (k == null) continue;
                    try
                    {
                        if (s.Type == "Fan" && v == Math.Floor(v) && v >= 0 && v <= int.MaxValue)
                            k.SetValue("Value", (int)v, RegistryValueKind.DWord);
                        else
                            k.SetValue("Value", v.ToString("0.###", CultureInfo.InvariantCulture), RegistryValueKind.String);
                        s.Written = v;
                    }
                    catch { CloseSensorKey(s); }   // reopened on the next flush
                }
            }
        }

        /// <summary>
        /// Whether the running HWiNFO lists our device (it reads custom sensors only when it starts): true / false from
        /// its shared memory (Global\HWiNFO_SENS_SM2, header "HWiS"; sensor entries hold the original, untranslated
        /// name at +8), null when HWiNFO or its shared memory is not running.
        /// </summary>
        public static bool? LoadedByHwInfo()
        {
            try
            {
                using (var mmf = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting(@"Global\HWiNFO_SENS_SM2", System.IO.MemoryMappedFiles.MemoryMappedFileRights.Read))
                using (var v = mmf.CreateViewAccessor(0, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read))
                {
                    if (v.ReadUInt32(0) != 0x53695748) return null;
                    uint off = v.ReadUInt32(20), size = v.ReadUInt32(24), count = v.ReadUInt32(28);
                    var name = new byte[128];
                    for (uint i = 0; i < count; i++)
                    {
                        v.ReadArray(off + (long)i * size + 8, name, 0, name.Length);
                        int n = Array.IndexOf(name, (byte)0);
                        if (System.Text.Encoding.ASCII.GetString(name, 0, n < 0 ? name.Length : n) == DeviceName) return true;
                    }
                    return false;
                }
            }
            catch (Exception) { return null; }
        }

        /// <summary>Deletes our whole device key (user-triggered). Other devices under "Custom" are left alone.</summary>
        public static void RemoveAll()
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(CustomPath + @"\" + DeviceName, false); }
            catch { }
        }

        /// <summary>Closes the registry keys; the sensors stay in the registry.</summary>
        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                foreach (Sensor s in _sensors) CloseSensorKey(s);
                if (_device != null)
                {
                    try { _device.Close(); } catch { }
                    _device = null;
                }
            }
        }

        // Sub-keys of our device left over from a previous version with other sensors.
        private void RemoveStaleKeys()
        {
            try
            {
                var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Sensor s in _sensors) keep.Add(s.KeyName);
                foreach (string sub in _device.GetSubKeyNames())
                {
                    if (keep.Contains(sub)) continue;
                    try { _device.DeleteSubKeyTree(sub, false); } catch { }
                }
            }
            catch { }
        }

        private RegistryKey OpenSensorKey(Sensor s)
        {
            if (s.Key != null) return s.Key;
            if (_device == null) return null;
            try
            {
                s.Key = _device.CreateSubKey(s.KeyName);
                if (s.Key != null)
                {
                    s.Key.SetValue("Name", s.Name, RegistryValueKind.String);
                    if (s.Type == "Other") s.Key.SetValue("Unit", s.Unit, RegistryValueKind.String);
                    else s.Key.DeleteValue("Unit", false);
                }
            }
            catch { CloseSensorKey(s); }
            return s.Key;
        }

        private static void CloseSensorKey(Sensor s)
        {
            if (s.Key != null)
            {
                try { s.Key.Close(); } catch { }
                s.Key = null;
            }
            s.Written = double.NaN;   // force a rewrite once the key is back
        }
    }
}
