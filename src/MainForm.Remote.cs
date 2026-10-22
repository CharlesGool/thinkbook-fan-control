using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows.Forms;

namespace ThinkBookFanControl
{
    /// <summary>
    /// Command-line control (tbfc.exe through RemoteServer). Commands run on the UI thread and go through the same
    /// settings / controls as the window, so the window always shows what a command changed.
    /// </summary>
    public partial class MainForm
    {
        private RemoteServer _remote;
        private Reading _lastReading;
        private CheckBox _remoteOn, _remoteLan;

        private const string RemoteHelp =
            "tbfc [--host 主机] [--port 端口] [--token 令牌] <命令>\n" +
            "  status [--json]                 当前温度、转速、模式、CPU、PL、频率上限\n" +
            "  mode quiet|auto|performance|max|fixed|curve\n" +
            "  fixed <转速> [风扇号]            设定固定转速并切到固定转速模式\n" +
            "  profile <曲线方案名>             切到该自定义曲线方案\n" +
            "  pl [on|off]                      开 / 关自定义 PL1 / PL2\n" +
            "  pl <PL1> <PL2> [Tau 秒]          设定并开启（W）\n" +
            "  freq [on|off]                    开 / 关频率上限，不带参数显示\n" +
            "  freq <efficiency|balanced|performance> <ac|dc> <平时 MHz> [全核满载 MHz]\n" +
            "                                   平时 0 = 不限制，全核满载 0 = 同平时\n" +
            "  epp [on|off]                     开 / 关性能偏好（EPP），不带参数显示\n" +
            "  epp <efficiency|balanced|performance> <ac|dc> <0-100>   0 = 性能优先，100 = 省电\n" +
            "  power [efficiency|balanced|performance]   切换 Windows 电源模式（代替 Fn+Q），不带参数显示\n" +
            "  boost on|off                     打开程序时允许系统临时加速\n" +
            "  dcturbo on|off                   电池供电时也允许睿频\n" +
            "  hwinfo on|off                    HWiNFO 自定义传感器\n" +
            "  quit                             交还 EC 自动控制并退出程序\n";

