using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace ThinkBookFanControl
{
    /// <summary>
    /// Command-line control port for tbfc.exe: one request per TCP connection, a single UTF-8 line
    /// "&lt;token&gt; &lt;command&gt; [args...]" (arguments with spaces in double quotes); the reply is "OK" or "ERR &lt;message&gt;" on
    /// the first line followed by the command's output. Listens on 127.0.0.1, or on every interface when LAN access is
    /// allowed; the token is required either way. Requests are handled one at a time on a background thread.
    /// </summary>
    public sealed class RemoteServer : IDisposable
    {
        public const int DefaultPort = 27610;
        public const string FirewallRule = "ThinkBook Fan Control (tbfc)";
        private const int MaxRequest = 8192;

        private readonly TcpListener _listener;
        private readonly string _token;
        private readonly Func<string[], string> _handler;
        private volatile bool _stop;

        /// <summary>handler runs a command and returns its output; an exception becomes "ERR message".</summary>
        public RemoteServer(int port, bool lan, string token, Func<string[], string> handler)
        {
            _token = token; _handler = handler;
            _listener = new TcpListener(lan ? IPAddress.Any : IPAddress.Loopback, port);
            _listener.Start();
            new Thread(Loop) { IsBackground = true, Name = "RemoteServer" }.Start();
        }

        private void Loop()
        {
            while (!_stop)
            {
                TcpClient c;
                try { c = _listener.AcceptTcpClient(); }
                catch (Exception) { if (_stop) return; Thread.Sleep(200); continue; }
                try { Handle(c); } catch (Exception) { }
            }
        }

        private void Handle(TcpClient c)
        {
            using (c)
            {
                c.ReceiveTimeout = c.SendTimeout = 5000;
                var stream = c.GetStream();
                var buf = new List<byte>();
                int b;
                while (buf.Count < MaxRequest && (b = stream.ReadByte()) >= 0 && b != '\n') buf.Add((byte)b);
                var parts = Split(Encoding.UTF8.GetString(buf.ToArray()).TrimEnd('\r'));
                string reply;
                if (parts.Count == 0 || !SameToken(parts[0], _token))
                {
                    Thread.Sleep(500); // slow down guessing
                    reply = "ERR 令牌错误（tbfc --token <令牌>，令牌在程序「安全与常规」里复制）\n";
                }
                else
                {
                    try { reply = "OK\n" + _handler(parts.GetRange(1, parts.Count - 1).ToArray()); }
                    catch (Exception ex) { reply = "ERR " + ex.Message + "\n"; }
                }
                var bytes = Encoding.UTF8.GetBytes(reply);
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        /// <summary>Whitespace-separated words; double quotes group words, \" is a literal quote.</summary>
        public static List<string> Split(string line)
        {
            var words = new List<string>();
            var cur = new StringBuilder();
            bool quoted = false, any = false;
            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (ch == '\\' && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; any = true; }
                else if (ch == '"') { quoted = !quoted; any = true; }
                else if (char.IsWhiteSpace(ch) && !quoted) { if (any) { words.Add(cur.ToString()); cur.Length = 0; any = false; } }
                else { cur.Append(ch); any = true; }
            }
            if (any) words.Add(cur.ToString());
            return words;
        }

        private static bool SameToken(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        public static string NewToken()
        {
            var bytes = new byte[16];
            using (var rng = new System.Security.Cryptography.RNGCryptoServiceProvider()) rng.GetBytes(bytes);
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>Allow / remove inbound TCP on the port for this exe in Windows Firewall (LAN access). Returns netsh's exit code.</summary>
        public static int SetFirewall(bool allow, int port, string exe)
        {
            Netsh("advfirewall firewall delete rule name=\"" + FirewallRule + "\"");
            if (!allow) return 0;
            return Netsh(string.Format("advfirewall firewall add rule name=\"{0}\" dir=in action=allow protocol=TCP localport={1} program=\"{2}\" profile=private,domain",
                FirewallRule, port, exe));
        }

        private static int Netsh(string args)
        {
            var psi = new ProcessStartInfo("netsh.exe", args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
            using (var p = Process.Start(psi)) { p.StandardOutput.ReadToEnd(); p.WaitForExit(10000); return p.ExitCode; }
        }

        public void Dispose()
        {
            _stop = true;
            try { _listener.Stop(); } catch (Exception) { }
        }
    }
}
