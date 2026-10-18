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
        public static bool ModelVerified;
        private static LenovoWmi _wmi;
        private static List<FanInfo> _fans;

        // SystemFamily prefixes of machines this has been tested on: ThinkBook 14+ 2025.
        private static readonly string[] VerifiedFamilies = { "ThinkBook 14 G7+" };

        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--watchdog") return Watchdog(args[1]);

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
                    _wmi = new LenovoWmi();
                    _wmi.ReadCpuTemp();
                    _fans = LenovoWmi.DiscoverFans(_wmi);
                    if (_fans.Count == 0) throw new NotSupportedException("没有找到可控制的风扇。");
                }
                catch (Exception ex)
                {
                    MessageBox.Show("无法访问联想 BIOS WMI 风扇接口：\n\n" + ex.Message +
                        "\n\n请确认以管理员身份运行，且本机支持 LENOVO_OTHER_METHOD（root\\WMI）。\n本机：" + ModelName, MainForm.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }

                StartWatchdog();
                var settings = Settings.Load(_fans);
                var controller = new FanController(_wmi, _fans, settings);
                bool minimized = Array.IndexOf(args, "--minimized") >= 0;
                var form = new MainForm(_fans, settings, controller, minimized);
                controller.Start();
                Application.Run(form);
                _wmi.Dispose();
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
            using (var wmi = new LenovoWmi())
                FanController.RestoreAuto(wmi, LenovoWmi.DiscoverFans(wmi));
            return 0;
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
                        string family = Convert.ToString(o["SystemFamily"]).Trim();
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
            if (_wmi != null && _fans != null) FanController.RestoreAuto(_wmi, _fans);
            MessageBox.Show("程序发生错误，风扇已交还 EC 自动控制。\n\n" + (ex != null ? ex.ToString() : ""), MainForm.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Environment.Exit(2);
        }
    }
}
