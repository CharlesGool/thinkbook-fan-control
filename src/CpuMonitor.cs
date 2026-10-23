using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

namespace ThinkBookFanControl
{
    /// <summary>
    /// CPU frequency / utilisation (PDH, no driver), package power and PL1 / PL2 (MSR through PawnIO, or Lenovo WMI
    /// on models that publish the power-limit capabilities). Used from the controller thread only.
    /// </summary>
    public sealed class CpuMonitor : IDisposable
    {
        private PdhQuery _pdh;
        private int _perf = -1, _freq = -1, _util = -1;
        private readonly BusyCoreMeter _busy;
        private readonly CpuCores _cores;
        private bool _coreTemps = true;   // cleared once if MSR 0x19C is refused
        private readonly MsrPower _msr;
        private readonly WmiPowerLimits _wmi;
        private readonly string _msrError;
        private volatile bool _locked;   // MSR 0x610 lock bit, refreshed by every Sample()

        /// <summary>Why package power is unavailable (null when available).</summary>
        public readonly string PowerUnavailable;

        public CpuMonitor(LenovoWmi wmi)
        {
            try
            {
                _pdh = new PdhQuery();
                _perf = _pdh.Add(@"\Processor Information(_Total)\% Processor Performance");
                _freq = _pdh.Add(@"\Processor Information(_Total)\Processor Frequency");
                _util = _pdh.Add(@"\Processor Information(_Total)\% Processor Utility");
                _pdh.Collect();
            }
            catch (Exception) { _pdh = null; }
            try { _busy = new BusyCoreMeter(); } catch (Exception) { }
            try { _cores = new CpuCores(); } catch (Exception) { }

            try
            {
                _msr = new MsrPower();
                double a, b; bool locked;
                _msr.ReadLimits(out a, out b, out locked);
                _locked = locked;
            }
            catch (Exception ex) { _msrError = ex.Message; }
            if (wmi != null) _wmi = WmiPowerLimits.TryOpen(wmi);
            PowerUnavailable = _msr != null ? null : _msrError;
        }

        /// <summary>"MSR 0x610（PawnIO）" / "联想 WMI", or null.</summary>
        public string LimitSource { get { return _wmi != null ? "联想 WMI" : _msr != null ? "MSR 0x610（PawnIO）" : null; } }
        public bool CanWriteLimits { get { return _wmi != null || (_msr != null && !_locked); } }

        /// <summary>Why PL1 / PL2 cannot be set (null when they can).</summary>
        public string LimitsUnavailable
        {
            get
            {
                if (CanWriteLimits) return null;
                if (_msr != null) return "BIOS 已锁定功耗限制（MSR 0x610 bit 63 = 1），只能读取";
                return _msrError ?? "本机没有可写的功耗限制接口";
            }
        }

        /// <summary>Highest turbo frequency in MHz (MSR 0x1AD through PawnIO), 0 when unknown.</summary>
        public int MaxTurboMhz { get { return _msr != null ? _msr.MaxTurboMhz : 0; } }

        public int MinLimitW { get { return _wmi != null ? _wmi.MinW : 5; } }
        public int MaxLimitW { get { return _wmi != null ? _wmi.MaxW : 250; } }

        /// <summary>Clamp a requested PL1 / PL2 pair to the supported range, PL2 never below PL1.</summary>
        public void ClampLimits(ref int pl1, ref int pl2)
        {
            pl1 = Settings.Clamp(pl1, MinLimitW, MaxLimitW);
            pl2 = Settings.Clamp(Math.Max(pl1, pl2), MinLimitW, MaxLimitW);
        }