        /// <summary>(Re)start the control port from the settings; errors go to a message box or the status line.</summary>
        private void ApplyRemoteSettings(bool interactive)
        {
            if (_remote != null) { _remote.Dispose(); _remote = null; }
            bool on, lan; int port; string token;
            lock (_settings)
            {
                if (string.IsNullOrEmpty(_settings.RemoteToken)) { _settings.RemoteToken = RemoteServer.NewToken(); Changed(); }
                on = _settings.RemoteEnabled; lan = _settings.RemoteLan; port = _settings.RemotePort; token = _settings.RemoteToken;
            }
            if (!on) return;
            try { _remote = new RemoteServer(port, lan, token, RunRemoteFromNet); }
            catch (Exception ex)
            {
                string msg = "命令行控制端口 " + port + " 无法打开：" + ex.Message;
                if (interactive) MessageBox.Show(this, msg, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else { _statusLabel.ForeColor = System.Drawing.Color.Firebrick; _statusLabel.Text = msg; }
            }
        }

        private void SetRemote(bool on, bool lan)
        {
            if (lan && MessageBox.Show(this,
                    "将在所有网卡上监听端口 " + _settings.RemotePort + "，并添加 Windows 防火墙规则（仅专用 / 域网络）。\n" +
                    "同一局域网的电脑可以用 tbfc --host <本机地址> --token <令牌> 控制风扇和 CPU 功耗。\n\n确定要打开吗？",
                    Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            {
                _loading = true; _remoteLan.Checked = false; _loading = false;
                return;
            }
            bool wasLan;
            lock (_settings) { wasLan = _settings.RemoteLan; _settings.RemoteEnabled = on; _settings.RemoteLan = on && lan; }
            Changed();
            if (wasLan != (on && lan))
                try { RemoteServer.SetFirewall(on && lan, _settings.RemotePort, Application.ExecutablePath); } catch (Exception) { }
            _remoteLan.Enabled = on;
            ApplyRemoteSettings(true);
        }

        private string RunRemoteFromNet(string[] args)
        {
            string result = null; Exception error = null;
            Invoke(new Action(() => { try { result = RunRemote(args); } catch (Exception ex) { error = ex; } }));
            if (error != null) throw error;
            return result;
        }

        private string RunRemote(string[] a)
        {
            if (a.Length == 0) return RemoteHelp;
            string cmd = a[0].ToLowerInvariant();
            switch (cmd)
            {
                case "help": case "-h": case "--help": case "?": return RemoteHelp;
                case "status": return Array.IndexOf(a, "--json") > 0 ? StatusJson() : StatusText();
                case "mode": return CmdMode(Arg(a, 1, "模式"));
                case "fixed": return CmdFixed(a);
                case "profile": return CmdProfile(string.Join(" ", a, 1, a.Length - 1));
                case "pl": return CmdPl(a);
                case "freq": return CmdFreq(a);
                case "epp": return CmdEpp(a);
                case "power": case "powermode":
                    if (a.Length > 1) { PowerPlan.SetMode(ParsePowerMode(a[1])); _controller.Poke(); }
                    return "Windows 电源模式：" + PowerPlan.ModeName(PowerPlan.CurrentMode) + (PowerPlan.OnAc ? "（插电）" : "（电池）") + "\n";
                case "boost": return Toggle(a, "打开程序时允许系统临时加速", v => _settings.AllowBoost = v, _allowBoost);
                case "dcturbo": return Toggle(a, "电池供电时也允许睿频", v => _settings.DcTurbo = v, _dcTurbo);
                case "hwinfo": return Toggle(a, "HWiNFO 自定义传感器", v => _settings.HwInfoEnabled = v, _hwinfo);
                case "quit": case "exit": case "restore":
                    BeginInvoke(new Action(ExitApp));
                    return "正在把风扇交还 EC 自动控制并退出\n";
                default: throw new ArgumentException("未知命令「" + a[0] + "」，tbfc help 查看用法");
            }
        }

        private static string Arg(string[] a, int i, string what)
        {
            if (i >= a.Length) throw new ArgumentException("缺少参数：" + what);
            return a[i];
        }

        private static bool OnOff(string s)
        {
            switch (s.ToLowerInvariant())
            {
                case "on": case "1": case "true": case "开": return true;
                case "off": case "0": case "false": case "关": return false;
                default: throw new ArgumentException("应为 on 或 off：" + s);
            }
        }

        private string Toggle(string[] a, string name, Action<bool> set, CheckBox box)
        {
            bool on = OnOff(Arg(a, 1, "on / off"));
            lock (_settings) set(on);
            _loading = true; box.Checked = on; _loading = false;
            Changed();
            return name + "：" + (on ? "开" : "关") + "\n";
        }

        private string CmdMode(string name)
        {
            FanMode m;
            switch (name.ToLowerInvariant())
            {
                case "quiet": case "安静": m = FanMode.Quiet; break;
                case "auto": case "balanced": case "均衡": case "ec": m = FanMode.Auto; break;
                case "performance": case "perf": case "性能": m = FanMode.Performance; break;
                case "max": case "full": case "全速": m = FanMode.Max; break;
                case "fixed": case "固定": m = FanMode.Fixed; break;
                case "curve": case "曲线": m = FanMode.Curve; break;
                default: throw new ArgumentException("未知模式「" + name + "」：quiet / auto / performance / max / fixed / curve");
            }
            if (!_radios.ContainsKey(m)) throw new InvalidOperationException("本机接口不支持" + ModeName(m, false));
            _radios[m].Checked = true;
            return "模式：" + ModeName(m, true) + "\n";
        }

        private string CmdFixed(string[] a)
        {
            if (!_radios.ContainsKey(FanMode.Fixed)) throw new InvalidOperationException("本机接口不支持固定转速");
            int v = int.Parse(Arg(a, 1, "转速"), CultureInfo.InvariantCulture);
            int fan = a.Length > 2 ? int.Parse(a[2], CultureInfo.InvariantCulture) - 1 : 0;
            if (fan < 0 || fan >= _fans.Count) throw new ArgumentException("风扇号应为 1–" + _fans.Count);
            if (a.Length <= 2) { lock (_settings) _settings.LinkFixed = true; _loading = true; _linkFixed.Checked = true; _loading = false; }
            SetFixed(fan, v);
            _radios[FanMode.Fixed].Checked = true;
            var sb = new StringBuilder("固定转速：");
            lock (_settings) for (int i = 0; i < _fans.Count; i++) sb.Append(i > 0 ? "，" : "").Append(_fans[i].Name).Append(' ').Append(_fans[i].Format(_settings.FixedRpm[i]));
            return sb.Append('\n').ToString();
        }

        private string CmdProfile(string name)
        {
            if (name.Length == 0) throw new ArgumentException("缺少曲线方案名");
            int idx;
            lock (_settings) idx = _settings.Profiles.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
            {
                var names = new List<string>();
                lock (_settings) foreach (var p in _settings.Profiles) names.Add(p.Name);
                throw new ArgumentException("没有曲线方案「" + name + "」，现有：" + string.Join("、", names.ToArray()));
            }
            SelectProfile(idx);
            _radios[FanMode.Curve].Checked = true;
            return "自定义温度曲线：" + name + "\n";
        }

        private string CmdPl(string[] a)
        {
            var cpu = _controller.Cpu;
            if (a.Length == 1) return PlText() + "\n";
            if (!cpu.CanWriteLimits) throw new InvalidOperationException("不支持设置 PL1 / PL2：" + cpu.LimitsUnavailable);
            if (a.Length == 2)
            {
                bool on = OnOff(a[1]);
                lock (_settings) _settings.PowerLimitEnabled = on;
                _loading = true; _plOn.Checked = on; _loading = false;
                Changed();
                return "自定义 PL1 / PL2：" + (on ? "开" : "关（恢复接管前的值）") + "\n";
            }
            int pl1 = int.Parse(a[1], CultureInfo.InvariantCulture), pl2 = int.Parse(a[2], CultureInfo.InvariantCulture);
            cpu.ClampLimits(ref pl1, ref pl2);
            int tau = a.Length > 3 ? SnapTau(int.Parse(a[3], CultureInfo.InvariantCulture)) : -1;
            lock (_settings)
            {
                _settings.Pl1W = pl1; _settings.Pl2W = pl2;
                if (tau > 0 && cpu.CanSetTau) _settings.Pl1TauS = tau;
                _settings.PowerLimitEnabled = true;
            }
            _loading = true;
            _pl1.Value = pl1; _pl2.Value = pl2;
            if (tau > 0 && cpu.CanSetTau) SelectTau(tau);
            _plOn.Checked = true;
            _loading = false;
            Changed();
            return "自定义 PL1 / PL2：开，" + pl1 + " / " + pl2 + " W" + (tau > 0 && cpu.CanSetTau ? "，Tau " + tau + " 秒" : "") + "\n";
        }

        private static PowerMode ParsePowerMode(string s)
        {
            switch (s.ToLowerInvariant())
            {
                case "efficiency": case "eff": case "能效": case "最佳能效": return PowerMode.Efficiency;
                case "balanced": case "bal": case "平衡": return PowerMode.Balanced;
                case "performance": case "perf": case "性能": case "最佳性能": return PowerMode.Performance;
                default: throw new ArgumentException("电源模式应为 efficiency / balanced / performance：" + s);
            }
        }

        private string CmdFreq(string[] a)
        {
            if (a.Length == 1) return FreqText();
            if (a.Length == 2)
            {
                bool on = OnOff(a[1]);
                lock (_settings) _settings.FreqLimitEnabled = on;
                _loading = true; _freqOn.Checked = on; _loading = false;
                Changed();
                return "频率上限：" + (on ? "开" : "关（恢复原值）") + "\n";
            }
            PowerMode m = ParsePowerMode(a[1]);
            string src = Arg(a, 2, "ac / dc").ToLowerInvariant();
            bool battery = src == "dc" || src == "battery" || src == "电池";
            if (!battery && src != "ac" && src != "插电") throw new ArgumentException("应为 ac（插电）或 dc（电池）：" + a[2]);
            int mhz = ParseMhz(Arg(a, 3, "平时 MHz"));
            int multi = a.Length > 4 ? ParseMhz(a[4]) : -1;
            int idx = FreqLimiter.Index(m, battery);
            lock (_settings) { _settings.FreqMhz[idx] = mhz; if (multi >= 0) _settings.FreqMultiMhz[idx] = multi; else multi = _settings.FreqMultiMhz[idx]; }
            _loading = true; SelectFreq(_freqBoxes[idx], mhz); SelectFreq(_freqMultiBoxes[idx], multi); _loading = false;
            Changed();
            bool enabled; lock (_settings) enabled = _settings.FreqLimitEnabled;
            return PowerPlan.ModeName(m) + " · " + (battery ? "电池" : "插电") + "：平时 " + new FreqItem(mhz) + "，全核满载 " + new FreqItem(multi, true) +
                (enabled ? "" : "（频率上限当前是关的：tbfc freq on）") + "\n";
        }

        private static int ParseMhz(string v)
        {
            v = v.ToLowerInvariant();
            int mhz = v == "none" || v == "unlimited" || v == "不限制" || v == "same" ? 0 : int.Parse(v, CultureInfo.InvariantCulture);
            return Settings.Clamp(mhz, 0, Settings.FreqMaxMhz);
        }

        private string CmdEpp(string[] a)
        {
            if (a.Length == 1) return EppText();
            if (a.Length == 2)
            {
                bool on = OnOff(a[1]);
                lock (_settings) _settings.EppEnabled = on;
                _loading = true; _eppOn.Checked = on; _loading = false;
                Changed();
                return "性能偏好（EPP）：" + (on ? "开" : "关（恢复原值）") + "\n";
            }
            PowerMode m = ParsePowerMode(a[1]);
            string src = Arg(a, 2, "ac / dc").ToLowerInvariant();
            bool battery = src == "dc" || src == "battery" || src == "电池";
            if (!battery && src != "ac" && src != "插电") throw new ArgumentException("应为 ac（插电）或 dc（电池）：" + a[2]);
            int v = Settings.Clamp(int.Parse(Arg(a, 3, "0-100"), CultureInfo.InvariantCulture), 0, 100);
            int idx = FreqLimiter.Index(m, battery);
            lock (_settings) _settings.EppPct[idx] = v;
            _loading = true; SelectEpp(_eppBoxes[idx], v); _loading = false;
            Changed();
            bool enabled; lock (_settings) enabled = _settings.EppEnabled;
            return PowerPlan.ModeName(m) + " · " + (battery ? "电池" : "插电") + "：EPP " + v + (enabled ? "" : "（性能偏好当前是关的：tbfc epp on）") + "\n";
        }

        private string EppText()
        {
            var r = _lastReading;
            bool on; int[] t;
            lock (_settings) { on = _settings.EppEnabled; t = (int[])_settings.EppPct.Clone(); }
            var sb = new StringBuilder("性能偏好（EPP）：").Append(on ? "开" : "关").Append('\n');
            if (r != null && r.EppNow >= 0) sb.Append("  当前：").Append(r.OnAc ? "插电" : "电池").Append(" · ").Append(PowerPlan.ModeName(r.Mode)).Append(" → EPP ").Append(r.EppNow).Append('\n');
            foreach (PowerMode m in new[] { PowerMode.Efficiency, PowerMode.Balanced, PowerMode.Performance })
                sb.Append("  ").Append(PowerPlan.ModeName(m)).Append("：插电 ").Append(t[FreqLimiter.Index(m, false)]).Append(" · 电池 ").Append(t[FreqLimiter.Index(m, true)]).Append('\n');
            return sb.ToString();
        }

        // ------------------------------------------------------------------ status

        private string PlText()
        {
            var r = _lastReading;
            var cpu = _controller.Cpu;
            bool on; int pl1, pl2, tau;
            lock (_settings) { on = _settings.PowerLimitEnabled; pl1 = _settings.Pl1W; pl2 = _settings.Pl2W; tau = _settings.Pl1TauS; }
            var sb = new StringBuilder("PL1 / PL2：");
            if (r == null || double.IsNaN(r.Pl1W)) sb.Append("--");
            else
            {
                sb.Append(r.Pl1W.ToString("0.#")).Append(" / ").Append(r.Pl2W.ToString("0.#")).Append(" W");
                if (!double.IsNaN(r.Pl1TauS)) sb.Append(" · Tau ").Append(r.Pl1TauS.ToString("0.##")).Append(" 秒");
                if (r.MmioLower)
                    sb.Append(" · 动态 ").Append(r.MmioPl1W.ToString("0.#")).Append(" / ").Append(r.MmioPl2W.ToString("0.#"))
                      .Append(" W，实际 ").Append(r.EffectivePl1W.ToString("0.#")).Append(" / ").Append(r.EffectivePl2W.ToString("0.#")).Append(" W");
            }
            if (!cpu.CanWriteLimits) sb.Append("（不支持设置：").Append(cpu.LimitsUnavailable).Append('）');
            else sb.Append("（自定义：").Append(on ? "开" : "关").Append("，设定 ").Append(pl1).Append(" / ").Append(pl2).Append(" W，Tau ").Append(tau).Append(" 秒）");
            return sb.ToString();
        }

        private string FreqText()
        {
            var r = _lastReading;
            var sb = new StringBuilder();
            bool on, boost, dcTurbo; int[] t, mt;
            lock (_settings) { on = _settings.FreqLimitEnabled; boost = _settings.AllowBoost; dcTurbo = _settings.DcTurbo; t = (int[])_settings.FreqMhz.Clone(); mt = (int[])_settings.FreqMultiMhz.Clone(); }
            sb.Append("频率上限：").Append(on ? "开" : "关").Append(" · 打开程序时允许临时加速：").Append(boost ? "开" : "关").Append(" · 电池睿频：").Append(dcTurbo ? "开" : "关").Append('\n');
            if (r != null) sb.Append("  当前：").Append(r.OnAc ? "插电" : "电池").Append(" · ").Append(PowerPlan.ModeName(r.Mode))
                .Append(" · ").Append(r.MultiCoreLoad ? "全核满载" : "平时").Append(double.IsNaN(r.BusyCores) ? "" : "（负载约 " + r.BusyCores.ToString("0.0") + " 核）").Append(" → ")
                .Append(r.FreqP <= 0 ? "不限制" : "最高 " + new FreqItem(r.FreqP)).Append(r.FreqBoosting ? "（临时加速中）" : "").Append('\n');
            foreach (PowerMode m in new[] { PowerMode.Efficiency, PowerMode.Balanced, PowerMode.Performance })
                for (int b = 0; b < 2; b++)
                {
                    int i = FreqLimiter.Index(m, b == 1);
                    sb.Append("  ").Append(PowerPlan.ModeName(m)).Append(" · ").Append(b == 1 ? "电池" : "插电").Append("：平时 ").Append(new FreqItem(Math.Max(0, t[i])))
                      .Append("，全核满载 ").Append(new FreqItem(mt[i], true)).Append('\n');
                }
            return sb.ToString();
        }

        private string StatusText()
        {
            var r = _lastReading;
            var sb = new StringBuilder();
            sb.Append(AppName).Append(" · ").Append(Program.ModelName).Append(" · ").Append(_controller.Backend.Name).Append('\n');
            FanMode mode; string profile;
            lock (_settings) { mode = _settings.Mode; profile = _settings.Profile.Name; }
            sb.Append("模式：").Append(ModeName(mode, true)).Append(mode == FanMode.Curve ? "（" + profile + "）" : "").Append('\n');
            if (r == null) return sb.Append("（还没有读数）\n").ToString();
            sb.Append("CPU：").Append(r.Temp >= 0 ? r.Temp + " °C" : "--")
              .Append(" · ").Append(double.IsNaN(r.CpuMhz) ? "--" : (r.CpuMhz / 1000).ToString("0.00") + " GHz")
              .Append(" · 占用 ").Append(double.IsNaN(r.CpuUsage) ? "--" : r.CpuUsage.ToString("0") + " %")
              .Append(" · 功耗 ").Append(double.IsNaN(r.CpuPowerW) ? "--" : r.CpuPowerW.ToString("0.0") + " W").Append('\n');
            for (int i = 0; i < _fans.Count; i++)
            {
                var f = _fans[i];
                sb.Append(f.Name).Append("：").Append(f.HasRpm ? (r.Rpm[i] >= 0 ? r.Rpm[i] + " RPM" : "--") : "--")
                  .Append("（目标 ").Append(TargetText(f, r.Target[i], true)).Append("）\n");
            }
            if (r.Failsafe) sb.Append("⚠ 高温保护已触发\n");
            sb.Append(PlText()).Append('\n');
            sb.Append(FreqText());
            sb.Append(EppText());
            return sb.ToString();
        }

        private static string J(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.AppendFormat("\\u{0:x4}", (int)c);
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        private static string J(double v) { return double.IsNaN(v) || double.IsInfinity(v) ? "null" : v.ToString("0.###", CultureInfo.InvariantCulture); }

        private string StatusJson()
        {
            var r = _lastReading;
            var sb = new StringBuilder("{");
            FanMode mode; string profile; bool plOn, freqOn, boost, dcTurbo, hw; int pl1, pl2, tau; int[] t, mt;
            lock (_settings)
            {
                mode = _settings.Mode; profile = _settings.Profile.Name; plOn = _settings.PowerLimitEnabled; pl1 = _settings.Pl1W; pl2 = _settings.Pl2W;
                tau = _settings.Pl1TauS; freqOn = _settings.FreqLimitEnabled; boost = _settings.AllowBoost; dcTurbo = _settings.DcTurbo; hw = _settings.HwInfoEnabled;
                t = (int[])_settings.FreqMhz.Clone(); mt = (int[])_settings.FreqMultiMhz.Clone();
            }
            sb.Append("\"model\":").Append(J(Program.ModelName)).Append(",\"backend\":").Append(J(_controller.Backend.Name));
            sb.Append(",\"mode\":").Append(J(mode.ToString().ToLowerInvariant())).Append(",\"profile\":").Append(J(profile));
            if (r != null)
            {
                sb.Append(",\"cpu\":{\"tempC\":").Append(r.Temp >= 0 ? r.Temp.ToString() : "null").Append(",\"mhz\":").Append(J(r.CpuMhz))
                  .Append(",\"usage\":").Append(J(r.CpuUsage)).Append(",\"powerW\":").Append(J(r.CpuPowerW)).Append('}');
                sb.Append(",\"fans\":[");
                for (int i = 0; i < _fans.Count; i++)
                    sb.Append(i > 0 ? "," : "").Append("{\"name\":").Append(J(_fans[i].Name)).Append(",\"rpm\":").Append(r.Rpm[i] >= 0 ? r.Rpm[i].ToString() : "null")
                      .Append(",\"target\":").Append(r.Target[i] == FanTarget.Auto ? "\"auto\"" : r.Target[i] == FanTarget.Stop ? "\"stop\"" : r.Target[i].ToString())
                      .Append(",\"unit\":").Append(J(_fans[i].Unit)).Append('}');
                sb.Append("],\"failsafe\":").Append(r.Failsafe ? "true" : "false");
                sb.Append(",\"powerLimits\":{\"pl1W\":").Append(J(r.Pl1W)).Append(",\"pl2W\":").Append(J(r.Pl2W)).Append(",\"tauS\":").Append(J(r.Pl1TauS))
                  .Append(",\"mmioPl1W\":").Append(J(r.MmioPl1W)).Append(",\"mmioPl2W\":").Append(J(r.MmioPl2W))
                  .Append(",\"effectivePl1W\":").Append(J(r.EffectivePl1W)).Append(",\"effectivePl2W\":").Append(J(r.EffectivePl2W))
                  .Append(",\"mmioChange\":").Append(J(r.MmioChange))
                  .Append(",\"custom\":").Append(plOn ? "true" : "false").Append(",\"setPl1W\":").Append(pl1).Append(",\"setPl2W\":").Append(pl2).Append(",\"setTauS\":").Append(tau)
                  .Append(",\"writable\":").Append(_controller.Cpu.CanWriteLimits ? "true" : "false").Append('}');
                sb.Append(",\"freq\":{\"enabled\":").Append(freqOn ? "true" : "false").Append(",\"allowBoost\":").Append(boost ? "true" : "false")
                  .Append(",\"boosting\":").Append(r.FreqBoosting || r.PowerBoosting ? "true" : "false").Append(",\"dcTurbo\":").Append(dcTurbo ? "true" : "false")
                  .Append(",\"powerSource\":").Append(r.OnAc ? "\"ac\"" : "\"dc\"").Append(",\"powerMode\":").Append(J(r.Mode.ToString().ToLowerInvariant()))
                  .Append(",\"currentCapMhz\":").Append(r.FreqP).Append(",\"busyCores\":").Append(J(r.BusyCores))
                  .Append(",\"multiCoreLoad\":").Append(r.MultiCoreLoad ? "true" : "false").Append(",\"table\":{");
                bool first = true;
                foreach (PowerMode m in new[] { PowerMode.Efficiency, PowerMode.Balanced, PowerMode.Performance })
                {
                    sb.Append(first ? "" : ",").Append(J(m.ToString().ToLowerInvariant())).Append(":{\"ac\":").Append(t[FreqLimiter.Index(m, false)])
                      .Append(",\"dc\":").Append(t[FreqLimiter.Index(m, true)]).Append(",\"acMulti\":").Append(mt[FreqLimiter.Index(m, false)])
                      .Append(",\"dcMulti\":").Append(mt[FreqLimiter.Index(m, true)]).Append('}');
                    first = false;
                }
                sb.Append("}}");
            }
            bool eppOn; int[] et;
            lock (_settings) { eppOn = _settings.EppEnabled; et = (int[])_settings.EppPct.Clone(); }
            sb.Append(",\"epp\":{\"enabled\":").Append(eppOn ? "true" : "false").Append(",\"current\":").Append(r != null ? r.EppNow : -1).Append(",\"table\":{");
            bool firstEpp = true;
            foreach (PowerMode m in new[] { PowerMode.Efficiency, PowerMode.Balanced, PowerMode.Performance })
            {
                sb.Append(firstEpp ? "" : ",").Append(J(m.ToString().ToLowerInvariant())).Append(":{\"ac\":").Append(et[FreqLimiter.Index(m, false)])
                  .Append(",\"dc\":").Append(et[FreqLimiter.Index(m, true)]).Append('}');
                firstEpp = false;
            }
            sb.Append("}}");
            sb.Append(",\"hwinfo\":").Append(hw ? "true" : "false").Append("}\n");
            return sb.ToString();
        }
    }
}
