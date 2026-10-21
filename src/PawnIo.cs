using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ThinkBookFanControl
{
    /// <summary>
    /// PawnIO (https://pawnio.eu): a signed kernel driver that runs small, signed, allow-listed modules. Works with
    /// Memory Integrity (HVCI) on, unlike WinRing0 / inpout. Each instance holds one loaded module (embedded as
    /// resource "pawnio.&lt;name&gt;.bin", from https://github.com/namazso/PawnIO.Modules, LGPL-2.1).
    /// The driver itself must be installed by the user (installer from pawnio.eu, or winget install namazso.PawnIO).
    /// </summary>
    public sealed class PawnIo : IDisposable
    {
        private const uint DeviceType = 41394u << 16;
        private const uint IoctlLoadBinary = DeviceType | (0x821 << 2);
        private const uint IoctlExecute = DeviceType | (0x841 << 2);
        private const int FnNameLength = 32;

        private SafeFileHandle _handle;
        private readonly object _lock = new object();
        public readonly string Module;

        private PawnIo(SafeFileHandle h, string module) { _handle = h; Module = module; }

        /// <summary>Installed driver version from the uninstall entry, null when PawnIO is not installed.</summary>
        public static string InstalledVersion
        {
            get
            {
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                        using (var k = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO"))
                        {
                            var v = k == null ? null : k.GetValue("DisplayVersion") as string;
                            if (!string.IsNullOrEmpty(v)) return v;
                        }
                    }
                    catch (Exception) { }
                }
                return null;
            }
        }

        public const string NotInstalledText = "需要安装 PawnIO 驱动（可点下方按钮用程序内置的安装程序安装）";

        /// <summary>Open the driver and load an embedded module. Throws NotSupportedException with a readable reason.</summary>
        public static PawnIo Open(string module)
        {
            byte[] blob;
            using (var s = typeof(PawnIo).Assembly.GetManifestResourceStream("pawnio." + module + ".bin"))
            {
                if (s == null) throw new NotSupportedException("程序未内置 PawnIO 模块 " + module);
                blob = new byte[s.Length];
                int n = 0;
                while (n < blob.Length) { int r = s.Read(blob, n, blob.Length - n); if (r <= 0) break; n += r; }
            }
            var h = CreateFile(@"\\?\GLOBALROOT\Device\PawnIO", 0xC0000000 /* GENERIC_READ | GENERIC_WRITE */, 3, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
            if (h.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                h.Dispose();
                throw new NotSupportedException(InstalledVersion == null ? NotInstalledText : "无法打开 PawnIO 驱动：" + new Win32Exception(err).Message);
            }
            int ret;
            if (!DeviceIoControl(h, IoctlLoadBinary, blob, blob.Length, null, 0, out ret, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                h.Dispose();
                throw new NotSupportedException("PawnIO 模块 " + module + " 不支持本机（" + new Win32Exception(err).Message + "）");
            }
            return new PawnIo(h, module);
        }

        /// <summary>Run a module function. Throws Win32Exception on failure (e.g. access denied for a non-allow-listed MSR).</summary>
        public long[] Execute(string fn, long[] input, int outCount)
        {
            var inBuf = new byte[FnNameLength + input.Length * 8];
            Encoding.ASCII.GetBytes(fn, 0, Math.Min(fn.Length, FnNameLength - 1), inBuf, 0);
            Buffer.BlockCopy(input, 0, inBuf, FnNameLength, input.Length * 8);
            var outBuf = new byte[outCount * 8];
            int read;
            lock (_lock)
            {
                if (_handle == null) throw new ObjectDisposedException("PawnIo");
                if (!DeviceIoControl(_handle, IoctlExecute, inBuf, inBuf.Length, outBuf, outBuf.Length, out read, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            var result = new long[outCount];
            Buffer.BlockCopy(outBuf, 0, result, 0, Math.Min(read, outBuf.Length));
            return result;
        }

        public void Dispose()
        {
            lock (_lock) { if (_handle != null) { _handle.Dispose(); _handle = null; } }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr overlapped);
    }

    /// <summary>
    /// The official PawnIO installer (namazso, signed; redistribution of the unmodified installer is allowed), embedded
    /// as resource "pawnio.PawnIO_setup.exe" so the driver can be installed from inside the app.
    /// </summary>
    public static class PawnIoSetup
    {
        public const string BundledVersion = "2.2.0";
        private const string Sha256 = "1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032";

        /// <summary>True when PawnIO is missing or older than the bundled installer.</summary>
        public static bool NeedsInstall
        {
            get
            {
                Version v;
                return !Version.TryParse(PawnIo.InstalledVersion ?? "", out v) || v < new Version(BundledVersion);
            }
        }

        /// <summary>
        /// Run the bundled installer silently ("-install -silent") and wait for it. Returns true when a reboot is
        /// needed (exit code 3010); throws with a readable message on failure.
        /// </summary>
        public static bool Install()
        {
            string path = Path.Combine(Path.GetTempPath(), "PawnIO_setup_" + BundledVersion + ".exe");
            using (var s = typeof(PawnIoSetup).Assembly.GetManifestResourceStream("pawnio.PawnIO_setup.exe"))
            {
                if (s == null) throw new InvalidOperationException("程序未内置 PawnIO 安装程序");
                using (var f = File.Create(path)) s.CopyTo(f);
            }
            try
            {
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var f = File.OpenRead(path))
                    if (BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "") != Sha256)
                        throw new InvalidOperationException("内置的 PawnIO 安装程序校验失败");
                var psi = new System.Diagnostics.ProcessStartInfo(path, "-install -silent") { UseShellExecute = true, Verb = "runas" };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    p.WaitForExit();
                    if (p.ExitCode == 3010) return true;
                    if (p.ExitCode != 0) throw new InvalidOperationException("PawnIO 安装程序返回 " + p.ExitCode + "（" + new Win32Exception(p.ExitCode).Message + "）");
                }
                return false;
            }
            finally { try { File.Delete(path); } catch (Exception) { } }
        }
    }

    /// <summary>
    /// Named mutexes that hardware monitoring tools (HWiNFO, LibreHardwareMonitor, ...) hold around shared I/O, so
    /// two programs never interleave a multi-step EC / Super I/O transaction.
    /// </summary>
    public sealed class GlobalLock : IDisposable
    {
        public const string IsaBus = @"Global\Access_ISABUS.HTP.Method";
        public const string Ec = @"Global\Access_EC";

        private readonly Mutex _mutex;

        public GlobalLock(string name)
        {
            try { _mutex = new Mutex(false, name); }
            catch (UnauthorizedAccessException) { try { _mutex = Mutex.OpenExisting(name); } catch (Exception) { } }
            catch (Exception) { }
        }

        /// <summary>Returns false on timeout (the caller then skips this I/O round).</summary>
        public bool Enter(int timeoutMs)
        {
            if (_mutex == null) return true;
            try { return _mutex.WaitOne(timeoutMs); }
            catch (AbandonedMutexException) { return true; }
        }

        public void Exit() { if (_mutex != null) try { _mutex.ReleaseMutex(); } catch (Exception) { } }

        public void Dispose() { if (_mutex != null) _mutex.Dispose(); }
    }
}
