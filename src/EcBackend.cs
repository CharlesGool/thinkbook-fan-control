using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;

namespace ThinkBookFanControl
{
    /// <summary>
    /// Direct EC register access for older ThinkBooks without the WMI fan interface, through PawnIO (LpcIO for the
    /// ITE "D2EC" window at 0x4E/0x4F, LpcACPIEC for the standard ACPI EC at 0x62/0x66). Only enabled for known
    /// models (SystemFamily), and only after the EC answers like an ITE chip.
    ///
    /// Register maps:
    ///   ThinkBook 14+/16+ 2022–2023 (G4+ IAP / G5+): PWM duty 0x1804 / 0x1805 (0–127), rewritten every second
    ///     because the EC's own logic overwrites it; CPU temperature = ACPI EC register 0x07. (TB FanFun V2.6)
    ///   ThinkBook 14p/16p 2021–2022 (G2 ACH / G3 ARH): target RPM / 100 at 0xC83C / 0xC83D (0 = EC auto), measured
    ///     RPM at 0xC830..0xC833 (big-endian pairs), DTS CPU temperature at 0xC41C. (markgoo/Thinkbook14PAutoFanController)
    /// </summary>
    public sealed class EcBackend : IFanBackend
    {
        private enum Kind { Pwm, Rpm }

        private class Profile
        {
            public string Name;
            public Regex Family;
            public Kind Kind;
            public int[] Registers;   // write registers per fan
        }

        private static readonly Profile[] Profiles =
        {
            new Profile
            {
                Name = "ThinkBook 14+/16+ 2022–2023（EC PWM，实验性）",
                Family = new Regex(@"^ThinkBook 1[46] G[45]\+", RegexOptions.IgnoreCase),
                Kind = Kind.Pwm, Registers = new[] { 0x1804, 0x1805 },
            },
            new Profile
            {
                Name = "ThinkBook 14p/16p 2021–2022（EC 转速寄存器，实验性）",
                Family = new Regex(@"^ThinkBook 1[46]p G[23]", RegexOptions.IgnoreCase),
                Kind = Kind.Rpm, Registers = new[] { 0xC83C, 0xC83D },
            },
        };

        private const int PwmFullScale = 127;
        private const int D2ecIndex = 0x2E, D2ecData = 0x2F;   // ITE SIO registers behind the 0x4E/0x4F pair
        private const int D2ecAddrHi = 0x11, D2ecAddrLo = 0x10, D2ecRw = 0x12;
        private const int AcpiEcData = 0x62, AcpiEcCmd = 0x66;

        private readonly Profile _profile;
        private readonly List<FanInfo> _fans = new List<FanInfo>();
        private PawnIo _lpc, _acpi;
        private readonly GlobalLock _isaLock = new GlobalLock(GlobalLock.IsaBus);
        private readonly GlobalLock _ecLock = new GlobalLock(GlobalLock.Ec);
        private readonly bool[] _writing;      // PWM: whether we currently hold the register (false = EC auto)
        private readonly int[] _takeoverDuty;  // PWM: the EC's duty when we took the register

        private EcBackend(Profile p)
        {
            _profile = p;
            _writing = new bool[p.Registers.Length];
            _takeoverDuty = new int[p.Registers.Length];
            for (int i = 0; i < p.Registers.Length; i++)
            {
                var f = new FanInfo { Id = i + 1, Name = "风扇" + (i + 1) };
                if (p.Kind == Kind.Pwm) { f.Unit = "%"; f.MinRpm = 20; f.MaxRpm = 100; f.Step = 1; f.LowestStable = 10; f.HasRpm = false; }
                else { f.MinRpm = 1500; f.MaxRpm = 5000; f.Step = 100; f.LowestStable = 0; } // 0 means auto here: no stop
                _fans.Add(f);
            }
        }

        /// <summary>The model has an EC register profile here (it then needs PawnIO).</summary>
        public static bool IsKnownModel(string family)
        {
            foreach (var c in Profiles) if (c.Family.IsMatch(family ?? "")) return true;
            return false;
        }

        public static EcBackend Open(string family)
        {
            Profile p = null;
            foreach (var c in Profiles) if (c.Family.IsMatch(family ?? "")) { p = c; break; }
            if (p == null) return null; // not a known model: silently not applicable

            var b = new EcBackend(p);
            try
            {
                b._lpc = PawnIo.Open("LpcIO");
                b._lpc.Execute("ioctl_select_slot", new long[] { 1 }, 0); // index/data = 0x4E/0x4F
                if (p.Kind == Kind.Pwm) b._acpi = PawnIo.Open("LpcACPIEC");

                // Sanity checks before anything is ever written: an ITE EC must answer in the D2EC window.
                int id1 = b.Read(0x2000), id2 = b.Read(0x2001);
                if (id1 == 0x00 || id1 == 0xFF || (id1 != 0x55 && id1 != 0x85 && id1 != 0x89 && id1 != 0x82))
                    throw new NotSupportedException(string.Format("EC 芯片 ID 异常（0x{0:X2}{1:X2}），不是预期的 ITE EC", id1, id2));
                if (p.Kind == Kind.Rpm && id1 == 0x55) b.Write(0x1060, (byte)(b.Read(0x1060) | 0x80)); // IT557x: map 0xC000+
                foreach (int reg in p.Registers)
                {
                    int v = b.Read(reg);
                    if (p.Kind == Kind.Pwm && v > PwmFullScale) throw new NotSupportedException(string.Format("PWM 寄存器 0x{0:X4} 读数异常（{1}）", reg, v));
                }
                int t = b.ReadCpuTemp();
                if (t <= 0 || t >= 120) throw new NotSupportedException("EC 温度读数异常（" + t + "°C）");
                return b;
            }
            catch (Exception)
            {
                b.Dispose();
                throw;
            }
        }