        public void Sample(Reading r)
        {
            r.CpuMhz = r.CpuUsage = r.CpuPowerW = r.Pl1W = r.Pl2W = r.Pl1TauS = r.MmioPl1W = r.MmioPl2W = double.NaN;
            r.CoreTempAvg = r.CoreTempMax = r.PCoreMhz = r.ECoreMhz = double.NaN;
            if (_pdh != null && _pdh.Collect())
            {
                double perf = _pdh.Get(_perf), freq = _pdh.Get(_freq), util = _pdh.Get(_util);
                r.CpuMhz = perf * freq / 100;
                r.CpuUsage = double.IsNaN(util) ? util : Math.Max(0, Math.Min(100, util));
            }
            if (_busy != null) r.BusyCores = _busy.Sample();
            if (_cores != null)
            {
                _cores.SampleFrequencies(r);
                if (_msr != null && _coreTemps)
                {
                    try { _cores.SampleTemperatures(_msr, r); }
                    catch (Exception) { _coreTemps = false; }
                }
            }
            if (_msr != null)
            {
                try { r.CpuPowerW = _msr.PackagePower(); } catch (Exception) { }
                try { double a, b; if (_msr.ReadMmio(out a, out b)) { r.MmioPl1W = a; r.MmioPl2W = b; } } catch (Exception) { }
            }
            try
            {
                double pl1, pl2;
                if (_wmi != null) { _wmi.Read(out pl1, out pl2); r.Pl1W = pl1; r.Pl2W = pl2; }
                else if (_msr != null) { double tau; bool locked; _msr.ReadLimits(out pl1, out pl2, out tau, out locked); r.Pl1W = pl1; r.Pl2W = pl2; r.Pl1TauS = tau; _locked = locked; }
            }
            catch (Exception) { }
        }

        public bool ReadLimits(out double pl1, out double pl2)
        {
            double tau;
            return ReadLimits(out pl1, out pl2, out tau);
        }

        public bool ReadLimits(out double pl1, out double pl2, out double tau)
        {
            pl1 = pl2 = tau = double.NaN;
            bool locked;
            try
            {
                if (_wmi != null) _wmi.Read(out pl1, out pl2);
                else if (_msr != null) _msr.ReadLimits(out pl1, out pl2, out tau, out locked);
                else return false;
                return !double.IsNaN(pl1) && !double.IsNaN(pl2);
            }
            catch (Exception) { return false; }
        }

        /// <summary>The current limits exactly as the hardware holds them (to put back later), or null when unreadable.</summary>
        public object SaveLimits()
        {
            try
            {
                if (_wmi != null) { double a, b; _wmi.Read(out a, out b); return new[] { (int)a, (int)b }; }
                if (_msr != null) return _msr.ReadRaw();
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>Put back what SaveLimits returned. Throws when refused.</summary>
        public void RestoreLimits(object saved)
        {
            if (saved is ulong) _msr.RestoreRaw((ulong)saved);
            else if (saved is int[]) { var v = (int[])saved; _wmi.Write(v[0], v[1]); }
        }

        /// <summary>PL1 time window (Tau) can be set: MSR route only.</summary>
        public bool CanSetTau { get { return _wmi == null && _msr != null && !_locked; } }

        /// <summary>The Tau the CPU will actually use for a requested value (NaN when Tau cannot be set).</summary>
        public double QuantizeTau(double seconds) { return CanSetTau ? _msr.QuantizeTau(seconds) : double.NaN; }

        /// <summary>Throws with a readable message when the write is refused or does not stick. tau &lt;= 0 keeps the current one.</summary>
        public void WriteLimits(int pl1, int pl2, double tau)
        {
            ClampLimits(ref pl1, ref pl2);
            if (_wmi != null) _wmi.Write(pl1, pl2);
            else if (_msr != null) _msr.WriteLimits(pl1, pl2, tau);
            else throw new NotSupportedException(LimitsUnavailable ?? "不支持");
        }

        /// <summary>Forget the energy baseline (after resume the counter may have restarted).</summary>
        public void ResetPowerSample() { if (_msr != null) _msr.ResetEnergy(); }

        public void Dispose()
        {
            if (_pdh != null) { _pdh.Dispose(); _pdh = null; }
            if (_cores != null) _cores.Dispose();
            if (_msr != null) _msr.Dispose();
        }
    }

