using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace ThinkBookFanControl
{
    /// <summary>
    /// Fallback for Lenovo laptops (IdeaPad / older ThinkBook / Legion) with the "Lenovo ACPI-Compliant Virtual Power
    /// Controller" driver: it only offers the Energy Manager "dust removal" switch, i.e. EC normal or full speed.
    ///   0x831020C0  set mode, in = { 6, 1, mode } (mode 0 = normal, 1 = dust removal / full speed)
    ///   0x831020C4  read state, in = { 14 }, out = 1 DWORD (no fixed meaning: compared against the value read in normal mode)
    /// Dust removal runs ~9 s at full speed, pauses ~2 s and ends after ~2 min, so full speed is held by switching it
    /// off and on again before each 9 s burst ends (as Lenovo-Fan-Control / allstone.lt ideafan do).
    /// The device is opened per call, exclusively, like the reference implementation.
    /// </summary>
    public sealed class EnergyDrvBackend : IFanBackend
    {
        private const string DevicePath = @"\\.\EnergyDrv";
        private const uint IoctlSetMode = 0x831020C0;
        private const uint IoctlReadState = 0x831020C4;
        private const uint ModeNormal = 0;
        private const uint ModeFast = 1;

        /// <summary>Restart dust removal after this long, before the EC ends the ~9 s burst (ticks are ~1 s apart).</summary>
        private const int RestartMs = 7500;

        private readonly List<FanInfo> _fans;
        private readonly Stopwatch _sinceStart = new Stopwatch();
        private bool _fast;
        private long _normalState = -1;  // read-state value seen in normal mode, -1 = not known yet
        private bool _disposed;

        private EnergyDrvBackend()
        {
            _fans = new List<FanInfo>
            {
                new FanInfo { Id = 1, MinRpm = 0, MaxRpm = 100, Step = 100, LowestStable = 0, Unit = "%", HasRpm = false, Name = "风扇" }
            };
        }

        public static EnergyDrvBackend Open()
        {
            using (SafeFileHandle h = OpenDevice(GenericRead))
            {
                if (h.IsInvalid)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == ErrorFileNotFound || err == ErrorPathNotFound)
                        throw new NotSupportedException("未找到 \\\\.\\EnergyDrv（联想电源管理驱动）");
                    throw new NotSupportedException("无法打开 \\\\.\\EnergyDrv：" + new Win32Exception(err).Message);
                }
                uint state;
                if (!ReadState(h, out state))
                    throw new NotSupportedException("\\\\.\\EnergyDrv 不响应风扇状态查询");
            }
            return new EnergyDrvBackend();
        }

        public string Name { get { return "联想 EnergyDrv（仅支持 自动 / 全速）"; } }
        public List<FanInfo> Fans { get { return _fans; } }
        public bool CanSetSpeed { get { return false; } }
        // Called every tick while at full speed so WriteFan can restart the burst in time.
        public int ReassertSeconds { get { return 1; } }

        public int ReadCpuTemp()
        {
            double max = double.NaN;
            using (var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"))
            {
                foreach (ManagementObject o in searcher.Get())
                {
                    using (o)
                    {
                        object v = o["CurrentTemperature"];
                        if (v == null) continue;
                        double c = Convert.ToDouble(v) / 10.0 - 273.15;
                        if (c <= 0 || c > 120) continue;
                        if (double.IsNaN(max) || c > max) max = c;
                    }
                }
            }
            if (double.IsNaN(max))
                throw new InvalidOperationException("无法读取温度（MSAcpi_ThermalZoneTemperature 无有效数据）");
            return (int)Math.Round(max);
        }

        public int ReadFanRpm(FanInfo fan) { return -1; }

        public void WriteFan(FanInfo fan, int target)
        {
            if (target >= 1)
            {
                if (!_fast)
                {
                    StartFast();
                }
                else if (_sinceStart.ElapsedMilliseconds >= RestartMs)
                {
                    // Stop and restart dust removal to reset the EC's burst timer; the fan barely slows down.
                    SetModeAndWait(ModeNormal);
                    StartFast();
                }
            }
            else
            {
                SetModeAndWait(ModeNormal);
                _fast = false;
                _sinceStart.Reset();
            }
        }

        public void RestoreAuto()
        {
            try { SetMode(ModeNormal); } catch (Exception) { }
            _fast = false;
            _sinceStart.Reset();
        }

        public void Dispose()
        {
            // Nothing is held open between calls; RestoreAuto keeps working for the crash handler.
            _disposed = true;
        }

        private void StartFast()
        {
            if (_normalState < 0) SetModeAndWait(ModeNormal); // learn the normal-mode state value first
            SetModeAndWait(ModeFast);
            _fast = true;
            _sinceStart.Reset();
            _sinceStart.Start();
        }

        /// <summary>Set the mode and wait (briefly) until the driver reports it, re-sending like the reference does.</summary>
        private void SetModeAndWait(uint mode)
        {
            if (_disposed) throw new ObjectDisposedException("EnergyDrvBackend");
            for (int i = 0; i < 20; i++)
            {
                SetMode(mode);
                Thread.Sleep(i == 0 ? 50 : 10);
                uint state;
                using (SafeFileHandle h = OpenDevice(GenericRead))
                {
                    if (h.IsInvalid || !ReadState(h, out state)) return; // cannot confirm: trust the write
                }
                if (mode == ModeNormal)
                {
                    if (_normalState < 0) _normalState = state;
                    if (state == _normalState) return;
                }
                else if (_normalState < 0 || state != _normalState) return;
            }
        }

        private static void SetMode(uint mode)
        {
            using (SafeFileHandle h = OpenDevice(GenericWrite))
            {
                if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法打开 \\\\.\\EnergyDrv");
                byte[] inBuf = new byte[12];
                Buffer.BlockCopy(new uint[] { 6u, 1u, mode }, 0, inBuf, 0, inBuf.Length);
                uint returned;
                if (!DeviceIoControl(h, IoctlSetMode, inBuf, (uint)inBuf.Length, null, 0, out returned, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "EnergyDrv 设置风扇模式失败");
            }
        }

        private static bool ReadState(SafeFileHandle h, out uint state)
        {
            byte[] inBuf = BitConverter.GetBytes(14u);
            byte[] outBuf = new byte[4];
            uint returned;
            state = 0;
            if (!DeviceIoControl(h, IoctlReadState, inBuf, (uint)inBuf.Length, outBuf, (uint)outBuf.Length, out returned, IntPtr.Zero))
                return false;
            state = BitConverter.ToUInt32(outBuf, 0);
            return true;
        }

        private static SafeFileHandle OpenDevice(uint access)
        {
            return CreateFile(DevicePath, access, 0, IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);
        }

        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint OpenExisting = 3;
        private const uint FileAttributeNormal = 0x80;
        private const int ErrorFileNotFound = 2;
        private const int ErrorPathNotFound = 3;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, byte[] lpInBuffer,
            uint nInBufferSize, byte[] lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);
    }
}
