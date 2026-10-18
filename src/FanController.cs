using System;
using System.Collections.Generic;
using System.Threading;

namespace ThinkBookFanControl
{
    public class Reading
    {
        public int Temp;          // raw CPU temperature, -1 if unreadable
        public double CurveTemp;  // smoothed + hysteresis temperature fed to the curve
        public int[] Rpm;         // measured RPM per fan
        public int[] Target;      // written target per fan, 0 = EC auto, LenovoWmi.StopValue = stopped
        public bool Failsafe;     // over-temperature protection active
        public string Error;
    }

    /// <summary>
    /// Background loop (1 Hz): reads sensors, computes per-fan targets from the settings and writes them.
    /// Owns the LenovoWmi instance; all WMI calls happen on this thread.
    /// </summary>
    public class FanController
    {
        private const int ReassertSeconds = 15;   // re-write unchanged targets in case the EC drops them
        private const int FailsafeReleaseC = 5;   // leave over-temperature mode this many °C below the limit
        private const int MaxReadErrors = 3;      // hand control back to the EC after this many failed reads

        private readonly LenovoWmi _wmi;
        private readonly List<FanInfo> _fans;
        private readonly Settings _settings;
        private readonly List<CurvePoint>[] _quiet, _performance;
        private readonly int[] _written;
        private readonly int[] _sinceWrite;
        private readonly Queue<int> _tempWindow = new Queue<int>();
        private double _curveTemp = double.NaN;
        private bool _failsafe;
        private bool _lowActive;
        private int _readErrors;
        private Thread _thread;
        private volatile bool _running;
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);

        public event Action<Reading> Updated;

        public FanController(LenovoWmi wmi, List<FanInfo> fans, Settings settings)
        {
            _wmi = wmi; _fans = fans; _settings = settings;
            _quiet = Curve.Preset(FanMode.Quiet, fans);
            _performance = Curve.Preset(FanMode.Performance, fans);
            _written = new int[fans.Count];
            _sinceWrite = new int[fans.Count];
            ForceRewrite();
        }

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
            lock (_written) { for (int i = 0; i < _written.Length; i++) _written[i] = -1; }
            _wake.Set();
        }

        /// <summary>Stop the loop and hand all fans back to EC automatic control.</summary>
        public void StopAndRestore()
        {
            _running = false;
            _wake.Set();
            if (_thread != null && Thread.CurrentThread != _thread) _thread.Join(3000);
            RestoreAuto(_wmi, _fans);
        }

        public static void RestoreAuto(LenovoWmi wmi, List<FanInfo> fans)
        {
            foreach (var f in fans)
            {
                try { wmi.WriteFanRpm(f.Id, 0); } catch (Exception) { }
            }
        }

        private void Loop()
        {
            while (_running)
            {
                try { Tick(); }
                catch (Exception ex)
                {
                    var r = new Reading { Temp = -1, Rpm = new int[_fans.Count], Target = new int[_fans.Count], Error = ex.Message };
                    Raise(r);
                }
                _wake.WaitOne(1000);
            }
        }

        private void Tick()
        {
            var r = new Reading { Rpm = new int[_fans.Count], Target = new int[_fans.Count] };

            // ---- sensors
            try { r.Temp = _wmi.ReadCpuTemp(); _readErrors = 0; }
            catch (Exception ex) { r.Temp = -1; r.Error = "读取温度失败: " + ex.Message; _readErrors++; }
            for (int i = 0; i < _fans.Count; i++)
            {
                try { r.Rpm[i] = _wmi.ReadFanRpm(_fans[i].Id); } catch (Exception) { r.Rpm[i] = -1; }
            }

            // ---- snapshot settings
            FanMode mode; int[] fixedRpm; List<CurvePoint>[] curves; bool linkFixed, linkCurves, allowLow; int hyst, smooth, critical, lowMaxTemp;
            lock (_settings)
            {
                mode = _settings.Mode;
                fixedRpm = (int[])_settings.FixedRpm.Clone();
                curves = new List<CurvePoint>[_fans.Count];
                for (int i = 0; i < _fans.Count; i++) curves[i] = new List<CurvePoint>(_settings.Curves[i]);
                linkFixed = _settings.LinkFixed; linkCurves = _settings.LinkCurves;
                hyst = _settings.HysteresisC; smooth = _settings.SmoothingSec; critical = _settings.CriticalTempC;
                allowLow = _settings.AllowLowSpeed; lowMaxTemp = _settings.LowSpeedMaxTempC;
            }

            // ---- smoothed temperature with hysteresis: rises are followed at once, falls lag by `hyst` °C
            if (r.Temp >= 0)
            {
                _tempWindow.Enqueue(r.Temp);
                while (_tempWindow.Count > smooth) _tempWindow.Dequeue();
                double avg = 0; foreach (int t in _tempWindow) avg += t; avg /= _tempWindow.Count;
                _curveTemp = double.IsNaN(_curveTemp) ? avg : Math.Max(avg, Math.Min(_curveTemp, avg + hyst));

                if (r.Temp >= critical) _failsafe = true;
                else if (r.Temp < critical - FailsafeReleaseC) _failsafe = false;
            }
            r.CurveTemp = _curveTemp;
            r.Failsafe = _failsafe;

            // ---- targets
            bool sensorLost = _readErrors >= MaxReadErrors;
            // Below-rated speeds and stop only while it is cool enough (and the temperature is actually known).
            // Leave at the limit, re-enter only after cooling by the hysteresis, so the fan does not cycle on/off.
            if (!allowLow || sensorLost || r.Temp < 0 || r.Temp >= lowMaxTemp) _lowActive = false;
            else if (r.Temp < lowMaxTemp - Math.Max(2, hyst)) _lowActive = true;
            bool lowOk = _lowActive;
            for (int i = 0; i < _fans.Count; i++)
            {
                FanInfo f = _fans[i];
                int target;  // desired RPM, -1 = EC auto
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
                if (target < 0) target = 0; // EC auto
                else
                {
                    // The EC takes small values literally (1 RPM = stop), so anything below the rated
                    // minimum is only written when low speed is allowed and it is cool enough.
                    target = Settings.SnapRpm(target, f, lowOk);
                    if (target == 0) target = LenovoWmi.StopValue;
                }
                r.Target[i] = target;

                bool write;
                lock (_written)
                {
                    _sinceWrite[i]++;
                    write = _written[i] != target || (target != 0 && _sinceWrite[i] >= ReassertSeconds);
                }
                if (write)
                {
                    try
                    {
                        _wmi.WriteFanRpm(f.Id, target);
                        lock (_written) { _written[i] = target; _sinceWrite[i] = 0; }
                    }
                    catch (Exception ex) { r.Error = "写入" + f.Name + "失败: " + ex.Message; }
                }
            }
            Raise(r);
        }

        private void Raise(Reading r)
        {
            var h = Updated;
            if (h != null) h(r);
        }
    }
}
