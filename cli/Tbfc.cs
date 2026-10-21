using System;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace ThinkBookFanControl.Cli
{
    /// <summary>
    /// tbfc: command-line client for ThinkBook Fan Control's control port (no admin rights needed).
    ///   tbfc [--host H] [--port P] [--token T] [--timeout S] &lt;command&gt; [args...]
    /// On this machine host / port / token default to the app's settings.ini (same Windows user); TBFC_HOST, TBFC_PORT and
    /// TBFC_TOKEN can set them too. Exit code: 0 = OK, 1 = the app refused the command, 2 = could not connect / bad usage.
    /// </summary>
    internal static class Tbfc
    {
        private const int DefaultPort = 27610;

        private static int Main(string[] args)
        {
            try { Console.OutputEncoding = new UTF8Encoding(false); } catch (IOException) { }
            string host = Environment.GetEnvironmentVariable("TBFC_HOST");
            string token = Environment.GetEnvironmentVariable("TBFC_TOKEN");
            string portText = Environment.GetEnvironmentVariable("TBFC_PORT");
            int port = 0, timeout = 10;
            int i = 0;
            for (; i < args.Length; i++)
            {
                string a = args[i];
                if ((a == "--host" || a == "-H") && i + 1 < args.Length) host = args[++i];
                else if ((a == "--port" || a == "-p") && i + 1 < args.Length) portText = args[++i];
                else if ((a == "--token" || a == "-t") && i + 1 < args.Length) token = args[++i];
                else if (a == "--timeout" && i + 1 < args.Length) int.TryParse(args[++i], out timeout);
                else break;
            }
            if (!string.IsNullOrEmpty(portText) && !int.TryParse(portText, out port)) { Console.Error.WriteLine("端口无效：" + portText); return 2; }

            bool local = string.IsNullOrEmpty(host) || host == "localhost" || host == "127.0.0.1" || host == "::1" || host == ".";
            if (local)
            {
                host = "127.0.0.1";
                string iniToken; int iniPort;
                ReadSettings(out iniToken, out iniPort);
                if (string.IsNullOrEmpty(token)) token = iniToken;
                if (port == 0) port = iniPort;
            }
            if (port == 0) port = DefaultPort;
            if (string.IsNullOrEmpty(token))
            {
                Console.Error.WriteLine(local ? "找不到令牌：本机没有运行过 ThinkBook 风扇控制，或用 --token 指定" : "远程控制需要 --token <令牌>（在程序「安全与常规」→「复制令牌」）");
                return 2;
            }

            var line = new StringBuilder(token);
            for (; i < args.Length; i++) line.Append(' ').Append(Quote(args[i]));
            if (line.Length == token.Length) line.Append(" help");

            try
            {
                using (var c = new TcpClient())
                {
                    var ar = c.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(3000)) throw new SocketException(10060);
                    c.EndConnect(ar);
                    c.ReceiveTimeout = c.SendTimeout = timeout * 1000;
                    var s = c.GetStream();
                    var req = Encoding.UTF8.GetBytes(line.Append('\n').ToString());
                    s.Write(req, 0, req.Length);
                    string reply;
                    using (var r = new StreamReader(s, Encoding.UTF8)) reply = r.ReadToEnd();
                    int nl = reply.IndexOf('\n');
                    string head = nl < 0 ? reply : reply.Substring(0, nl), body = nl < 0 ? "" : reply.Substring(nl + 1);
                    if (head.StartsWith("OK")) { Console.Write(body); return 0; }
                    Console.Error.WriteLine(head.StartsWith("ERR ") ? head.Substring(4) : head);
                    return 1;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("无法连接 " + host + ":" + port + "（" + ex.Message + "）。");
                Console.Error.WriteLine(local ? "请确认 ThinkBook 风扇控制正在运行，并在「安全与常规」里勾选了「命令行控制」。"
                                              : "请确认对方程序勾选了「允许局域网访问」，并且防火墙允许该端口。");
                return 2;
            }
        }

        private static string Quote(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
            return "\"" + a.Replace("\"", "\\\"") + "\"";
        }

        /// <summary>RemoteToken / RemotePort from %AppData%\ThinkBookFanControl\settings.ini.</summary>
        private static void ReadSettings(out string token, out int port)
        {
            token = null; port = 0;
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ThinkBookFanControl", "settings.ini");
                foreach (var l in File.ReadAllLines(path, Encoding.UTF8))
                {
                    if (l.StartsWith("RemoteToken=")) token = l.Substring(12).Trim();
                    else if (l.StartsWith("RemotePort=")) int.TryParse(l.Substring(11).Trim(), out port);
                }
            }
            catch (Exception) { }
        }
    }
}
