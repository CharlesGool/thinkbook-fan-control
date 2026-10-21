using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ThinkBookFanControl
{
    public partial class MainForm : Form
    {
        public const string AppName = "ThinkBook 风扇控制";
        private const string TaskName = "ThinkBookFanControl";
        private const string OldTaskName = "LenovoFanCurve";

        // Layout constants below are in 96-DPI units; S() converts to device pixels (process is system-DPI aware).
        private static int S(int v) { return (int)Math.Round(v * Palette.DpiScale); }
        private static Size Sz(int w, int h) { return new Size(S(w), S(h)); }
        private static Padding Pd(int l, int t, int r, int b) { return new Padding(S(l), S(t), S(r), S(b)); }

        private readonly List<FanInfo> _fans;
        private readonly Settings _settings;
        private readonly FanController _controller;
        private readonly Timer _saveTimer = new Timer { Interval = 800 };
        private readonly Stopwatch _uiClock = Stopwatch.StartNew();
        private long _uiShownAt = long.MinValue / 2, _historyAt = long.MinValue / 2;
        private ComboBox _uiRefresh;
        private bool _exiting, _loading, _hwinfoHinted;

        private Label _tempLabel, _statusLabel, _freqLabel, _powerLabel, _plLabel, _usageLabel;
        private Label[] _rpmLabels, _targetLabels;
        private GroupBox _plBox;
        private Label _plInfo;
        private CheckBox _plOn, _hwinfo;
        private NumericUpDown _pl1, _pl2;
        private ComboBox _tauBox;
        private double _lastPl1 = double.NaN, _lastPl2 = double.NaN;
        private CheckBox _freqOn, _dcTurbo, _lenovoPpm, _allowBoost, _lpmOff, _dttOff;
        private ComboBox[] _freqBoxes, _freqMultiBoxes, _eppBoxes;
        private CheckBox _eppOn;
        private Label _eppInfo;
        private Label _freqInfo;
        private Button _pawnInstall;
        // Display order and labels of the modes (radio buttons and tray menu).
        private static readonly FanMode[] ModeOrder = { FanMode.Quiet, FanMode.Auto, FanMode.Performance, FanMode.Max, FanMode.Fixed, FanMode.Curve };
        private static string ModeName(FanMode m, bool full)
        {
            switch (m)
            {
                case FanMode.Quiet: return "安静模式";
                case FanMode.Auto: return full ? "均衡模式（系统默认，EC 控制）" : "均衡模式（系统默认）";
                case FanMode.Performance: return "性能模式";
                case FanMode.Max: return "全速";
                case FanMode.Fixed: return "固定转速";
                default: return "自定义温度曲线";
            }
        }
        private const string CurveHint = "温度曲线：拖动圆点调整 · 双击空白处添加点 · 右键点删除（橙色虚线 = 当前曲线温度）";

        private readonly Dictionary<FanMode, RadioButton> _radios = new Dictionary<FanMode, RadioButton>();
        private readonly Dictionary<FanMode, ToolStripMenuItem> _trayModes = new Dictionary<FanMode, ToolStripMenuItem>();
        private Button _copyPreset;
        private Label _curveHint;
        private TrackBar[] _tracks;
        private NumericUpDown[] _fixedNums;
        private CheckBox _linkFixed, _linkCurves, _minToTray, _autoStart, _allowLow;
        private NumericUpDown _lowMaxTemp;
        private Label[] _fixedLabels;
        private ComboBox _curveFan;
        private NumericUpDown _hyst, _smooth, _critical, _rampUp, _rampDown;
        private CheckBox _rampOn;
        private CurveEditor _curve;
        private HistoryView _history;
        private GroupBox _fixedBox, _curveBox;
        private FlowLayoutPanel _profilePanel;
        private ComboBox _profileBox;
        private ToolStripMenuItem _trayProfiles;
        private NotifyIcon _tray;
        private Icon _trayIcon;

        public MainForm(List<FanInfo> fans, Settings settings, FanController controller, bool startMinimized)
        {
            _fans = fans; _settings = settings; _controller = controller;
            Text = AppName;
            Font = new Font("Microsoft YaHei UI", 9f);
            AutoScaleMode = AutoScaleMode.None;
            Icon = Program.AppIcon;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = Sz(1040, 700);
            MinimumSize = Sz(900, 640);
            BackColor = Color.FromArgb(246, 247, 249);

            BuildUi();
            BuildTray();
            LoadIntoUi();

            _saveTimer.Tick += (s, e) => { _saveTimer.Stop(); _settings.Save(_fans); };
            _controller.Updated += r => { try { BeginInvoke(new Action<Reading>(OnReading), r); } catch (InvalidOperationException) { } };
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionEnding += OnSessionEnding;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            ApplyRemoteSettings(false);
            EnsureServiceChoices();
            bool lan; int port; lock (_settings) { lan = _settings.RemoteEnabled && _settings.RemoteLan; port = _settings.RemotePort; }
            if (lan) System.Threading.ThreadPool.QueueUserWorkItem(_ => { try { RemoteServer.SetFirewall(true, port, Application.ExecutablePath); } catch (Exception) { } });

            if (startMinimized)
            {
                WindowState = FormWindowState.Minimized;
                ShowInTaskbar = false;
                Load += (s, e) => BeginInvoke(new Action(Hide));
            }
        }

        // ------------------------------------------------------------------ UI construction

        private void BuildUi()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = Pd(10, 10, 10, 10) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(330)));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(root);

            // ---- status bar
            var status = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, BackColor = Color.White, Padding = Pd(8, 6, 8, 6), Margin = Pd(0, 0, 0, 8) };
            root.Controls.Add(status, 0, 0);
            root.SetColumnSpan(status, 2);
            _tempLabel = Tile(status, "CPU 温度", Palette.Temp);
            _freqLabel = Tile(status, "CPU 频率", Palette.Text);
            _usageLabel = Tile(status, "CPU 占用", Palette.Text);
            _powerLabel = Tile(status, "CPU 功耗", Palette.Power);
            _plLabel = Tile(status, "PL1 / PL2", Palette.Text);
            _rpmLabels = new Label[_fans.Count];
            _targetLabels = new Label[_fans.Count];
            for (int i = 0; i < _fans.Count; i++)
            {
                // One tile per fan: measured RPM big, the target in the caption (fans without a sensor show the target big).
                Label caption;
                if (_fans[i].HasRpm) { _rpmLabels[i] = Tile(status, _fans[i].Name, Palette.FanColor(i), out caption); _targetLabels[i] = caption; }
                else _targetLabels[i] = Tile(status, _fans[i].Name + " 目标", Palette.FanColor(i), out caption);
            }
            var cpu = _controller.Cpu;
            var tips = new ToolTip();
            if (cpu.PowerUnavailable != null) tips.SetToolTip(_powerLabel, "无法读取：" + cpu.PowerUnavailable);
            if (cpu.LimitSource == null) tips.SetToolTip(_plLabel, "无法读取：" + cpu.LimitsUnavailable);
            _statusLabel = new Label { AutoSize = true, ForeColor = Palette.Text, Margin = Pd(12, 14, 0, 0), MaximumSize = Sz(360, 0) };
            status.Controls.Add(_statusLabel);

            // ---- left column
            var left = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Margin = Pd(0, 0, 8, 0) };
            root.Controls.Add(left, 0, 1);

            var modeBox = Group(left, "运行模式");
            var modeFlow = Flow(modeBox);
            foreach (var m in ModeOrder)
            {
                var mode = m;
                if (!ModeSupported(mode)) continue;
                var rb = Radio(modeFlow, ModeName(mode, true));
                rb.CheckedChanged += (s, e) => { if (rb.Checked) SetMode(mode); };
                _radios[mode] = rb;
            }
            _copyPreset = new Button { Text = "将当前预设复制为自定义曲线", AutoSize = true, Margin = Pd(3, 6, 3, 3) };
            _copyPreset.Click += (s, e) => CopyPresetToCustom();
            modeFlow.Controls.Add(_copyPreset);

            _fixedBox = Group(left, "固定转速");
            var fixedFlow = Flow(_fixedBox);
            _tracks = new TrackBar[_fans.Count];
            _fixedNums = new NumericUpDown[_fans.Count];
            _fixedLabels = new Label[_fans.Count];
            for (int i = 0; i < _fans.Count; i++)
            {
                int idx = i;
                var f = _fans[i];
                _fixedLabels[i] = new Label { AutoSize = true, ForeColor = Palette.FanColor(i), Margin = Pd(3, 6, 3, 0) };
                fixedFlow.Controls.Add(_fixedLabels[i]);
                var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Pd(0, 0, 0, 0) };
                _tracks[i] = new TrackBar { Minimum = f.MinRpm / f.Step, Maximum = f.MaxRpm / f.Step, TickFrequency = 5, Width = S(190), SmallChange = 1, LargeChange = 5, TickStyle = TickStyle.None };
                _fixedNums[i] = new NumericUpDown { Minimum = f.MinRpm, Maximum = f.MaxRpm, Increment = f.Step, Width = S(80), Margin = Pd(3, 6, 3, 3) };
                _tracks[i].ValueChanged += (s, e) => { if (!_loading) SetFixed(idx, RpmOfTrack(idx)); };
                _fixedNums[i].ValueChanged += (s, e) => { if (!_loading) SetFixed(idx, (int)_fixedNums[idx].Value); };
                row.Controls.Add(_tracks[i]); row.Controls.Add(_fixedNums[i]);
                fixedFlow.Controls.Add(row);
            }
            _linkFixed = Check(fixedFlow, "所有风扇同步");
            _linkFixed.CheckedChanged += (s, e) => { if (_loading) return; lock (_settings) _settings.LinkFixed = _linkFixed.Checked; if (_linkFixed.Checked) SetFixed(0, _settings.FixedRpm[0]); Changed(); };

            _curveBox = Group(left, "曲线设置");
            var curveFlow = Flow(_curveBox);
            // Profile controls only matter in custom-curve mode; smoothing / hysteresis also drive the presets.
            _profilePanel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = Pd(0, 0, 0, 0) };
            curveFlow.Controls.Add(_profilePanel);
            var profRow = Row(_profilePanel, "曲线方案");
            _profileBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(180) };
            _profileBox.SelectedIndexChanged += (s, e) => { if (!_loading && _profileBox.SelectedIndex >= 0) SelectProfile(_profileBox.SelectedIndex); };
            profRow.Controls.Add(_profileBox);
            var profButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Pd(0, 0, 0, 0) };
            _profilePanel.Controls.Add(profButtons);
            SmallButton(profButtons, "新建", (s, e) => NewProfile(false));
            SmallButton(profButtons, "另存为", (s, e) => NewProfile(true));
            SmallButton(profButtons, "重命名", (s, e) => RenameProfile());
            SmallButton(profButtons, "删除", (s, e) => DeleteProfile());
            curveFlow = _profilePanel;
            _linkCurves = Check(curveFlow, "所有风扇使用同一条曲线");
            _linkCurves.CheckedChanged += (s, e) =>
            {
                if (_loading) return;
                lock (_settings) _settings.LinkCurves = _linkCurves.Checked;
                _curve.Linked = _linkCurves.Checked; _curveFan.Enabled = !_linkCurves.Checked; Changed();
            };
            var fanRow = Row(curveFlow, "正在编辑");
            _curveFan = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(130) };
            foreach (var f in _fans) _curveFan.Items.Add(f.Name);
            _curveFan.SelectedIndexChanged += (s, e) => { _curve.ActiveFan = Math.Max(0, _curveFan.SelectedIndex); };
            fanRow.Controls.Add(_curveFan);
            var reset = new Button { Text = "重置为默认曲线", AutoSize = true, Margin = Pd(3, 6, 3, 3) };
            reset.Click += (s, e) =>
            {
                lock (_settings) { int i = _settings.LinkCurves ? 0 : _curve.ActiveFan; _settings.Curves[i].Clear(); _settings.Curves[i].AddRange(Curve.Default(_fans[i])); }
                _curve.Invalidate(); Changed();
            };
            curveFlow.Controls.Add(reset);
            curveFlow = (FlowLayoutPanel)_profilePanel.Parent;
            _hyst = Num(Row(curveFlow, "降温回差 (°C)"), 0, 15);
            _hyst.ValueChanged += (s, e) => { if (_loading) return; lock (_settings) _settings.HysteresisC = (int)_hyst.Value; Changed(); };
            _smooth = Num(Row(curveFlow, "温度平滑 (秒)"), 1, 30);
            _smooth.ValueChanged += (s, e) => { if (_loading) return; lock (_settings) _settings.SmoothingSec = (int)_smooth.Value; Changed(); };

            var rampBox = Group(left, "平滑调速");
            var rampFlow = Flow(rampBox);
            _rampOn = Check(rampFlow, "逐步改变转速（高温保护时直接全速）");
            _rampOn.CheckedChanged += (s, e) =>
            {
                if (_loading) return;
                lock (_settings) _settings.RampEnabled = _rampOn.Checked;
                _rampUp.Enabled = _rampDown.Enabled = _rampOn.Checked; Changed();
            };
            _rampUp = Num(Row(rampFlow, "升速 (RPM/秒)"), Settings.RampMin, Settings.RampMax);
            _rampUp.Increment = 50;
            _rampUp.ValueChanged += (s, e) => { if (_loading) return; lock (_settings) _settings.RampUpRpmPerSec = (int)_rampUp.Value; Changed(); };
            _rampDown = Num(Row(rampFlow, "降速 (RPM/秒)"), Settings.RampMin, Settings.RampMax);
            _rampDown.Increment = 50;
            _rampDown.ValueChanged += (s, e) => { if (_loading) return; lock (_settings) _settings.RampDownRpmPerSec = (int)_rampDown.Value; Changed(); };
            bool rpmFans = _fans.TrueForAll(f => f.IsRpm);
            if (!rpmFans) tips.SetToolTip(_rampOn, "本机风扇按 % 控制：速率按 5500 RPM = 100 % 换算");
            rampBox.Visible = _controller.Backend.CanSetSpeed;

            _plBox = Group(left, "CPU 功耗限制（PL1 / PL2）");
            var plFlow = Flow(_plBox);
            _plInfo = new Label { AutoSize = true, MaximumSize = Sz(282, 0), Margin = Pd(3, 4, 3, 4), ForeColor = Palette.Text };
            plFlow.Controls.Add(_plInfo);
            _plOn = Check(plFlow, "自定义 PL1 / PL2（退出时恢复原值）");
            _plOn.CheckedChanged += (s, e) => { if (!_loading) SetPowerLimitEnabled(_plOn.Checked); };
            _pl1 = Num(Row(plFlow, "PL1 长时功耗 (W)"), cpu.MinLimitW, cpu.MaxLimitW);
            _pl1.ValueChanged += (s, e) => { if (!_loading) SetPowerLimits((int)_pl1.Value, (int)_pl2.Value, true); };
            _pl2 = Num(Row(plFlow, "PL2 短时功耗 (W)"), cpu.MinLimitW, cpu.MaxLimitW);
            _pl2.ValueChanged += (s, e) => { if (!_loading) SetPowerLimits((int)_pl1.Value, (int)_pl2.Value, false); };
            _tauBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(80), MaxDropDownItems = 16, Margin = Pd(3, 5, 3, 3) };
            foreach (int t in TauChoices) _tauBox.Items.Add(new TauItem(t));
            Row(plFlow, "PL1 持续时间 Tau").Controls.Add(_tauBox);
            tips.SetToolTip(_tauBox, "PL1 时间窗口：短时间内允许按 PL2 运行，平均功耗超过 PL1 的时间超过 Tau 后回落到 PL1。\n" +
                "硬件只支持 2^Y×(1+F/4) 形式的值，这里列出 1–448 秒之间全部可用的整数值。");
            _tauBox.SelectedIndexChanged += (s, e) =>
            {
                if (_loading || _tauBox.SelectedItem == null) return;
                lock (_settings) _settings.Pl1TauS = ((TauItem)_tauBox.SelectedItem).Seconds;
                Changed();
            };
            _tauBox.Enabled = cpu.CanSetTau;
            if (!cpu.CanWriteLimits)
            {
                _plOn.Enabled = _pl1.Enabled = _pl2.Enabled = _tauBox.Enabled = false;
                _plInfo.ForeColor = Color.FromArgb(180, 83, 9);
                _plInfo.Text = "不支持设置：" + (cpu.LimitsUnavailable ?? "本机没有可写的功耗限制接口");
            }
            _pawnInstall = new Button { Text = PawnIo.InstalledVersion == null ? "安装 PawnIO 驱动（程序内置 v" + PawnIoSetup.BundledVersion + "）" : "更新 PawnIO 驱动到 v" + PawnIoSetup.BundledVersion, AutoSize = true, Margin = Pd(3, 6, 3, 3) };
            tips.SetToolTip(_pawnInstall, "实时功耗、PL1 / PL2 / Tau、旧机型的 EC 风扇控制需要 PawnIO 驱动（签名驱动，兼容内存完整性）。\n安装程序来自 pawnio.eu 官方，已内置在本程序中。");
            _pawnInstall.Click += (s, e) => InstallPawnIo();
            _pawnInstall.Visible = PawnIoSetup.NeedsInstall;
            plFlow.Controls.Add(_pawnInstall);

            var freqBox = Group(left, "CPU 最高频率（P 核和 E 核一起限制）");
            var fqFlow = Flow(freqBox);
            _freqOn = Check(fqFlow, "按电源模式和是否插电限制频率");
            tips.SetToolTip(_freqOn, "写入 Windows 电源设置的「最大处理器频率」：「平衡」写入当前电源计划，「最佳能效 / 最佳性能」写入对应的电源模式覆盖层，\n" +
                "切换电源模式、插拔电源时由 Windows 自动换用对应的值。关闭此选项或退出程序时恢复原值。\n" +
                "实测本机：P 核的上限同时限制所有核心（E 核约低 100 MHz），Windows 的 E 核单独上限不起作用，所以只能一起限制。");
            _freqOn.CheckedChanged += (s, e) => { if (!_loading) SetFreqEnabled(_freqOn.Checked); };
            var grid = new TableLayoutPanel { ColumnCount = 3, RowCount = 7, AutoSize = true, Margin = Pd(0, 2, 0, 2) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(100)));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(91)));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(91)));
            var hSingle = new Label { Text = "平时", AutoSize = true, Margin = Pd(3, 2, 0, 0) };
            var hMulti = new Label { Text = "全核满载", AutoSize = true, Margin = Pd(3, 2, 0, 0) };
            tips.SetToolTip(hSingle, "除全核满载以外都用这个上限（单线程、游戏、日常使用），「全核满载」选了「同平时」时也用它。");
            tips.SetToolTip(hMulti, string.Format("全核满载时的上限：最近 0.5 秒平均有约 {0:0.#} 个核心在忙（逻辑处理器的 70%）时换成这个上限，\n" +
                "最近 1 秒平均降到 {1:0.#} 个以下时换回平时的上限。由本程序按负载切换（硬件按核心数限频的寄存器无法写入），\n不会高于平时的上限。",
                FanController.MultiCoreLoad, FanController.SingleCoreLoad));
            grid.Controls.Add(new Label(), 0, 0);
            grid.Controls.Add(hSingle, 1, 0);
            grid.Controls.Add(hMulti, 2, 0);
            _freqBoxes = new ComboBox[FreqLimiter.Cells];
            _freqMultiBoxes = new ComboBox[FreqLimiter.Cells];
            int maxMhz = cpu.MaxTurboMhz > 0 ? cpu.MaxTurboMhz : 5000;
            int gridRow = 1;
            foreach (var mode in new[] { PowerMode.Efficiency, PowerMode.Balanced, PowerMode.Performance })
                for (int b = 0; b < 2; b++, gridRow++)
                {
                    int idx = FreqLimiter.Index(mode, b == 1);
                    grid.Controls.Add(new Label { Text = PowerPlan.ModeName(mode) + " · " + (b == 1 ? "电池" : "插电"), AutoSize = true, Margin = Pd(0, 7, 0, 0) }, 0, gridRow);
                    for (int c = 0; c < 2; c++)
                    {
                        bool multi = c == 1;
                        var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(87), MaxDropDownItems = 16, Margin = Pd(2, 3, 2, 3) };
                        box.Items.Add(new FreqItem(0, multi));
                        for (int mhz = maxMhz; mhz >= 800; mhz -= 200) box.Items.Add(new FreqItem(mhz, multi));
                        box.SelectedIndexChanged += (s, e) =>
                        {
                            if (_loading || box.SelectedItem == null) return;
                            int v = ((FreqItem)box.SelectedItem).Mhz;
                            lock (_settings) { if (multi) _settings.FreqMultiMhz[idx] = v; else _settings.FreqMhz[idx] = v; }
                            Changed();
                        };
                        if (multi) _freqMultiBoxes[idx] = box; else _freqBoxes[idx] = box;
                        grid.Controls.Add(box, c + 1, gridRow);
                    }
                }
            fqFlow.Controls.Add(grid);
            _allowBoost = Check(fqFlow, "打开程序时允许系统临时加速");
            tips.SetToolTip(_allowBoost, "打开程序时联想进程管理服务会临时提高频率上限（实测 4.8 / 5.0 GHz），PL 也可能被临时提高。\n" +
                "勾选：新打开的程序到前台后的 10 秒内（连续打开会延长，最长 30 秒）不改回，之后恢复你的设定值。\n" +
                "不勾选：一被改动就立即改回，始终保持设定值。\n对频率上限和自定义 PL1 / PL2 都有效；全核满载时一律立即改回。停用联想进程管理服务后频率上限不再有这种加速。");
            _allowBoost.CheckedChanged += (s, e) => { if (_loading) return; lock (_settings) _settings.AllowBoost = _allowBoost.Checked; Changed(); };
            _dcTurbo = Check(fqFlow, "电池供电时也允许睿频");
            tips.SetToolTip(_dcTurbo, "联想默认在电池供电时关闭睿频（PERFBOOSTMODE = 0），此时高于基础频率的上限不起作用。\n勾选后电池下使用与插电相同的睿频模式；取消勾选恢复原值。");
            _dcTurbo.CheckedChanged += (s, e) => { if (_loading) return; lock (_settings) _settings.DcTurbo = _dcTurbo.Checked; Changed(); };
            _lenovoPpm = Check(fqFlow, "不让联想进程管理服务调节频率");
            tips.SetToolTip(_lenovoPpm, "联想进程管理服务（LenovoProcessManagement）会按电量 / 场景改写频率上限、EPP 和睿频。\n勾选后把它的 Policy_DisablePPMCtrl 设为 1 并重启该服务（系统设置，退出本程序后仍然保持）；\n取消勾选恢复原值。实测：设为 1 后它不再按电量 / 场景改，但负载变化和打开程序时仍会短暂改写，要彻底避免请用下一项。");
            _lenovoPpm.CheckedChanged += (s, e) => { if (!_loading) SetLenovoPpm(_lenovoPpm.Checked, true); };
            _lenovoPpm.Visible = LenovoPpm.Get() >= 0;
            _lpmOff = Check(fqFlow, "停用联想进程管理服务（彻底不再改写频率上限）");
            tips.SetToolTip(_lpmOff, "实测即使上面一项已勾选，联想进程管理服务（LNVDispatcherService）仍会在负载变化后 1–2 秒、打开程序时\n" +
                "把频率上限改成 4.8 / 5.0 GHz（偶尔改成不限制）。本程序会在 0.03–0.2 秒内改回，但核心频率仍会被短暂拉高。\n" +
                "勾选后停止该服务并设为禁用（系统设置，退出本程序后仍保持）；取消勾选恢复原来的启动类型并重新启动服务。\n" +
                "之后剩下的改写来自 Intel DTT（经 Intel IPF 的处理器驱动执行），用下一项停用。\n" +
                "Fn+Q 由这个服务处理：停用后 Fn+Q 不再切换模式，用托盘菜单「电源模式」切换。\n" +
                "它还会写动态 PL（MMIO 功耗限制）：启动时按模式写（平衡 53 / 75 W），停止时写成 24 / 70 W；停用后重启电脑，动态 PL 保持 BIOS 设定值。");
            _lpmOff.CheckedChanged += (s, e) => { if (!_loading) SetServiceOff(_lpmOff, LenovoPpm.ServiceName, _lpmOff.Checked); };
            _lpmOff.Visible = ServiceSwitch.StartType(LenovoPpm.ServiceName) >= 0;
            _dttOff = Check(fqFlow, "停用 Intel DTT 扩展组件（不再改写频率上限和 EPP）");
            tips.SetToolTip(_dttOff, "Intel DTT 的策略由 Intel IPF 的处理器驱动执行：负载类型变化时把频率上限改成不限制 / 5.0 / 4.8 GHz、EPP 改成 25\n" +
                "（实测：全核满载约 5 秒时、负载降下来后）。本程序约 0.1 秒内改回；勾选后就不再发生（实测 3 轮负载没有一次改写）。\n" +
                "勾选 = 在设备管理器里禁用「软件组件 → Intel(R) Dynamic Tuning Technology Device Extension Component」，退出本程序后仍保持；\n" +
                "取消勾选重新启用。代价：DTT 的温度 / 功率管理策略不再生效，CPU 自身的温度墙和 EC 的风扇保护不受影响；\n" +
                "另外 MMIO 功耗限制（HWiNFO 里的「动态」PL）不再随 Fn+Q 模式变化；联想进程管理服务启停时也会写它，\n" +
                "两者都停用后重启电脑，它保持 BIOS 设定值。自定义 PL 高于它时按它生效。\n" +
                "停用 DTT / IPF 服务没有这个效果，处理器参与者设备本身 Windows 不允许禁用。");
            _dttOff.CheckedChanged += (s, e) => { if (!_loading) SetDttOff(_dttOff.Checked); };
            _dttOff.Visible = DttComponent.State >= 0;
            _freqInfo = new Label { AutoSize = true, MaximumSize = Sz(282, 0), Margin = Pd(3, 4, 3, 4), ForeColor = Palette.Text };
            fqFlow.Controls.Add(_freqInfo);

            var eppGroup = Group(left, "性能偏好 EPP（0 = 性能优先，100 = 省电）");
            var epFlow = Flow(eppGroup);
            _eppOn = Check(epFlow, "按电源模式和是否插电设置性能偏好");
            tips.SetToolTip(_eppOn, "写入 Windows 电源设置的「处理器能源性能偏好」（PERFEPP / PERFEPP1，P 核和 E 核用同一个值）。\n" +
                "数值越小，CPU 越积极升频（单核更容易跑到最高频率）；越大越省电。\n" +
                "Windows 默认（插电 / 电池）：平衡 25 / 50，最佳能效 50 / 72，最佳性能 25 / 33。\n" +
                "和频率上限一样分别写入电源计划和电源模式覆盖层，被其他程序改回时立即写回；关闭此选项或退出程序时恢复原值。");
            _eppOn.CheckedChanged += (s, e) => { if (_loading) return; lock (_settings) _settings.EppEnabled = _eppOn.Checked; Changed(); };
            var eppGrid = new TableLayoutPanel { ColumnCount = 3, RowCount = 4, AutoSize = true, Margin = Pd(0, 2, 0, 2) };
            eppGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(76)));
            eppGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(103)));
            eppGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(103)));
            eppGrid.Controls.Add(new Label(), 0, 0);
            eppGrid.Controls.Add(new Label { Text = "插电", AutoSize = true, Margin = Pd(3, 2, 0, 0) }, 1, 0);
            eppGrid.Controls.Add(new Label { Text = "电池", AutoSize = true, Margin = Pd(3, 2, 0, 0) }, 2, 0);
            _eppBoxes = new ComboBox[FreqLimiter.Cells];
            int eppRow = 1;
            foreach (var mode in new[] { PowerMode.Efficiency, PowerMode.Balanced, PowerMode.Performance })
            {
                eppGrid.Controls.Add(new Label { Text = PowerPlan.ModeName(mode), AutoSize = true, Margin = Pd(0, 7, 0, 0) }, 0, eppRow);
                for (int b = 0; b < 2; b++)
                {
                    int idx = FreqLimiter.Index(mode, b == 1);
                    var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(99), MaxDropDownItems = 16, Margin = Pd(2, 3, 2, 3) };
                    for (int v = 0; v <= 100; v += 10) box.Items.Add(new EppItem(v));
                    box.SelectedIndexChanged += (s, e) =>
                    {
                        if (_loading || box.SelectedItem == null) return;
                        lock (_settings) _settings.EppPct[idx] = ((EppItem)box.SelectedItem).Value;
                        Changed();
                    };
                    _eppBoxes[idx] = box;
                    eppGrid.Controls.Add(box, b + 1, eppRow);
                }
                eppRow++;
            }
            epFlow.Controls.Add(eppGrid);
            _eppInfo = new Label { AutoSize = true, MaximumSize = Sz(282, 0), Margin = Pd(3, 4, 3, 4), ForeColor = Palette.Text };
            epFlow.Controls.Add(_eppInfo);

            var genBox = Group(left, "安全与常规");
            var genFlow = Flow(genBox);
            genFlow.Controls.Add(new Label
            {
                Text = "本机：" + Program.ModelName + (Program.ModelVerified ? "（已验证）" : "（未验证机型，请留意温度和转速）") +
                       "\n接口：" + _controller.Backend.Name,
                AutoSize = true, MaximumSize = Sz(280, 0), Margin = Pd(3, 4, 3, 4),
                ForeColor = Program.ModelVerified ? Palette.Text : Color.FromArgb(180, 83, 9)
            });
            _critical = Num(Row(genFlow, "高温保护 (°C)，达到即全速"), 75, 100);
            _critical.ValueChanged += (s, e) => { if (_loading) return; lock (_settings) _settings.CriticalTempC = (int)_critical.Value; Changed(); };
            _allowLow = Check(genFlow, "允许低于额定最低转速 / 停转");
            _allowLow.CheckedChanged += (s, e) => { if (!_loading) SetAllowLow(_allowLow.Checked); };
            _lowMaxTemp = Num(Row(genFlow, "    仅在温度低于 (°C) 时生效"), 35, 70);
            _lowMaxTemp.ValueChanged += (s, e) => { if (_loading) return; lock (_settings) _settings.LowSpeedMaxTempC = (int)_lowMaxTemp.Value; Changed(); };
            bool lowSupported = _controller.Backend.CanSetSpeed && _fans.Exists(f => f.LowestStable > 0);
            _allowLow.Visible = _lowMaxTemp.Parent.Visible = lowSupported;
            if (!_controller.Backend.CanSetSpeed) _fixedBox.Visible = _curveBox.Visible = false;
            _uiRefresh = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(80), Margin = Pd(3, 4, 3, 3) };
            foreach (int ms in Settings.UiRefreshChoices) _uiRefresh.Items.Add((ms / 1000.0).ToString("0.#") + " 秒");
            Row(genFlow, "界面数值刷新间隔").Controls.Add(_uiRefresh);
            tips.SetToolTip(_uiRefresh, "顶部数值和各分组信息的刷新间隔。风扇和频率上限的控制不受影响（全核满载判断仍是每 0.25 秒），\n下方的历史曲线固定每秒一个点。");
            _uiRefresh.SelectedIndexChanged += (s, e) =>
            {
                if (_loading || _uiRefresh.SelectedIndex < 0) return;
                lock (_settings) _settings.UiRefreshMs = Settings.UiRefreshChoices[_uiRefresh.SelectedIndex];
                Changed();
            };
            _minToTray = Check(genFlow, "关闭窗口时最小化到托盘");
            _minToTray.CheckedChanged += (s, e) => { if (_loading) return; lock (_settings) _settings.MinimizeToTray = _minToTray.Checked; Changed(); };
            _autoStart = Check(genFlow, "开机自动启动（计划任务，免 UAC）");
            _autoStart.CheckedChanged += (s, e) => { if (!_loading) SetAutoStart(_autoStart.Checked); };
            _hwinfo = Check(genFlow, "在 HWiNFO 中显示（自定义传感器）");
            tips.SetToolTip(_hwinfo, "写入 HKCU\\Software\\HWiNFO64\\Sensors\\Custom\\" + HwInfoSensors.DeviceName +
                "\n在 HWiNFO 的传感器窗口里显示风扇转速 / 目标、CPU 温度 / 功耗 / PL1 / PL2 / 频率 / 占用。\nHWiNFO 只在启动时读取传感器列表：第一次打开后请重启 HWiNFO（没加载时本程序会提示）。\n传感器组在列表最下面，中文界面显示为「ThinkBook 风扇 Control」。\n取消勾选会删除这些注册表项。");
            _hwinfo.CheckedChanged += (s, e) => { if (_loading) return; lock (_settings) _settings.HwInfoEnabled = _hwinfo.Checked; Changed(); };
            _remoteOn = Check(genFlow, "命令行控制（tbfc.exe）");
            tips.SetToolTip(_remoteOn, "用不需要管理员权限的 tbfc.exe 控制本程序，例如：\n  tbfc status\n  tbfc mode quiet\n  tbfc fixed 3000\n  tbfc pl 35 50 28\n  tbfc freq balanced ac 2400\n" +
                "本机使用时自动读取令牌；远程使用需要 --host 和 --token。tbfc help 查看全部命令。");
            _remoteOn.CheckedChanged += (s, e) => { if (!_loading) SetRemote(_remoteOn.Checked, _remoteLan.Checked); };
            var remoteRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Pd(0, 0, 0, 0) };
            _remoteLan = new CheckBox { Text = "允许局域网访问", AutoSize = true, Margin = Pd(20, 6, 3, 3) };
            tips.SetToolTip(_remoteLan, "在所有网卡上监听控制端口，并添加 Windows 防火墙规则（仅专用 / 域网络）。\n其他电脑：tbfc --host <本机 IP> --token <令牌> status");
            _remoteLan.CheckedChanged += (s, e) => { if (!_loading) SetRemote(_remoteOn.Checked, _remoteLan.Checked); };
            remoteRow.Controls.Add(_remoteLan);
            var copyToken = new Button { Text = "复制令牌", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Pd(6, 3, 0, 3), Padding = Pd(4, 0, 4, 0) };
            copyToken.Click += (s, e) =>
            {
                string tok; int port; lock (_settings) { tok = _settings.RemoteToken; port = _settings.RemotePort; }
                try { Clipboard.SetText(tok); } catch (Exception) { }
                MessageBox.Show(this, "令牌已复制到剪贴板：\n" + tok + "\n\n远程使用：tbfc --host <本机 IP> --port " + port + " --token " + tok + " status", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            remoteRow.Controls.Add(copyToken);
            genFlow.Controls.Add(remoteRow);
            var quit = new Button { Text = "恢复自动并退出", AutoSize = true, Margin = Pd(3, 8, 3, 3) };
            quit.Click += (s, e) => ExitApp();
            genFlow.Controls.Add(quit);

            // ---- right column: curve + history
            var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Margin = Pd(0, 0, 0, 0) };
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 62));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
            root.Controls.Add(right, 1, 1);
            _curveHint = new Label { Text = CurveHint, AutoSize = true, ForeColor = Palette.Text, Margin = Pd(0, 0, 0, 4) };
            right.Controls.Add(_curveHint, 0, 0);
            _curve = new CurveEditor { Dock = DockStyle.Fill, Margin = Pd(0, 0, 0, 8) };
            _curve.Bind(_settings.Curves, _fans);
            _curve.CurveChanged += (s, e) => Changed();
            right.Controls.Add(_curve, 0, 1);
            _history = new HistoryView { Dock = DockStyle.Fill, Margin = Pd(0, 0, 0, 0) };
            _history.Bind(_fans);
            right.Controls.Add(_history, 0, 2);
            if (!_controller.Backend.CanSetSpeed)
            {
                // Only auto / full speed: no curve to edit, the history takes the whole column.
                _curveHint.Visible = _curve.Visible = false;
                right.RowStyles[1] = new RowStyle(SizeType.Absolute, 0);
                right.RowStyles[2] = new RowStyle(SizeType.Percent, 100);
            }

        }

        private Label Tile(Control parent, string caption, Color color)
        {
            Label c;
            return Tile(parent, caption, color, out c);
        }

        private Label Tile(Control parent, string caption, Color color, out Label captionLabel)
        {
            var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = Pd(4, 0, 14, 0) };
            captionLabel = new Label { Text = caption, AutoSize = true, ForeColor = Palette.Text, Margin = Pd(0, 0, 0, 0) };
            p.Controls.Add(captionLabel);
            var v = new Label { Text = "--", AutoSize = true, ForeColor = color, Font = new Font(Font.FontFamily, 16f, FontStyle.Bold), Margin = Pd(0, 0, 0, 0) };
            p.Controls.Add(v);
            parent.Controls.Add(p);
            return v;
        }

        private static GroupBox Group(Control parent, string text)
        {
            var g = new GroupBox { Text = text, Width = S(300), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = Pd(8, 4, 8, 8), Margin = Pd(0, 0, 0, 8), BackColor = Color.White };
            parent.Controls.Add(g);
            return g;
        }
        private static FlowLayoutPanel Flow(Control parent)
        {
            var f = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Dock = DockStyle.Fill, MinimumSize = Sz(282, 0) };
            parent.Controls.Add(f);
            return f;
        }
        private static FlowLayoutPanel Row(Control parent, string label)
        {
            var r = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Pd(0, 0, 0, 0) };
            r.Controls.Add(new Label { Text = label, AutoSize = true, Margin = Pd(3, 8, 6, 3) });
            parent.Controls.Add(r);
            return r;
        }
        private static RadioButton Radio(Control parent, string text) { var r = new RadioButton { Text = text, AutoSize = true, Margin = Pd(3, 3, 3, 3) }; parent.Controls.Add(r); return r; }
        private static CheckBox Check(Control parent, string text) { var c = new CheckBox { Text = text, AutoSize = true, Margin = Pd(3, 6, 3, 3) }; parent.Controls.Add(c); return c; }
        private static NumericUpDown Num(Control parent, int min, int max) { var n = new NumericUpDown { Minimum = min, Maximum = max, Width = S(60), Margin = Pd(3, 5, 3, 3) }; parent.Controls.Add(n); return n; }

        private void BuildTray()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("显示主界面", null, (s, e) => ShowMain());
            menu.Items.Add(new ToolStripSeparator());
            foreach (var m in ModeOrder)
            {
                var mode = m;
                if (!ModeSupported(mode)) continue;
                var item = new ToolStripMenuItem(ModeName(mode, false), null, (s, e) => _radios[mode].Checked = true);
                _trayModes[mode] = item;
                menu.Items.Add(item);
            }
            // Custom curve profiles, rebuilt each time the submenu opens.
            _trayProfiles = new ToolStripMenuItem("曲线方案");
            _trayProfiles.DropDownItems.Add("(none)");
            _trayProfiles.DropDownOpening += (s, e) =>
            {
                _trayProfiles.DropDownItems.Clear();
                lock (_settings)
                    for (int i = 0; i < _settings.Profiles.Count; i++)
                    {
                        int idx = i;
                        var item = new ToolStripMenuItem(_settings.Profiles[i].Name, null, (s2, e2) => { SelectProfile(idx); _radios[FanMode.Curve].Checked = true; });
                        item.Checked = _settings.Mode == FanMode.Curve && i == _settings.ActiveProfile;
                        _trayProfiles.DropDownItems.Add(item);
                    }
            };
            if (_controller.Backend.CanSetSpeed) menu.Items.Add(_trayProfiles);
            // Windows power mode: Fn+Q only works while Lenovo's process management service runs.
            var power = new ToolStripMenuItem("电源模式");
            foreach (var pm in new[] { PowerMode.Efficiency, PowerMode.Balanced, PowerMode.Performance })
            {
                var p = pm;
                power.DropDownItems.Add(new ToolStripMenuItem(PowerPlan.ModeName(p), null, (s, e) => SetPowerMode(p)) { Tag = p });
            }
            power.DropDownOpening += (s, e) =>
            {
                var cur = PowerPlan.CurrentMode;
                foreach (ToolStripMenuItem i in power.DropDownItems) i.Checked = (PowerMode)i.Tag == cur;
            };
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(power);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("恢复自动并退出", null, (s, e) => ExitApp());
            _trayIcon = FanIcon.CreateTrayIcon(SystemInformation.SmallIconSize.Width);
            _tray = new NotifyIcon { Icon = _trayIcon, Text = Text, Visible = true, ContextMenuStrip = menu };
            _tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowMain(); };
        }

        private void SetPowerMode(PowerMode m)
        {
            try { PowerPlan.SetMode(m); _controller.Poke(); }
            catch (Exception ex) { MessageBox.Show(this, "切换电源模式失败：" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private void LoadIntoUi()
        {
            _loading = true;
            lock (_settings)
            {
                if (!_settings.AllowLowSpeed) _settings.ClampToRated(_fans);
                _allowLow.Checked = _settings.AllowLowSpeed;
                _lowMaxTemp.Value = _settings.LowSpeedMaxTempC;
                ApplyLowSpeedUi(_settings.AllowLowSpeed);
                _linkFixed.Checked = _settings.LinkFixed;
                _linkCurves.Checked = _settings.LinkCurves;
                _hyst.Value = _settings.HysteresisC;
                _smooth.Value = _settings.SmoothingSec;
                _rampOn.Checked = _settings.RampEnabled;
                _rampUp.Value = _settings.RampUpRpmPerSec;
                _rampDown.Value = _settings.RampDownRpmPerSec;
                _rampUp.Enabled = _rampDown.Enabled = _settings.RampEnabled;
                _critical.Value = _settings.CriticalTempC;
                _minToTray.Checked = _settings.MinimizeToTray;
                _hwinfo.Checked = _settings.HwInfoEnabled;
                _remoteOn.Checked = _settings.RemoteEnabled;
                _remoteLan.Checked = _settings.RemoteLan;
                _remoteLan.Enabled = _settings.RemoteEnabled;
                if (Array.IndexOf(_settings.FreqMhz, -1) >= 0)
                {
                    // Start from what Windows / Lenovo currently use, so turning the option on changes nothing yet.
                    int[] cur = null;
                    try { cur = FreqLimiter.ReadTable(); } catch (Exception) { }
                    for (int i = 0; i < _settings.FreqMhz.Length; i++)
                        if (_settings.FreqMhz[i] < 0) _settings.FreqMhz[i] = cur != null ? Math.Min(cur[i], Settings.FreqMaxMhz) : 0;
                }
                for (int i = 0; i < _freqBoxes.Length; i++) SelectFreq(_freqBoxes[i], _settings.FreqMhz[i]);
                for (int i = 0; i < _freqMultiBoxes.Length; i++) SelectFreq(_freqMultiBoxes[i], _settings.FreqMultiMhz[i]);
                if (Array.IndexOf(_settings.EppPct, -1) >= 0)
                {
                    // Start from the current values (Windows defaults or Lenovo's), so turning the option on changes nothing yet.
                    int[] cur = null;
                    try { cur = FreqLimiter.ReadTable(PowerPlan.Epp1); } catch (Exception) { }
                    for (int i = 0; i < _settings.EppPct.Length; i++)
                        if (_settings.EppPct[i] < 0) _settings.EppPct[i] = cur != null ? Settings.Clamp(cur[i], 0, 100) : 50;
                }
                for (int i = 0; i < _eppBoxes.Length; i++) SelectEpp(_eppBoxes[i], _settings.EppPct[i]);
                _eppOn.Checked = _settings.EppEnabled;
                _allowBoost.Checked = _settings.AllowBoost;
                _freqOn.Checked = _settings.FreqLimitEnabled;
                _dcTurbo.Checked = _settings.DcTurbo;
                _lenovoPpm.Checked = LenovoPpm.Get() == 1;
                _lpmOff.Checked = ServiceSwitch.StartType(LenovoPpm.ServiceName) == ServiceSwitch.Disabled;
                _dttOff.Checked = DttComponent.State == 1;
                _uiRefresh.SelectedIndex = Math.Max(0, Array.IndexOf(Settings.UiRefreshChoices, _settings.UiRefreshMs));
                if (_settings.Pl1W <= 0 || _settings.Pl2W <= 0)
                {
                    // First time: start from what the CPU currently uses (the controller thread is not running yet).
                    // "Unlimited" BIOS values (4095.875 W) end up at the top of the range.
                    double a, b;
                    if (_controller.Cpu.ReadLimits(out a, out b) && a > 0)
                    {
                        _settings.Pl1W = (int)Math.Min(10000, Math.Round(a));
                        _settings.Pl2W = (int)Math.Min(10000, Math.Round(b));
                    }
                    else { _settings.Pl1W = 45; _settings.Pl2W = 65; }
                }
                int pl1 = _settings.Pl1W, pl2 = _settings.Pl2W;
                _controller.Cpu.ClampLimits(ref pl1, ref pl2);
                _settings.Pl1W = pl1; _settings.Pl2W = pl2;
                _pl1.Value = Math.Max(_pl1.Minimum, Math.Min(_pl1.Maximum, _settings.Pl1W));
                _pl2.Value = Math.Max(_pl2.Minimum, Math.Min(_pl2.Maximum, _settings.Pl2W));
                if (_settings.Pl1TauS <= 0)
                {
                    double a, b, t;
                    if (_controller.Cpu.ReadLimits(out a, out b, out t) && t >= 1) _settings.Pl1TauS = (int)Math.Min(448, Math.Round(t));
                }
                _settings.Pl1TauS = SnapTau(_settings.Pl1TauS > 0 ? _settings.Pl1TauS : 28);
                SelectTau(_settings.Pl1TauS);
                _plOn.Checked = _settings.PowerLimitEnabled && _controller.Cpu.CanWriteLimits;
                _copyPreset.Visible = _controller.Backend.CanSetSpeed;
                _radios[_radios.ContainsKey(_settings.Mode) ? _settings.Mode : FanMode.Auto].Checked = true;
                foreach (var p in _settings.Profiles) _profileBox.Items.Add(p.Name);
                _profileBox.SelectedIndex = _settings.ActiveProfile;
            }
            MigrateAutoStart();
            _autoStart.Checked = IsAutoStart();
            if (_curveFan.Items.Count > 0) _curveFan.SelectedIndex = 0;
            _curveFan.Enabled = !_linkCurves.Checked;
            _curve.Linked = _linkCurves.Checked;
            _loading = false;
            UpdateModeUi();
        }

        // ------------------------------------------------------------------ behaviour

        private void SetMode(FanMode m)
        {
            if (!_loading) { lock (_settings) _settings.Mode = m; Changed(); }
            UpdateModeUi();
        }

        private void UpdateModeUi()
        {
            FanMode m; lock (_settings) m = _settings.Mode;
            _fixedBox.Enabled = m == FanMode.Fixed;
            _curveBox.Enabled = m == FanMode.Curve || m == FanMode.Quiet || m == FanMode.Performance; // smoothing / hysteresis
            _profilePanel.Enabled = m == FanMode.Curve;
            _curve.Enabled = m == FanMode.Curve;
            foreach (var kv in _trayModes) kv.Value.Checked = kv.Key == m;

            // Quiet / Performance show their built-in curve read-only; other modes show the custom curve.
            var preset = Curve.Preset(m, _fans);
            bool low; lock (_settings) low = _settings.AllowLowSpeed;
            if (preset != null && !low)
                // Show what actually runs: without low speed the "stop" point is raised to the rated minimum.
                for (int i = 0; i < preset.Length; i++)
                    for (int j = 0; j < preset[i].Count; j++)
                        preset[i][j] = new CurvePoint(preset[i][j].Temp, Settings.Clamp(preset[i][j].Rpm, _fans[i].MinRpm, _fans[i].MaxRpm));
            _copyPreset.Enabled = preset != null;
            if (preset != null)
            {
                _curve.Bind(preset, _fans);
                _curve.Linked = false;
                _curve.ActiveFan = 0;
                _curveHint.Text = ModeName(m, false) + "曲线（内置，只读；点「将当前预设复制为自定义曲线」后可修改）";
            }
            else
            {
                _curve.Bind(_settings.Curves, _fans);
                _curve.Linked = _linkCurves.Checked;
                _curve.ActiveFan = Math.Max(0, _curveFan.SelectedIndex);
                _curveHint.Text = m == FanMode.Curve ? CurveHint : "当前模式不使用曲线（下图为自定义曲线，切到「自定义温度曲线」后生效）";
            }
        }

        private void CopyPresetToCustom()
        {
            FanMode m; lock (_settings) m = _settings.Mode;
            var preset = Curve.Preset(m, _fans);
            if (preset == null) return;
            lock (_settings)
            {
                // Presets differ per fan (rated min/max), so the copy keeps separate curves.
                var p = new CurveProfile { Name = _settings.UniqueProfileName(ModeName(m, false) + "（自定义）"), Link = false, Curves = preset };
                _settings.Profiles.Add(p);
                _settings.ActiveProfile = _settings.Profiles.Count - 1;
                if (!_settings.AllowLowSpeed) _settings.ClampToRated(_fans);
            }
            ProfileChanged();
            _radios[FanMode.Curve].Checked = true;
        }

        // ------------------------------------------------------------------ curve profiles

        private void SelectProfile(int index)
        {
            lock (_settings) _settings.ActiveProfile = Settings.Clamp(index, 0, _settings.Profiles.Count - 1);
            ProfileChanged();
        }

        /// <summary>Active profile switched / added / removed / renamed: refresh everything that shows it.</summary>
        private void ProfileChanged()
        {
            _loading = true;
            lock (_settings)
            {
                _profileBox.Items.Clear();
                foreach (var p in _settings.Profiles) _profileBox.Items.Add(p.Name);
                _profileBox.SelectedIndex = _settings.ActiveProfile;
                _linkCurves.Checked = _settings.LinkCurves;
            }
            _curveFan.Enabled = !_linkCurves.Checked;
            _loading = false;
            UpdateModeUi();
            Changed();
        }

        private void NewProfile(bool copyCurrent)
        {
            string name;
            lock (_settings) name = _settings.UniqueProfileName(copyCurrent ? _settings.Profile.Name + " 副本" : "新曲线");
            name = Prompt(copyCurrent ? "另存为新的曲线方案" : "新建曲线方案", name);
            if (name == null) return;
            lock (_settings)
            {
                if (_settings.Profiles.Exists(p => p.Name == name)) { NameTaken(name); return; }
                _settings.Profiles.Add(copyCurrent ? _settings.Profile.Clone(name) : CurveProfile.Default(name, _fans));
                _settings.ActiveProfile = _settings.Profiles.Count - 1;
                if (!_settings.AllowLowSpeed) _settings.ClampToRated(_fans);
            }
            ProfileChanged();
        }

        private void RenameProfile()
        {
            string old; lock (_settings) old = _settings.Profile.Name;
            string name = Prompt("重命名曲线方案", old);
            if (name == null || name == old) return;
            lock (_settings)
            {
                if (_settings.Profiles.Exists(p => p.Name == name)) { NameTaken(name); return; }
                _settings.Profile.Name = name;
            }
            ProfileChanged();
        }

        private void DeleteProfile()
        {
            string name; int count;
            lock (_settings) { name = _settings.Profile.Name; count = _settings.Profiles.Count; }
            if (count <= 1) { MessageBox.Show(this, "至少需要保留一个曲线方案。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (MessageBox.Show(this, "删除曲线方案「" + name + "」？", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            lock (_settings)
            {
                _settings.Profiles.RemoveAt(_settings.ActiveProfile);
                _settings.ActiveProfile = Math.Max(0, _settings.ActiveProfile - 1);
            }
            ProfileChanged();
        }

        private void NameTaken(string name)
        {
            MessageBox.Show(this, "已经有名为「" + name + "」的曲线方案了。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>Small text prompt; returns the trimmed text, or null when cancelled / empty.</summary>
        private string Prompt(string title, string initial)
        {
            using (var f = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, StartPosition = FormStartPosition.CenterParent, Font = Font, AutoScaleMode = AutoScaleMode.None, ClientSize = Sz(320, 110) })
            {
                var label = new Label { Text = "名称：", AutoSize = true, Location = new Point(S(12), S(16)) };
                var box = new TextBox { Text = initial, Location = new Point(S(12), S(38)), Width = S(296), MaxLength = 40 };
                var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Location = new Point(S(152), S(72)), Size = Sz(75, 28) };
                var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(S(233), S(72)), Size = Sz(75, 28) };
                f.Controls.AddRange(new Control[] { label, box, ok, cancel });
                f.AcceptButton = ok; f.CancelButton = cancel;
                box.SelectAll();
                if (f.ShowDialog(this) != DialogResult.OK) return null;
                string name = box.Text.Trim();
                return name.Length == 0 ? null : name;
            }
        }

        private static void SmallButton(Control parent, string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Pd(3, 2, 0, 3), Padding = Pd(4, 0, 4, 0) };
            b.Click += click;
            parent.Controls.Add(b);
        }

        private void SetFixed(int idx, int rpm)
        {
            bool link, low;
            lock (_settings) { link = _settings.LinkFixed; low = _settings.AllowLowSpeed; }
            // Stepping up from "stop" lands on the lowest stable speed instead of snapping back to 0.
            int prev; lock (_settings) prev = _settings.FixedRpm[idx];
            int v0 = Settings.SnapRpm(rpm, _fans[idx], low);
            if (v0 == 0 && prev == 0 && rpm > 0) v0 = _fans[idx].LowestStable;
            for (int i = 0; i < _fans.Count; i++)
            {
                if (i != idx && !link) continue;
                int v = Settings.SnapRpm(v0, _fans[i], low);
                lock (_settings) _settings.FixedRpm[i] = v;
            }
            RefreshFixedUi();
            Changed();
        }

        // With low speed allowed the track's lowest tick means "stop".
        private int RpmOfTrack(int i)
        {
            bool low; lock (_settings) low = _settings.AllowLowSpeed && _fans[i].LowestStable > 0;
            return low && _tracks[i].Value == _tracks[i].Minimum ? 0 : _tracks[i].Value * _fans[i].Step;
        }

        private void RefreshFixedUi()
        {
            _loading = true;
            for (int i = 0; i < _fans.Count; i++)
            {
                int v; lock (_settings) v = _settings.FixedRpm[i];
                _tracks[i].Value = v == 0 ? _tracks[i].Minimum : Settings.Clamp(v / _fans[i].Step, _tracks[i].Minimum, _tracks[i].Maximum);
                _fixedNums[i].Value = Math.Max(_fixedNums[i].Minimum, Math.Min(_fixedNums[i].Maximum, v));
            }
            _loading = false;
        }

        private void ApplyLowSpeedUi(bool low)
        {
            bool was = _loading;
            _loading = true;
            for (int i = 0; i < _fans.Count; i++)
            {
                var f = _fans[i];
                bool fanLow = low && f.LowestStable > 0;
                _fixedLabels[i].Text = fanLow
                    ? string.Format("{0}（{1}–{2}，0 = 停转）", f.Name, f.LowestStable, f.Format(f.MaxRpm))
                    : string.Format("{0}（{1}–{2}）", f.Name, f.MinRpm, f.Format(f.MaxRpm));
                _tracks[i].Minimum = fanLow ? f.LowestStable / f.Step - 1 : f.MinRpm / f.Step;
                _fixedNums[i].Minimum = fanLow ? 0 : f.MinRpm;
            }
            _lowMaxTemp.Enabled = low;
            _curve.AllowLow = low;
            _loading = was;
            RefreshFixedUi();
            _loading = was;
        }

        private void SetAllowLow(bool on)
        {
            if (on && MessageBox.Show(this,
                    "打开后，固定转速和曲线可以设到 " + _fans[0].Format(_fans[0].LowestStable) + "，或设为 0 让风扇停转。\n\n" +
                    "· 只有温度低于设定值时才会生效，超过后自动提回额定最低转速。\n" +
                    "· 程序被结束或崩溃时，守护进程会把风扇交还 EC 自动控制。\n\n确定要打开吗？",
                    Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            {
                _loading = true; _allowLow.Checked = false; _loading = false;
                return;
            }
            lock (_settings)
            {
                _settings.AllowLowSpeed = on;
                if (!on) _settings.ClampToRated(_fans);
            }
            ApplyLowSpeedUi(on);
            UpdateModeUi();
            Changed();
        }

        private void Changed()
        {
            _controller.Poke();
            _saveTimer.Stop(); _saveTimer.Start();
        }

        private void OnReading(Reading r)
        {
            _lastReading = r;
            // The controller reads up to 4 times a second (all-core caps) and early when a power setting is rewritten:
            // the history gets one point a second (its 5-minute width), the numbers the interval chosen in the window.
            long now = _uiClock.ElapsedMilliseconds;
            if (now - _historyAt >= 950)
            {
                _historyAt = now;
                var points = new int[_fans.Count];
                for (int i = 0; i < _fans.Count; i++)
                    points[i] = _fans[i].HasRpm ? r.Rpm[i] : r.Target[i] == FanTarget.Auto ? -1 : Math.Max(0, r.Target[i]); // EC auto: unknown, not 0 %
                _history.Add(r.Temp, points, r.CpuPowerW);
            }
            int every; lock (_settings) every = _settings.UiRefreshMs;
            if (!r.Failsafe && now - _uiShownAt < every - 100) return;
            _uiShownAt = now;
            _tempLabel.Text = r.Temp >= 0 ? r.Temp + " °C" : "--";
            SetText(_freqLabel, double.IsNaN(r.CpuMhz) ? "--" : (r.CpuMhz / 1000).ToString("0.00") + " GHz");
            SetText(_usageLabel, double.IsNaN(r.CpuUsage) ? "--" : r.CpuUsage.ToString("0") + " %");
            SetText(_powerLabel, double.IsNaN(r.CpuPowerW) ? "--" : r.CpuPowerW.ToString("0.0") + " W");
            SetText(_plLabel, double.IsNaN(r.Pl1W) ? "--" : r.Pl1W.ToString("0.#") + " / " + r.Pl2W.ToString("0.#") + " W");
            UpdatePowerInfo(r);
            UpdateFreqInfo(r);
            UpdateEppInfo(r);

            var tip = "CPU " + (r.Temp >= 0 ? r.Temp + "°C" : "--") + (double.IsNaN(r.CpuPowerW) ? "" : " " + r.CpuPowerW.ToString("0") + "W");
            for (int i = 0; i < _fans.Count; i++)
            {
                var f = _fans[i];
                if (_rpmLabels[i] != null) _rpmLabels[i].Text = r.Rpm[i] >= 0 ? r.Rpm[i] + " RPM" : "--";
                // While ramping show "now → heading to".
                string target = r.Desired != null && r.Desired[i] != r.Target[i]
                    ? TargetText(f, r.Target[i], false) + " → " + TargetText(f, r.Desired[i], true)
                    : TargetText(f, r.Target[i], true);
                SetText(_targetLabels[i], f.HasRpm ? f.Name + " · 目标 " + target : target);
                tip += " | " + (f.HasRpm ? (r.Rpm[i] >= 0 ? r.Rpm[i].ToString() : "--") : TargetText(f, r.Target[i], true));
            }
            if (r.Failsafe) { _statusLabel.ForeColor = Color.Firebrick; _statusLabel.Text = "⚠ 高温保护已触发：风扇全速，温度回落后自动恢复"; }
            else if (r.Error != null) { _statusLabel.ForeColor = Color.Firebrick; _statusLabel.Text = r.Error; }
            else if (r.HwInfoNeedsRestart) { _statusLabel.ForeColor = Color.FromArgb(180, 83, 9); _statusLabel.Text = "HWiNFO 需要重启一次才能显示本程序的传感器"; }
            else if (_settings.SaveError != null) { _statusLabel.ForeColor = Color.FromArgb(180, 83, 9); _statusLabel.Text = "⚠ 设置保存失败：" + _settings.SaveError; }
            else { _statusLabel.ForeColor = Palette.Text; _statusLabel.Text = ""; }
            if (r.HwInfoNeedsRestart && !_hwinfoHinted)
            {
                _hwinfoHinted = true;
                _tray.ShowBalloonTip(10000, "HWiNFO 需要重启一次",
                    "HWiNFO 只在启动时读取自定义传感器。重启 HWiNFO 后，传感器窗口最下面会出现「ThinkBook Fan Control」（中文界面显示为「ThinkBook 风扇 Control」）。",
                    ToolTipIcon.Info);
            }
            else if (!r.HwInfoNeedsRestart) _hwinfoHinted = false;

            _curve.SetLive(r.CurveTemp, r.Target);
            if (_fans.TrueForAll(f => f.HasRpm)) tip += " RPM";
            _tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

        private static void SetText(Label l, string s) { if (l.Text != s) l.Text = s; }

        private void UpdatePowerInfo(Reading r)
        {
            _lastPl1 = r.Pl1W; _lastPl2 = r.Pl2W;
            var cpu = _controller.Cpu;
            if (cpu.LimitSource == null) return; // static "not supported" text set at build time
            var sb = new System.Text.StringBuilder();
            sb.Append("来源：").Append(cpu.LimitSource);
            if (!double.IsNaN(r.Pl1W)) sb.Append("\n当前：PL1 ").Append(r.Pl1W.ToString("0.#")).Append(" W · PL2 ").Append(r.Pl2W.ToString("0.#")).Append(" W");
            if (!double.IsNaN(r.Pl1TauS)) sb.Append(" · Tau ").Append(r.Pl1TauS.ToString(r.Pl1TauS < 10 ? "0.##" : "0")).Append(" 秒");
            // The CPU enforces the lower of the MSR and MMIO copies. Intel DTT sets the MMIO copy per Fn+Q mode while it runs
            // (measured ~55 / 75 W in balanced); with its extension component disabled it no longer follows Fn+Q at all
            // (stayed 53 / 75 W through all three modes; 25 / 35 W, the BIOS default, right after disabling) and caps
            // every higher MSR value. PawnIO's MCHBAR module is read-only, so only BIOS / DTT can raise it.
            bool mmioLower = !double.IsNaN(r.MmioPl1W) && !double.IsNaN(r.Pl1W) && (r.MmioPl1W < r.Pl1W - 0.5 || r.MmioPl2W < r.Pl2W - 0.5);
            if (!double.IsNaN(r.MmioPl1W))
                sb.Append("\nMMIO：PL1 ").Append(r.MmioPl1W.ToString("0.#")).Append(" W · PL2 ").Append(r.MmioPl2W.ToString("0.#")).Append(" W（CPU 按两者中较低的生效）");
            if (mmioLower)
                sb.Append("\n⚠ 动态 PL（MMIO）更低，实际按它生效。它由 BIOS 开机写入，之后被联想进程管理服务和 Intel DTT 按模式改写，")
                  .Append("本程序不能写：两者都停用后重启电脑，就保持 BIOS 里设的值。");
            if (!cpu.CanWriteLimits) sb.Append("\n不支持设置：").Append(cpu.LimitsUnavailable);
            bool on; lock (_settings) on = _settings.PowerLimitEnabled;
            if (on && r.PowerLimitApplied) sb.Append("\n✓ 已应用自定义值");
            if (r.PowerBoosting) sb.Append("\n打开程序后系统临时提高了 PL（").Append(r.BoostSecondsLeft).Append(" 秒后恢复设定值）");
            if (r.PowerLimitError != null) sb.Append("\n⚠ ").Append(r.PowerLimitError);
            _plInfo.ForeColor = r.PowerLimitError != null || !cpu.CanWriteLimits || mmioLower ? Color.FromArgb(180, 83, 9) : Palette.Text;
            SetText(_plInfo, sb.ToString());
        }

        private void SetPowerLimitEnabled(bool on)
        {
            if (on && MessageBox.Show(this,
                    "打开后程序会持续把 CPU 的 PL1 / PL2 保持在设定值（BIOS、Intel DTT 或 Fn+Q 切换模式改回时会再次写入）。\n\n" +
                    "· 关闭此选项或退出程序时恢复为接管前的数值；程序被强制结束时不会恢复（重启后由 BIOS 重新设定）。\n" +
                    "· 功耗越高温度越高，请配合风扇曲线使用；CPU 自身的温度墙仍然有效。\n\n确定要打开吗？",
                    Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            {
                _loading = true; _plOn.Checked = false; _loading = false;
                return;
            }
            lock (_settings) _settings.PowerLimitEnabled = on;
            Changed();
        }

        private void SetPowerLimits(int pl1, int pl2, bool pl1Changed)
        {
            if (pl2 < pl1)
            {
                // PL2 (short-term) is never below PL1 (long-term): move the other one along.
                _loading = true;
                if (pl1Changed) _pl2.Value = pl1; else _pl1.Value = pl2;
                _loading = false;
                pl1 = (int)_pl1.Value; pl2 = (int)_pl2.Value;
            }
            lock (_settings) { _settings.Pl1W = pl1; _settings.Pl2W = pl2; }
            Changed();
        }

        private void UpdateFreqInfo(Reading r)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("当前：").Append(r.OnAc ? "插电" : "电池").Append(" · ").Append(PowerPlan.ModeName(r.Mode))
              .Append(" · ").Append(r.MultiCoreLoad ? "全核满载" : "平时").Append(double.IsNaN(r.BusyCores) ? "" : "（负载约 " + r.BusyCores.ToString("0.0") + " 核）");
            if (r.FreqP >= 0) sb.Append(" → ").Append(r.FreqP == 0 ? "不限制" : "最高 " + new FreqItem(r.FreqP));
            if (r.FreqBoosting) sb.Append("\n打开程序后的临时加速中（").Append(r.BoostSecondsLeft).Append(" 秒后恢复设定值）");
            if (r.TurboOff) sb.Append("\n睿频已关闭：高于基础频率的上限不起作用");
            bool warn = r.FreqError != null;
            if (r.FreqCorrections > 0) sb.Append("\n1 分钟内被其他程序改写 ").Append(r.FreqCorrections).Append(" 次，已立即改回（").Append(RewriterHint()).Append("）");
            if (r.FreqError != null) sb.Append("\n⚠ ").Append(r.FreqError);
            _freqInfo.ForeColor = warn ? Color.FromArgb(180, 83, 9) : Palette.Text;
            SetText(_freqInfo, sb.ToString());
        }

        /// <summary>
        /// Whole seconds the RAPL time window can hold exactly (2^Y × (1 + F/4) units of 1/1024 s, the usual Intel time
        /// unit): 1–448 s.
        /// </summary>
        private static readonly int[] TauChoices = { 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 14, 16, 20, 24, 28, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 448 };

        private sealed class TauItem
        {
            public readonly int Seconds;
            public TauItem(int s) { Seconds = s; }
            public override string ToString() { return Seconds + " 秒"; }
        }

        /// <summary>The closest offered Tau.</summary>
        private static int SnapTau(int seconds)
        {
            int best = TauChoices[0];
            foreach (int t in TauChoices) if (Math.Abs(t - seconds) < Math.Abs(best - seconds)) best = t;
            return best;
        }

        private void SelectTau(int seconds)
        {
            int snapped = SnapTau(seconds);
            for (int i = 0; i < _tauBox.Items.Count; i++)
                if (((TauItem)_tauBox.Items[i]).Seconds == snapped) { _tauBox.SelectedIndex = i; return; }
        }

        /// <summary>An EPP choice: 0 = performance .. 100 = efficiency.</summary>
        private sealed class EppItem
        {
            public readonly int Value;
            public EppItem(int v) { Value = v; }
            public override string ToString() { return Value == 0 ? "0（性能优先）" : Value == 100 ? "100（省电）" : Value.ToString(); }
        }

        /// <summary>Select the value, adding it (in order) when the list does not have it, e.g. Windows' 25 / 33 / 72.</summary>
        private static void SelectEpp(ComboBox box, int v)
        {
            v = Settings.Clamp(v, 0, 100);
            int at = box.Items.Count;
            for (int i = 0; i < box.Items.Count; i++)
            {
                int x = ((EppItem)box.Items[i]).Value;
                if (x == v) { box.SelectedIndex = i; return; }
                if (x > v) { at = i; break; }
            }
            box.Items.Insert(at, new EppItem(v));
            box.SelectedIndex = at;
        }

        private void UpdateEppInfo(Reading r)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("当前：").Append(r.OnAc ? "插电" : "电池").Append(" · ").Append(PowerPlan.ModeName(r.Mode));
            if (r.EppNow >= 0) sb.Append(" → EPP ").Append(r.EppNow);
            if (r.EppCorrections > 0) sb.Append("\n1 分钟内被其他程序改写 ").Append(r.EppCorrections).Append(" 次，已立即改回（").Append(RewriterHint()).Append("）");
            if (r.EppError != null) sb.Append("\n⚠ ").Append(r.EppError);
            _eppInfo.ForeColor = r.EppError != null ? Color.FromArgb(180, 83, 9) : Palette.Text;
            SetText(_eppInfo, sb.ToString());
        }

        /// <summary>A frequency choice in the dropdowns; 0 = no limit (single-core) / same as single-core (multi-core).</summary>
        private sealed class FreqItem
        {
            public readonly int Mhz;
            public readonly bool Multi;
            public FreqItem(int mhz) : this(mhz, false) { }
            public FreqItem(int mhz, bool multi) { Mhz = mhz; Multi = multi; }
            public override string ToString() { return Mhz == 0 ? (Multi ? "同平时" : "不限制") : (Mhz / 1000.0).ToString("0.0##") + " GHz"; }
        }

        /// <summary>Select the value, adding it (in order) when the list does not have it, e.g. a value Lenovo wrote.</summary>
        private static void SelectFreq(ComboBox box, int mhz)
        {
            mhz = Math.Max(0, mhz);
            int at = box.Items.Count;
            for (int i = 0; i < box.Items.Count; i++)
            {
                int v = ((FreqItem)box.Items[i]).Mhz;
                if (v == mhz) { box.SelectedIndex = i; return; }
                if (v != 0 && v < mhz) { at = i; break; }
            }
            box.Items.Insert(at, new FreqItem(mhz, box.Items.Count > 0 && ((FreqItem)box.Items[0]).Multi));
            box.SelectedIndex = at;
        }

        /// <summary>
        /// Who most likely rewrote the caps / EPP. Measured: Lenovo's service, and once that is off Intel IPF's processor
        /// participant driver (WUDFHost with ipf_umdf2 / ipfsrv, where DTT's policies run): it writes "unlimited" about
        /// 5 s into an all-core load and 5.0 GHz when the load drops, EPP 25 both times. Stopping the DTT (dptftcs) or IPF
        /// (ipfsvc) services does not stop it, and Windows refuses to disable that device (processor thermal subsystem).
        /// </summary>
        private string RewriterHint()
        {
            if (!(_lpmOff.Visible && _lpmOff.Checked)) return "联想进程管理服务在负载变化 / 打开程序时改的，可停用该服务";
            if (!(_dttOff.Visible && _dttOff.Checked)) return "Intel DTT 在负载变化时把频率上限和 EPP 一起改了，约 0.1 秒内改回，可停用 DTT 扩展组件";
            return "约 0.1 秒内改回";
        }

        /// <summary>
        /// The Lenovo / DTT options are system settings that stay after exit, but Lenovo's or Intel's software (e.g. an
        /// update) can turn them back on: when this app turned them on, make sure they still hold at start.
        /// </summary>
        private void EnsureServiceChoices()
        {
            int ppmOriginal, lpmOriginal, dttOriginal;
            lock (_settings) { ppmOriginal = _settings.LenovoPpmOriginal; lpmOriginal = _settings.LpmOriginalStart; dttOriginal = _settings.DttExtOriginal; }
            if (ppmOriginal < 0 && lpmOriginal < 0 && dttOriginal < 0) return;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try { if (ppmOriginal >= 0 && LenovoPpm.Get() == 0) LenovoPpm.Set(1); } catch (Exception) { }
                try
                {
                    if (lpmOriginal >= 0)
                    {
                        bool running;
                        using (var sc = new System.ServiceProcess.ServiceController(LenovoPpm.ServiceName))
                            running = sc.Status != System.ServiceProcess.ServiceControllerStatus.Stopped;
                        if (running || ServiceSwitch.StartType(LenovoPpm.ServiceName) != ServiceSwitch.Disabled) ServiceSwitch.Disable(LenovoPpm.ServiceName);
                    }
                }
                catch (Exception) { }
                try { if (dttOriginal >= 0 && DttComponent.State == 0) DttComponent.SetDisabled(true); } catch (Exception) { }
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        _loading = true;
                        _lenovoPpm.Checked = LenovoPpm.Get() == 1;
                        _lpmOff.Checked = ServiceSwitch.StartType(LenovoPpm.ServiceName) == ServiceSwitch.Disabled;
                        _dttOff.Checked = DttComponent.State == 1;
                        _loading = false;
                    }));
                }
                catch (InvalidOperationException) { }
            });
        }

        private void SetFreqEnabled(bool on)
        {
            lock (_settings) _settings.FreqLimitEnabled = on;
            Changed();
            // Measured on the ThinkBook 14+ 2025: with its PPM control on, Lenovo's service writes its own cap back within 1–3 s.
            if (on && LenovoPpm.Get() == 0 && MessageBox.Show(this,
                    "联想进程管理服务（LenovoProcessManagement）会在 1–3 秒内把频率上限改回它自己的值（实测），\n" +
                    "这里设置的上限无法保持。\n\n是否让它停止调节频率？（把 Policy_DisablePPMCtrl 设为 1 并重启该服务；\n" +
                    "这是系统设置，退出本程序后仍然保持，取消勾选「不让联想进程管理服务调节频率」即可恢复。）",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                _loading = true; _lenovoPpm.Checked = true; _loading = false;
                SetLenovoPpm(true, false);
            }
        }

        private void SetLenovoPpm(bool off, bool confirm)
        {
            if (off && confirm && MessageBox.Show(this,
                    "将修改联想进程管理服务（LenovoProcessManagement）的设置 Policy_DisablePPMCtrl = 1 并重启该服务，\n" +
                    "之后它不再按电量 / 场景改写 CPU 频率上限、EPP 和睿频。\n\n这是系统设置，退出本程序后仍然保持；取消勾选可恢复原值。\n\n确定要修改吗？",
                    Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            {
                _loading = true; _lenovoPpm.Checked = false; _loading = false;
                return;
            }
            int target;
            lock (_settings)
            {
                if (off) { if (_settings.LenovoPpmOriginal < 0) _settings.LenovoPpmOriginal = Math.Max(0, LenovoPpm.Get()); target = 1; }
                else { target = _settings.LenovoPpmOriginal == 1 ? 1 : 0; _settings.LenovoPpmOriginal = -1; }
            }
            Changed();
            _lenovoPpm.Enabled = false;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                string err = null;
                try { LenovoPpm.Set(target); } catch (Exception ex) { err = ex.Message; }
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        _lenovoPpm.Enabled = true;
                        _loading = true; _lenovoPpm.Checked = LenovoPpm.Get() == 1; _loading = false;
                        if (err != null) MessageBox.Show(this, "修改联想进程管理服务失败：" + err, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }));
                }
                catch (InvalidOperationException) { }
            });
        }

        private void SetDttOff(bool off)
        {
            if (off && MessageBox.Show(this,
                    "将禁用 Intel DTT 扩展组件（设备管理器 → 软件组件 → Intel(R) Dynamic Tuning Technology Device Extension Component）。\n\n" +
                    "· Intel DTT 不再在负载变化时改写频率上限和 EPP，这里的设定会一直保持。\n" +
                    "· DTT 的温度 / 功率管理策略随之不再生效：机身可能更热，电池下大负载时功耗不再被它压低。\n" +
                    "  CPU 自身的温度墙和 EC 的风扇保护不受影响。\n" +
                    "· Fn+Q 切模式时改动态 PL（MMIO 功耗限制）的也是 DTT；禁用后动态 PL 不再随模式变化。联想进程管理服务启停时也会写它，\n" +
                    "  两者都停用后重启电脑，它保持 BIOS 设定值。CPU 按 MSR 和 MMIO 中较低的执行，这里设的 PL 高于它时不起作用。\n" +
                    "· 这是系统设置，退出本程序后仍然保持；取消勾选会重新启用该组件。\n\n确定要禁用吗？",
                    Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            {
                _loading = true; _dttOff.Checked = false; _loading = false;
                return;
            }
            lock (_settings) _settings.DttExtOriginal = off ? 0 : -1;
            Changed();
            _dttOff.Enabled = false;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                string err = null;
                try { DttComponent.SetDisabled(off); } catch (Exception ex) { err = ex.Message; }
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        _dttOff.Enabled = true;
                        _loading = true; _dttOff.Checked = DttComponent.State == 1; _loading = false;
                        if (err != null) MessageBox.Show(this, (off ? "禁用" : "启用") + " Intel DTT 扩展组件失败：" + err, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }));
                }
                catch (InvalidOperationException) { }
            });
        }

        /// <summary>Stop and disable a service that rewrites the caps, or put it back as it was.</summary>
        private void SetServiceOff(CheckBox box, string name, bool off)
        {
            const string what = "联想进程管理服务（LenovoProcessManagement）";
            const string effect = "· 它不再在负载变化和打开程序时改写频率上限（剩下 Intel DTT 的改写，可用下一项停用）。\n" +
                "· 联想的应用加速 / 场景调度随之失效，Fn+Q 不再切换模式（Fn+Q 由这个服务处理），可用托盘菜单「电源模式」切换。\n" +
                "· 它也写动态 PL（MMIO 功耗限制）：停止时写成 24 / 70 W，重启电脑后才回到 BIOS 设定值，之后不再被它改动。\n";
            if (off && MessageBox.Show(this, "将停止" + what + "并把它设为禁用。\n\n" + effect +
                    "· 这是系统设置，退出本程序后仍然保持；取消勾选会恢复原来的启动类型并重新启动服务。\n\n确定要停用吗？",
                    Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            {
                _loading = true; box.Checked = false; _loading = false;
                return;
            }
            int restoreTo;
            lock (_settings)
            {
                int now = ServiceSwitch.StartType(name), orig = _settings.LpmOriginalStart;
                if (off) { if (orig < 0) _settings.LpmOriginalStart = now == ServiceSwitch.Disabled || now < 2 ? 2 : now; restoreTo = -1; }
                else { restoreTo = orig >= 2 && orig != ServiceSwitch.Disabled ? orig : 2; _settings.LpmOriginalStart = -1; }
            }
            Changed();
            box.Enabled = _lenovoPpm.Enabled = false; // the policy switch restarts the same service
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                string err = null;
                try { if (off) ServiceSwitch.Disable(name); else ServiceSwitch.Restore(name, restoreTo); } catch (Exception ex) { err = ex.Message; }
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        box.Enabled = _lenovoPpm.Enabled = true;
                        _loading = true; box.Checked = ServiceSwitch.StartType(name) == ServiceSwitch.Disabled; _loading = false;
                        if (err != null) MessageBox.Show(this, (off ? "停用" : "恢复") + what + "失败：" + err, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }));
                }
                catch (InvalidOperationException) { }
            });
        }

        private void InstallPawnIo()
        {
            if (MessageBox.Show(this,
                    "将运行内置的 PawnIO " + PawnIoSetup.BundledVersion + " 官方安装程序（pawnio.eu，签名驱动，兼容内存完整性）。\n" +
                    "安装后需要重新启动本程序。\n\n确定要安装吗？",
                    Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            _pawnInstall.Enabled = false;
            _pawnInstall.Text = "正在安装 PawnIO……";
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                string err = null; bool reboot = false;
                try { reboot = PawnIoSetup.Install(); } catch (Exception ex) { err = ex.Message; }
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (err != null)
                        {
                            _pawnInstall.Enabled = true;
                            _pawnInstall.Text = "安装 PawnIO 驱动（程序内置 v" + PawnIoSetup.BundledVersion + "）";
                            MessageBox.Show(this, "安装 PawnIO 失败：" + err, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                        _pawnInstall.Visible = false;
                        string msg = reboot ? "PawnIO 已安装，Windows 提示需要重启电脑后才能完全生效。\n\n现在先重新启动本程序吗？" : "PawnIO 已安装。现在重新启动本程序以启用功耗读取和 PL 设置吗？";
                        if (MessageBox.Show(this, msg, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes) RestartApp();
                    }));
                }
                catch (InvalidOperationException) { }
            });
        }

        /// <summary>Start a new instance (it waits for this one to exit before taking the single-instance mutex) and exit.</summary>
        private void RestartApp()
        {
            try
            {
                Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--after " + Process.GetCurrentProcess().Id) { UseShellExecute = false });
            }
            catch (Exception ex) { MessageBox.Show(this, "无法重新启动：" + ex.Message, Text); return; }
            ExitApp();
        }

        private bool ModeSupported(FanMode m) { return _controller.Backend.CanSetSpeed || m == FanMode.Auto || m == FanMode.Max; }

        private string TargetText(FanInfo f, int target, bool unit)
        {
            if (target == FanTarget.Auto) return "自动";
            if (target == FanTarget.Stop) return "停转";
            if (!_controller.Backend.CanSetSpeed) return "全速";
            return unit ? f.Format(target) : target.ToString();
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume) _controller.ForceRewrite();
        }

        private void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            _controller.StopAndRestore();
        }

        private void ShowMain()
        {
            ShowInTaskbar = true;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            bool tray; lock (_settings) tray = _settings.MinimizeToTray;
            if (WindowState == FormWindowState.Minimized && tray && Visible) Hide();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            bool tray; lock (_settings) tray = _settings.MinimizeToTray;
            if (!_exiting && tray && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            Shutdown();
            base.OnFormClosing(e);
        }

        private void ExitApp()
        {
            _exiting = true;
            Close();
        }

        private void Shutdown()
        {
            if (_remote != null) { _remote.Dispose(); _remote = null; }
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionEnding -= OnSessionEnding;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _saveTimer.Stop();
            _settings.Save(_fans);
            _controller.StopAndRestore();
            _tray.Visible = false;
            _tray.Dispose();
            FanIcon.DestroyTrayIcon(_trayIcon);
        }

        // Light / dark taskbar switch: redraw the tray glyph in the contrasting colour.
        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category != UserPreferenceCategory.General && e.Category != UserPreferenceCategory.VisualStyle) return;
            BeginInvoke(new Action(() =>
            {
                var old = _trayIcon;
                _trayIcon = FanIcon.CreateTrayIcon(SystemInformation.SmallIconSize.Width);
                _tray.Icon = _trayIcon;
                FanIcon.DestroyTrayIcon(old);
            }));
        }

        // ------------------------------------------------------------------ autostart (Task Scheduler, runs elevated without UAC)

        private static int Schtasks(string args)
        {
            var psi = new ProcessStartInfo("schtasks.exe", args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = Process.Start(psi)) { p.WaitForExit(10000); return p.ExitCode; }
        }

        private static bool IsAutoStart() { try { return Schtasks("/Query /TN " + TaskName) == 0; } catch (Exception) { return false; } }

        /// <summary>
        /// Replace the task registered under the old app name (it points to the old exe). Silent; the old task
        /// is removed only once the new one exists.
        /// </summary>
        private void MigrateAutoStart()
        {
            try
            {
                if (Schtasks("/Query /TN " + OldTaskName) != 0) return;
                if (IsAutoStart() || CreateTask() == 0) Schtasks("/Delete /F /TN " + OldTaskName);
            }
            catch (Exception) { }
        }

        private static int CreateTask()
        {
            string user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            string xml = string.Format(TaskXml, System.Security.SecurityElement.Escape(user), System.Security.SecurityElement.Escape(Application.ExecutablePath));
            string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ThinkBookFanControl-task.xml");
            System.IO.File.WriteAllText(tmp, xml, System.Text.Encoding.Unicode);
            try { return Schtasks("/Create /F /TN " + TaskName + " /XML \"" + tmp + "\""); }
            finally { try { System.IO.File.Delete(tmp); } catch (Exception) { } }
        }

        // XML instead of /SC ONLOGON flags: schtasks cannot clear the "only on AC power" conditions, which matter on a laptop.
        private const string TaskXml = @"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{0}</UserId><Delay>PT10S</Delay></LogonTrigger></Triggers>
  <Principals><Principal id=""Author""><UserId>{0}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>5</Priority>
  </Settings>
  <Actions Context=""Author""><Exec><Command>{1}</Command><Arguments>--minimized</Arguments></Exec></Actions>
</Task>";

        private void SetAutoStart(bool on)
        {
            int code;
            try { code = on ? CreateTask() : Schtasks("/Delete /F /TN " + TaskName); }
            catch (Exception) { code = -1; }
            if (code != 0)
            {
                MessageBox.Show(this, "设置开机自启动失败（schtasks 返回 " + code + "）。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _loading = true; _autoStart.Checked = IsAutoStart(); _loading = false;
            }
        }
    }
}
