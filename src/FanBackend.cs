using System;
using System.Collections.Generic;

namespace ThinkBookFanControl
{
    /// <summary>One controllable fan. Speeds (Min/Max/Step/LowestStable) are in the backend's Unit.</summary>
    public class FanInfo
    {
        public int Id;            // backend-specific fan ID (EC fan ID for WMI, register index for EC)
        public int MinRpm;        // rated range in Unit (RPM, or % duty for PWM backends)
        public int MaxRpm;
        public int Step = 100;    // granularity of targets in Unit
        public int LowestStable;  // lowest target that still holds when low speed is allowed (0 = no low speed)
        public string Unit = "RPM";
        public bool HasRpm = true; // the backend can measure the actual RPM
        public string Name;

        public bool IsRpm { get { return Unit == "RPM"; } }
        public string Format(int v) { return v + (IsRpm ? " RPM" : " %"); }
    }

    /// <summary>Controller-level target encoding shared by all backends.</summary>
    public static class FanTarget
    {
        public const int Auto = 0;   // hand the fan back to the EC
        public const int Stop = -1;  // stop the fan (only when the backend supports low speed)
    }

    /// <summary>
    /// A way to read the CPU temperature and fan speeds and to set fan targets. Implementations are used from the
    /// controller thread only (plus RestoreAuto from the watchdog / crash handler), so they need not be thread-safe.
    /// </summary>
    public interface IFanBackend : IDisposable
    {
        /// <summary>Shown in the UI, e.g. "联想 BIOS WMI（LENOVO_OTHER_METHOD）".</summary>
        string Name { get; }
        List<FanInfo> Fans { get; }
        /// <summary>false: only EC auto and full speed (no RPM targets, no curves).</summary>
        bool CanSetSpeed { get; }
        /// <summary>Re-write unchanged targets this often, in case the EC drops / overwrites them.</summary>
        int ReassertSeconds { get; }
        /// <summary>°C; throws when the temperature cannot be read.</summary>
        int ReadCpuTemp();
        /// <summary>Measured RPM, or -1 when this backend cannot measure it.</summary>
        int ReadFanRpm(FanInfo fan);
        /// <summary>FanTarget.Auto, FanTarget.Stop, or a target in the fan's Unit within [LowestStable or Min, Max].</summary>
        void WriteFan(FanInfo fan, int target);
        /// <summary>Hand every fan back to EC automatic control. Must not throw.</summary>
        void RestoreAuto();
    }

    public static class FanBackends
    {
        /// <summary>
        /// Pick the first backend that works on this machine: Lenovo WMI (ThinkBook+ 2024/2025 and other models with
        /// LENOVO_FAN_TEST_DATA), then the EC register profiles for known older models (needs PawnIO), then the
        /// EnergyDrv full-speed switch. Throws NotSupportedException with the reasons when none works.
        /// </summary>
        public static IFanBackend Open(string family, out string notes)
        {
            var reasons = new List<string>();
            IFanBackend b;
            if ((b = Try(() => LenovoWmi.Open(), "联想 BIOS WMI", reasons)) != null) { notes = string.Join("\n", reasons); return b; }
            if ((b = Try(() => EcBackend.Open(family), "EC 寄存器", reasons)) != null) { notes = string.Join("\n", reasons); return b; }
            if ((b = Try(() => EnergyDrvBackend.Open(), "EnergyDrv", reasons)) != null) { notes = string.Join("\n", reasons); return b; }
            throw new NotSupportedException(string.Join("\n", reasons));
        }

        private static IFanBackend Try(Func<IFanBackend> open, string name, List<string> reasons)
        {
            try
            {
                var b = open();
                if (b != null) return b;
                reasons.Add(name + "：不适用");
            }
            catch (Exception ex) { reasons.Add(name + "：" + ex.Message); }
            return null;
        }
    }
}