    /// <summary>
    /// Busy cores from the idle threads' cycle counts (QueryIdleProcessorCycleTime, TSC cycles): a logical processor's
    /// busy share is 1 - idle cycles / TSC cycles, and the shares are added up, so a thread that the scheduler moves
    /// between cores still counts as about one. Measured on the ThinkBook 14+ 2025, the tick-based times (% Processor
    /// Time, GetSystemTimes, GetThreadTimes) see one fully busy thread as 0–0.2 cores and two to four as almost nothing
    /// (only all cores busy shows correctly); the cycle counts are right. Shares under NoiseFloor are dropped (the rest
    /// scaled up) so background work spread thinly over many processors does not add up to a core.
    /// </summary>
    internal sealed class BusyCoreMeter
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool QueryIdleProcessorCycleTime(ref uint bufferLength, [Out] ulong[] idleCycles);
        [DllImport("kernel32.dll")]
        private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        private const double NoiseFloor = 0.15;
        private ulong[] _last, _now;
        private long _lastTicks;
        private double _tscHz;

        public BusyCoreMeter()
        {
            uint size = 0;
            QueryIdleProcessorCycleTime(ref size, null); // fails, but reports the size needed
            if (size == 0) size = (uint)(Environment.ProcessorCount * 8);
            _last = new ulong[size / 8];
            _now = new ulong[_last.Length];
            _tscHz = CalibrateTsc();
            if (!Read(_last)) throw new InvalidOperationException("QueryIdleProcessorCycleTime");
            _lastTicks = Stopwatch.GetTimestamp();
        }

        /// <summary>Busy cores since the previous call (NaN when unreadable or called again too soon).</summary>
        public double Sample()
        {
            long ticks = Stopwatch.GetTimestamp();
            double secs = (ticks - _lastTicks) / (double)Stopwatch.Frequency;
            if (secs < 0.05 || !Read(_now)) return double.NaN;
            // An idle processor cannot count more cycles than the TSC: corrects a calibration that came out low.
            for (int i = 0; i < _now.Length; i++) _tscHz = Math.Max(_tscHz, (_now[i] - _last[i]) / secs);
            double sum = 0;
            for (int i = 0; i < _now.Length; i++)
            {
                double busy = 1 - (_now[i] - _last[i]) / (secs * _tscHz);
                if (busy > NoiseFloor) sum += (Math.Min(1, busy) - NoiseFloor) / (1 - NoiseFloor);
            }
            var t = _last; _last = _now; _now = t;
            _lastTicks = ticks;
            return sum;
        }

        private bool Read(ulong[] a)
        {
            uint size = (uint)(a.Length * 8);
            return QueryIdleProcessorCycleTime(ref size, a);
        }

        /// <summary>TSC rate: this thread's cycles while spinning 20 ms (best of three, being preempted only lowers it).</summary>
        private static double CalibrateTsc()
        {
            double best = 0;
            for (int k = 0; k < 3; k++)
            {
                ulong c0, c1;
                long t0 = Stopwatch.GetTimestamp(), t1;
                QueryThreadCycleTime(GetCurrentThread(), out c0);
                do t1 = Stopwatch.GetTimestamp(); while (t1 - t0 < Stopwatch.Frequency / 50);
                QueryThreadCycleTime(GetCurrentThread(), out c1);
                best = Math.Max(best, (c1 - c0) / ((t1 - t0) / (double)Stopwatch.Frequency));
            }
            return best;
        }
    }

    /// <summary>
    /// Per-core readings: frequency per core type (P / E, from GetSystemCpuSetInformation's EfficiencyClass: the highest
    /// class is P) as the average of each logical processor's "% Processor Performance" × "Processor Frequency", and
    /// the DTS temperature of every core (MSR 0x19C, read on that core by pinning this thread to it).
    /// </summary>
    internal sealed class CpuCores : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct GroupAffinity { public UIntPtr Mask; public ushort Group; public ushort R0, R1, R2; }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemCpuSetInformation(byte[] info, int length, out int returned, IntPtr process, int flags);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetThreadGroupAffinity(IntPtr thread, ref GroupAffinity affinity, out GroupAffinity previous);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        private readonly ushort[] _group;
        private readonly byte[] _index, _core;
        private readonly bool[] _big;
        public readonly bool Hybrid;
        private PdhQuery _pdh;
        private readonly int[] _perf, _freq;

