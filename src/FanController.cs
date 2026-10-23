using System;
using System.Collections.Generic;
using System.Threading;

namespace ThinkBookFanControl
{
    public class Reading
    {
        public int Temp;          // raw CPU temperature, -1 if unreadable
        public double CurveTemp;  // smoothed + hysteresis temperature fed to the curve
        public int[] Rpm;         // measured RPM per fan, -1 if unknown
        public int[] Target;      // written target per fan (fan Unit), FanTarget.Auto / FanTarget.Stop
        public int[] Desired;     // where the ramp is heading (same encoding as Target)
        public bool Failsafe;     // over-temperature protection active
        public string Error;

        // CPU, NaN when unavailable
        public double CpuMhz, CpuUsage, CpuPowerW, Pl1W, Pl2W, Pl1TauS, MmioPl1W, MmioPl2W;
        public bool PowerLimitApplied;  // our PL1 / PL2 are in effect
        public string PowerLimitError;
        public string MmioChange;       // last change of the MMIO copy since start ("time: old → new, temperature"), null = none

        /// <summary>What the CPU enforces: PL1 and PL2 each the lower of the MSR and MMIO copies (MSR alone without MMIO).</summary>
        public double EffectivePl1W { get { return double.IsNaN(MmioPl1W) ? Pl1W : Math.Min(Pl1W, MmioPl1W); } }
        public double EffectivePl2W { get { return double.IsNaN(MmioPl2W) ? Pl2W : Math.Min(Pl2W, MmioPl2W); } }
        public bool MmioLower { get { return !double.IsNaN(Pl1W) && (EffectivePl1W < Pl1W - 0.5 || EffectivePl2W < Pl2W - 0.5); } }

        // Windows power state and the frequency caps that apply to it right now (MHz, 0 = no limit, -1 = unknown)
        public PowerMode Mode;
        public bool OnAc;
        public int FreqP = -1, FreqE = -1;
        public bool TurboOff;           // PERFBOOSTMODE = 0 in the current state
        public bool FreqBoosting, PowerBoosting; // the system raised caps / PL after an app launch (allowed, restored afterwards)
        public int BoostSecondsLeft;    // app-launch boost window, 0 = none
        public double BusyCores = double.NaN; // busy time of all logical processors added up, in cores (NaN = unknown)
        public double CoreTempAvg = double.NaN, CoreTempMax = double.NaN; // per-core DTS temperatures (MSR 0x19C), °C
        public double PCoreMhz = double.NaN, ECoreMhz = double.NaN;      // average per core type; E = NaN on non-hybrid CPUs
        public bool MultiCoreLoad;      // the all-core-load frequency caps are in use
        public int EppNow = -1;         // EPP that applies right now (0 = performance .. 100 = efficiency), -1 = unknown
        public int EppCorrections;      // EPP values somebody else changed back within the last minute
        public string EppError;
        public int FreqCorrections;     // values somebody else changed back within the last minute
        public string FreqError;
        public bool HwInfoNeedsRestart; // HWiNFO is running but was started before our sensors existed
    }

    /// <summary>
    /// Background loop (1 Hz): reads sensors, computes per-fan targets from the settings and writes them, applies the
    /// CPU power limits and publishes everything to HWiNFO. Owns the backend; all hardware access happens on this thread.
    /// </summary>
    public class FanController
    {
        private const int FailsafeReleaseC = 5;   // leave over-temperature mode this many °C below the limit
        private const int SensorLostMs = 3000;    // hand control back to the EC after this long without a temperature
        private const int PowerLimitRetryMs = 2000; // Intel DTT raises PL1 / PL2 for a while after app launches

        private readonly IFanBackend _backend;
        private readonly List<FanInfo> _fans;
        private readonly Settings _settings;
        private readonly CpuMonitor _cpu;
        private readonly LenovoWmi _ownedWmi;     // opened only for the power-limit features when the backend is not WMI
        private readonly List<CurvePoint>[] _quiet, _performance;
        private readonly int[] _written;
        private readonly long[] _writtenAt;       // _clock ms of the last write per fan
        private readonly double[] _ramp;          // current ramp position per fan (fan Unit, 0 = stopped), NaN = unknown
        // Poke() makes extra ticks, so everything that means "seconds" is measured on the clock, not counted in ticks.
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private readonly System.Diagnostics.Stopwatch _tickClock = System.Diagnostics.Stopwatch.StartNew();
        private readonly Queue<int> _tempWindow = new Queue<int>();
        private long _tempSampledAt = long.MinValue / 2, _tempOkAt; // "/ 2": now - sentinel must not overflow
        private double _curveTemp = double.NaN;
        private bool _failsafe;
        private bool _lowActive;
        private Thread _thread;
        private volatile bool _running, _stopped, _resumed, _restored;
        private readonly object _hw = new object(); // held by a tick; StopAndRestore takes it so no write follows the restore
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);

