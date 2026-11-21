// Prayer Tray - a lightweight Windows tray app for UAE prayer times from Awqaf (awqaf.gov.ae).
// Built with the .NET Framework compiler that ships with Windows (C# 5). See build.ps1.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Media;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("Prayer Tray")]
[assembly: System.Reflection.AssemblyProduct("Prayer Tray")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

namespace PrayerTray
{
    // ---------------------------------------------------------------- settings
    static class Settings
    {
        // Default area = the awqaf.gov.ae page's own default; users pick theirs from the tray menu.
        public const int DefaultOfficialId = 36;
        public const string ApiBase = "https://internationalprayertime.awqaf.gov.ae/api/v1/";
        // Public key used by the awqaf.gov.ae web page itself.
        public const string ApiKey = "ak_1148a6086913233acc33709fc9aa6e93";
        public const int ReminderMinutesBeforeNext = 30;
        public const int AthanAlertWindowMinutes = 20;   // don't show a stale athan alert after waking from sleep
        public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string RunName = "PrayerTray";
    }

    // ---------------------------------------------------------------- theme
    static class Theme
    {
        public static float Scale = 1f;
        public static int S(float v) { return (int)Math.Round(v * Scale); }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

    }

    // ---------------------------------------------------------------- data
    class Prayer
    {
        public string Key, En;
        public DateTime Time;
        public bool Obligatory;
    }

    class DayInfo
    {
        public DateTime Date;
        public string Hijri;
        public List<Prayer> Prayers = new List<Prayer>();
        public Prayer Get(string key)
        {
            foreach (var p in Prayers) if (p.Key == key) return p;
            return null;
        }
    }

    public class StoreData
    {
        public Dictionary<string, Dictionary<string, string>> Days { get; set; }
        public Dictionary<string, List<string>> Prayed { get; set; }
        public Dictionary<string, List<string>> Fired { get; set; }
        public string LastFetchDate { get; set; }
        public bool StartupConfigured { get; set; }
        public string FirstRun { get; set; }
        public bool Silent { get; set; }
        public bool AdhkarOff { get; set; }
        public int OfficialId { get; set; }
        public string Area { get; set; }
        public string City { get; set; }
    }

    static class Store
    {
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrayerTray");
        static readonly string FilePath = Path.Combine(Dir, "data.json");
        public static StoreData Data;

        public static readonly string[] Keys = { "fajr", "shurooq", "dhuhr", "asr", "maghrib", "isha" };
        public static readonly string[] En = { "Fajr", "Shurooq", "Dhuhr", "Asr", "Maghrib", "Isha" };

        public static string Key(DateTime d) { return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }

        public static void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    Data = new JavaScriptSerializer().Deserialize<StoreData>(File.ReadAllText(FilePath, Encoding.UTF8));
            }
            catch (Exception ex) { Log.Write("load: " + ex.Message); }
            if (Data == null) Data = new StoreData();
            if (Data.Days == null) Data.Days = new Dictionary<string, Dictionary<string, string>>();
            if (Data.Prayed == null) Data.Prayed = new Dictionary<string, List<string>>();
            if (Data.Fired == null) Data.Fired = new Dictionary<string, List<string>>();
            if (Data.FirstRun == null) Data.FirstRun = Key(DateTime.Today);
            if (Data.OfficialId == 0) { Data.OfficialId = Settings.DefaultOfficialId; Data.Area = "Musaffah"; Data.City = "Abu Dhabi"; }
        }

        public static void Save()
        {
            try
            {
                Prune();
                Directory.CreateDirectory(Dir);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(Data), Encoding.UTF8);
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch (Exception ex) { Log.Write("save: " + ex.Message); }
        }

        static void Prune()
        {
            string cutDays = Key(DateTime.Today.AddDays(-3)), cutState = Key(DateTime.Today.AddDays(-3)), cutPrayed = Key(DateTime.Today.AddDays(-60));
            foreach (var k in new List<string>(Data.Days.Keys)) if (string.CompareOrdinal(k, cutDays) < 0) Data.Days.Remove(k);
            foreach (var k in new List<string>(Data.Prayed.Keys)) if (string.CompareOrdinal(k, cutPrayed) < 0) Data.Prayed.Remove(k);
            foreach (var k in new List<string>(Data.Fired.Keys)) if (string.CompareOrdinal(k, cutState) < 0) Data.Fired.Remove(k);
        }

        static bool InList(Dictionary<string, List<string>> d, string day, string item)
        {
            List<string> l;
            return d.TryGetValue(day, out l) && l.Contains(item);
        }

        public static bool IsPrayed(string day, string key) { return InList(Data.Prayed, day, key); }
        public static bool IsFired(string day, string ev) { return InList(Data.Fired, day, ev); }

        public static void SetPrayed(string day, string key, bool on)
        {
            List<string> l;
            if (!Data.Prayed.TryGetValue(day, out l)) Data.Prayed[day] = l = new List<string>();
            l.Remove(key);
            if (on) l.Add(key);
            Save();
        }

        public static void SetFired(string day, string ev)
        {
            List<string> l;
            if (!Data.Fired.TryGetValue(day, out l)) Data.Fired[day] = l = new List<string>();
            if (!l.Contains(ev)) l.Add(ev);
            Save();
        }

        public static DayInfo GetDay(DateTime date)
        {
            Dictionary<string, string> raw;
            if (!Data.Days.TryGetValue(Key(date), out raw)) return null;
            var day = new DayInfo { Date = date.Date };
            raw.TryGetValue("hijri", out day.Hijri);
            for (int i = 0; i < Keys.Length; i++)
            {
                string s; DateTime t;
                if (!raw.TryGetValue(Keys[i], out s) || !TryTime(date, s, out t)) return null;
                day.Prayers.Add(new Prayer { Key = Keys[i], En = En[i], Time = t, Obligatory = Keys[i] != "shurooq" });
            }
            return day;
        }

        static bool TryTime(DateTime date, string s, out DateTime t)
        {
            DateTime x;
            bool ok = DateTime.TryParseExact(s, new[] { "H:mm", "H:mm:ss" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out x);
            t = date.Date + x.TimeOfDay;
            return ok;
        }
    }

    static class Log
    {
        public static void Write(string msg)
        {
            try
            {
                Directory.CreateDirectory(Store.Dir);
                string f = Path.Combine(Store.Dir, "app.log");
                if (File.Exists(f) && new FileInfo(f).Length > 200000) File.Delete(f);
                File.AppendAllText(f, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
            }
            catch { }
        }
    }

    // ---------------------------------------------------------------- awqaf api
    static class Api
    {
        static string Send(string path, string body)
        {
            var req = (HttpWebRequest)WebRequest.Create(Settings.ApiBase + path);
            req.Method = body == null ? "GET" : "POST";
            req.ContentType = "application/json";
            req.Accept = "application/json";
            req.Headers["x-api-key"] = Settings.ApiKey;
            req.Headers["Accept-Language"] = "en";
            req.UserAgent = "PrayerTray/1.0";
            req.Timeout = 25000;
            if (body != null)
            {
                byte[] b = Encoding.UTF8.GetBytes(body);
                using (var s = req.GetRequestStream()) s.Write(b, 0, b.Length);
            }
            using (var r = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        static string Str(IDictionary d, string k)
        {
            return d.Contains(k) && d[k] != null ? Convert.ToString(d[k], CultureInfo.InvariantCulture) : null;
        }

        static string Range(DateTime a, DateTime b)
        {
            return "\"start_date\":\"" + Store.Key(a) + "\",\"end_date\":\"" + Store.Key(b) + "\"";
        }

        // Emirates, each with "name" and "cities" (each city: "name", "official_id")
        public static IEnumerable Emirates()
        {
            var root = (IDictionary)new JavaScriptSerializer().DeserializeObject(Send("location/emirates?lang=en", null));
            return (IEnumerable)root["result"];
        }

        // Returns date -> { fajr, shurooq, dhuhr, asr, maghrib, isha, hijri }
        public static Dictionary<string, Dictionary<string, string>> Fetch(DateTime start)
        {
            var js = new JavaScriptSerializer();
            DateTime end = start.AddDays(30);
            string json = Send("prayertimes", "{" + Range(start, end) + ",\"official_id\":" + Store.Data.OfficialId + "}");

            var res = new Dictionary<string, Dictionary<string, string>>();
            var root = (IDictionary)js.DeserializeObject(json);
            var result = (IDictionary)root["result"];
            string[] apiKeys = { "fajer", "shuroq", "dhohur", "aser", "maghreb", "isha" };
            foreach (IDictionary pt in (IEnumerable)result["prayer_times"])
            {
                string date = Str(pt, "date");
                if (date == null) continue;
                if (date.Length > 10) date = date.Substring(0, 10);
                var day = new Dictionary<string, string>();
                for (int i = 0; i < apiKeys.Length; i++) day[Store.Keys[i]] = Str(pt, apiKeys[i]);
                res[date] = day;
            }

            try
            {
                var h = (IDictionary)js.DeserializeObject(Send("hijri/date-range", "{" + Range(start, end) + "}"));
                foreach (IDictionary item in (IEnumerable)h["result"])
                {
                    string g = Str(item, "gregorian_date");
                    if (g != null && g.Length > 10) g = g.Substring(0, 10);
                    var hd = item.Contains("hijri_date") ? item["hijri_date"] as IDictionary : null;
                    if (g != null && hd != null && res.ContainsKey(g))
                        res[g]["hijri"] = (Str(hd, "day") + " " + Str(hd, "month_name") + " " + Str(hd, "year")).Trim();
                }
            }
            catch (Exception ex) { Log.Write("hijri: " + ex.Message); }
            return res;
        }
    }

    // ---------------------------------------------------------------- native helpers
    static class Native
    {
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
        [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr p, IntPtr min, IntPtr max);
        [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, int mods, int vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // true while a fullscreen app, game or presentation is in front
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
        public static void SetCueBanner(TextBox t, string hint) { SendMessage(t.Handle, 0x1501, (IntPtr)1, hint); }   // grey placeholder text

        public static bool UserBusy()
        {
            try { int st; if (SHQueryUserNotificationState(out st) == 0) return st == 2 || st == 3 || st == 4; } catch { }
            return false;
        }

        public static void RoundCorners(IntPtr hwnd)
        {
            try { int v = 2; DwmSetWindowAttribute(hwnd, 33, ref v, 4); } catch { }
        }

        public static void TrimMemory()
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1));
            }
            catch { }
        }
    }

    // ---------------------------------------------------------------- global hotkey (Alt+P)
    class HotkeyWindow : NativeWindow, IDisposable
    {
        readonly Action onHotkey;
        public HotkeyWindow(Action onHotkey)
        {
            this.onHotkey = onHotkey;
            CreateHandle(new CreateParams());
            if (!Native.RegisterHotKey(Handle, 1, 0x1 | 0x4000, (int)Keys.P)) Log.Write("hotkey Alt+P is used by another app");
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x312) onHotkey();
            base.WndProc(ref m);
        }
        public void Dispose() { Native.UnregisterHotKey(Handle, 1); DestroyHandle(); }
    }

    // ---------------------------------------------------------------- base popup window
    class PopupForm : Form
    {
        protected readonly List<KeyValuePair<Rectangle, Action>> Hits = new List<KeyValuePair<Rectangle, Action>>();
        protected Point Mouse = new Point(-1, -1);

        public PopupForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.White;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x20000;   // drop shadow
                cp.ExStyle |= 0x80 | 0x8;   // tool window (no alt-tab), topmost
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.RoundCorners(Handle);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Mouse = e.Location;
            bool over = false;
            foreach (var h in Hits) if (h.Key.Contains(e.Location)) over = true;
            Cursor = over ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            Mouse = new Point(-1, -1);
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            foreach (var h in new List<KeyValuePair<Rectangle, Action>>(Hits))
                if (h.Key.Contains(e.Location)) { h.Value(); return; }
        }

        protected bool Hover(Rectangle r) { return r.Contains(Mouse); }

    }

    // ---------------------------------------------------------------- monochrome tokens
    static class Mono
    {
        public static readonly Color Paper = Color.White;
        public static readonly Color Ink = Color.Black;
        public static readonly Color Grey = Color.FromArgb(118, 118, 118);
        public static readonly Color Hair = Color.FromArgb(228, 228, 228);
        public static string T(DateTime t) { return t.ToString("h:mm", CultureInfo.InvariantCulture); }
    }

    // ---------------------------------------------------------------- athan / reminder alert
    class AlertForm : PopupForm
    {
        readonly string title, sub, primaryText, secondaryText;
        readonly Action onPrimary;
        public Action OnSecondary;
        readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer { Interval = 15 };
        readonly System.Windows.Forms.Timer life = new System.Windows.Forms.Timer();
        int targetY, step;
        static readonly Font fTitle = new Font("Bahnschrift SemiBold", 22f);
        static readonly Font fSub = new Font("Segoe UI", 10.5f);
        static readonly Font fBtn = new Font("Segoe UI Semibold", 9.5f);

        public AlertForm(string title, string sub, string primaryText, Action onPrimary, string secondaryText, int lifeMinutes)
        {
            this.title = title; this.sub = sub;
            this.primaryText = primaryText; this.onPrimary = onPrimary; this.secondaryText = secondaryText;
            Size = new Size(Theme.S(320), Theme.S(primaryText == null ? 96 : 150));
            var wa = Screen.PrimaryScreen.WorkingArea;
            targetY = wa.Bottom - Height - Theme.S(14);
            Location = new Point(wa.Right - Width - Theme.S(14), wa.Bottom);
            Opacity = 0;
            anim.Tick += delegate
            {
                step++;
                double t = Math.Min(1.0, step / 16.0), ease = 1 - Math.Pow(1 - t, 3);
                Top = (int)(wa.Bottom + (targetY - wa.Bottom) * ease);
                Opacity = Math.Min(1.0, t * 1.4);
                if (t >= 1) anim.Stop();
            };
            life.Interval = Math.Max(1, lifeMinutes) * 60000;
            life.Tick += delegate { Close(); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override void OnShown(EventArgs e) { base.OnShown(e); anim.Start(); life.Start(); }
        protected override void OnFormClosed(FormClosedEventArgs e) { anim.Dispose(); life.Dispose(); base.OnFormClosed(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Hits.Clear();
            int W = ClientSize.Width, H = ClientSize.Height, pad = Theme.S(22);

            TextRenderer.DrawText(g, title, fTitle, new Point(pad - Theme.S(2), Theme.S(16)), Mono.Ink, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, sub, fSub, new Point(pad, Theme.S(56)), Mono.Grey, TextFormatFlags.NoPadding);

            var cr = new Rectangle(W - Theme.S(38), Theme.S(14), Theme.S(24), Theme.S(24));
            using (var p = new Pen(Hover(cr) ? Mono.Ink : Mono.Grey, Theme.S(1.4f)))
            {
                int m = Theme.S(7);
                g.DrawLine(p, cr.X + m, cr.Y + m, cr.Right - m, cr.Bottom - m);
                g.DrawLine(p, cr.Right - m, cr.Y + m, cr.X + m, cr.Bottom - m);
            }
            Hits.Add(new KeyValuePair<Rectangle, Action>(cr, Close));

            if (primaryText == null) { using (var p = new Pen(Mono.Ink)) g.DrawRectangle(p, 0, 0, W - 1, H - 1); return; }
            int bh = Theme.S(34), by = H - bh - pad;
            var r1 = new Rectangle(pad, by, Theme.S(112), bh);
            using (var path = Theme.Round(r1, Theme.S(3)))
            using (var b = new SolidBrush(Hover(r1) ? Color.FromArgb(48, 48, 48) : Mono.Ink)) g.FillPath(b, path);
            TextRenderer.DrawText(g, primaryText, fBtn, r1, Mono.Paper, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            Hits.Add(new KeyValuePair<Rectangle, Action>(r1, delegate { if (onPrimary != null) onPrimary(); Close(); }));

            if (secondaryText != null)
            {
                var r2 = new Rectangle(r1.Right + Theme.S(8), by, Theme.S(130), bh);
                TextRenderer.DrawText(g, secondaryText, fBtn, r2, Hover(r2) ? Mono.Ink : Mono.Grey, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                Hits.Add(new KeyValuePair<Rectangle, Action>(r2, delegate { if (OnSecondary != null) OnSecondary(); Close(); }));
            }

            using (var p = new Pen(Mono.Ink)) g.DrawRectangle(p, 0, 0, W - 1, H - 1);
        }
    }

    // ---------------------------------------------------------------- tray panel (today's times)
    class PanelForm : PopupForm
    {
        readonly TrayApp app;
        readonly System.Windows.Forms.Timer clock = new System.Windows.Forms.Timer { Interval = 1000 };
        public DateTime HiddenAt = DateTime.MinValue;

        static readonly Font fNextName = new Font("Bahnschrift SemiLight", 13f);
        static readonly Font fBig = new Font("Bahnschrift Light", 46f);
        static readonly Font fIn = new Font("Segoe UI", 10f);
        static readonly Font fName = new Font("Segoe UI", 11f);
        static readonly Font fTime = new Font("Bahnschrift", 13f);

        const int TopH = 128, RowH = 42, HistH = 150, BottomPad = 18;
        public static DateTime? FakeNow;      // snapshots only
        public static bool ForceRamadan;      // snapshots only
        static readonly Font fDate = new Font("Bahnschrift", 9.5f);
        static readonly Font fDateToday = new Font("Bahnschrift SemiBold", 9.5f);

        public PanelForm(TrayApp app)
        {
            this.app = app;
            Size = new Size(Theme.S(300), Theme.S(TopH + RowH * 6 + HistH + BottomPad));
            clock.Tick += delegate { Invalidate(); };
            KeyPreview = true;
            KeyDown += delegate (object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Hide(); };
        }

        public void ShowNearTray()
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - Theme.S(12), wa.Bottom - Height - Theme.S(12));
            Show(); Activate(); clock.Start(); Invalidate();
        }

        protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); Hide(); }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible) { clock.Stop(); HiddenAt = DateTime.Now; Native.TrimMemory(); }
        }

        public static string Countdown(TimeSpan ts)
        {
            if (ts.TotalMinutes < 1) return "now";
            int h = (int)ts.TotalHours, m = ts.Minutes;
            return h > 0 ? h + "h " + m.ToString("00") + "m" : m + " min";
        }

        static void Mark(Graphics g, Rectangle c, bool filled, Color fg, Color bg)
        {
            if (filled)
            {
                using (var b = new SolidBrush(fg)) g.FillEllipse(b, c);
                using (var p = new Pen(bg, Theme.S(1.8f)))
                {
                    p.StartCap = p.EndCap = LineCap.Round;
                    g.DrawLines(p, new[] {
                        new PointF(c.X + c.Width * 0.28f, c.Y + c.Height * 0.52f),
                        new PointF(c.X + c.Width * 0.44f, c.Y + c.Height * 0.68f),
                        new PointF(c.X + c.Width * 0.73f, c.Y + c.Height * 0.35f) });
                }
            }
            else using (var p = new Pen(fg, Theme.S(1.4f))) g.DrawEllipse(p, c);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Hits.Clear();
            int W = ClientSize.Width, pad = Theme.S(24);
            DateTime now = FakeNow ?? DateTime.Now;
            var day = Store.GetDay(now.Date);

            // Ramadan: Suhoor ends at Fajr, Iftar at Maghrib; otherwise the next prayer
            string label = null; DateTime target = DateTime.MinValue;
            if (day != null && (ForceRamadan || (day.Hijri != null && day.Hijri.IndexOf("Rama", StringComparison.OrdinalIgnoreCase) >= 0)))
            {
                if (now < day.Get("fajr").Time) { label = "Suhoor ends"; target = day.Get("fajr").Time; }
                else if (now < day.Get("maghrib").Time) { label = "Iftar"; target = day.Get("maghrib").Time; }
            }
            DayInfo nd; var next = app.Next(now, out nd);
            if (label == null && next != null) { label = next.En; target = next.Time; }
            if (label != null)
            {
                TextRenderer.DrawText(g, label, fNextName, new Point(pad, Theme.S(20)), Mono.Ink, TextFormatFlags.NoPadding);
                var big = Mono.T(target);
                var sz = TextRenderer.MeasureText(g, big, fBig, Size.Empty, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, big, fBig, new Point(pad - Theme.S(4), Theme.S(40)), Mono.Ink, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, "in " + Countdown(target - now), fIn, new Point(pad + sz.Width + Theme.S(6), Theme.S(40) + sz.Height - Theme.S(26)), Mono.Grey, TextFormatFlags.NoPadding);
            }

            int y = Theme.S(TopH);
            if (day == null)
            {
                string msg = app.LastError != null ? "Can't reach awqaf.gov.ae.\nRetrying every 10 minutes." : "Loading today's times…";
                TextRenderer.DrawText(g, msg, fName, new Rectangle(0, y, W, Theme.S(RowH * 6)), Mono.Grey, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            using (var pen = new Pen(Mono.Hair)) g.DrawLine(pen, pad, y, W - pad, y);
            DayInfo cd; var current = app.Current(now, out cd);
            string dk = Store.Key(day.Date);
            foreach (var p in day.Prayers)
            {
                var row = new Rectangle(0, y, W, Theme.S(RowH));
                bool isCurrent = current != null && cd != null && cd.Date == day.Date && current.Key == p.Key;
                Color fg = isCurrent ? Mono.Paper : (p.Obligatory ? Mono.Ink : Mono.Grey);
                if (isCurrent) using (var b = new SolidBrush(Mono.Ink)) g.FillRectangle(b, row);

                string name = p.Key == "shurooq" ? "Sunrise" : p.En;
                TextRenderer.DrawText(g, name, fName, new Rectangle(pad, row.Y, Theme.S(140), row.Height), fg, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, Mono.T(p.Time), fTime, new Rectangle(0, row.Y, W - pad - Theme.S(34), row.Height), fg, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

                if (p.Obligatory && now >= p.Time)
                {
                    int d = Theme.S(18);
                    var c = new Rectangle(W - pad - d, row.Y + (row.Height - d) / 2, d, d);
                    bool prayed = Store.IsPrayed(dk, p.Key);
                    Mark(g, c, prayed, fg, isCurrent ? Mono.Ink : Mono.Paper);
                    string key = p.Key;
                    Hits.Add(new KeyValuePair<Rectangle, Action>(row, delegate { app.MarkPrayed(dk, key, !prayed); }));
                }
                y += Theme.S(RowH);
            }
            DrawHistory(g, now, y, W, pad, current, cd);
            using (var p = new Pen(Mono.Ink)) g.DrawRectangle(p, 0, 0, W - 1, ClientSize.Height - 1);
        }

        // Last 7 days: one column per day, one dot per prayer (Fajr at top, Isha at bottom).
        // Filled = prayed, ring = not marked, faint = not due yet. Click a dot to toggle it.
        void DrawHistory(Graphics g, DateTime now, int y, int W, int pad, Prayer current, DayInfo cd)
        {
            string[] keys = { "fajr", "dhuhr", "asr", "maghrib", "isha" };
            DateTime first;
            if (!DateTime.TryParseExact(Store.Data.FirstRun, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out first)) first = now.Date;
            var today = Store.GetDay(now.Date);
            y += Theme.S(14);
            using (var pen = new Pen(Mono.Hair)) g.DrawLine(pen, pad, y, W - pad, y);
            float colW = (W - 2 * pad) / 7f;
            int missed = 0, dot = Theme.S(10), step = Theme.S(17), top = y + Theme.S(42);
            for (int i = 0; i < 7; i++)
            {
                DateTime date = now.Date.AddDays(i - 6);
                string dk = Store.Key(date);
                float cx = pad + colW * (i + 0.5f);
                bool isToday = i == 6;
                TextRenderer.DrawText(g, date.Day.ToString(), isToday ? fDateToday : fDate,
                    new Rectangle((int)(cx - colW / 2), y + Theme.S(12), (int)colW, Theme.S(18)), isToday ? Mono.Ink : Mono.Grey,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
                for (int j = 0; j < keys.Length; j++)
                {
                    var c = new Rectangle((int)(cx - dot / 2f), top + j * step - dot / 2, dot, dot);
                    bool tracked = date >= first.Date;
                    bool due = tracked && (date < now.Date || (today != null && now >= today.Get(keys[j]).Time));
                    if (!due)
                    {
                        int sd = Theme.S(4);
                        using (var b = new SolidBrush(Mono.Hair)) g.FillEllipse(b, cx - sd / 2f, c.Y + (dot - sd) / 2f, sd, sd);
                        continue;
                    }
                    bool prayed = Store.IsPrayed(dk, keys[j]);
                    bool isCurrent = current != null && cd != null && cd.Date == date && current.Key == keys[j];
                    if (prayed) using (var b = new SolidBrush(Mono.Ink)) g.FillEllipse(b, c);
                    else
                    {
                        using (var p = new Pen(Mono.Ink, Theme.S(1.2f))) g.DrawEllipse(p, c);
                        if (!isCurrent) missed++;
                    }
                    string k = keys[j];
                    Hits.Add(new KeyValuePair<Rectangle, Action>(new Rectangle((int)(cx - colW / 2), c.Y - Theme.S(4), (int)colW, step),
                        delegate { app.MarkPrayed(dk, k, !prayed); }));
                }
            }
            if (missed > 0)
                TextRenderer.DrawText(g, missed + (missed == 1 ? " prayer" : " prayers") + " not marked this week", fIn,
                    new Point(pad, top + keys.Length * step - Theme.S(2)), Mono.Grey, TextFormatFlags.NoPadding);
        }
    }

    // ---------------------------------------------------------------- menu styling
    class MenuColors : ProfessionalColorTable
    {
        static readonly Color Sel = Color.FromArgb(238, 238, 238);
        public override Color ToolStripDropDownBackground { get { return Color.White; } }
        public override Color ImageMarginGradientBegin { get { return Color.White; } }
        public override Color ImageMarginGradientMiddle { get { return Color.White; } }
        public override Color ImageMarginGradientEnd { get { return Color.White; } }
        public override Color MenuBorder { get { return Color.Black; } }
        public override Color MenuItemBorder { get { return Sel; } }
        public override Color MenuItemSelected { get { return Sel; } }
        public override Color MenuItemSelectedGradientBegin { get { return Sel; } }
        public override Color MenuItemSelectedGradientEnd { get { return Sel; } }
        public override Color SeparatorDark { get { return Color.FromArgb(228, 228, 228); } }
        public override Color CheckBackground { get { return Sel; } }
        public override Color CheckSelectedBackground { get { return Color.FromArgb(222, 222, 222); } }
        public override Color ButtonSelectedBorder { get { return Color.Black; } }
        public override Color CheckPressedBackground { get { return Sel; } }
    }

    // ---------------------------------------------------------------- the app
    class TrayApp : ApplicationContext
    {
        readonly NotifyIcon tray = new NotifyIcon();
        readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer { Interval = 15000 };
        readonly Control invoker = new Control();
        readonly PanelForm panel;
        ToolStripMenuItem startupItem, locItem;
        ContextMenuStrip menu;
        AlertForm alert;
        HotkeyWindow hotkey;
        readonly Dictionary<string, DateTime> snoozes = new Dictionary<string, DateTime>();
        IntPtr iconHandle = IntPtr.Zero;
        string iconState = null;
        DateTime lastAttempt = DateTime.MinValue;
        public bool Fetching;
        public string LastError;

        public TrayApp(bool snapshotOnly) { }   // used only by --snapshot

        public TrayApp()
        {
            invoker.CreateControl();
            Store.Load();
            panel = new PanelForm(this);

            tray.ContextMenuStrip = BuildMenu();
            tray.MouseClick += delegate (object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) TogglePanel(); };

            if (!Store.Data.StartupConfigured)
            {
                SetStartup(true);
                Store.Data.StartupConfigured = true;
                Store.Save();
            }
            else if (IsStartupEnabled()) SetStartup(true);   // keep the path current if the exe moved

            hotkey = new HotkeyWindow(MarkCurrent);
            SystemEvents.PowerModeChanged += OnPower;
            SystemEvents.TimeChanged += OnTimeChanged;
            SystemEvents.UserPreferenceChanged += OnPrefs;
            tick.Tick += delegate { Tick(); };
            tick.Start();

            UpdateTray(DateTime.Now);
            tray.Visible = true;
            Tick();
            var trim = new System.Windows.Forms.Timer { Interval = 8000 };
            trim.Tick += delegate { trim.Stop(); trim.Dispose(); Native.TrimMemory(); };
            trim.Start();
        }

        public ContextMenuStrip BuildMenu()
        {
            menu = new ContextMenuStrip { Renderer = new ToolStripProfessionalRenderer(new MenuColors()), ShowImageMargin = true };
            menu.Font = new Font("Segoe UI", 9.5f);
            menu.ForeColor = Color.Black;
            menu.Items.Add("Show prayer times", null, delegate { ShowPanel(); });
            menu.Items.Add(new ToolStripMenuItem("Mark current as prayed", null, delegate { MarkCurrent(); }) { ShortcutKeyDisplayString = "Alt+P" });
            menu.Items.Add("Refresh times now", null, delegate { Fetch(true); });
            menu.Items.Add("Preview athan alert", null, delegate { PreviewAlert(); });
            menu.Items.Add(new ToolStripSeparator());
            locItem = new ToolStripMenuItem();
            locItem.DropDownItems.Add("Loading…");   // placeholder so the arrow shows
            locItem.DropDownOpening += delegate { if (locItem.DropDownItems.Count == 1) FillLocations(); };
            menu.Items.Add(locItem);
            var silentItem = new ToolStripMenuItem("Silent alerts", null, delegate { Store.Data.Silent = !Store.Data.Silent; Store.Save(); });
            var adhkarItem = new ToolStripMenuItem("Adhkar reminders", null, delegate { Store.Data.AdhkarOff = !Store.Data.AdhkarOff; Store.Save(); });
            menu.Items.Add(silentItem);
            menu.Items.Add(adhkarItem);
            startupItem = new ToolStripMenuItem("Start with Windows", null, delegate { SetStartup(!IsStartupEnabled()); });
            menu.Items.Add(startupItem);
            menu.Items.Add("Open data folder", null, delegate { try { Process.Start(Store.Dir); } catch { } });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { ExitThread(); });
            menu.Opening += delegate { silentItem.Checked = Store.Data.Silent; adhkarItem.Checked = !Store.Data.AdhkarOff; startupItem.Checked = IsStartupEnabled(); locItem.Text = Store.Data.Area + ", " + Store.Data.City; };
            locItem.Text = Store.Data.Area + ", " + Store.Data.City;
            return menu;
        }

        // Location submenu: a search box, then emirate > area, from the same list the Awqaf site uses.
        public ToolStripTextBox Search;
        readonly List<ToolStripMenuItem> found = new List<ToolStripMenuItem>();

        public void FillLocations()
        {
            locItem.DropDownItems.Clear();
            found.Clear();
            var all = new List<KeyValuePair<string, ToolStripMenuItem>>();   // area name -> "area, city" item
            try
            {
                Search = new ToolStripTextBox { AutoSize = false, Width = Theme.S(200) };
                Native.SetCueBanner(Search.TextBox, "Search area");
                locItem.DropDownItems.Add(Search);
                foreach (IDictionary em in Api.Emirates())
                {
                    string city = (string)em["name"];
                    var ei = new ToolStripMenuItem(city) { Checked = city == Store.Data.City };
                    ei.DropDown.Renderer = menu.Renderer;
                    var cities = new List<IDictionary>();
                    foreach (IDictionary c in (IEnumerable)em["cities"]) cities.Add(c);
                    cities.Sort((x, y) => string.Compare((string)x["name"], (string)y["name"], StringComparison.OrdinalIgnoreCase));
                    foreach (var c in cities)
                    {
                        string area = (string)c["name"];
                        int id = Convert.ToInt32(c["official_id"]);
                        var ci = new ToolStripMenuItem(area) { Checked = city == Store.Data.City && area == Store.Data.Area };
                        ci.Click += delegate { SetLocation(id, area, city); };
                        ei.DropDownItems.Add(ci);
                        all.Add(new KeyValuePair<string, ToolStripMenuItem>(area,
                            new ToolStripMenuItem(area + ", " + city, null, delegate { menu.Close(); SetLocation(id, area, city); })));
                    }
                    locItem.DropDownItems.Add(ei);
                }
            }
            catch (Exception ex)
            {
                Log.Write("emirates: " + ex.Message);
                locItem.DropDownItems.Clear();
                locItem.DropDownItems.Add("Loading…");
                locItem.DropDownItems.Add(new ToolStripMenuItem("Can't reach awqaf.gov.ae") { Enabled = false });
                return;
            }
            // typing shows matching areas from every emirate in place of the emirate list
            Search.TextChanged += delegate
            {
                string q = Search.Text.Trim();
                locItem.DropDown.SuspendLayout();
                foreach (var f in found) locItem.DropDownItems.Remove(f);
                found.Clear();
                for (int i = 1; i < locItem.DropDownItems.Count; i++) locItem.DropDownItems[i].Visible = q.Length == 0;
                if (q.Length > 0)
                {
                    foreach (var kv in all)
                        if (kv.Key.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 && found.Count < 15) found.Add(kv.Value);
                    if (found.Count == 0) found.Add(new ToolStripMenuItem("No matching area") { Enabled = false });
                    foreach (var f in found) locItem.DropDownItems.Add(f);
                }
                locItem.DropDown.ResumeLayout();
            };
            Search.KeyDown += delegate (object o, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter && found.Count > 0 && found[0].Enabled) { e.SuppressKeyPress = true; found[0].PerformClick(); }
            };
            locItem.DropDown.Renderer = menu.Renderer;
            locItem.DropDown.Opened += delegate { Search.Focus(); };
        }

        void SetLocation(int officialId, string area, string city)
        {
            Store.Data.OfficialId = officialId;
            Store.Data.Area = area;
            Store.Data.City = city;
            Store.Data.Days.Clear();          // old area's times
            Store.Data.LastFetchDate = null;
            Store.Save();
            locItem.DropDownItems.Clear();    // rebuild checkmarks next time
            locItem.DropDownItems.Add("Loading…");
            Fetch(true);
            UpdateTray(DateTime.Now);
        }

        void OnPower(object s, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume) invoker.BeginInvoke((MethodInvoker)delegate { lastAttempt = DateTime.MinValue; Tick(); });
        }

        void OnTimeChanged(object s, EventArgs e) { invoker.BeginInvoke((MethodInvoker)Tick); }

        void OnPrefs(object s, UserPreferenceChangedEventArgs e) { invoker.BeginInvoke((MethodInvoker)delegate { iconState = null; UpdateTray(DateTime.Now); }); }

        protected override void ExitThreadCore()
        {
            SystemEvents.PowerModeChanged -= OnPower;
            SystemEvents.TimeChanged -= OnTimeChanged;
            SystemEvents.UserPreferenceChanged -= OnPrefs;
            if (hotkey != null) hotkey.Dispose();
            tray.Visible = false;
            tray.Dispose();
            if (iconHandle != IntPtr.Zero) Native.DestroyIcon(iconHandle);
            base.ExitThreadCore();
        }

        // ---- startup (HKCU Run key)
        static bool IsStartupEnabled()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Settings.RunKey))
                return k != null && k.GetValue(Settings.RunName) != null;
        }

        static void SetStartup(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(Settings.RunKey))
                {
                    if (on) k.SetValue(Settings.RunName, "\"" + Application.ExecutablePath + "\"");
                    else if (k.GetValue(Settings.RunName) != null) k.DeleteValue(Settings.RunName);
                }
            }
            catch (Exception ex) { Log.Write("startup: " + ex.Message); }
        }

        // ---- panel
        void TogglePanel()
        {
            if (panel.Visible) { panel.Hide(); return; }
            if ((DateTime.Now - panel.HiddenAt).TotalMilliseconds < 350) return;   // click on the icon just closed it
            ShowPanel();
        }

        void ShowPanel() { panel.ShowNearTray(); }

        // ---- fetching
        public void Fetch(bool manual)
        {
            if (Fetching) return;
            Fetching = true;
            lastAttempt = DateTime.Now;
            if (panel.Visible) panel.Invalidate();
            DateTime start = DateTime.Today;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Dictionary<string, Dictionary<string, string>> got = null;
                string err = null;
                try { got = Api.Fetch(start); }
                catch (Exception ex) { err = ex.Message; Log.Write("fetch: " + ex); }
                invoker.BeginInvoke((MethodInvoker)delegate
                {
                    Fetching = false;
                    if (got != null && got.Count > 0)
                    {
                        foreach (var kv in got) Store.Data.Days[kv.Key] = kv.Value;
                        Store.Data.LastFetchDate = Store.Key(DateTime.Today);
                        Store.Save();
                        LastError = null;
                        Log.Write("fetched " + got.Count + " days");
                    }
                    else
                    {
                        LastError = err ?? "No data returned";
                        if (manual) tray.ShowBalloonTip(4000, "Prayer Times", "Couldn't update from awqaf.gov.ae. Will retry shortly.", ToolTipIcon.Warning);
                    }
                    Tick();
                    if (panel.Visible) panel.Invalidate();
                });
            });
        }

        // ---- schedule helpers
        DateTime Deadline(DayInfo day, Prayer p)
        {
            switch (p.Key)
            {
                case "fajr": return day.Get("shurooq").Time;
                case "dhuhr": return day.Get("asr").Time;
                case "asr": return day.Get("maghrib").Time;
                case "maghrib": return day.Get("isha").Time;
                default:
                    var tomorrow = Store.GetDay(day.Date.AddDays(1));
                    return tomorrow != null ? tomorrow.Get("fajr").Time : day.Get("fajr").Time.AddDays(1);
            }
        }

        static string NextName(string key)
        {
            switch (key)
            {
                case "fajr": return "Sunrise";
                case "dhuhr": return "Asr";
                case "asr": return "Maghrib";
                case "maghrib": return "Isha";
                default: return "Fajr";
            }
        }

        // The obligatory prayer whose time is running now (could be yesterday's Isha after midnight).
        public Prayer Current(DateTime now, out DayInfo dayOut)
        {
            dayOut = null;
            foreach (var d in new[] { now.Date, now.Date.AddDays(-1) })
            {
                var day = Store.GetDay(d);
                if (day == null) continue;
                for (int i = day.Prayers.Count - 1; i >= 0; i--)
                {
                    var p = day.Prayers[i];
                    if (!p.Obligatory || now < p.Time) continue;
                    if (now < Deadline(day, p)) { dayOut = day; return p; }
                    break;
                }
            }
            return null;
        }

        public Prayer Next(DateTime now, out DayInfo dayOut)
        {
            dayOut = null;
            foreach (var d in new[] { now.Date, now.Date.AddDays(1) })
            {
                var day = Store.GetDay(d);
                if (day == null) continue;
                foreach (var p in day.Prayers)
                    if (p.Obligatory && p.Time > now) { dayOut = day; return p; }
            }
            return null;
        }

        public void MarkPrayed(string dayKey, string key, bool on)
        {
            snoozes.Remove(dayKey + "|" + key);
            Store.SetPrayed(dayKey, key, on);
            if (on && alert != null && !alert.IsDisposed) alert.Close();
            UpdateTray(DateTime.Now);
            if (panel.Visible) panel.Invalidate();
        }

        // ---- main loop (every 15 s)
        void Tick()
        {
            DateTime now = DateTime.Now;
            string today = Store.Key(now.Date);
            bool haveToday = Store.Data.Days.ContainsKey(today);
            bool stale = Store.Data.LastFetchDate != today;
            double sinceAttempt = (now - lastAttempt).TotalMinutes;
            if (!Fetching && (stale || !haveToday) && sinceAttempt > (LastError == null ? 1 : 10))
                Fetch(false);

            CheckEvents(now);
            UpdateTray(now);
        }

        bool AlertOpen { get { return alert != null && !alert.IsDisposed && alert.Visible; } }

        void CheckEvents(DateTime now)
        {
            foreach (var d in new[] { now.Date.AddDays(-1), now.Date })
            {
                var day = Store.GetDay(d);
                if (day == null) continue;
                string dk = Store.Key(d);
                foreach (var p in day.Prayers)
                {
                    if (!p.Obligatory) continue;
                    bool prayed = Store.IsPrayed(dk, p.Key);
                    DateTime deadline = Deadline(day, p);

                    // 10-minute heads-up before the athan (silent)
                    if (now >= p.Time.AddMinutes(-10) && now < p.Time && !Store.IsFired(dk, p.Key + ":heads"))
                    {
                        Store.SetFired(dk, p.Key + ":heads");
                        int mins = Math.Max(1, (int)Math.Ceiling((p.Time - now).TotalMinutes));
                        ShowAlert(new AlertForm(p.En + " in " + mins + " min", "Athan at " + Mono.T(p.Time), null, null, null, 10), false);
                        return;
                    }

                    if (!prayed && now >= p.Time && now < p.Time.AddMinutes(Settings.AthanAlertWindowMinutes) && now < deadline && !Store.IsFired(dk, p.Key + ":athan"))
                    {
                        Store.SetFired(dk, p.Key + ":athan");
                        ShowAthan(dk, p);
                        return;
                    }

                    string sk = dk + "|" + p.Key;
                    DateTime snoozeAt;
                    if (snoozes.TryGetValue(sk, out snoozeAt) && now >= snoozeAt)
                    {
                        snoozes.Remove(sk);
                        if (!prayed && now < deadline) { ShowReminder(dk, p, deadline); return; }
                    }

                    DateTime remind = deadline.AddMinutes(-Settings.ReminderMinutesBeforeNext);
                    if (!prayed && now >= remind && now < deadline && !Store.IsFired(dk, p.Key + ":remind"))
                    {
                        Store.SetFired(dk, p.Key + ":athan");
                        Store.SetFired(dk, p.Key + ":remind");
                        ShowReminder(dk, p, deadline);
                        return;
                    }

                    // morning / evening adhkar, 20 min after Fajr and Asr (silent, waits for other alerts to close)
                    if (!Store.Data.AdhkarOff && (p.Key == "fajr" || p.Key == "asr") && !AlertOpen &&
                        now >= p.Time.AddMinutes(20) && now < p.Time.AddMinutes(60) && !Store.IsFired(dk, p.Key + ":adhkar"))
                    {
                        Store.SetFired(dk, p.Key + ":adhkar");
                        bool morning = p.Key == "fajr";
                        ShowAlert(new AlertForm(morning ? "Morning adhkar" : "Evening adhkar", morning ? "أذكار الصباح" : "أذكار المساء",
                            "Done", null, null, 15), false);
                        return;
                    }
                }
            }
        }

        void MarkCurrent()
        {
            DayInfo cd; var cur = Current(DateTime.Now, out cd);
            if (cur == null) { tray.ShowBalloonTip(2500, "", "No prayer to mark right now", ToolTipIcon.None); return; }
            string dk = Store.Key(cd.Date);
            if (Store.IsPrayed(dk, cur.Key)) { tray.ShowBalloonTip(2500, "", cur.En + " is already marked", ToolTipIcon.None); return; }
            MarkPrayed(dk, cur.Key, true);
            tray.ShowBalloonTip(2500, "", cur.En + " marked as prayed", ToolTipIcon.None);
        }

        void ShowAlert(AlertForm f, bool sound)
        {
            if (alert != null && !alert.IsDisposed) alert.Close();
            alert = f;
            f.FormClosed += delegate { Native.TrimMemory(); };
            f.Show();
            if (sound && !Store.Data.Silent && !Native.UserBusy()) PlaySound();
        }

        static void PlaySound()
        {
            try
            {
                string wav = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "adhan.wav");
                if (File.Exists(wav)) new SoundPlayer(wav).Play();
                else SystemSounds.Asterisk.Play();
            }
            catch { }
        }

        void ShowAthan(string dk, Prayer p)
        {
            string key = p.Key;
            ShowAlert(new AlertForm(p.En, "Time to pray, " + Mono.T(p.Time),
                "I prayed", delegate { MarkPrayed(dk, key, true); }, "Dismiss", 30), true);
        }

        void ShowReminder(string dk, Prayer p, DateTime deadline)
        {
            string key = p.Key;
            int mins = Math.Max(1, (int)Math.Round((deadline - DateTime.Now).TotalMinutes));
            var f = new AlertForm(p.En, "Not marked yet. " + NextName(p.Key) + " in " + mins + " min.",
                "I prayed", delegate { MarkPrayed(dk, key, true); }, "Remind in 10 min", 30);
            f.OnSecondary = delegate { if (DateTime.Now.AddMinutes(10) < deadline) snoozes[dk + "|" + key] = DateTime.Now.AddMinutes(10); };
            ShowAlert(f, true);
        }

        void PreviewAlert()
        {
            DayInfo d; var p = Current(DateTime.Now, out d) ?? Next(DateTime.Now, out d);
            string name = p != null ? p.En : "Asr";
            string t = p != null ? Mono.T(p.Time) : Mono.T(DateTime.Now);
            ShowAlert(new AlertForm(name, "Time to pray, " + t, "I prayed", null, "Dismiss", 2), true);
        }

        // ---- tray icon + tooltip
        void UpdateTray(DateTime now)
        {
            DayInfo cd; var cur = Current(now, out cd);
            bool pending = cur != null && !Store.IsPrayed(Store.Key(cd.Date), cur.Key);
            string state = pending ? "pending" : "ok";
            if (state != iconState)
            {
                iconState = state;
                SetIcon(pending);
            }

            DayInfo nd; var next = Next(now, out nd);
            string text;
            if (next != null)
                text = "Next: " + next.En + " " + Mono.T(next.Time) + " (in " + PanelForm.Countdown(next.Time - now) + ")";
            else
                text = Fetching ? "Prayer Times · updating…" : "Prayer Times · " + (LastError != null ? "offline, retrying" : "loading…");
            if (pending) text = cur.En + " not marked yet\n" + text;
            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }

        static bool LightTaskbar()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("SystemUsesLightTheme");
                    return v is int && (int)v == 1;
                }
            }
            catch { return false; }
        }

        void SetIcon(bool pending)
        {
            int n = SystemInformation.SmallIconSize.Width;
            bool light = LightTaskbar();
            Color fg = light ? Color.Black : Color.White;
            using (var bmp = new Bitmap(n, n))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    // crescent drawn with a transparent bite so it works on any taskbar color
                    using (var path = new GraphicsPath())
                    using (var bite = new GraphicsPath())
                    {
                        float d = n * 0.8f, x = n * 0.08f, y = n * 0.1f;
                        path.AddEllipse(x, y, d, d);
                        bite.AddEllipse(x + d * 0.26f, y - d * 0.06f, d * 0.86f, d * 0.86f);
                        using (var region = new Region(path))
                        {
                            region.Exclude(bite);
                            using (var b = new SolidBrush(fg)) g.FillRegion(b, region);
                        }
                        float sx = x + d * 0.72f, sy = y + d * 0.42f, sr = d * 0.16f;
                        var pts = new PointF[10];
                        for (int i = 0; i < 10; i++)
                        {
                            double a = -Math.PI / 2 + i * Math.PI / 5;
                            float rr = i % 2 == 0 ? sr : sr * 0.45f;
                            pts[i] = new PointF(sx + (float)(Math.Cos(a) * rr), sy + (float)(Math.Sin(a) * rr));
                        }
                        using (var b = new SolidBrush(fg)) g.FillPolygon(b, pts);
                    }
                    if (pending)
                    {
                        float r = n * 0.34f;
                        using (var b = new SolidBrush(fg)) g.FillEllipse(b, n - r, n - r, r - 0.5f, r - 0.5f);
                    }
                }
                IntPtr h = bmp.GetHicon();
                var old = tray.Icon;
                tray.Icon = Icon.FromHandle(h);
                if (iconHandle != IntPtr.Zero) Native.DestroyIcon(iconHandle);
                iconHandle = h;
                if (old != null) old.Dispose();
            }
        }
    }

    static class Program
    {
        static void Save(Form f, string path)
        {
            using (var bmp = new Bitmap(f.Width, f.Height)) { f.DrawToBitmap(bmp, new Rectangle(Point.Empty, f.Size)); bmp.Save(path); }
        }

        // Renders the windows to PNGs for design review (nothing is saved): PrayerTray.exe --snapshot <folder>
        static void Snapshot(string dir)
        {
            Store.Load();
            Directory.CreateDirectory(dir);
            // sample week so the history grid has something to show (memory only)
            Store.Data.FirstRun = Store.Key(DateTime.Today.AddDays(-4));
            Store.Data.Prayed = new Dictionary<string, List<string>>();
            string[] all = { "fajr", "dhuhr", "asr", "maghrib", "isha" };
            for (int i = 1; i <= 4; i++) Store.Data.Prayed[Store.Key(DateTime.Today.AddDays(-i))] = new List<string>(all);
            Store.Data.Prayed[Store.Key(DateTime.Today.AddDays(-2))].Remove("fajr");
            Store.Data.Prayed[Store.Key(DateTime.Today)] = new List<string> { "fajr", "dhuhr", "asr" };
            var app = new TrayApp(true);
            PanelForm.FakeNow = DateTime.Today.AddHours(17).AddMinutes(50);
            Save(new PanelForm(app), Path.Combine(dir, "panel.png"));
            PanelForm.FakeNow = DateTime.Today.AddHours(14);
            PanelForm.ForceRamadan = true;
            Save(new PanelForm(app), Path.Combine(dir, "panel-ramadan.png"));
            Save(new AlertForm("Asr in 10 min", "Athan at 3:08", null, null, null, 1), Path.Combine(dir, "heads-up.png"));
            Save(new AlertForm("Asr", "Not marked yet. Maghrib in 30 min.", "I prayed", null, "Remind in 10 min", 1), Path.Combine(dir, "reminder.png"));
            Save(new AlertForm("Evening adhkar", "أذكار المساء", "Done", null, null, 1), Path.Combine(dir, "adhkar.png"));
        }

        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--snapshot")
            {
                try { Native.SetProcessDPIAware(); } catch { }
                using (var g = Graphics.FromHwnd(IntPtr.Zero)) Theme.Scale = g.DpiX / 96f;
                Snapshot(args[1]);
                return;
            }
            bool created;
            using (var mutex = new Mutex(true, "Local\\PrayerTray_SingleInstance_7c5e24", out created))
            {
                if (!created) return;
                try { Native.SetProcessDPIAware(); } catch { }
                using (var g = Graphics.FromHwnd(IntPtr.Zero)) Theme.Scale = g.DpiX / 96f;
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;   // TLS 1.2
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e) { Log.Write("ui: " + e.Exception); };
                Application.Run(new TrayApp());
            }
        }
    }
}