        public CpuCores()
        {
            int len;
            GetSystemCpuSetInformation(null, 0, out len, IntPtr.Zero, 0);
            var buf = new byte[len];
            if (!GetSystemCpuSetInformation(buf, len, out len, IntPtr.Zero, 0)) throw new InvalidOperationException("GetSystemCpuSetInformation");
            var g = new List<ushort>(); var ix = new List<byte>(); var core = new List<byte>(); var cls = new List<byte>();
            for (int p = 0; p + 24 <= len; )
            {
                int size = BitConverter.ToInt32(buf, p);
                if (size <= 0) break;
                if (BitConverter.ToInt32(buf, p + 4) == 0) // CpuSetInformation
                {
                    g.Add(BitConverter.ToUInt16(buf, p + 12)); ix.Add(buf[p + 14]); core.Add(buf[p + 15]); cls.Add(buf[p + 18]);
                }
                p += size;
            }
            if (g.Count == 0) throw new InvalidOperationException("no CPU sets");
            _group = g.ToArray(); _index = ix.ToArray(); _core = core.ToArray();
            byte top = 0, low = 255;
            foreach (byte c in cls) { top = Math.Max(top, c); low = Math.Min(low, c); }
            Hybrid = top != low;
            _big = new bool[cls.Count];
            for (int i = 0; i < cls.Count; i++) _big[i] = cls[i] == top;

            _pdh = new PdhQuery();
            _perf = new int[_group.Length]; _freq = new int[_group.Length];
            for (int i = 0; i < _group.Length; i++)
            {
                string inst = _group[i] + "," + _index[i];
                _perf[i] = _pdh.Add(@"\Processor Information(" + inst + @")\% Processor Performance");
                _freq[i] = _pdh.Add(@"\Processor Information(" + inst + @")\Processor Frequency");
            }
            _pdh.Collect();
        }

        public void SampleFrequencies(Reading r)
        {
            if (_pdh == null || !_pdh.Collect()) return;
            double pSum = 0, eSum = 0; int pN = 0, eN = 0;
            for (int i = 0; i < _group.Length; i++)
            {
                double mhz = _pdh.Get(_perf[i]) * _pdh.Get(_freq[i]) / 100;
                if (double.IsNaN(mhz)) continue;
                if (_big[i]) { pSum += mhz; pN++; } else { eSum += mhz; eN++; }
            }
            if (pN > 0) r.PCoreMhz = pSum / pN;
            if (Hybrid && eN > 0) r.ECoreMhz = eSum / eN;
        }

        /// <summary>Throws when MSR 0x19C cannot be read (the caller then stops trying).</summary>
        public void SampleTemperatures(MsrPower msr, Reading r)
        {
            var thread = GetCurrentThread();
            GroupAffinity saved = default(GroupAffinity);
            bool pinned = false;
            double sum = 0, max = double.NaN; int n = 0;
            var seen = new HashSet<int>();
            try
            {
                for (int i = 0; i < _group.Length; i++)
                {
                    if (!seen.Add(_group[i] << 8 | _core[i])) continue; // one reading per physical core
                    var a = new GroupAffinity { Mask = (UIntPtr)(1UL << _index[i]), Group = _group[i] };
                    GroupAffinity prev;
                    if (!SetThreadGroupAffinity(thread, ref a, out prev)) continue;
                    if (!pinned) { saved = prev; pinned = true; }
                    double t = msr.CoreTemp();
                    if (double.IsNaN(t)) continue;
                    sum += t; n++;
                    if (double.IsNaN(max) || t > max) max = t;
                }
            }
            finally
            {
                GroupAffinity ignored;
                if (pinned) SetThreadGroupAffinity(thread, ref saved, out ignored);
            }
            if (n > 0) { r.CoreTempAvg = sum / n; r.CoreTempMax = max; }
        }

