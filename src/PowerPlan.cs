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

        /// <summary>
        /// Switch the Windows power mode of the current power source, like the taskbar slider. Lenovo's Fn+Q does this
        /// through its process management service; with that service off the tray menu / tbfc use this instead.
        /// </summary>
        public static void SetMode(PowerMode m)
        {
            uint err = PowerSetActiveOverlayScheme(m == PowerMode.Efficiency ? OverlayEfficiency : m == PowerMode.Performance ? OverlayBestPerformance : Guid.Empty);
            if (err != 0) throw new System.ComponentModel.Win32Exception((int)err);
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

        /// <summary>PERFEPP / PERFEPP1: energy performance preference for core class 0 / 1, 0 = performance .. 100 = efficiency.</summary>
        public static readonly Guid Epp0 = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6863");
        public static readonly Guid Epp1 = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6864");

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
        [DllImport("powrprof.dll")] private static extern uint PowerSetActiveOverlayScheme(Guid overlaySchemeGuid);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr p);
    }

    /// <summary>
    /// Keeps a CPU maximum frequency per power mode x power source in the Windows power settings: 6 values indexed by
    /// Index(mode, battery), written into the base scheme (Balanced) and the two power-mode overlays, so Windows itself
    /// switches between them when the power source or mode changes.
    /// Each value goes into both PROCFREQMAX1 and PROCFREQMAX: measured on the ThinkBook 14+ 2025 (APERF / MPERF per
    /// core), PROCFREQMAX1 caps every core (E cores land ~100 MHz below it) and PROCFREQMAX alone has no effect, so a
    /// separate E core cap cannot be had; on CPUs without core classes PROCFREQMAX is the one that counts.
    /// Originals are restored by Restore. Controller thread only.
    /// </summary>
    public sealed class FreqLimiter
    {
        private sealed class Saved { public Guid Scheme, Setting; public bool Ac; public uint Value; }
        private readonly Dictionary<string, Saved> _original = new Dictionary<string, Saved>();
        private static readonly PowerMode[] Modes = { PowerMode.Efficiency, PowerMode.Balanced, PowerMode.Performance };

        public const int Cells = 6;
        public static int Index(PowerMode m, bool battery) { return (int)m * 2 + (battery ? 1 : 0); }

        /// <summary>The 6 caps as currently configured (PROCFREQMAX1, the one that caps the cores).</summary>
        public static int[] ReadTable() { return ReadTable(PowerPlan.FreqMaxClass1); }

        /// <summary>The 6 values of a processor setting as currently configured (mode x power source).</summary>
        public static int[] ReadTable(Guid setting)
        {
            var t = new int[Cells];
            Guid active = PowerPlan.ActiveScheme;
            foreach (PowerMode m in Modes)
                for (int b = 0; b < 2; b++)
                    t[Index(m, b == 1)] = (int)ReadOr(PowerPlan.SchemeOf(m, active), setting, b == 0, 0);
            return t;
        }

        /// <summary>
        /// Write `table` into every one of `settings` (same layout as the caps, entries &lt; 0 left alone), e.g. both EPP
        /// classes. Returns the number of values written; the scheme is re-applied when that is &gt; 0.
        /// </summary>
        public int EnforceTable(Guid[] settings, int[] table)
        {
            Guid active = PowerPlan.ActiveScheme;
            int written = 0;
            foreach (PowerMode m in Modes)
            {
                Guid scheme = PowerPlan.SchemeOf(m, active);
                for (int b = 0; b < 2; b++)
                {
                    int v = table[Index(m, b == 1)];
                    if (v < 0) continue;
                    foreach (Guid setting in settings) written += Ensure(scheme, setting, b == 0, (uint)v);
                }
            }
            if (written > 0) PowerPlan.Apply(active);
            return written;
        }

        private static uint ReadOr(Guid scheme, Guid setting, bool ac, uint fallback)
        {
            try { return PowerPlan.Read(scheme, setting, ac); } catch (Exception) { return fallback; }
        }

        /// <summary>
        /// Everything Enforce would write, read without writing: null when it all matches; otherwise whether some cap is
        /// currently higher than ours (somebody is boosting) in `raised`.
        /// </summary>
        public string Mismatch(int[] table, bool dcTurbo, out bool raised)
        {
            raised = false;
            Guid active = PowerPlan.ActiveScheme;
            var sb = new System.Text.StringBuilder();
            foreach (PowerMode m in Modes)
            {
                Guid scheme = PowerPlan.SchemeOf(m, active);
                for (int b = 0; b < 2; b++)
                {
                    int v = table[Index(m, b == 1)];
                    if (v < 0) continue;
                    foreach (Guid setting in new[] { PowerPlan.FreqMaxClass1, PowerPlan.FreqMaxClass0 })
                    {
                        uint cur = ReadOr(scheme, setting, b == 0, uint.MaxValue);
                        if (cur == (uint)v) continue;
                        sb.Append(PowerPlan.ModeName(m)).Append(b == 0 ? "插电" : "电池").Append('=').Append(cur).Append(' ');
                        if (v > 0 && (cur == 0 || cur > (uint)v)) raised = true;
                    }
                }
                if (dcTurbo && ReadOr(scheme, PowerPlan.BoostMode, false, uint.MaxValue) == 0) sb.Append("电池睿频关 ");
            }
            return sb.Length == 0 ? null : sb.ToString();
        }

        /// <summary>
        /// Write every value that differs (table entries &lt; 0 are left alone); with dcTurbo also copy the AC turbo mode
        /// to battery. Returns the number of values written; the scheme is re-applied when that is &gt; 0.
        /// </summary>
        public int Enforce(int[] table, bool dcTurbo)
        {
            Guid active = PowerPlan.ActiveScheme;
            int written = 0;
            foreach (PowerMode m in Modes)
            {
                Guid scheme = PowerPlan.SchemeOf(m, active);
                for (int b = 0; b < 2; b++)
                {
                    int v = table[Index(m, b == 1)];
                    if (v < 0) continue;
                    written += Ensure(scheme, PowerPlan.FreqMaxClass1, b == 0, (uint)v);
                    written += Ensure(scheme, PowerPlan.FreqMaxClass0, b == 0, (uint)v);
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
    /// so values another program writes (Lenovo's process management service raises the caps after load changes / app launches)
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
            // One-shot notifications: re-arm right after each signal, before reporting it, so a write that follows
            // closely (the caps of both core classes are written back to back) is not missed.
            if (RegNotifyChangeKeyValue(_key.Handle, true, RegNotifyChangeLastSet, _event.SafeWaitHandle, true) != 0) return;
            while (!_stop)
            {
                _event.WaitOne();
                if (_stop) return;
                if (RegNotifyChangeKeyValue(_key.Handle, true, RegNotifyChangeLastSet, _event.SafeWaitHandle, true) != 0) return;
                var h = Changed;
                if (h != null) h();
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
    /// Stop a service and set it to disabled, or put its start type back and start it again (through the service
    /// control manager, so it takes effect at once). Used for Lenovo's process management service: measured on the
    /// ThinkBook 14+ 2025, even with Policy_DisablePPMCtrl = 1 it writes 4.8 / 5.0 GHz caps (sometimes 0 = unlimited)
    /// 1–2 s after load changes and on app launches. They are put back within 30–220 ms but still show up as short
    /// frequency spikes. With the service stopped there were none; stopping Intel IPF (ipfsvc) and DTT (dptftcs)
    /// instead changed nothing. System setting: stays until changed back.
    /// </summary>
    public static class ServiceSwitch
    {
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenSCManager(string machine, string database, uint access);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenService(IntPtr scm, string name, uint access);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool ChangeServiceConfig(IntPtr service, uint type, uint start, uint errorControl, string path,
            string group, IntPtr tag, string dependencies, string user, string password, string displayName);
        [DllImport("advapi32.dll")]
        private static extern bool CloseServiceHandle(IntPtr handle);

        private const uint NoChange = 0xFFFFFFFF, ScManagerConnect = 0x1, ServiceChangeConfig = 0x2;
        public const int Disabled = 4;

        /// <summary>Start type (2 = automatic, 3 = manual, 4 = disabled), -1 when the service does not exist.</summary>
        public static int StartType(string name)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name))
                {
                    object v = k == null ? null : k.GetValue("Start");
                    return v is int ? (int)v : -1;
                }
            }
            catch (Exception) { return -1; }
        }

        /// <summary>Disable and stop the service.</summary>
        public static void Disable(string name)
        {
            SetStartType(name, Disabled);
            using (var sc = new System.ServiceProcess.ServiceController(name))
                if (sc.Status != System.ServiceProcess.ServiceControllerStatus.Stopped)
                {
                    sc.Stop();
                    sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                }
        }

        /// <summary>Put the start type back and start the service again (unless it was disabled before).</summary>
        public static void Restore(string name, int startType)
        {
            SetStartType(name, startType);
            if (startType == Disabled) return;
            using (var sc = new System.ServiceProcess.ServiceController(name))
                if (sc.Status != System.ServiceProcess.ServiceControllerStatus.Running)
                {
                    sc.Start();
                    sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
                }
        }

        private static void SetStartType(string name, int startType)
        {
            IntPtr scm = OpenSCManager(null, null, ScManagerConnect);
            if (scm == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            try
            {
                IntPtr svc = OpenService(scm, name, ServiceChangeConfig);
                if (svc == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
                try
                {
                    if (!ChangeServiceConfig(svc, NoChange, (uint)startType, NoChange, null, null, IntPtr.Zero, null, null, null, null))
                        throw new System.ComponentModel.Win32Exception();
                }
                finally { CloseServiceHandle(svc); }
            }
            finally { CloseServiceHandle(scm); }
        }
    }

    /// <summary>
    /// Intel DTT's device extension component (Device Manager: Software components → "Intel(R) Dynamic Tuning Technology
    /// Device Extension Component"). Measured on the ThinkBook 14+ 2025: Intel IPF's processor participant driver
    /// (WUDFHost with ipf_umdf2 / ipfsrv) applies DTT's processor power settings whenever the workload type changes:
    /// caps unlimited about 5 s into an all-core load, 5.0 / 4.8 GHz when it drops, EPP 25 each time. With this component
    /// disabled there were none in three rounds of that load; stopping the dptftcs / ipfsvc services changed nothing,
    /// and Windows refuses to disable the participant device itself. Disabled / enabled like Device Manager does
    /// (pnputil, persistent).
    /// </summary>
    public static class DttComponent
    {
        /// <summary>Device instance ID, null when the component is not present.</summary>
        public static string InstanceId
        {
            get
            {
                try
                {
                    using (var s = new System.Management.ManagementObjectSearcher("SELECT DeviceID FROM Win32_PnPEntity WHERE DeviceID LIKE '%DttExtComponent%'"))
                        foreach (System.Management.ManagementObject o in s.Get())
                            using (o) return (string)o["DeviceID"];
                }
                catch (Exception) { }
                return null;
            }
        }

        /// <summary>1 = disabled, 0 = enabled, -1 = not present.</summary>
        public static int State
        {
            get
            {
                try
                {
                    using (var s = new System.Management.ManagementObjectSearcher("SELECT ConfigManagerErrorCode FROM Win32_PnPEntity WHERE DeviceID LIKE '%DttExtComponent%'"))
                        foreach (System.Management.ManagementObject o in s.Get())
                            using (o) return Convert.ToInt32(o["ConfigManagerErrorCode"]) == 22 ? 1 : 0; // 22 = disabled
                }
                catch (Exception) { }
                return -1;
            }
        }

        /// <summary>Disable or enable it (takes a few seconds). Throws with pnputil's message when refused.</summary>
        public static void SetDisabled(bool disabled)
        {
            string id = InstanceId;
            if (id == null) throw new InvalidOperationException("没有找到 Intel DTT 扩展组件");
            var psi = new System.Diagnostics.ProcessStartInfo("pnputil.exe", (disabled ? "/disable-device \"" : "/enable-device \"") + id + "\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                StandardOutputEncoding = System.Text.Encoding.Default
            };
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(30000);
                if (p.ExitCode != 0 && p.ExitCode != 3010) // 3010: done, takes full effect after a restart
                    throw new InvalidOperationException("pnputil 返回 " + p.ExitCode + "：" + output.Trim());
            }
        }
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

        /// <summary>Write the policy and restart the service so it is read again (takes a few seconds; left stopped if it was).</summary>
        public static void Set(int value)
        {
            using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(KeyPath, true))
            {
                if (k == null) throw new InvalidOperationException("没有找到联想进程管理服务的设置");
                k.SetValue(ValueName, value, Microsoft.Win32.RegistryValueKind.DWord);
            }
            using (var sc = new System.ServiceProcess.ServiceController(ServiceName))
            {
                if (sc.Status == System.ServiceProcess.ServiceControllerStatus.Stopped) return;
                sc.Stop();
                sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                sc.Start();
                sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
            }
        }
    }
}
