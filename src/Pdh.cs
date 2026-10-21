using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ThinkBookFanControl
{
    /// <summary>
    /// Minimal wrapper over a PDH (Performance Data Helper) query.
    /// Counters are added by their English path (PdhAddEnglishCounter), so they work on any display language.
    /// Rate counters (e.g. "% Processor Performance") need two Collect() calls before Get() returns a value.
    /// </summary>
    public sealed class PdhQuery : IDisposable
    {
        private const uint PDH_FMT_DOUBLE = 0x00000200;
        private const uint PDH_FMT_NOCAP100 = 0x00008000;
        private const uint PDH_CSTATUS_VALID_DATA = 0;
        private const uint PDH_CSTATUS_NEW_DATA = 1;

        // PDH_FMT_COUNTERVALUE: DWORD CStatus, then an 8-byte aligned union (16 bytes on x86 and x64).
        [StructLayout(LayoutKind.Explicit)]
        private struct PDH_FMT_COUNTERVALUE
        {
            [FieldOffset(0)] public uint CStatus;
            [FieldOffset(8)] public double doubleValue;
            [FieldOffset(8)] public long largeValue;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhOpenQueryW(string szDataSource, IntPtr dwUserData, out IntPtr phQuery);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhAddEnglishCounterW(IntPtr hQuery, string szFullCounterPath, IntPtr dwUserData, out IntPtr phCounter);

        [DllImport("pdh.dll")]
        private static extern uint PdhCollectQueryData(IntPtr hQuery);

        [DllImport("pdh.dll")]
        private static extern uint PdhGetFormattedCounterValue(IntPtr hCounter, uint dwFormat, out uint lpdwType, out PDH_FMT_COUNTERVALUE pValue);

        [DllImport("pdh.dll")]
        private static extern uint PdhCloseQuery(IntPtr hQuery);

        private readonly object _sync = new object();
        private readonly List<IntPtr> _counters = new List<IntPtr>();
        private IntPtr _query;

        public PdhQuery()
        {
            IntPtr q;
            uint status = PdhOpenQueryW(null, IntPtr.Zero, out q);
            if (status != 0 || q == IntPtr.Zero)
                throw new InvalidOperationException("无法创建性能计数器查询（PdhOpenQuery 失败，0x" + status.ToString("X8") + "）。",
                                                    new Win32Exception((int)status));
            _query = q;
        }

        /// <summary>Adds a counter by English path; returns its index, or -1 if it does not exist on this machine.</summary>
        public int Add(string englishPath)
        {
            lock (_sync)
            {
                if (_query == IntPtr.Zero || string.IsNullOrEmpty(englishPath)) return -1;
                IntPtr c;
                uint status = PdhAddEnglishCounterW(_query, englishPath, IntPtr.Zero, out c);
                if (status != 0 || c == IntPtr.Zero) return -1;
                _counters.Add(c);
                return _counters.Count - 1;
            }
        }

        /// <summary>Samples all counters of the query.</summary>
        public bool Collect()
        {
            lock (_sync)
            {
                if (_query == IntPtr.Zero) return false;
                return PdhCollectQueryData(_query) == 0;
            }
        }

        /// <summary>Formatted value of the counter from the last Collect(); NaN when not (yet) available.</summary>
        public double Get(int index)
        {
            lock (_sync)
            {
                if (_query == IntPtr.Zero || index < 0 || index >= _counters.Count) return double.NaN;
                uint type;
                PDH_FMT_COUNTERVALUE v;
                uint status = PdhGetFormattedCounterValue(_counters[index], PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, out type, out v);
                if (status != 0) return double.NaN;
                if (v.CStatus != PDH_CSTATUS_VALID_DATA && v.CStatus != PDH_CSTATUS_NEW_DATA) return double.NaN;
                if (double.IsInfinity(v.doubleValue)) return double.NaN;
                return v.doubleValue;
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_query == IntPtr.Zero) return;
                PdhCloseQuery(_query);   // also frees the counter handles
                _query = IntPtr.Zero;
                _counters.Clear();
            }
        }
    }
}