        public void Dispose() { if (_pdh != null) { _pdh.Dispose(); _pdh = null; } }
    }

    /// <summary>
    /// Intel RAPL through PawnIO's IntelMSR module: 0x606 units, 0x610 PKG_POWER_LIMIT (PL1 bits 14:0 + enable 15,
    /// PL1 time window "Tau" bits 23:17, PL2 bits 46:32 + enable 47, lock 63), 0x611 package energy counter. The MMIO
    /// mirror at MCHBAR + 0x59A0 is read (IntelMCHBAR module, read-only) to show it: the CPU enforces the lower of the
    /// MSR and MMIO limits, and Intel DTT / the BIOS may program the MMIO copy.
    /// </summary>
    public sealed class MsrPower : IDisposable
    {
        private const uint RaplPowerUnit = 0x606, PkgPowerLimit = 0x610, PkgEnergyStatus = 0x611;
        private const int MchbarPowerLimit = 0x59A0;
        private const ulong TauMask = 0x7FUL << 17;

        private readonly PawnIo _msr;
        private PawnIo _mchbar;
        private readonly double _powerUnit, _energyUnit, _timeUnit;
        private long _lastEnergy = -1;
        private readonly Stopwatch _energyClock = new Stopwatch();
        private double _lastPower = double.NaN;

        public MsrPower()
        {
            _msr = PawnIo.Open("IntelMSR");
            try
            {
                ulong units = Read(RaplPowerUnit);
                _powerUnit = 1.0 / (1 << (int)(units & 0xF));
                _energyUnit = 1.0 / (1L << (int)((units >> 8) & 0x1F));
                _timeUnit = 1.0 / (1L << (int)((units >> 16) & 0xF));
                Read(PkgPowerLimit);
            }
            catch (Exception ex)
            {
                _msr.Dispose();
                throw new NotSupportedException("无法读取 RAPL MSR：" + ex.Message, ex);
            }
            try { _mchbar = PawnIo.Open("IntelMCHBAR"); } catch (Exception) { _mchbar = null; }
            try { MaxTurboMhz = (int)(Read(0x1AD) & 0xFF) * 100; } catch (Exception) { MaxTurboMhz = 0; } // 1-core turbo ratio
        }

        /// <summary>Highest single-core turbo frequency (MSR 0x1AD), 0 when unknown.</summary>
        public readonly int MaxTurboMhz;

        private int _tjMax = -1;

        /// <summary>
        /// DTS temperature of the core this thread runs on (MSR 0x19C: TjMax − bits 22:16, valid bit 31), NaN when not
        /// valid. TjMax is MSR 0x1A2 bits 23:16 (105 °C on the ThinkBook 14+ 2025's Core Ultra 9 285H). Throws when refused.
        /// </summary>
        public double CoreTemp()
        {
            if (_tjMax < 0) _tjMax = (int)((Read(0x1A2) >> 16) & 0xFF);
            ulong v = Read(0x19C);
            return (v >> 31 & 1) == 0 ? double.NaN : _tjMax - (int)((v >> 16) & 0x7F);
        }

        private ulong Read(uint msr) { return (ulong)_msr.Execute("ioctl_read_msr", new long[] { msr }, 1)[0]; }

        public ulong ReadRaw() { return Read(PkgPowerLimit); }

        /// <summary>Write back a value saved with ReadRaw, bit for bit (keeps the BIOS's enable / clamp / time window bits).</summary>
        public void RestoreRaw(ulong saved)
        {
            ulong v = Read(PkgPowerLimit);
            if (v == saved) return;
            if ((v >> 63) != 0) throw new InvalidOperationException("BIOS 已锁定功耗限制（MSR 0x610 bit 63），无法写入");
            _msr.Execute("ioctl_write_msr", new long[] { PkgPowerLimit, (long)(saved & ~(1UL << 63)) }, 0);
        }