        // Power limits: what the hardware held before we took over (restored bit for bit), last attempt, error.
        private object _plOriginal;
        private int _plTriedPl1, _plTriedPl2, _plTriedTau;
        private long _plTriedAt = long.MinValue / 2;
        private string _plError;
        private bool _plWasBoosting;
        private double _mmioPl1 = double.NaN, _mmioPl2 = double.NaN;
        private string _mmioChange;

        private HwInfoPublisher _hwinfo;
        private long _hwinfoCheckedAt = long.MinValue / 2;
        private int _hwinfoMissing;

        // P / E core frequency caps in the Windows power settings.
        private readonly FreqLimiter _freq = new FreqLimiter();
        private int[] _freqLast;            // table at the last enforce (null = not enforcing)
        private bool _freqLastTurbo;
        private long _freqCheckedAt = long.MinValue / 2;
        private volatile bool _freqForce, _eppForce;
        private readonly List<long> _freqCorrections = new List<long>();
        private string _freqError;
        private const int FreqCheckMs = 5000;
        private const int FreqReactMs = 30, FreqReactSlowMs = 200; // see ReactMs
        private PowerSettingsWatcher _freqWatcher;  // shared by the frequency caps and EPP

        // Energy performance preference per mode x power source (separate instance: turning one feature off does not
        // put back the other's values).
        private readonly FreqLimiter _epp = new FreqLimiter();
        private static readonly Guid[] EppSettings = { PowerPlan.Epp1, PowerPlan.Epp0 };
        private int[] _eppLast;
        private long _eppCheckedAt = long.MinValue / 2;
        private readonly List<long> _eppCorrections = new List<long>();
        private string _eppError;
        // App-launch boost: after a newly started program comes to the foreground, Lenovo's process management service
        // raises the caps (and PL may be raised) for a while; with boost allowed they are left alone until the window ends
        // (it also rewrites the caps after every load change, so "until it stops writing" would never end).
        private const int LaunchBoostMs = 10000, LaunchBoostMaxMs = 30000;
        private long _launchStart, _launchUntil = long.MinValue / 2;
        private int _lastForegroundPid;
        private bool _freqHeld;
        private bool _multiLoad;
        private long _powerWroteAt = long.MinValue / 2; // last time we applied power settings (caps / EPP)
        private volatile bool _fastTicks;
        private long _fullTickAt = long.MinValue / 2;
        private readonly double[] _loadHistory = new double[4]; // last busy-core samples, newest at _loadNext - 1
        private int _loadNext, _loadSamples;

        public event Action<Reading> Updated;

        public FanController(IFanBackend backend, Settings settings)
        {
            _backend = backend; _fans = backend.Fans; _settings = settings;
            _quiet = Curve.Preset(FanMode.Quiet, _fans);
            _performance = Curve.Preset(FanMode.Performance, _fans);
            _written = new int[_fans.Count];
            _writtenAt = new long[_fans.Count];
            _ramp = new double[_fans.Count];
            var wmi = backend as LenovoWmi;
            if (wmi == null) wmi = _ownedWmi = LenovoWmi.OpenRaw();
            _cpu = new CpuMonitor(wmi);
            ForceRewrite();
        }

        public IFanBackend Backend { get { return _backend; } }
        public CpuMonitor Cpu { get { return _cpu; } }

