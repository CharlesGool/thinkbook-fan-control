using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace ThinkBookFanControl
{
    public static class Program
    {
        public static Icon AppIcon;
        public static string ModelName = "未知机型";
        public static string Family = "";
        public static bool ModelVerified;
        private static IFanBackend _backend;

        // SystemFamily prefixes of machines this has been tested on: ThinkBook 14+ 2025.
        private static readonly string[] VerifiedFamilies = { "ThinkBook 14 G7+" };

        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--watchdog") return Watchdog(args[1]);
            if (args.Length == 2 && args[0] == "--diag") return Diagnose(args[1]);
            int after = Array.IndexOf(args, "--after"), oldPid;
            if (after >= 0 && after + 1 < args.Length && int.TryParse(args[after + 1], out oldPid))
            {
                // Restarted by the previous instance (e.g. after installing PawnIO): let it exit first.
                try { using (var old = System.Diagnostics.Process.GetProcessById(oldPid)) old.WaitForExit(15000); }
                catch (ArgumentException) { }
            }

            bool created;
            using (var mutex = new Mutex(true, "ThinkBookFanControlMutex", out created))
            {
                if (!created)
                {
                    MessageBox.Show(MainForm.AppName + " 已经在运行（见系统托盘）。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                LoadIcons();
                DetectModel();

                // Whatever happens, never leave the fans pinned at a fixed speed.
                Application.ThreadException += (s, e) => Crash(e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Crash(e.ExceptionObject as Exception);

                try
                {
                    string notes;
                    _backend = FanBackends.Open(Family, out notes);
                }
                catch (Exception ex)
                {
                    if (EcBackend.IsKnownModel(Family) && PawnIoSetup.NeedsInstall &&
                        MessageBox.Show("本机型（" + ModelName + "）需要 PawnIO 驱动才能控制风扇。\n\n现在安装程序内置的 PawnIO " + PawnIoSetup.BundledVersion +
                            "（pawnio.eu 官方签名驱动）吗？", MainForm.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    {
                        try
                        {
                            PawnIoSetup.Install();
                            System.Diagnostics.Process.Start(Application.ExecutablePath, "--after " + System.Diagnostics.Process.GetCurrentProcess().Id);
                            return 0;
                        }
                        catch (Exception ix) { MessageBox.Show("安装 PawnIO 失败：" + ix.Message, MainForm.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
                    }
                    MessageBox.Show("没有找到可用的风扇控制接口：\n\n" + ex.Message +
                        "\n\n请确认以管理员身份运行。\n本机：" + ModelName, MainForm.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }

                StartWatchdog();
                var fans = _backend.Fans;
                var settings = Settings.Load(fans);
                var controller = new FanController(_backend, settings);
                bool minimized = Array.IndexOf(args, "--minimized") >= 0;
                var form = new MainForm(fans, settings, controller, minimized);
                controller.Start();
                Application.Run(form);
                _backend.Dispose();
                return 0;
            }
        }

        /// <summary>
        /// Second copy of the exe that waits for the main process to exit (normally, crash or killed from
        /// Task Manager) and then hands all fans back to EC auto control, so a fan is never left stopped.
        /// </summary>
        private static int Watchdog(string pidArg)
        {
            int pid;
            if (!int.TryParse(pidArg, out pid)) return 1;
            try
            {
                using (var parent = System.Diagnostics.Process.GetProcessById(pid)) parent.WaitForExit();
            }
            catch (ArgumentException) { } // already gone
            DetectModel();
            try
            {
                string notes;
                using (var backend = FanBackends.Open(Family, out notes)) backend.RestoreAuto();
            }
            catch (Exception) { return 1; } // nothing to restore through
            return 0;
        }

        /// <summary>
        /// Read-only report of what this machine supports (for compatibility feedback): `--diag report.txt`.
        /// Opens the backend and samples sensors twice; never writes a fan target or power limit.
        /// </summary>
        private static int Diagnose(string path)
        {
            var sb = new System.Text.StringBuilder();
            DetectModel();
            sb.AppendLine(MainForm.AppName + " 诊断信息  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("机型：" + ModelName + "（SystemFamily = " + Family + "）");
            sb.AppendLine("PawnIO：" + (PawnIo.InstalledVersion ?? "未安装"));
            IFanBackend backend = null;
            try
            {
                string notes;
                backend = FanBackends.Open(Family, out notes);
                sb.AppendLine("风扇接口：" + backend.Name);
                if (notes.Length > 0) sb.AppendLine("  跳过：" + notes.Replace("\n", "\n        "));
                try { sb.AppendLine("  CPU 温度：" + backend.ReadCpuTemp() + " °C"); } catch (Exception ex) { sb.AppendLine("  CPU 温度：读取失败 " + ex.Message); }
                foreach (var f in backend.Fans)
                {
                    string rpm;
                    try { int v = backend.ReadFanRpm(f); rpm = v >= 0 ? v + " RPM" : "无转速传感器"; } catch (Exception ex) { rpm = "读取失败 " + ex.Message; }
                    sb.AppendLine(string.Format("  {0}（ID {1}）：范围 {2}–{3}，步进 {4}，最低稳定 {5}；当前 {6}",
                        f.Name, f.Id, f.MinRpm, f.Format(f.MaxRpm), f.Step, f.LowestStable, rpm));
                }
            }
            catch (Exception ex) { sb.AppendLine("风扇接口：无可用接口\n  " + ex.Message.Replace("\n", "\n  ")); }

            var wmi = backend as LenovoWmi;
            LenovoWmi raw = wmi == null ? LenovoWmi.OpenRaw() : null;
            using (var cpu = new CpuMonitor(wmi ?? raw))
            {
                var r = new Reading { Rpm = new int[0], Target = new int[0] };
                cpu.Sample(r);
                Thread.Sleep(1100);
                cpu.Sample(r);
                sb.AppendLine(string.Format("CPU：频率 {0:0} MHz，占用 {1:0.0} %，封装功耗 {2:0.0} W", r.CpuMhz, r.CpuUsage, r.CpuPowerW));
                if (cpu.PowerUnavailable != null) sb.AppendLine("  功耗读取不可用：" + cpu.PowerUnavailable);
                sb.AppendLine(string.Format("  PL1 / PL2：{0:0.#} / {1:0.#} W（来源：{2}）", r.Pl1W, r.Pl2W, cpu.LimitSource ?? "无"));
                if (!double.IsNaN(r.MmioPl1W)) sb.AppendLine(string.Format("  MMIO PL1 / PL2：{0:0.#} / {1:0.#} W", r.MmioPl1W, r.MmioPl2W));
                sb.AppendLine("  可设置 PL1 / PL2：" + (cpu.CanWriteLimits ? "是" : "否（" + cpu.LimitsUnavailable + "）"));
                if (!double.IsNaN(r.Pl1TauS)) sb.AppendLine(string.Format("  PL1 Tau：{0:0.###} 秒（可设置：{1}）", r.Pl1TauS, cpu.CanSetTau ? "是" : "否"));
            }
            try
            {
                sb.AppendLine("电源：" + (PowerPlan.OnAc ? "插电" : "电池") + " · 电源模式 " + PowerPlan.ModeName(PowerPlan.CurrentMode) + " · 电源计划 " + PowerPlan.ActiveScheme);
                var t = FreqLimiter.ReadTable();
                foreach (PowerMode m in new[] { PowerMode.Efficiency, PowerMode.Balanced, PowerMode.Performance })
                    sb.AppendLine(string.Format("  {0}：插电 {1} MHz，电池 {2} MHz（PROCFREQMAX1，0 = 不限）", PowerPlan.ModeName(m),
                        t[FreqLimiter.Index(m, false)], t[FreqLimiter.Index(m, true)]));
                int ppm = LenovoPpm.Get();
                sb.AppendLine("  联想进程管理服务 Policy_DisablePPMCtrl：" + (ppm < 0 ? "无此服务" : ppm.ToString()));
                int dtt = DttComponent.State;
                sb.AppendLine("  Intel DTT 扩展组件：" + (dtt < 0 ? "无" : dtt == 1 ? "已禁用" : "已启用"));
                foreach (string svc in new[] { LenovoPpm.ServiceName, "ipfsvc", "dptftcs" })
                {
                    int start = ServiceSwitch.StartType(svc);
                    if (start < 0) continue;
                    string state;
                    try { using (var sc = new System.ServiceProcess.ServiceController(svc)) state = sc.Status.ToString(); }
                    catch (Exception) { state = "?"; }
                    sb.AppendLine("  服务 " + svc + "：" + state + "，启动类型 " + start + "（2 自动，3 手动，4 禁用）");
                }
            }
            catch (Exception ex) { sb.AppendLine("电源设置读取失败：" + ex.Message); }
            if (raw != null) raw.Dispose();
            if (backend != null) backend.Dispose();
            try { System.IO.File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(true)); return 0; }
            catch (Exception) { return 1; }
        }

        private static void StartWatchdog()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath,
                    "--watchdog " + System.Diagnostics.Process.GetCurrentProcess().Id) { UseShellExecute = false, CreateNoWindow = true };
                System.Diagnostics.Process.Start(psi).Dispose();
            }
            catch (Exception) { }
        }

        private static void LoadIcons()
        {
            try
            {
                using (var s = typeof(Program).Assembly.GetManifestResourceStream("app.ico")) AppIcon = new Icon(s);
            }
            catch (Exception) { AppIcon = SystemIcons.Application; }
        }

        private static void DetectModel()
        {
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher("SELECT Manufacturer, SystemFamily, Model FROM Win32_ComputerSystem"))
                    foreach (System.Management.ManagementObject o in searcher.Get())
                    {
                        string family = Family = Convert.ToString(o["SystemFamily"]).Trim();
                        string model = Convert.ToString(o["Model"]).Trim();
                        ModelName = family.Length > 0 ? family + (model.Length > 0 ? " (" + model + ")" : "") : model;
                        foreach (var v in VerifiedFamilies)
                            if (family.StartsWith(v, StringComparison.OrdinalIgnoreCase)) ModelVerified = true;
                        break;
                    }
            }
            catch (Exception) { }
        }

        private static void Crash(Exception ex)
        {
            if (_backend != null) _backend.RestoreAuto();
            MessageBox.Show("程序发生错误，风扇已交还 EC 自动控制。\n\n" + (ex != null ? ex.ToString() : ""), MainForm.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Environment.Exit(2);
        }
    }
}