        public void ReadLimits(out double pl1, out double pl2, out bool locked)
        {
            double tau;
            ReadLimits(out pl1, out pl2, out tau, out locked);
        }

        public void ReadLimits(out double pl1, out double pl2, out double tau, out bool locked)
        {
            ulong v = Read(PkgPowerLimit);
            pl1 = (v & 0x7FFF) * _powerUnit;
            pl2 = ((v >> 32) & 0x7FFF) * _powerUnit;
            tau = DecodeWindow((int)((v & TauMask) >> 17));
            locked = (v >> 63) != 0;
        }

        /// <summary>RAPL time window field (7 bits): 2^Y * (1 + F/4) time units, Y = bits 4:0, F = bits 6:5.</summary>
        private double DecodeWindow(int field)
        {
            return (1L << (field & 0x1F)) * (4 + ((field >> 5) & 3)) / 4.0 * _timeUnit;
        }

        /// <summary>Field whose window is closest to the requested seconds.</summary>
        private int EncodeWindow(double seconds)
        {
            int best = 0;
            for (int f = 0; f < 0x80; f++)
                if (Math.Abs(DecodeWindow(f) - seconds) < Math.Abs(DecodeWindow(best) - seconds)) best = f;
            return best;
        }

        /// <summary>The window the CPU will actually use for a requested Tau (representable values only).</summary>
        public double QuantizeTau(double seconds) { return DecodeWindow(EncodeWindow(seconds)); }

        public bool ReadMmio(out double pl1, out double pl2)
        {
            pl1 = pl2 = double.NaN;
            if (_mchbar == null) return false;
            ulong v;
            try { v = (ulong)_mchbar.Execute("ioctl_read_qword", new long[] { MchbarPowerLimit }, 1)[0]; }
            catch (Exception) { _mchbar.Dispose(); _mchbar = null; return false; }
            pl1 = (v & 0x7FFF) * _powerUnit;
            pl2 = ((v >> 32) & 0x7FFF) * _powerUnit;
            return true;
        }

        /// <summary>Average package power since the previous call (W), from the wrapping 32-bit energy counter.</summary>
        public double PackagePower()
        {
            long e = (long)(Read(PkgEnergyStatus) & 0xFFFFFFFF);
            double dt = _energyClock.Elapsed.TotalSeconds;
            if (_lastEnergy >= 0 && dt > 0.2 && dt < 5)
            {
                long delta = e - _lastEnergy;
                if (delta < 0) delta += 1L << 32;
                double w = delta * _energyUnit / dt;
                // A counter that restarted (sleep / resume) shows up as an absurd value: drop that sample.
                _lastPower = w < 1000 ? w : double.NaN;
            }
            if (_lastEnergy < 0 || dt > 0.2) { _lastEnergy = e; _energyClock.Restart(); }
            return _lastPower;
        }

        public void ResetEnergy() { _lastEnergy = -1; _lastPower = double.NaN; }

        /// <summary>tauSeconds &lt;= 0 keeps the current PL1 time window.</summary>
        public void WriteLimits(int pl1W, int pl2W, double tauSeconds)
        {
            ulong v = Read(PkgPowerLimit);
            if ((v >> 63) != 0) throw new InvalidOperationException("BIOS 已锁定功耗限制（MSR 0x610 bit 63），无法写入");
            ulong raw1 = (ulong)Math.Round(pl1W / _powerUnit), raw2 = (ulong)Math.Round(pl2W / _powerUnit);
            if (pl1W <= 0 || pl2W <= 0 || raw1 > 0x7FFF || raw2 > 0x7FFF) throw new ArgumentOutOfRangeException("pl1W", "PL1 / PL2 超出 MSR 0x610 可表示的范围");
            ulong nv = (v & ~0x7FFFUL) | raw1 | (1UL << 15);
            nv = (nv & ~(0x7FFFUL << 32)) | (raw2 << 32) | (1UL << 47);
            if (tauSeconds > 0) nv = (nv & ~TauMask) | ((ulong)EncodeWindow(tauSeconds) << 17);
            if (nv != v) _msr.Execute("ioctl_write_msr", new long[] { PkgPowerLimit, (long)nv }, 0);
            ulong back = Read(PkgPowerLimit);
            if ((back & 0x7FFF) != raw1 || ((back >> 32) & 0x7FFF) != raw2 || (back & TauMask) != (nv & TauMask))
                throw new InvalidOperationException("写入 MSR 0x610 后读回不一致，可能被固件拒绝");
        }

