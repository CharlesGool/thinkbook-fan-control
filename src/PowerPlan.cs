using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ThinkBookFanControl
{
    /// <summary>Windows 11 power mode (the "power mode" slider overlay), simplified to the three user-visible choices.</summary>
    public enum PowerMode { Efficiency = 0, Balanced = 1, Performance = 2 }

    /// <summary>
    /// Windows power plan access (powrprof.dll): per-core-class maximum frequency of the active scheme, the active
    /// power-mode overlay and AC / battery. Frequencies are in MHz, 0 = no limit.
    /// </summary>
    public static class PowerPlan
    {
        public static readonly Guid SubProcessor = new Guid("54533251-82be-4824-96c1-47b60b740d00");
        /// <summary>PROCFREQMAX: maximum frequency for efficiency class 0 (on hybrid CPUs the E-cores).</summary>
        public static readonly Guid FreqMaxClass0 = new Guid("75b0ae3f-bce0-45a7-8c89-c9611c25e100");
        /// <summary>PROCFREQMAX1: maximum frequency for efficiency class 1 (on hybrid CPUs the P-cores).</summary>
        public static readonly Guid FreqMaxClass1 = new Guid("75b0ae3f-bce0-45a7-8c89-c9611c25e101");

        private static readonly Guid OverlayEfficiency = new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a");
        private static readonly Guid OverlayBetterPerformance = new Guid("3af9b8d9-7c97-431d-ad78-34a8bfea439f");
        private static readonly Guid OverlayBestPerformance = new Guid("ded574b5-45a0-4f42-8737-46345c09c238");

        public static string ModeName(PowerMode m)
        {
            return m == PowerMode.Efficiency ? "最佳能效" : m == PowerMode.Performance ? "最佳性能" : "平衡";
        }

        public static bool OnAc
        {
            get { return System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus != System.Windows.Forms.PowerLineStatus.Offline; }
        }

        /// <summary>The power mode the user picked (Settings → 电源 → 电源模式), Balanced when unknown.</summary>
        public static PowerMode CurrentMode
        {
            get
            {
                Guid g;
                try { if (PowerGetEffectiveOverlayScheme(out g) != 0) return PowerMode.Balanced; }
                catch (EntryPointNotFoundException) { return PowerMode.Balanced; }
                if (g == OverlayEfficiency) return PowerMode.Efficiency;
                if (g == OverlayBestPerformance || g == OverlayBetterPerformance) return PowerMode.Performance;
                return PowerMode.Balanced;
            }
        }

        public static Guid ActiveScheme
        {
            get
            {
                IntPtr p;
                uint err = PowerGetActiveScheme(IntPtr.Zero, out p);
                if (err != 0) throw new System.ComponentModel.Win32Exception((int)err);
                try { return (Guid)Marshal.PtrToStructure(p, typeof(Guid)); }
                finally { LocalFree(p); }
            }
        }

        public static uint Read(Guid scheme, Guid setting, bool ac)
        {
            uint v; Guid sub = SubProcessor;
            uint err = ac ? PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out v)
                          : PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out v);
            if (err != 0) throw new System.ComponentModel.Win32Exception((int)err);
            return v;
        }

        public static void Write(Guid scheme, Guid setting, bool ac, uint value)
        {
            Guid sub = SubProcessor;
            uint err = ac ? PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, value)
                          : PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, value);
            if (err != 0) throw new System.ComponentModel.Win32Exception((int)err);
        }

        /// <summary>Re-apply the scheme so written values take effect.</summary>
        public static void Apply(Guid scheme)
        {
            uint err = PowerSetActiveScheme(IntPtr.Zero, ref scheme);
            if (err != 0) throw new System.ComponentModel.Win32Exception((int)err);
        }

        /// <summary>PERFBOOSTMODE (0 = turbo off, 2 = aggressive, 6 = efficient aggressive at guaranteed, ...).</summary>
        public static readonly Guid BoostMode = new Guid("be337238-0d82-4146-a960-4f3749d470c7");
        public static readonly Guid BalancedScheme = new Guid("381b4222-f694-41f0-9685-ff5bb260df2e");

        /// <summary>
        /// Where the values for a power mode live: the overlays override the base scheme for every setting they define
        /// (Windows ships "best power efficiency" with a 3000 MHz battery cap), so each mode is written separately.
        /// </summary>
        public static Guid SchemeOf(PowerMode m, Guid active)
        {
            return m == PowerMode.Efficiency ? OverlayEfficiency : m == PowerMode.Performance ? OverlayBestPerformance : active;
        }


        [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);
        [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);
        [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroup, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerReadDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroup, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerWriteACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroup, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerWriteDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroup, ref Guid setting, uint value);
        // Undocumented but stable since Windows 10 1709 (used by the taskbar power slider).
        [DllImport("powrprof.dll")] private static extern uint PowerGetEffectiveOverlayScheme(out Guid effectiveOverlayGuid);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr p);
    }

    /// <summary>
    /// Keeps per-core-class maximum frequencies in the Windows power settings: a table of 12 values, indexed by
    /// Index(mode, battery, eCore), written into the base scheme (Balanced) and the two power-mode overlays, so Windows
    /// itself switches between them when the power source or mode changes. Values somebody else (Lenovo's process
    /// management service) writes back are corrected on the next Enforce. Originals are restored by Restore.
    /// Controller thread only.
    /// </summary>
    public sealed class FreqLimiter
    {
        private sealed class Saved { public Guid Scheme, Setting; public bool Ac; public uint Value; }
        private readonly Dictionary<string, Saved> _original = new Dictionary<string, Saved>();

        public static int Index(PowerMode m, bool battery, bool eCore) { return (int)m * 4 + (battery ? 2 : 0) + (eCore ? 1 : 0); }

        /// <summary>The 12 values as currently configured (to start the table from what the system does now).</summary>
        public static int[] ReadTable()
        {
            var t = new int[12];
            Guid active = PowerPlan.ActiveScheme;
            foreach (PowerMode m in new[] { PowerMode.Efficiency, PowerMode.Balanced, PowerMode.Performance })
                for (int b = 0; b < 2; b++)
                    for (int e = 0; e < 2; e++)
                        t[Index(m, b == 1, e == 1)] = (int)ReadOr(PowerPlan.SchemeOf(m, active), e == 1 ? PowerPlan.FreqMaxClass0 : PowerPlan.FreqMaxClass1, b == 0, 0);
            return t;
        }

        private static uint ReadOr(Guid scheme, Guid setting, bool ac, uint fallback)
        {
            try { return PowerPlan.Read(scheme, setting, ac); } catch (Exception) { return fallback; }
        }

        /// <summary>
        /// Write every value that differs (table entries &lt; 0 are left alone); with dcTurbo also copy the AC turbo mode
        /// to battery. Returns the number of values written; the scheme is re-applied when that is &gt; 0.
        /// </summary>
        public int Enforce(int[] table, bool dcTurbo)
        {
            Guid active = PowerPlan.ActiveScheme;
            int written = 0;
            foreach (PowerMode m in new[] { PowerMode.Efficiency, PowerMode.Balanced, PowerMode.Performance })
            {
                Guid scheme = PowerPlan.SchemeOf(m, active);
                for (int b = 0; b < 2; b++)
                    for (int e = 0; e < 2; e++)
                    {
                        int v = table[Index(m, b == 1, e == 1)];
                        if (v >= 0) written += Ensure(scheme, e == 1 ? PowerPlan.FreqMaxClass0 : PowerPlan.FreqMaxClass1, b == 0, (uint)v);
                    }
                if (dcTurbo)
                {
                    uint ac = ReadOr(scheme, PowerPlan.BoostMode, true, 0);
                    written += Ensure(scheme, PowerPlan.BoostMode, false, ac != 0 ? ac : 2);
                }
            }
            if (written > 0) PowerPlan.Apply(active);
            return written;
        }

        private int Ensure(Guid scheme, Guid setting, bool ac, uint value)
        {
            uint cur;
            try { cur = PowerPlan.Read(scheme, setting, ac); } catch (Exception) { cur = uint.MaxValue; }
            if (cur == value) return 0;
            string key = scheme + "|" + setting + "|" + ac;
            if (!_original.ContainsKey(key) && cur != uint.MaxValue) _original[key] = new Saved { Scheme = scheme, Setting = setting, Ac = ac, Value = cur };
            PowerPlan.Write(scheme, setting, ac, value);
            return 1;
        }

        public bool HasChanges { get { return _original.Count > 0; } }

        /// <summary>Put back every value we changed. Throws on the first failure (the rest stay saved for a retry).</summary>
        public void Restore() { Restore(Guid.Empty); }

        /// <summary>Put back the values of one setting only (Guid.Empty = all).</summary>
        public void Restore(Guid setting)
        {
            bool any = false;
            foreach (var kv in new List<KeyValuePair<string, Saved>>(_original))
            {
                if (setting != Guid.Empty && kv.Value.Setting != setting) continue;
                PowerPlan.Write(kv.Value.Scheme, kv.Value.Setting, kv.Value.Ac, kv.Value.Value);
                _original.Remove(kv.Key);
                any = true;
            }
            if (any) PowerPlan.Apply(PowerPlan.ActiveScheme);
        }
    }

    /// <summary>
    /// Signals as soon as any power setting is written (registry change under ...\Control\Power\User\PowerSchemes),
    /// so values another program writes (Intel IPF raises the frequency caps for a few seconds after every app launch)
    /// can be put back within milliseconds instead of at the next poll. Runs its own thread; Changed is raised there.
    /// </summary>
    public sealed class PowerSettingsWatcher : IDisposable
    {
        private const string SchemesPath = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";
        private const int RegNotifyChangeLastSet = 4;
        private readonly Microsoft.Win32.RegistryKey _key;
        private readonly System.Threading.AutoResetEvent _event = new System.Threading.AutoResetEvent(false);
        private readonly System.Threading.Thread _thread;
        private volatile bool _stop;

        public event Action Changed;

        public PowerSettingsWatcher()
        {
            _key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(SchemesPath, false);
            if (_key == null) throw new InvalidOperationException("找不到电源方案注册表");
            _thread = new System.Threading.Thread(Loop) { IsBackground = true, Name = "PowerSettingsWatcher" };
            _thread.Start();
        }

        private void Loop()
        {
            while (!_stop)
            {
                // One-shot: re-arm after every signal.
                int err = RegNotifyChangeKeyValue(_key.Handle, true, RegNotifyChangeLastSet, _event.SafeWaitHandle, true);
                if (err != 0) return;
                _event.WaitOne();
                if (_stop) return;
                var h = Changed;
                if (h != null) h();
                System.Threading.Thread.Sleep(50); // the power service writes several values at once: let it finish
            }
        }

        public void Dispose()
        {
            _stop = true;
            _event.Set();
            _key.Close();
        }

        [DllImport("advapi32.dll")]
        private static extern int RegNotifyChangeKeyValue(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, bool watchSubtree, int filter, Microsoft.Win32.SafeHandles.SafeWaitHandle ev, bool asynchronous);
    }

    /// <summary>
    /// Lenovo Process Management service (LNVDispatcherService) policy: with Policy_DisablePPMCtrl = 0 it rewrites the
    /// processor frequency caps / EPP / turbo on its own schedule (e.g. per battery level). Setting it to 1 and restarting
    /// the service stops that. It is a system setting: it stays until changed back.
    /// </summary>
    public static class LenovoPpm
    {
        private const string KeyPath = @"SYSTEM\CurrentControlSet\Services\LenovoProcessManagement\Performance\PowerSlider";
        private const string ValueName = "Policy_DisablePPMCtrl";
        public const string ServiceName = "LenovoProcessManagement";

        /// <summary>Current policy value, -1 when the service / key is not present.</summary>
        public static int Get()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(KeyPath))
                {
                    object v = k == null ? null : k.GetValue(ValueName);
                    return v is int ? (int)v : -1;
                }
            }
            catch (Exception) { return -1; }
        }

        /// <summary>Write the policy and restart the service so it is read again (takes a few seconds).</summary>
        public static void Set(int value)
        {
            using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(KeyPath, true))
            {
                if (k == null) throw new InvalidOperationException("没有找到联想进程管理服务的设置");
                k.SetValue(ValueName, value, Microsoft.Win32.RegistryValueKind.DWord);
            }
            using (var sc = new System.ServiceProcess.ServiceController(ServiceName))
            {
                if (sc.Status != System.ServiceProcess.ServiceControllerStatus.Stopped)
                {
                    sc.Stop();
                    sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                }
                sc.Start();
                sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
            }
        }
    }
}
