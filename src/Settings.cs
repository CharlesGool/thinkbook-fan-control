using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ThinkBookFanControl
{
    // Auto = 均衡（EC 默认）. New values are appended so saved settings keep parsing.
    public enum FanMode { Auto, Fixed, Curve, Max, Quiet, Performance }

    public struct CurvePoint
    {
        public int Temp;
        public int Rpm;
        public CurvePoint(int temp, int rpm) { Temp = temp; Rpm = rpm; }
    }

    public static class Curve
    {
        public static List<CurvePoint> Default(FanInfo fan)
        {
            return new List<CurvePoint>
            {
                new CurvePoint(40, fan.MinRpm),
                new CurvePoint(55, Math.Max(fan.MinRpm, 2200)),
                new CurvePoint(65, 2800),
                new CurvePoint(75, 3600),
                new CurvePoint(85, Math.Min(fan.MaxRpm, 4600)),
                new CurvePoint(95, fan.MaxRpm),
            };
        }

        /// <summary>
        /// Built-in quiet curve: below the EC default (which ran ~2400–2700 RPM at 65 °C on the test machine).
        /// The 0 point means "stop" and only applies when low speed is allowed and it is cool enough;
        /// otherwise the controller raises it to the rated minimum.
        /// </summary>
        public static List<CurvePoint> Quiet(FanInfo fan)
        {
            return new List<CurvePoint>
            {
                new CurvePoint(45, 0),
                new CurvePoint(50, fan.MinRpm),
                new CurvePoint(65, Math.Max(fan.MinRpm, 2000)),
                new CurvePoint(75, 2600),
                new CurvePoint(85, 3600),
                new CurvePoint(92, fan.MaxRpm),
            };
        }

        /// <summary>Built-in performance curve: above the EC default, ramps up early.</summary>
        public static List<CurvePoint> Performance(FanInfo fan)
        {
            return new List<CurvePoint>
            {
                new CurvePoint(40, Math.Max(fan.MinRpm, 2400)),
                new CurvePoint(55, 3000),
                new CurvePoint(65, 3800),
                new CurvePoint(75, Math.Min(fan.MaxRpm, 4600)),
                new CurvePoint(85, fan.MaxRpm),
            };
        }

        /// <summary>Per-fan preset curves for Quiet / Performance, null for other modes.</summary>
        public static List<CurvePoint>[] Preset(FanMode mode, List<FanInfo> fans)
        {
            if (mode != FanMode.Quiet && mode != FanMode.Performance) return null;
            var arr = new List<CurvePoint>[fans.Count];
            for (int i = 0; i < fans.Count; i++) arr[i] = mode == FanMode.Quiet ? Quiet(fans[i]) : Performance(fans[i]);
            return arr;
        }

        /// <summary>Linear interpolation; flat beyond the end points.</summary>
        public static double Evaluate(List<CurvePoint> pts, double temp)
        {
            if (pts.Count == 0) return 0;
            if (temp <= pts[0].Temp) return pts[0].Rpm;
            for (int i = 1; i < pts.Count; i++)
            {
                if (temp <= pts[i].Temp)
                {
                    CurvePoint a = pts[i - 1], b = pts[i];
                    return a.Rpm + (b.Rpm - a.Rpm) * (temp - a.Temp) / (double)(b.Temp - a.Temp);
                }
            }
            return pts[pts.Count - 1].Rpm;
        }

        public static string Serialize(List<CurvePoint> pts)
        {
            var sb = new StringBuilder();
            foreach (var p in pts) { if (sb.Length > 0) sb.Append(','); sb.Append(p.Temp).Append(':').Append(p.Rpm); }
            return sb.ToString();
        }

        public static List<CurvePoint> Parse(string s)
        {
            var list = new List<CurvePoint>();
            foreach (var part in s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split(':');
                int t, r;
                if (kv.Length == 2 && int.TryParse(kv[0], out t) && int.TryParse(kv[1], out r)) list.Add(new CurvePoint(t, r));
            }
            list.Sort((a, b) => a.Temp.CompareTo(b.Temp));
            return list;
        }
    }

    /// <summary>A named custom curve set: one curve per fan, optionally shared.</summary>
    public class CurveProfile
    {
        public string Name;
        public bool Link = true;
        public List<CurvePoint>[] Curves;

        public static CurveProfile Default(string name, List<FanInfo> fans)
        {
            var p = new CurveProfile { Name = name, Curves = new List<CurvePoint>[fans.Count] };
            for (int i = 0; i < fans.Count; i++) p.Curves[i] = Curve.Default(fans[i]);
            return p;
        }

        public CurveProfile Clone(string name)
        {
            var p = new CurveProfile { Name = name, Link = Link, Curves = new List<CurvePoint>[Curves.Length] };
            for (int i = 0; i < Curves.Length; i++) p.Curves[i] = new List<CurvePoint>(Curves[i]);
            return p;
        }
    }

    /// <summary>All user settings. Shared between UI and controller thread: lock on the instance.</summary>
    public class Settings
    {
        public const string AppDirName = "ThinkBookFanControl";
        private const string OldAppDirName = "LenovoFanCurve";

        public FanMode Mode = FanMode.Auto;
        public int[] FixedRpm;
        public List<CurveProfile> Profiles = new List<CurveProfile>();
        public int ActiveProfile;
        public bool LinkFixed = true;
        public int HysteresisC = 4;
        public int SmoothingSec = 3;
        public int CriticalTempC = 92;
        public bool MinimizeToTray = true;
        public bool AllowLowSpeed;          // allow 1000..min RPM and 0 = stop
        public int LowSpeedMaxTempC = 50;   // low speed / stop only below this temperature

        /// <summary>The active custom curve profile; Curves / LinkCurves read and write through it.</summary>
        public CurveProfile Profile { get { return Profiles[ActiveProfile]; } }
        public List<CurvePoint>[] Curves { get { return Profile.Curves; } }
        public bool LinkCurves { get { return Profile.Link; } set { Profile.Link = value; } }

        /// <summary>Name not used by any profile yet: "base", "base 2", "base 3", ...</summary>
        public string UniqueProfileName(string baseName)
        {
            for (int n = 1; ; n++)
            {
                string name = n == 1 ? baseName : baseName + " " + n;
                if (!Profiles.Exists(p => p.Name == name)) return name;
            }
        }

        private static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppDirName); } }
        private static string FilePath { get { return Path.Combine(Dir, "settings.ini"); } }
        private static string OldFilePath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), OldAppDirName, "settings.ini"); } }

        public static Settings Load(List<FanInfo> fans)
        {
            var s = new Settings();
            s.FixedRpm = new int[fans.Count];
            for (int i = 0; i < fans.Count; i++) s.FixedRpm[i] = Math.Min(fans[i].MaxRpm, Math.Max(fans[i].MinRpm, 3000));

            var kv = new Dictionary<string, string>();
            try
            {
                string path = File.Exists(FilePath) ? FilePath : OldFilePath; // migrate from the old app name
                if (File.Exists(path))
                    foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0) kv[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }
            }
            catch (Exception) { }

            string v;
            FanMode m;
            if (kv.TryGetValue("Mode", out v) && Enum.TryParse(v, out m)) s.Mode = m;
            if (kv.TryGetValue("LinkFixed", out v)) s.LinkFixed = v == "1";
            if (kv.TryGetValue("MinimizeToTray", out v)) s.MinimizeToTray = v == "1";
            if (kv.TryGetValue("AllowLowSpeed", out v)) s.AllowLowSpeed = v == "1";
            s.HysteresisC = Int(kv, "HysteresisC", s.HysteresisC, 0, 15);
            s.SmoothingSec = Int(kv, "SmoothingSec", s.SmoothingSec, 1, 30);
            s.CriticalTempC = Int(kv, "CriticalTempC", s.CriticalTempC, 75, 100);
            s.LowSpeedMaxTempC = Int(kv, "LowSpeedMaxTempC", s.LowSpeedMaxTempC, 35, 70);
            for (int i = 0; i < fans.Count; i++) s.FixedRpm[i] = Int(kv, "Fixed" + fans[i].Id, s.FixedRpm[i], 0, fans[i].MaxRpm);

            // Profiles: Profile<n>.Name / .Link / .Curve<fanId>. Older files had a single LinkCurves + Curve<fanId>.
            int count = Int(kv, "ProfileCount", 0, 0, 100);
            for (int n = 0; n < count; n++)
            {
                string pre = "Profile" + n + ".";
                if (!kv.TryGetValue(pre + "Name", out v) || v.Length == 0) continue;
                var p = CurveProfile.Default(v, fans);
                string link;
                if (kv.TryGetValue(pre + "Link", out link)) p.Link = link == "1";
                ReadCurves(kv, pre, fans, p);
                s.Profiles.Add(p);
            }
            if (s.Profiles.Count == 0)
            {
                var p = CurveProfile.Default("我的曲线", fans);
                if (kv.TryGetValue("LinkCurves", out v)) p.Link = v == "1";
                ReadCurves(kv, "", fans, p);
                s.Profiles.Add(p);
            }
            s.ActiveProfile = Int(kv, "ActiveProfile", 0, 0, s.Profiles.Count - 1);
            return s;
        }

        private static void ReadCurves(Dictionary<string, string> kv, string prefix, List<FanInfo> fans, CurveProfile p)
        {
            string v;
            for (int i = 0; i < fans.Count; i++)
                if (kv.TryGetValue(prefix + "Curve" + fans[i].Id, out v))
                {
                    var c = Curve.Parse(v);
                    if (c.Count >= 2) p.Curves[i] = c;
                }
        }

        private static int Int(Dictionary<string, string> kv, string key, int def, int lo, int hi)
        {
            string v; int n;
            if (kv.TryGetValue(key, out v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return Clamp(n, lo, hi);
            return def;
        }

        public void Save(List<FanInfo> fans)
        {
            var sb = new StringBuilder();
            lock (this)
            {
                sb.AppendLine("Mode=" + Mode);
                sb.AppendLine("LinkFixed=" + (LinkFixed ? 1 : 0));
                sb.AppendLine("HysteresisC=" + HysteresisC);
                sb.AppendLine("SmoothingSec=" + SmoothingSec);
                sb.AppendLine("CriticalTempC=" + CriticalTempC);
                sb.AppendLine("MinimizeToTray=" + (MinimizeToTray ? 1 : 0));
                sb.AppendLine("AllowLowSpeed=" + (AllowLowSpeed ? 1 : 0));
                sb.AppendLine("LowSpeedMaxTempC=" + LowSpeedMaxTempC);
                for (int i = 0; i < fans.Count; i++) sb.AppendLine("Fixed" + fans[i].Id + "=" + FixedRpm[i]);
                sb.AppendLine("ActiveProfile=" + ActiveProfile);
                sb.AppendLine("ProfileCount=" + Profiles.Count);
                for (int n = 0; n < Profiles.Count; n++)
                {
                    var p = Profiles[n];
                    sb.AppendLine("Profile" + n + ".Name=" + p.Name);
                    sb.AppendLine("Profile" + n + ".Link=" + (p.Link ? 1 : 0));
                    for (int i = 0; i < fans.Count; i++) sb.AppendLine("Profile" + n + ".Curve" + fans[i].Id + "=" + Curve.Serialize(p.Curves[i]));
                }
            }
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception) { }
        }

        public static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

        /// <summary>
        /// Snap a user RPM to what the fan can do: 100 RPM steps within [min, max], or with low speed allowed
        /// [LowestStableRpm, max] plus 0 = stop.
        /// </summary>
        public static int SnapRpm(int rpm, FanInfo fan, bool allowLow)
        {
            rpm = (int)Math.Round(rpm / 100.0) * 100;
            if (!allowLow) return Clamp(rpm, fan.MinRpm, fan.MaxRpm);
            if (rpm < LenovoWmi.LowestStableRpm) return 0;
            return Clamp(rpm, LenovoWmi.LowestStableRpm, fan.MaxRpm);
        }

        /// <summary>Raise everything below the rated minimum back to it (when low speed gets disabled).</summary>
        public void ClampToRated(List<FanInfo> fans)
        {
            for (int i = 0; i < fans.Count; i++)
            {
                FixedRpm[i] = Clamp(FixedRpm[i], fans[i].MinRpm, fans[i].MaxRpm);
                foreach (var p in Profiles)
                    for (int j = 0; j < p.Curves[i].Count; j++)
                        p.Curves[i][j] = new CurvePoint(p.Curves[i][j].Temp, Clamp(p.Curves[i][j].Rpm, fans[i].MinRpm, fans[i].MaxRpm));
            }
        }
    }
}
