using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ThinkBookFanControl
{
    public class MainForm : Form
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
        private bool _exiting, _loading;

        private Label _tempLabel, _statusLabel;
        private Label[] _rpmLabels, _targetLabels;
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
        private NumericUpDown _hyst, _smooth, _critical;
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
            _rpmLabels = new Label[_fans.Count];
            _targetLabels = new Label[_fans.Count];
            for (int i = 0; i < _fans.Count; i++)
            {
                _rpmLabels[i] = Tile(status, _fans[i].Name + " 实际转速", Palette.FanColor(i));
                _targetLabels[i] = Tile(status, _fans[i].Name + " 目标", Palette.Text);
            }
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
                _tracks[i] = new TrackBar { Minimum = f.MinRpm / 100, Maximum = f.MaxRpm / 100, TickFrequency = 5, Width = S(190), SmallChange = 1, LargeChange = 5, TickStyle = TickStyle.None };
                _fixedNums[i] = new NumericUpDown { Minimum = f.MinRpm, Maximum = f.MaxRpm, Increment = 100, Width = S(80), Margin = Pd(3, 6, 3, 3) };
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

            var genBox = Group(left, "安全与常规");
            var genFlow = Flow(genBox);
            genFlow.Controls.Add(new Label
            {
                Text = "本机：" + Program.ModelName + (Program.ModelVerified ? "（已验证）" : "（未验证机型，请留意温度和转速）"),
                AutoSize = true, MaximumSize = Sz(280, 0), Margin = Pd(3, 4, 3, 4),
                ForeColor = Program.ModelVerified ? Palette.Text : Color.FromArgb(180, 83, 9)
            });
            _critical = Num(Row(genFlow, "高温保护 (°C)，达到即全速"), 75, 100);
            _critical.ValueChanged += (s, e) => { if (_loading) return; lock (_settings) _settings.CriticalTempC = (int)_critical.Value; Changed(); };
            _allowLow = Check(genFlow, "允许低于额定最低转速 / 停转");
            _allowLow.CheckedChanged += (s, e) => { if (!_loading) SetAllowLow(_allowLow.Checked); };
            _lowMaxTemp = Num(Row(genFlow, "    仅在温度低于 (°C) 时生效"), 35, 70);
            _lowMaxTemp.ValueChanged += (s, e) => { if (_loading) return; lock (_settings) _settings.LowSpeedMaxTempC = (int)_lowMaxTemp.Value; Changed(); };
            _minToTray = Check(genFlow, "关闭窗口时最小化到托盘");
            _minToTray.CheckedChanged += (s, e) => { if (_loading) return; lock (_settings) _settings.MinimizeToTray = _minToTray.Checked; Changed(); };
            _autoStart = Check(genFlow, "开机自动启动（计划任务，免 UAC）");
            _autoStart.CheckedChanged += (s, e) => { if (!_loading) SetAutoStart(_autoStart.Checked); };
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

        }

        private Label Tile(Control parent, string caption, Color color)
        {
            var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = Pd(4, 0, 18, 0) };
            p.Controls.Add(new Label { Text = caption, AutoSize = true, ForeColor = Palette.Text, Margin = Pd(0, 0, 0, 0) });
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
            menu.Items.Add(_trayProfiles);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("恢复自动并退出", null, (s, e) => ExitApp());
            _trayIcon = FanIcon.CreateTrayIcon(SystemInformation.SmallIconSize.Width);
            _tray = new NotifyIcon { Icon = _trayIcon, Text = Text, Visible = true, ContextMenuStrip = menu };
            _tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowMain(); };
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
                _critical.Value = _settings.CriticalTempC;
                _minToTray.Checked = _settings.MinimizeToTray;
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
            if (v0 == 0 && prev == 0 && rpm > 0) v0 = LenovoWmi.LowestStableRpm;
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
            bool low; lock (_settings) low = _settings.AllowLowSpeed;
            return low && _tracks[i].Value == _tracks[i].Minimum ? 0 : _tracks[i].Value * 100;
        }

        private void RefreshFixedUi()
        {
            _loading = true;
            for (int i = 0; i < _fans.Count; i++)
            {
                int v; lock (_settings) v = _settings.FixedRpm[i];
                _tracks[i].Value = v == 0 ? _tracks[i].Minimum : Settings.Clamp(v / 100, _tracks[i].Minimum, _tracks[i].Maximum);
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
                _fixedLabels[i].Text = low
                    ? string.Format("{0}（{1}–{2} RPM，0 = 停转）", f.Name, LenovoWmi.LowestStableRpm, f.MaxRpm)
                    : string.Format("{0}（{1}–{2} RPM）", f.Name, f.MinRpm, f.MaxRpm);
                _tracks[i].Minimum = low ? LenovoWmi.LowestStableRpm / 100 - 1 : f.MinRpm / 100;
                _fixedNums[i].Minimum = low ? 0 : f.MinRpm;
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
                    "打开后，固定转速和曲线可以设到 " + LenovoWmi.LowestStableRpm + " RPM，或设为 0 让风扇停转。\n\n" +
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
            _tempLabel.Text = r.Temp >= 0 ? r.Temp + " °C" : "--";
            var tip = "CPU " + (r.Temp >= 0 ? r.Temp + "°C" : "--");
            for (int i = 0; i < _fans.Count; i++)
            {
                _rpmLabels[i].Text = r.Rpm[i] >= 0 ? r.Rpm[i] + " RPM" : "--";
                _targetLabels[i].Text = r.Target[i] == 0 ? "自动" : r.Target[i] == LenovoWmi.StopValue ? "停转" : r.Target[i] + " RPM";
                tip += " | " + (r.Rpm[i] >= 0 ? r.Rpm[i].ToString() : "--");
            }
            if (r.Failsafe) { _statusLabel.ForeColor = Color.Firebrick; _statusLabel.Text = "⚠ 高温保护已触发：风扇全速，温度回落后自动恢复"; }
            else if (r.Error != null) { _statusLabel.ForeColor = Color.Firebrick; _statusLabel.Text = r.Error; }
            else { _statusLabel.ForeColor = Palette.Text; _statusLabel.Text = ""; }

            _history.Add(r.Temp, r.Rpm);
            _curve.SetLive(r.CurveTemp, r.Target);
            tip += " RPM";
            _tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
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