        public string Name { get { return _profile.Name; } }
        public List<FanInfo> Fans { get { return _fans; } }
        public bool CanSetSpeed { get { return true; } }
        public int ReassertSeconds { get { return _profile.Kind == Kind.Pwm ? 1 : 15; } }

        public int ReadCpuTemp()
        {
            int t = _profile.Kind == Kind.Pwm ? AcpiRead(0x07) : Read(0xC41C);
            if (t <= 0 || t >= 150) throw new InvalidOperationException("EC 温度读数异常（" + t + "）");
            return t;
        }

        public int ReadFanRpm(FanInfo fan)
        {
            if (_profile.Kind != Kind.Rpm) return -1;
            int baseReg = 0xC830 + (fan.Id - 1) * 2;
            return (Read(baseReg) << 8) | Read(baseReg + 1);
        }

        public void WriteFan(FanInfo fan, int target)
        {
            int i = fan.Id - 1, reg = _profile.Registers[i];
            if (_profile.Kind == Kind.Rpm)
            {
                // 0 = EC auto; the register holds RPM / 100. No stop: FanTarget.Stop falls back to auto.
                int v = target <= 0 ? 0 : Settings.Clamp((target + 50) / 100, 1, 200);
                Write(reg, (byte)v);
                return;
            }
            if (target == FanTarget.Auto) { Release(i); return; }
            int pct = target == FanTarget.Stop ? 0 : Settings.Clamp(target, 0, 100);
            int duty = pct == 0 ? 0 : (int)Math.Round((pct - 1) / 99.0 * (PwmFullScale - 1) + 1, MidpointRounding.AwayFromZero);
            if (!_writing[i]) _takeoverDuty[i] = Read(reg);
            Write(reg, (byte)duty);
            _writing[i] = true;
        }

        /// <summary>
        /// Stop overwriting the PWM register so the EC takes over again. The EC may only rewrite it when its own fan
        /// level changes, so first leave a safe duty behind (never "stopped"): what the EC had when we took over, but at
        /// least half speed.
        /// </summary>
        private void Release(int i)
        {
            if (!_writing[i]) return;
            Write(_profile.Registers[i], (byte)Math.Max(_takeoverDuty[i], PwmFullScale / 2));
            _writing[i] = false;
        }

        public void RestoreAuto()
        {
            for (int i = 0; i < _fans.Count; i++)
            {
                try
                {
                    if (_profile.Kind == Kind.Rpm) Write(_profile.Registers[i], 0);
                    else if (_writing[i]) Release(i);
                    else if (Read(_profile.Registers[i]) == 0) { _writing[i] = true; Release(i); } // watchdog: un-stop a fan we left stopped
                }
                catch (Exception) { }
            }
        }

        // ---- ITE D2EC: 16-bit EC RAM address through SIO registers 0x2E/0x2F at 0x4E/0x4F

        private void SioOut(int reg, int value) { _lpc.Execute("ioctl_superio_outb", new long[] { reg, value & 0xFF }, 0); }
        private int SioIn(int reg) { return (int)(_lpc.Execute("ioctl_superio_inb", new long[] { reg }, 1)[0] & 0xFF); }

        private void Select(int addr)
        {
            SioOut(D2ecIndex, D2ecAddrHi); SioOut(D2ecData, addr >> 8);
            SioOut(D2ecIndex, D2ecAddrLo); SioOut(D2ecData, addr & 0xFF);
            SioOut(D2ecIndex, D2ecRw);
        }

        private int Read(int addr)
        {
            if (!_isaLock.Enter(500)) throw new TimeoutException("EC 总线忙");
            try { Select(addr); return SioIn(D2ecData); }
            finally { _isaLock.Exit(); }
        }

        private void Write(int addr, byte value)
        {
            if (!_isaLock.Enter(500)) throw new TimeoutException("EC 总线忙");
            try { Select(addr); SioOut(D2ecData, value); }
            finally { _isaLock.Exit(); }
        }

        // ---- Standard ACPI EC (0x62 data, 0x66 command/status): command 0x80 = read byte

        private int AcpiRead(int addr)
        {
            if (!_ecLock.Enter(500)) throw new TimeoutException("EC 忙");
            try
            {
                // Drop a stale byte another EC user left in the output buffer, or it would be read as our answer.
                for (int n = 0; n < 16 && (AcpiIn(AcpiEcCmd) & 1) != 0; n++) AcpiIn(AcpiEcData);
                WaitStatus(2, false); AcpiOut(AcpiEcCmd, 0x80);
                WaitStatus(2, false); AcpiOut(AcpiEcData, addr);
                WaitStatus(1, true);
                return AcpiIn(AcpiEcData);
            }
            finally { _ecLock.Exit(); }
        }

        /// <summary>Poll the status port until (status &amp; mask) != 0 equals set (IBF = 2 must clear, OBF = 1 must set).</summary>
        private void WaitStatus(int mask, bool set)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (((AcpiIn(AcpiEcCmd) & mask) != 0) != set)
            {
                if (sw.ElapsedMilliseconds > 50) throw new TimeoutException("ACPI EC 无响应");
                Thread.SpinWait(200);
            }
        }

        private void AcpiOut(int port, int value) { _acpi.Execute("ioctl_pio_write", new long[] { port, value & 0xFF }, 0); }
        private int AcpiIn(int port) { return (int)(_acpi.Execute("ioctl_pio_read", new long[] { port }, 1)[0] & 0xFF); }

        public void Dispose()
        {
            if (_lpc != null) { _lpc.Dispose(); _lpc = null; }
            if (_acpi != null) { _acpi.Dispose(); _acpi = null; }
            _isaLock.Dispose();
            _ecLock.Dispose();
        }
    }
}