        public void Dispose()
        {
            _msr.Dispose();
            if (_mchbar != null) _mchbar.Dispose();
        }
    }

    /// <summary>
    /// Lenovo WMI power limits (Legion-style capability IDs, device 0x01 = CPU): 0x0102_FF00 = long-term (PL1),
    /// 0x0101_FF00 = short-term (PL2), mode byte 0xFF = custom. Only used when LENOVO_CAPABILITY_DATA_01 lists them
    /// (the ThinkBook 14+ 2025 does not). Writing requires the machine to be in its custom performance mode.
    /// </summary>
    public sealed class WmiPowerLimits
    {
        private const uint Pl1Id = 0x0102FF00, Pl2Id = 0x0101FF00;
        private readonly LenovoWmi _wmi;
        public int MinW = 5, MaxW = 250;

        private WmiPowerLimits(LenovoWmi wmi) { _wmi = wmi; }

        public static WmiPowerLimits TryOpen(LenovoWmi wmi)
        {
            bool pl1 = false, pl2 = false;
            int min = int.MaxValue, max = 0;
            try
            {
                using (var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM LENOVO_CAPABILITY_DATA_01"))
                    foreach (ManagementObject o in searcher.Get())
                        using (o)
                        {
                            uint id = Convert.ToUInt32(o["IDs"]);
                            if (id != Pl1Id && id != Pl2Id) continue;
                            if (id == Pl1Id) pl1 = true; else pl2 = true;
                            min = Math.Min(min, Convert.ToInt32(o["MinValue"]));
                            max = Math.Max(max, Convert.ToInt32(o["MaxValue"]));
                        }
            }
            catch (Exception) { return null; }
            if (!pl1 || !pl2 || max <= 0) return null;
            var w = new WmiPowerLimits(wmi) { MinW = Math.Max(1, min), MaxW = max };
            double a, b;
            try { w.Read(out a, out b); } catch (Exception) { return null; }
            return a > 0 ? w : null;
        }

        private uint Get(uint id)
        {
            try { uint v = _wmi.GetFeature(id); if (v != 0) return v; } catch (ManagementException) { }
            return _wmi.GetFeature(id & 0xFFFF00FF); // some firmware only answers with the mode byte cleared
        }

        public void Read(out double pl1, out double pl2) { pl1 = Get(Pl1Id); pl2 = Get(Pl2Id); }

        public void Write(int pl1, int pl2)
        {
            int mode = SmartFanMode();
            if (mode >= 0 && mode != 0xFF) throw new InvalidOperationException("请先把电脑切换到“自定义”性能模式（Fn+Q / 联想管家），再设置 PL1/PL2");
            _wmi.SetFeature(Pl1Id, (uint)pl1);
            _wmi.SetFeature(Pl2Id, (uint)pl2);
        }

        /// <summary>LENOVO_GAMEZONE_DATA.GetSmartFanMode (1 quiet, 2 balanced, 3 performance, 0xFF custom), -1 if unavailable.</summary>
        private static int SmartFanMode()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM LENOVO_GAMEZONE_DATA"))
                    foreach (ManagementObject o in searcher.Get())
                        using (o)
                        using (var outp = o.InvokeMethod("GetSmartFanMode", null, null))
                            return Convert.ToInt32(outp["Data"]);
            }
            catch (Exception) { }
            return -1;
        }
    }
}