        public void Start()
        {
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "FanController" };
            _thread.Start();
        }

        /// <summary>Apply settings changes immediately instead of waiting for the next tick.</summary>
        public void Poke() { _wake.Set(); }

        /// <summary>Forget what was written, e.g. after resume from sleep.</summary>
        public void ForceRewrite()
        {
            lock (_written) { for (int i = 0; i < _written.Length; i++) { _written[i] = int.MinValue; _ramp[i] = double.NaN; } }
            _plTriedAt = long.MinValue / 2;
            _freqForce = _eppForce = true;
            _resumed = true;
            _wake.Set();
        }

        /// <summary>Stop the loop, hand all fans back to EC automatic control and restore the original power limits.</summary>
        public void StopAndRestore()
        {
            _running = false;
            _stopped = true; // a tick still running skips its remaining writes
            _wake.Set();
            bool locked = Thread.CurrentThread == _thread || Monitor.TryEnter(_hw, 5000);
            try
            {
                if (_restored) return; // SessionEnding and FormClosing both get here
                _restored = true;
                _backend.RestoreAuto();
                if (_plOriginal != null) { try { _cpu.RestoreLimits(_plOriginal); } catch (Exception) { } _plOriginal = null; }
                try { _freq.Restore(); } catch (Exception) { }
                try { _epp.Restore(); } catch (Exception) { }
                if (_freqWatcher != null) { _freqWatcher.Dispose(); _freqWatcher = null; }
                if (_hwinfo != null) { _hwinfo.Dispose(); _hwinfo = null; }
                _cpu.Dispose();
                if (_ownedWmi != null) _ownedWmi.Dispose();
            }
            finally { if (locked && Thread.CurrentThread != _thread) Monitor.Exit(_hw); }
        }

        private void Loop()
        {
            while (_running)
            {
                try
                {
                    lock (_hw)
                    {
                        if (!_stopped)
                        {
                            // Woken by a rewritten power setting between regular ticks: put the caps / EPP back at once,
                            // without the 50–100 ms of sensor reads a full tick takes (EPP stayed 0.1–0.2 s that way).
                            long now = _clock.ElapsedMilliseconds;
                            if ((_freqForce || _eppForce) && now - _fullTickAt < TickMs - 30) Reassert(now);
                            else { _fullTickAt = now; Tick(); }
                        }
                    }
                }
                catch (Exception ex)
                {
                    var r = new Reading { Temp = -1, Rpm = new int[_fans.Count], Target = new int[_fans.Count], Desired = new int[_fans.Count], Error = ex.Message };
                    for (int i = 0; i < r.Rpm.Length; i++) r.Rpm[i] = -1;
                    r.CurveTemp = r.CpuMhz = r.CpuUsage = r.CpuPowerW = r.Pl1W = r.Pl2W = r.Pl1TauS = r.MmioPl1W = r.MmioPl2W = double.NaN;
                    Raise(r);
                }
                // A reported change that came too soon is handled shortly; regular ticks keep their cadence (with
                // all-core caps the load is sampled 4 times a second so an all-core load gets its cap within ~0.5 s).
                _wake.WaitOne(_freqForce || _eppForce ? ReactMs : (int)Math.Max(1, TickMs - (_clock.ElapsedMilliseconds - _fullTickAt)));
            }
        }

        private int TickMs { get { return _fastTicks ? 250 : 1000; } }

        /// <summary>Only the power-setting part of a tick: caps and EPP as the last full tick decided them.</summary>
        private void Reassert(long now)
        {
            bool freqOn, dcTurbo, allowBoost, eppOn; int[] freqTable, freqMulti, eppTable;
            lock (_settings)
            {
                freqOn = _settings.FreqLimitEnabled; freqTable = (int[])_settings.FreqMhz.Clone(); dcTurbo = _settings.DcTurbo;
                freqMulti = (int[])_settings.FreqMultiMhz.Clone();
                eppOn = _settings.EppEnabled; eppTable = (int[])_settings.EppPct.Clone();
                allowBoost = _settings.AllowBoost;
            }
            var r = new Reading();
            ApplyFreqLimits(r, freqOn, EffectiveCaps(freqTable, freqMulti, _multiLoad), dcTurbo, allowBoost && now < _launchUntil && !_multiLoad, now);
            ApplyEpp(r, eppOn, eppTable, now);
        }

        private void Tick()
        {
            var r = new Reading { Rpm = new int[_fans.Count], Target = new int[_fans.Count], Desired = new int[_fans.Count] };
            double dt = Math.Min(2.0, _tickClock.Elapsed.TotalSeconds); // Poke() can make ticks early
            _tickClock.Restart();
            long now = _clock.ElapsedMilliseconds;
            if (_resumed) { _resumed = false; _cpu.ResetPowerSample(); }

            // ---- sensors
            try { r.Temp = _backend.ReadCpuTemp(); _tempOkAt = now; }
            catch (Exception ex) { r.Temp = -1; r.Error = "读取温度失败: " + ex.Message; }
            for (int i = 0; i < _fans.Count; i++)
            {
                try { r.Rpm[i] = _backend.ReadFanRpm(_fans[i]); } catch (Exception) { r.Rpm[i] = -1; }
            }
            _cpu.Sample(r);
            TrackMmio(r);

            // ---- snapshot settings
            FanMode mode; int[] fixedRpm, freqTable, freqMulti, eppTable; List<CurvePoint>[] curves; bool linkFixed, linkCurves, allowLow, rampOn, plOn, hwinfo, freqOn, dcTurbo, allowBoost, eppOn;
            int hyst, smooth, critical, lowMaxTemp, rampUp, rampDown, pl1, pl2, tau;
            lock (_settings)
            {
                mode = _settings.Mode;
                fixedRpm = (int[])_settings.FixedRpm.Clone();
                curves = new List<CurvePoint>[_fans.Count];
                for (int i = 0; i < _fans.Count; i++) curves[i] = new List<CurvePoint>(_settings.Curves[i]);
                linkFixed = _settings.LinkFixed; linkCurves = _settings.LinkCurves;
                hyst = _settings.HysteresisC; smooth = _settings.SmoothingSec; critical = _settings.CriticalTempC;
                allowLow = _settings.AllowLowSpeed; lowMaxTemp = _settings.LowSpeedMaxTempC;
                rampOn = _settings.RampEnabled; rampUp = _settings.RampUpRpmPerSec; rampDown = _settings.RampDownRpmPerSec;
                plOn = _settings.PowerLimitEnabled; pl1 = _settings.Pl1W; pl2 = _settings.Pl2W; tau = _settings.Pl1TauS;
                hwinfo = _settings.HwInfoEnabled;
                freqOn = _settings.FreqLimitEnabled; freqTable = (int[])_settings.FreqMhz.Clone(); dcTurbo = _settings.DcTurbo;
                freqMulti = (int[])_settings.FreqMultiMhz.Clone();
                eppOn = _settings.EppEnabled; eppTable = (int[])_settings.EppPct.Clone();
                allowBoost = _settings.AllowBoost;
            }
            if (!_backend.CanSetSpeed && mode != FanMode.Max) mode = FanMode.Auto; // only auto / full speed

            // ---- smoothed temperature with hysteresis: rises are followed at once, falls lag by `hyst` °C
            if (r.Temp >= 0)
            {
                if (now - _tempSampledAt >= 900) // one sample per second, however often we tick
                {
                    _tempSampledAt = now;
                    _tempWindow.Enqueue(r.Temp);
                }
                while (_tempWindow.Count > smooth) _tempWindow.Dequeue();
                if (_tempWindow.Count > 0)
                {
                    double avg = 0; foreach (int t in _tempWindow) avg += t; avg /= _tempWindow.Count;
                    _curveTemp = double.IsNaN(_curveTemp) ? avg : Math.Max(avg, Math.Min(_curveTemp, avg + hyst));
                }

                if (r.Temp >= critical) _failsafe = true;
                else if (r.Temp < critical - FailsafeReleaseC) _failsafe = false;
            }
            r.CurveTemp = _curveTemp;
            r.Failsafe = _failsafe;

            // ---- targets
            bool sensorLost = r.Temp < 0 && now - _tempOkAt >= SensorLostMs;
            // Below-rated speeds and stop only while it is cool enough (and the temperature is actually known).
            // Leave at the limit, re-enter only after cooling by the hysteresis, so the fan does not cycle on/off.
            if (!allowLow || sensorLost || r.Temp < 0 || r.Temp >= lowMaxTemp) _lowActive = false;
            else if (r.Temp < lowMaxTemp - Math.Max(2, hyst)) _lowActive = true;
            bool lowOk = _lowActive;
            for (int i = 0; i < _fans.Count; i++)
            {
                FanInfo f = _fans[i];
                int target;  // desired speed in the fan's unit, -1 = EC auto
                if (_failsafe) target = f.MaxRpm;
                else if (sensorLost && (mode == FanMode.Curve || mode == FanMode.Quiet || mode == FanMode.Performance))
                    target = -1; // no temperature: let the EC decide
                else
                {
                    switch (mode)
                    {
                        case FanMode.Fixed: target = fixedRpm[linkFixed ? 0 : i]; break;
                        case FanMode.Curve:
                            target = double.IsNaN(_curveTemp) ? -1 : (int)Math.Round(Curve.Evaluate(curves[linkCurves ? 0 : i], _curveTemp));
                            break;
                        case FanMode.Quiet:
                        case FanMode.Performance:
                            var preset = mode == FanMode.Quiet ? _quiet[i] : _performance[i];
                            target = double.IsNaN(_curveTemp) ? -1 : (int)Math.Round(Curve.Evaluate(preset, _curveTemp));
                            break;
                        case FanMode.Max: target = f.MaxRpm; break;
                        default: target = -1; break;
                    }
                }
                if (target < 0)
                {
                    target = r.Desired[i] = FanTarget.Auto;
                    lock (_written) _ramp[i] = double.NaN;
                }
                else
                {
                    // Anything below the rated minimum is only written when low speed is allowed and it is cool enough.
                    int desired = Settings.SnapRpm(target, f, lowOk); // 0 = stop
                    r.Desired[i] = desired == 0 ? FanTarget.Stop : desired;
                    double pos;
                    lock (_written) pos = _ramp[i];
                    int measured = f.IsRpm ? r.Rpm[i] : -1;
                    pos = Ramp(pos, desired, measured, lowOk && f.LowestStable > 0 ? f.LowestStable : f.MinRpm,
                               _failsafe || !rampOn || !_backend.CanSetSpeed, Curve.Scale(f, rampUp) * dt, Curve.Scale(f, rampDown) * dt);
                    lock (_written) _ramp[i] = pos;
                    target = pos <= 0 ? 0 : Settings.SnapRpm((int)Math.Round(pos), f, lowOk);
                    if (target == 0) target = FanTarget.Stop;
                }
                r.Target[i] = target;

                bool write;
                lock (_written)
                {
                    // Reassert a little early so a 1 s interval really means every tick.
                    write = _written[i] != target || (target != FanTarget.Auto && now - _writtenAt[i] >= _backend.ReassertSeconds * 1000 - 200);
                }
                if (write && !_stopped)
                {
                    try
                    {
                        _backend.WriteFan(f, target);
                        lock (_written) { _written[i] = target; _writtenAt[i] = now; }
                    }
                    catch (Exception ex) { r.Error = "写入" + f.Name + "失败: " + ex.Message; }
                }
            }

            DetectLaunch(now);
            bool boosting = allowBoost && now < _launchUntil && !_multiLoad; // never under all-core load
            r.BoostSecondsLeft = boosting ? (int)Math.Ceiling((_launchUntil - now) / 1000.0) : 0;
            if (!_stopped) ApplyPowerLimits(r, plOn, pl1, pl2, tau, boosting, now);
            UpdateLoadState(r, now);
            _fastTicks = freqOn && Array.Exists(freqMulti, m => m > 0);
            WatchPowerSettings(freqOn || eppOn);
            if (!_stopped) ApplyFreqLimits(r, freqOn, EffectiveCaps(freqTable, freqMulti, r.MultiCoreLoad), dcTurbo, boosting, now);
            if (!_stopped) ApplyEpp(r, eppOn, eppTable, now);
            Publish(r, hwinfo);
            Raise(r);
        }

        /// <summary>
        /// Remember the last change of the MMIO copy of PL1 / PL2 and how hot the CPU was then. Besides the BIOS at boot
        /// and Intel DTT, the EC writes it on its own: it puts PL1 / PL2 in its RAM (PL1V / PL2V) and raises ACPI event
        /// _Q48, whose DSDT handler copies them ×8 into MCHBAR + 0x59A0 (CPL1 / CPL2) unless EC fields NSAP / MMCM are set
        /// (the DSDT's thermal-mode code sets MMCM in one mode). On the ThinkBook 14+ 2025 it wrote 24 / 70 W at the temperature
        /// limit with the Lenovo service and the DTT component both disabled. Nothing here can write MMIO back.
        /// </summary>
        private void TrackMmio(Reading r)
        {
            if (!double.IsNaN(r.MmioPl1W))
            {
                if (!double.IsNaN(_mmioPl1) && (Math.Abs(r.MmioPl1W - _mmioPl1) >= 0.5 || Math.Abs(r.MmioPl2W - _mmioPl2) >= 0.5))
                    _mmioChange = string.Format("{0:HH:mm:ss} 由 {1:0.#} / {2:0.#} W 改为 {3:0.#} / {4:0.#} W{5}", DateTime.Now,
                        _mmioPl1, _mmioPl2, r.MmioPl1W, r.MmioPl2W, r.Temp >= 0 ? "，当时 CPU " + r.Temp + "°C" : "");
                _mmioPl1 = r.MmioPl1W; _mmioPl2 = r.MmioPl2W;
            }
            r.MmioChange = _mmioChange;
        }

        /// <summary>
        /// Keep our PL1 / PL2 in effect while enabled (the BIOS, Intel DTT or Lenovo's mode switch may change them),
        /// and put the original values back when disabled. Nothing is written before the original values are saved.
        /// </summary>
        private void ApplyPowerLimits(Reading r, bool on, int pl1, int pl2, int tau, bool boosting, long now)
        {
            bool retry = now - _plTriedAt >= PowerLimitRetryMs;
            if (on && _cpu.CanWriteLimits)
            {
                _cpu.ClampLimits(ref pl1, ref pl2);
                if (_plOriginal == null) _plOriginal = _cpu.SaveLimits();
                if (_plOriginal == null) { r.PowerLimitError = "读不到当前 PL1 / PL2，暂不接管"; return; }
                double tauWanted = tau > 0 ? _cpu.QuantizeTau(tau) : double.NaN; // NaN: leave Tau alone
                bool matches = LimitsMatch(r.Pl1W, r.Pl2W, r.Pl1TauS, pl1, pl2, tauWanted);
                bool changed = pl1 != _plTriedPl1 || pl2 != _plTriedPl2 || tau != _plTriedTau;
                bool raised = r.Pl1W > pl1 + 0.5 || r.Pl2W > pl2 + 0.5;
                if (!matches && !changed && raised && boosting) { r.PowerBoosting = true; retry = false; } // launch boost: leave it
                else if (_plWasBoosting && !matches) retry = true; // window over: put ours back now
                _plWasBoosting = r.PowerBoosting;
                if (!matches && (changed || retry))
                {
                    _plTriedPl1 = pl1; _plTriedPl2 = pl2; _plTriedTau = tau; _plTriedAt = now;
                    try
                    {
                        _cpu.WriteLimits(pl1, pl2, double.IsNaN(tauWanted) ? 0 : tau);
                        _plError = null;
                        double a, b, t;
                        if (_cpu.ReadLimits(out a, out b, out t)) { r.Pl1W = a; r.Pl2W = b; r.Pl1TauS = t; matches = LimitsMatch(a, b, t, pl1, pl2, tauWanted); }
                    }
                    catch (Exception ex) { _plError = ex.Message; }
                }
                r.PowerLimitApplied = matches;
            }
            else
            {
                bool justDisabled = _plTriedPl1 != 0;
                _plTriedPl1 = _plTriedPl2 = 0;
                if (_plOriginal == null) _plError = null;
                else if (retry || justDisabled)
                {
                    // Keep the saved values until they are back (e.g. WMI refuses outside custom mode): retry later.
                    _plTriedAt = now;
                    try { _cpu.RestoreLimits(_plOriginal); _plOriginal = null; _plError = null; }
                    catch (Exception ex) { _plError = "恢复原 PL1 / PL2 失败：" + ex.Message; }
                }
            }
            r.PowerLimitError = _plError;
        }

        /// <summary>
        /// Keep the P / E core caps in the Windows power settings (Windows itself switches them with the power source
        /// and power mode). Checked every few seconds: anything changed back by someone else is written again and
        /// counted, so the UI can point at Lenovo's process management service when it keeps fighting.
        /// </summary>
        private void ApplyFreqLimits(Reading r, bool on, int[] table, bool dcTurbo, bool boosting, long now)
        {
            try
            {
                r.Mode = PowerPlan.CurrentMode;
                r.OnAc = PowerPlan.OnAc;
                Guid scheme = PowerPlan.SchemeOf(r.Mode, PowerPlan.ActiveScheme);
                r.FreqP = (int)PowerPlan.Read(scheme, PowerPlan.FreqMaxClass1, r.OnAc);
                r.TurboOff = PowerPlan.Read(scheme, PowerPlan.BoostMode, r.OnAc) == 0;
            }
            catch (Exception) { }

            if (!on) _freqForce = false; // nothing to re-check (EPP may still be watching)
            if (on)
            {
                bool changed = _freqLast == null || !SameTable(table, _freqLast) || dcTurbo != _freqLastTurbo;
                if (changed || (_freqForce && now - _freqCheckedAt >= ReactMs) || now - _freqCheckedAt >= FreqCheckMs || _freqHeld)
                {
                    _freqCheckedAt = now; _freqForce = false;
                    try
                    {
                        if (_freqLastTurbo && !dcTurbo) _freq.Restore(PowerPlan.BoostMode);
                        bool hold = false;
                        if (!changed && boosting)
                        {
                            // Inside the app-launch window: leave caps somebody raised alone (checked every tick, so
                            // ours are back within a second after the window).
                            bool raised;
                            hold = _freq.Mismatch(table, dcTurbo, out raised) != null && raised;
                        }
                        _freqHeld = hold;
                        if (!hold)
                        {
                            int n = _freq.Enforce(table, dcTurbo);
                            if (n > 0)
                            {
                                _powerWroteAt = now;
                                // Show what holds now, not the value that was just put back.
                                try { r.FreqP = (int)PowerPlan.Read(PowerPlan.SchemeOf(r.Mode, PowerPlan.ActiveScheme), PowerPlan.FreqMaxClass1, r.OnAc); }
                                catch (Exception) { }
                            }
                            if (n > 0 && !changed) _freqCorrections.Add(now); // somebody wrote them back
                        }
                        _freqError = null;
                    }
                    catch (Exception ex) { _freqError = ex.Message; }
                    _freqLast = (int[])table.Clone(); _freqLastTurbo = dcTurbo;
                }
            }
            else if (_freq.HasChanges && (_freqLast != null || now - _freqCheckedAt >= FreqCheckMs))
            {
                _freqLast = null; _freqLastTurbo = false; _freqCheckedAt = now;
                try { _freq.Restore(); _freqError = null; }
                catch (Exception ex) { _freqError = "恢复频率设置失败：" + ex.Message; }
            }
            else if (!on) _freqLast = null;
            _freqCorrections.RemoveAll(t => now - t > 60000);
            r.FreqCorrections = _freqCorrections.Count;
            r.FreqError = _freqError;
            r.FreqBoosting = on && _freqHeld;
        }

        /// <summary>
        /// How soon a reported power-setting write is checked after the previous check. Intel DTT writes "unlimited"
        /// and then 5.0 GHz about 0.1 s apart; with a 200 ms minimum the second one stayed for 0.15–0.3 s, now both
        /// are put back within ~50–100 ms. One DTT change takes 3–4 corrections (both caps and EPP), so only more than 12 within a second (somebody writes straight
        /// back) it slows down to 200 ms so the two programs cannot end up in a busy loop.
        /// </summary>
        private int ReactMs
        {
            get
            {
                long now = _clock.ElapsedMilliseconds;
                int n = _freqCorrections.FindAll(t => now - t < 1000).Count + _eppCorrections.FindAll(t => now - t < 1000).Count;
                return n > 12 ? FreqReactSlowMs : FreqReactMs;
            }
        }

        /// <summary>
        /// React to other programs' power-setting writes at once (Lenovo's process management service rewrites the caps after
        /// after each app launch, Lenovo's service rewrites caps / EPP); the 5 s checks stay as a fallback.
        /// </summary>
        private void WatchPowerSettings(bool needed)
        {
            if (needed && _freqWatcher == null)
            {
                try { _freqWatcher = new PowerSettingsWatcher(); _freqWatcher.Changed += () => { _freqForce = _eppForce = true; _wake.Set(); }; }
                catch (Exception) { }
            }
            else if (!needed && _freqWatcher != null) { _freqWatcher.Dispose(); _freqWatcher = null; }
        }

        /// <summary>
        /// Keep the energy performance preference (both core classes) per mode x power source, like the frequency caps:
        /// Windows switches between the cells itself, changes by others are written back at once, and the original
        /// values come back when the option is turned off or the app exits.
        /// </summary>
        private void ApplyEpp(Reading r, bool on, int[] table, long now)
        {
            try { r.EppNow = (int)PowerPlan.Read(PowerPlan.SchemeOf(r.Mode, PowerPlan.ActiveScheme), PowerPlan.Epp1, r.OnAc); }
            catch (Exception) { }
            if (!on) _eppForce = false;
            if (on)
            {
                bool changed = _eppLast == null || !SameTable(table, _eppLast);
                if (changed || (_eppForce && now - _eppCheckedAt >= ReactMs) || now - _eppCheckedAt >= FreqCheckMs)
                {
                    _eppCheckedAt = now; _eppForce = false;
                    try
                    {
                        int n = _epp.EnforceTable(EppSettings, table);
                        if (n > 0) _powerWroteAt = now;
                        if (n > 0 && !changed) _eppCorrections.Add(now); // somebody wrote them back
                        _eppError = null;
                    }
                    catch (Exception ex) { _eppError = ex.Message; }
                    _eppLast = (int[])table.Clone();
                }
            }
            else if (_epp.HasChanges && (_eppLast != null || now - _eppCheckedAt >= FreqCheckMs))
            {
                _eppLast = null; _eppCheckedAt = now;
                try { _epp.Restore(); _eppError = null; }
                catch (Exception ex) { _eppError = "恢复性能偏好失败：" + ex.Message; }
            }
            else if (!on) _eppLast = null;
            _eppCorrections.RemoveAll(t => now - t > 60000);
            r.EppCorrections = _eppCorrections.Count;
            r.EppError = _eppError;
        }

        /// <summary>
        /// Normal vs all-core load for the two sets of caps (sampled every 250 ms while all-core caps are set). The
        /// all-core caps take over when the last two samples average MultiCoreLoad cores or more (0.25–0.5 s into an
        /// all-core load), and the normal caps come back when the last four average SingleCoreLoad or less (about 1 s
        /// after it ends). Anything lighter than an all-core load (single-threaded benchmarks, games) keeps the normal
        /// caps: CPU-Z's single-thread test read 2.5 busy cores with background work, so a low threshold misfired. Samples within LoadSettleMs of applying power settings are
        /// left out: applying a scheme wakes many processes at once (measured: a single-threaded load plus that burst
        /// averaged over 2.5 cores for half a second after each of Lenovo's rewrites was put back).
        /// </summary>
        private void UpdateLoadState(Reading r, long now)
        {
            if (!double.IsNaN(r.BusyCores) && now - _powerWroteAt >= LoadSettleMs)
            {
                _loadHistory[_loadNext] = r.BusyCores;
                _loadNext = (_loadNext + 1) % _loadHistory.Length;
                _loadSamples = Math.Min(_loadSamples + 1, _loadHistory.Length);
                if (!_multiLoad && RecentLoad(2) >= MultiCoreLoad) _multiLoad = true;
                else if (_multiLoad && _loadSamples == _loadHistory.Length && RecentLoad(4) <= SingleCoreLoad) _multiLoad = false;
            }
            r.MultiCoreLoad = _multiLoad;
        }

        /// <summary>Average of the newest n busy-core samples (fewer while there are not that many yet).</summary>
        private double RecentLoad(int n)
        {
            n = Math.Min(n, _loadSamples);
            double sum = 0;
            for (int i = 1; i <= n; i++) sum += _loadHistory[(_loadNext - i + _loadHistory.Length) % _loadHistory.Length];
            return sum / n;
        }

        // Busy cores: 70 % / 50 % of the logical processors (16 busy threads read 14 cores on the 285H, its two
        // low-power E cores stay idle).
        public static readonly double MultiCoreLoad = Math.Max(2.5, Environment.ProcessorCount * 0.7),
                                      SingleCoreLoad = Math.Max(2.0, Environment.ProcessorCount * 0.5);
        private const int LoadSettleMs = 600;

        /// <summary>The caps to keep now: under multi-core load the multi-core cap where set (never above the single-core one).</summary>
        private static int[] EffectiveCaps(int[] single, int[] multi, bool multiLoad)
        {
            if (!multiLoad) return single;
            var t = (int[])single.Clone();
            for (int i = 0; i < t.Length; i++)
                if (multi[i] > 0 && t[i] >= 0) t[i] = t[i] == 0 ? multi[i] : Math.Min(t[i], multi[i]);
            return t;
        }

        /// <summary>A newly started program (started within 10 s) came to the foreground: open / extend the boost window.</summary>
        private void DetectLaunch(long now)
        {
            uint pid;
            GetWindowThreadProcessId(GetForegroundWindow(), out pid);
            if (pid == 0 || (int)pid == _lastForegroundPid) return;
            _lastForegroundPid = (int)pid;
            try
            {
                using (var p = System.Diagnostics.Process.GetProcessById((int)pid))
                    if ((DateTime.Now - p.StartTime).TotalSeconds < 10)
                    {
                        if (now >= _launchUntil) _launchStart = now;
                        _launchUntil = Math.Min(now + LaunchBoostMs, _launchStart + LaunchBoostMaxMs);
                    }
            }
            catch (Exception) { } // exited already / no access
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        private static bool SameTable(int[] a, int[] b)
        {
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static bool LimitsMatch(double pl1, double pl2, double tau, int wantPl1, int wantPl2, double wantTau)
        {
            return Math.Abs(pl1 - wantPl1) < 0.5 && Math.Abs(pl2 - wantPl2) < 0.5 && (double.IsNaN(wantTau) || Math.Abs(tau - wantTau) < 0.01);
        }

        private void Publish(Reading r, bool on)
        {
            if (!on)
            {
                if (_hwinfo != null) { _hwinfo.Dispose(); _hwinfo = null; HwInfoSensors.RemoveAll(); }
                return;
            }
            if (_hwinfo == null) _hwinfo = new HwInfoPublisher(_fans);
            _hwinfo.Publish(r);

            // HWiNFO reads custom sensors only at its start: if it runs without ours (twice in a row, so a HWiNFO that
            // is still starting up does not count), ask for a restart.
            long now = _clock.ElapsedMilliseconds;
            if (now - _hwinfoCheckedAt >= 30000)
            {
                _hwinfoCheckedAt = now;
                bool? loaded = HwInfoSensors.LoadedByHwInfo();
                _hwinfoMissing = loaded == false ? _hwinfoMissing + 1 : 0;
            }
            r.HwInfoNeedsRestart = _hwinfoMissing >= 2;
        }

        /// <summary>
        /// Move the ramp position towards the desired speed (0 = stop) by at most up / down. The fan cannot run between
        /// 0 and the lowest writable speed, so that range is crossed in one step: start at the floor when leaving
        /// "stopped", and stop once the floor is reached on the way down.
        /// </summary>
        private static double Ramp(double pos, int desired, int measured, int floor, bool jump, double up, double down)
        {
            if (double.IsNaN(pos)) pos = measured >= 0 ? measured : desired; // start from where the fan actually is
            if (jump) return desired;
            if (desired > pos) return Math.Min(desired, Math.Max(pos, floor) + up);
            if (desired < pos)
            {
                pos = Math.Max(Math.Max(desired, floor), pos - down);
                return desired < floor && pos <= floor ? desired : pos;
            }
            return pos;
        }

        private void Raise(Reading r)
        {
            var h = Updated;
            if (h != null) h(r);
        }
    }

    /// <summary>The sensor set this app shows in HWiNFO (names are read by HWiNFO only when it starts).</summary>
    public sealed class HwInfoPublisher : IDisposable
    {
        private readonly HwInfoSensors _s = new HwInfoSensors();
        private readonly List<FanInfo> _fans;
        private readonly int[] _rpm, _target;
        private readonly int _temp, _power, _pl1, _pl2, _clock, _usage;

        public HwInfoPublisher(List<FanInfo> fans)
        {
            _fans = fans;
            _rpm = new int[fans.Count]; _target = new int[fans.Count];
            for (int i = 0; i < fans.Count; i++)
            {
                string n = "Fan " + (i + 1);
                _rpm[i] = fans[i].HasRpm ? _s.Declare("Fan", n) : -1;
                _target[i] = fans[i].IsRpm ? _s.Declare("Fan", n + " Target") : _s.Declare("Other", n + " Target", "%");
            }
            _temp = _s.Declare("Temp", "CPU");
            _power = _s.Declare("Power", "CPU Package Power");
            _pl1 = _s.Declare("Power", "CPU PL1");
            _pl2 = _s.Declare("Power", "CPU PL2");
            _clock = _s.Declare("Clock", "CPU Effective Clock");
            _usage = _s.Declare("Usage", "CPU Usage");
        }

        public void Publish(Reading r)
        {
            for (int i = 0; i < _fans.Count; i++)
            {
                if (_rpm[i] >= 0 && r.Rpm[i] >= 0) _s.Set(_rpm[i], r.Rpm[i]);
                _s.Set(_target[i], Math.Max(0, r.Target[i])); // 0 = EC auto or stopped
            }
            if (r.Temp >= 0) _s.Set(_temp, r.Temp);
            _s.Set(_power, r.CpuPowerW);
            _s.Set(_pl1, r.Pl1W);
            _s.Set(_pl2, r.Pl2W);
            _s.Set(_clock, double.IsNaN(r.CpuMhz) ? double.NaN : Math.Round(r.CpuMhz));
            _s.Set(_usage, r.CpuUsage);
            _s.Flush();
        }

        public void Dispose() { _s.Dispose(); }
    }
}
