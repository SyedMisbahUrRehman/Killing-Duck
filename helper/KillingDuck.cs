// Killing Duck - tab strip companion for Windows.
//
// Chrome extensions cannot draw on the browser's tab strip, so this small
// program does it for the extension. Chrome starts it through native
// messaging. It finds the tabs and their close buttons with UI Automation
// and draws the duck in a transparent window laid exactly over the tab strip.
// Only the duck's own pixels catch the mouse; everything else clicks through
// to Chrome.
//
// When the extension asks for a tab to be closed, the duck runs over and
// pecks that tab's close button, then tells the extension, which closes the
// tab. The duck never closes anything by itself.
//
// Users: double-click KillingDuck.exe. It installs itself for the current
//        Windows user (see Setup below) and Chrome starts it from then on.
// Build: helper\install.cmd  (uses the C# 5 compiler that ships with Windows,
//        so this file sticks to C# 5: no string interpolation, no ?. etc.)

using Microsoft.Win32;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Windows.Forms;

namespace KillingDuck
{
    // ------------------------------------------------------------------
    // Entry point
    // ------------------------------------------------------------------

    static class Program
    {
        public const string Version = "1.2.0";

        [STAThread]
        static int Main(string[] args)
        {
            EnableDpiAwareness();

            // Chrome passes the extension's origin; anyone else is a person
            // who double-clicked the exe (or Settings > Apps uninstalling it).
            if (!args.Any(a => a.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)))
            {
                Application.EnableVisualStyles();
                return Setup.Run(args.Contains("/uninstall", StringComparer.OrdinalIgnoreCase));
            }

            Log.Open();
            Log.Write("start v" + Version);

            Application.EnableVisualStyles();
            try
            {
                Application.Run(new DuckApp());
            }
            catch (Exception e)
            {
                Log.Write("fatal: " + e);
                return 1;
            }
            return 0;
        }

        static void EnableDpiAwareness()
        {
            // Per-monitor v2 makes window rectangles and UI Automation
            // rectangles both physical pixels, so they line up exactly.
            try
            {
                if (Win32.SetProcessDpiAwarenessContext(new IntPtr(-4))) return;
            }
            catch (EntryPointNotFoundException) { }
            try { Win32.SetProcessDPIAware(); } catch { }
        }

    }

    // ------------------------------------------------------------------
    // Install and uninstall, for the current Windows user only (no admin)
    // ------------------------------------------------------------------

    static class Setup
    {
        const string Title = "Killing Duck";
        const string HostName = "com.killingduck.tabstrip";
        const string HostKey = @"Software\Google\Chrome\NativeMessagingHosts\" + HostName;
        const string AppsKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KillingDuck";

        // The Chrome Web Store copy, and the unpacked copy (its ID is fixed by
        // "key" in manifest.json). Keep in step with helper\install.ps1.
        static readonly string[] ExtensionIds =
        {
            "bcebhbmffhngbmdigmkfgeiecpmbaale",
            "foifgpacdfdginklojklodfnoljjhgkj"
        };

        static string Dir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KillingDuck");
            }
        }

        public static int Run(bool uninstall)
        {
            try
            {
                if (uninstall) Uninstall();
                else Install();
                return 0;
            }
            catch (Exception e)
            {
                MessageBox.Show("The duck could not be set up:\n\n" + e.Message, Title,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        // Copies this exe to %LOCALAPPDATA%\KillingDuck, registers it with
        // Chrome and adds it to Settings > Apps so it can be removed there.
        static void Install()
        {
            string dir = Dir;
            Directory.CreateDirectory(dir);
            string exe = Path.Combine(dir, "KillingDuck.exe");

            if (!string.Equals(Path.GetFullPath(Application.ExecutablePath), Path.GetFullPath(exe),
                StringComparison.OrdinalIgnoreCase))
            {
                StopOthers();
                File.Copy(Application.ExecutablePath, exe, true);
            }

            string manifest = Path.Combine(dir, HostName + ".json");
            var host = new Dictionary<string, object>
            {
                { "name", HostName },
                { "description", "Killing Duck tab strip companion" },
                { "path", exe },
                { "type", "stdio" },
                { "allowed_origins", ExtensionIds.Select(id => "chrome-extension://" + id + "/").ToArray() }
            };
            // Chrome wants UTF-8 without a byte order mark.
            File.WriteAllText(manifest, new JavaScriptSerializer().Serialize(host), new UTF8Encoding(false));

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(HostKey))
                key.SetValue("", manifest);

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(AppsKey))
            {
                key.SetValue("DisplayName", "Killing Duck (tab strip duck)");
                key.SetValue("DisplayVersion", Program.Version);
                key.SetValue("Publisher", "Killing Duck");
                key.SetValue("DisplayIcon", exe);
                key.SetValue("InstallLocation", dir);
                key.SetValue("UninstallString", "\"" + exe + "\" /uninstall");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }

            MessageBox.Show(
                "The duck is installed.\n\n" +
                "Go back to Chrome and click Killing Duck in the toolbar. " +
                "The duck walks onto your tab strip.\n\n" +
                "To remove it later: Windows Settings > Apps > Installed apps > Killing Duck.",
                Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        static void Uninstall()
        {
            if (MessageBox.Show(
                    "Remove the duck from your tab strip?\n\n" +
                    "The Killing Duck extension keeps working in Chrome without it.",
                    Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            Registry.CurrentUser.DeleteSubKeyTree(HostKey, false);
            Registry.CurrentUser.DeleteSubKeyTree(AppsKey, false);
            StopOthers();

            MessageBox.Show("The duck is gone from your tab strip.", Title,
                MessageBoxButtons.OK, MessageBoxIcon.Information);

            // This exe cannot delete itself while it runs, so cmd removes the
            // folder a couple of seconds after it exits.
            Process.Start(new ProcessStartInfo("cmd.exe",
                "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + Dir + "\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }

        // The copy Chrome is running holds the exe open. The extension
        // reconnects on its own a few seconds after it stops.
        static void StopOthers()
        {
            int self = Process.GetCurrentProcess().Id;
            foreach (Process p in Process.GetProcessesByName("KillingDuck"))
            {
                using (p)
                {
                    if (p.Id == self) continue;
                    try
                    {
                        p.Kill();
                        p.WaitForExit(3000);
                    }
                    catch { }
                }
            }
        }
    }


    static class Log
    {
        static readonly object Gate = new object();
        static string path;

        public static void Open()
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "KillingDuck");
                Directory.CreateDirectory(dir);
                path = Path.Combine(dir, "helper.log");
                if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Delete(path);
            }
            catch
            {
                path = null;
            }
        }

        public static void Write(string line)
        {
            if (path == null) return;
            lock (Gate)
            {
                try
                {
                    File.AppendAllText(path,
                        DateTime.Now.ToString("HH:mm:ss.fff ", CultureInfo.InvariantCulture) + line + Environment.NewLine);
                }
                catch { }
            }
        }
    }

    // ------------------------------------------------------------------
    // Win32
    // ------------------------------------------------------------------

    static class Win32
    {
        public const uint GW_HWNDNEXT = 2, GW_HWNDPREV = 3;
        public const uint GA_ROOTOWNER = 3;
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOPMOST = 0x8;
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;
        public static readonly IntPtr HWND_TOP = IntPtr.Zero;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE { public int CX, CY; public SIZE(int w, int h) { CX = w; CY = h; } }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth, biHeight;
            public ushort biPlanes, biBitCount;
            public uint biCompression, biSizeImage;
            public int biXPelsPerMeter, biYPelsPerMeter;
            public uint biClrUsed, biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public uint bmiColors; }

        public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] public static extern IntPtr SetCapture(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
        [DllImport("user32.dll")] public static extern IntPtr SetCursor(IntPtr cursor);
        [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("user32.dll")]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT dst, ref SIZE size,
            IntPtr hdcSrc, ref POINT src, int key, ref BLENDFUNCTION blend, int flags);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits,
            IntPtr section, uint offset);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

        public static bool IsCloaked(IntPtr hwnd)
        {
            int cloaked;
            try { return DwmGetWindowAttribute(hwnd, 14, out cloaked, 4) == 0 && cloaked != 0; }
            catch { return false; }
        }

        public static string ClassOf(IntPtr hwnd)
        {
            var sb = new StringBuilder(64);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

    }

    // ------------------------------------------------------------------
    // Chrome windows
    // ------------------------------------------------------------------

    static class Chrome
    {
        static readonly Dictionary<uint, bool> ChromePids = new Dictionary<uint, bool>();
        static readonly object Gate = new object();

        // A visible top-level window that belongs to chrome.exe. VS Code and
        // other Electron apps use the same window class, so the process
        // name is what tells them apart.
        public static bool IsChromeWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !Win32.IsWindowVisible(hwnd)) return false;
            if (Win32.ClassOf(hwnd) != "Chrome_WidgetWin_1") return false;

            uint pid;
            Win32.GetWindowThreadProcessId(hwnd, out pid);
            lock (Gate)
            {
                bool isChrome;
                if (!ChromePids.TryGetValue(pid, out isChrome))
                {
                    try
                    {
                        using (var p = Process.GetProcessById((int)pid))
                            isChrome = string.Equals(p.ProcessName, "chrome", StringComparison.OrdinalIgnoreCase);
                    }
                    catch { isChrome = false; }
                    if (ChromePids.Count > 200) ChromePids.Clear();
                    ChromePids[pid] = isChrome;
                }
                return isChrome;
            }
        }

        public static List<IntPtr> Windows()
        {
            var list = new List<IntPtr>();
            Win32.EnumWindows(delegate (IntPtr h, IntPtr l)
            {
                if (IsChromeWindow(h) && !Win32.IsIconic(h)) list.Add(h);
                return true;
            }, IntPtr.Zero);
            return list;
        }
    }

    // ------------------------------------------------------------------
    // Tab strip geometry from UI Automation (runs on its own thread)
    // ------------------------------------------------------------------

    sealed class TabInfo
    {
        public string Name;
        public Rectangle Rect;    // relative to the window's top-left corner
        public Rectangle Close;   // empty when Chrome hides the close button
    }

    sealed class Strip
    {
        public IntPtr Hwnd;
        public Rectangle Region;  // the tab strip row, relative to the window
        public int WalkLeft;      // where tabs start, relative to the window
        public int WalkRight;     // where the toolbar buttons on the right start
        public readonly List<TabInfo> Tabs = new List<TabInfo>();

        public bool Valid { get { return Region.Width > 120 && Region.Height > 12; } }

        public string[] Names() { return Tabs.Select(t => t.Name).ToArray(); }
    }

    sealed class StripWatcher
    {
        public sealed class Search
        {
            public string[] Titles;
            public volatile bool Done;
            public IntPtr Result;
        }

        sealed class Elements
        {
            public AutomationElement Region, Container, TabStrip, Actions;
            public DateTime RetryAt;
        }

        public volatile Strip Home;
        public volatile IntPtr Candidate;
        // Tab titles of the extension's last focused window. A window whose
        // tabs do not match belongs to another Chrome profile (or is
        // incognito), so the duck stays out of it.
        public volatile string[] ProfileTitles = new string[0];

        volatile Search pending;
        volatile IntPtr forced;
        DateTime lastScan = DateTime.MinValue;
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly Dictionary<IntPtr, Elements> cache = new Dictionary<IntPtr, Elements>();

        public void Start()
        {
            var t = new Thread(Loop);
            t.IsBackground = true;
            t.Name = "uia";
            t.Start();
        }

        public void Poke() { wake.Set(); }

        public Search Find(string[] titles)
        {
            var s = new Search { Titles = titles ?? new string[0] };
            pending = s;
            wake.Set();
            return s;
        }

        public void ForceHome(IntPtr hwnd)
        {
            forced = hwnd;
            wake.Set();
        }

        void Loop()
        {
            while (true)
            {
                wake.WaitOne(200);
                try { Step(); }
                catch (Exception e) { Log.Write("uia: " + e.GetType().Name + ": " + e.Message); }
            }
        }

        void Step()
        {
            var search = pending;
            if (search != null && !search.Done)
            {
                search.Result = FindByTitles(search.Titles);
                search.Done = true;
                pending = null;
            }

            IntPtr force = forced;
            if (force != IntPtr.Zero)
            {
                forced = IntPtr.Zero;
                var s = Probe(force);
                if (s != null && s.Tabs.Count > 0)
                {
                    Home = s;
                    return;
                }
            }

            var home = Home;
            IntPtr cand = Candidate;
            if (cand != IntPtr.Zero && (home == null || home.Hwnd != cand))
            {
                var s = Probe(cand);
                if (s != null && s.Tabs.Count > 0 && Accepts(s))
                {
                    Home = s;
                    return;
                }
            }


            if (home == null)
            {
                // Nothing focused yet: look for the profile's last focused window.
                var titles = ProfileTitles;
                if (titles.Length > 0 && (DateTime.Now - lastScan).TotalSeconds > 2)
                {
                    lastScan = DateTime.Now;
                    IntPtr h = FindByTitles(titles);
                    if (h != IntPtr.Zero)
                    {
                        var s = Probe(h);
                        if (s != null) Home = s;
                    }
                }
                return;
            }

            if (!Win32.IsWindow(home.Hwnd))
            {
                cache.Remove(home.Hwnd);
                Home = null;
                return;
            }

            if (Win32.IsIconic(home.Hwnd)) return;

            var fresh = Probe(home.Hwnd);
            if (fresh != null) Home = fresh;
        }

        bool Accepts(Strip s)
        {
            var titles = ProfileTitles;
            if (titles.Length == 0) return false;
            return MatchScore(s, titles) >= Needed(titles.Length, s.Tabs.Count);
        }

        public static int Needed(int a, int b)
        {
            return Math.Max(1, (int)Math.Ceiling(0.6 * Math.Max(a, b)));
        }

        // Chrome's accessible tab names add things like " - Memory usage - 109 MB"
        // after the page title, so compare by prefix.
        public static bool NameMatches(string uiaName, string title)
        {
            if (string.IsNullOrEmpty(title)) return true;
            if (uiaName == null) return false;
            if (uiaName.StartsWith(title, StringComparison.Ordinal)) return true;
            int n = Math.Min(title.Length, 24);
            return n >= 8 && uiaName.StartsWith(title.Substring(0, n), StringComparison.Ordinal);
        }

        // How many titles appear in the strip in the same order. Walking both
        // lists together tolerates a tab being added or closed in between.
        public static int MatchScore(Strip s, string[] titles)
        {
            int hits = 0, j = 0;
            foreach (string title in titles)
            {
                for (int k = j; k < s.Tabs.Count; k++)
                {
                    if (!NameMatches(s.Tabs[k].Name, title)) continue;
                    hits++;
                    j = k + 1;
                    break;
                }
            }
            return hits;
        }

        IntPtr FindByTitles(string[] titles)
        {
            IntPtr best = IntPtr.Zero;
            int bestScore = 0;
            foreach (IntPtr h in Chrome.Windows())
            {
                var s = Probe(h);
                if (s == null || s.Tabs.Count == 0) continue;
                int score = MatchScore(s, titles) * 2 + (s.Tabs.Count == titles.Length ? 1 : 0);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = h;
                }
            }
            return bestScore >= Needed(titles.Length, 0) * 2 ? best : IntPtr.Zero;
        }

        public Strip Probe(IntPtr hwnd)
        {
            Win32.RECT wr;
            if (!Win32.GetWindowRect(hwnd, out wr)) return null;

            Elements el;
            if (!cache.TryGetValue(hwnd, out el) || (el.Container == null && DateTime.Now >= el.RetryAt))
            {
                el = Locate(hwnd);
                cache[hwnd] = el;
            }
            if (el.Container == null) return null;

            try
            {
                return Read(hwnd, el, wr);
            }
            catch (ElementNotAvailableException) { }
            catch (COMException) { }
            catch (InvalidOperationException) { }
            cache.Remove(hwnd);
            return null;
        }

        static Elements Locate(IntPtr hwnd)
        {
            var el = new Elements { RetryAt = DateTime.Now.AddSeconds(3) };
            try
            {
                AutomationElement root = AutomationElement.FromHandle(hwnd);
                el.Container = FindContainer(root, 0);
                if (el.Container == null) return el;

                var walker = TreeWalker.ControlViewWalker;
                AutomationElement parent = walker.GetParent(el.Container);
                if (parent != null && parent.Current.ClassName == "TabStrip")
                {
                    el.TabStrip = parent;
                    AutomationElement region = walker.GetParent(parent);
                    if (region != null && region.Current.ClassName.StartsWith("HorizontalTabStripRegionView", StringComparison.Ordinal))
                    {
                        el.Region = region;
                        for (var c = walker.GetFirstChild(region); c != null; c = walker.GetNextSibling(c))
                            if (c.Current.ClassName == "TabStripActionContainer") el.Actions = c;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Write("locate: " + e.GetType().Name + ": " + e.Message);
                el.Container = null;
            }
            return el;
        }

        // Depth-first search for the tab list, skipping web page content
        // (which can be huge and may contain its own tab lists).
        static AutomationElement FindContainer(AutomationElement el, int depth)
        {
            if (depth > 10) return null;
            var walker = TreeWalker.ControlViewWalker;
            for (AutomationElement c = walker.GetFirstChild(el); c != null; c = walker.GetNextSibling(c))
            {
                AutomationElement.AutomationElementInformation info = c.Current;
                if (info.ControlType == ControlType.Tab && (info.ClassName == "TabContainerImpl" || info.ClassName == "TabContainer")) return c;
                if (info.ControlType == ControlType.Document) continue;
                if (info.ClassName == "MultiContentsView" || info.ClassName == "Chrome_RenderWidgetHostHWND") continue;
                var found = FindContainer(c, depth + 1);
                if (found != null) return found;
            }
            return null;
        }

        static Strip Read(IntPtr hwnd, Elements el, Win32.RECT wr)
        {
            int ox = wr.Left, oy = wr.Top;
            var strip = new Strip { Hwnd = hwnd };

            Rectangle cont = Rel(el.Container.Current.BoundingRectangle, ox, oy);
            Rectangle region = el.Region != null ? Rel(el.Region.Current.BoundingRectangle, ox, oy) : Rectangle.Empty;
            if (region.IsEmpty && !cont.IsEmpty)
                region = new Rectangle(cont.X, cont.Y, Math.Max(cont.Width, (wr.Right - wr.Left) - cont.X - 150), cont.Height);
            strip.Region = region;

            Rectangle tabStrip = el.TabStrip != null ? Rel(el.TabStrip.Current.BoundingRectangle, ox, oy) : cont;
            strip.WalkLeft = tabStrip.IsEmpty ? region.Left : tabStrip.Left;
            Rectangle actions = el.Actions != null ? Rel(el.Actions.Current.BoundingRectangle, ox, oy) : Rectangle.Empty;
            strip.WalkRight = actions.IsEmpty ? region.Right : actions.Left;

            var request = new CacheRequest();
            request.Add(AutomationElement.NameProperty);
            request.Add(AutomationElement.BoundingRectangleProperty);
            request.Add(AutomationElement.ClassNameProperty);
            request.Add(AutomationElement.ControlTypeProperty);
            request.TreeScope = TreeScope.Element | TreeScope.Children;

            AutomationElementCollection items;
            using (request.Activate())
                items = el.Container.FindAll(TreeScope.Children, Condition.TrueCondition);

            foreach (AutomationElement item in items)
            {
                if (item.Cached.ControlType != ControlType.TabItem) continue;
                string cls = item.Cached.ClassName;
                if (!string.IsNullOrEmpty(cls) && cls != "Tab") continue;  // skip tab group headers

                var tab = new TabInfo
                {
                    Name = item.Cached.Name ?? "",
                    Rect = Rel(item.Cached.BoundingRectangle, ox, oy),
                    Close = Rectangle.Empty
                };

                // The close button is the right-most button inside the tab
                // (the mute button, when shown, sits to its left).
                foreach (AutomationElement child in item.CachedChildren)
                {
                    if (child.Cached.ControlType != ControlType.Button) continue;
                    Rectangle r = Rel(child.Cached.BoundingRectangle, ox, oy);
                    if (!r.IsEmpty && (tab.Close.IsEmpty || r.Right > tab.Close.Right)) tab.Close = r;
                }
                strip.Tabs.Add(tab);
            }
            return strip;
        }

        static Rectangle Rel(System.Windows.Rect r, int ox, int oy)
        {
            if (r.IsEmpty || double.IsInfinity(r.Width) || r.Width <= 0 || r.Height <= 0) return Rectangle.Empty;
            return new Rectangle(
                (int)Math.Round(r.X) - ox, (int)Math.Round(r.Y) - oy,
                (int)Math.Round(r.Width), (int)Math.Round(r.Height));
        }

    }

    // ------------------------------------------------------------------
    // Native messaging with the extension (stdin/stdout, 4-byte length + JSON)
    // ------------------------------------------------------------------

    sealed class NativeHost
    {
        readonly Stream input = Console.OpenStandardInput();
        readonly Stream output = Console.OpenStandardOutput();
        readonly object writeGate = new object();
        readonly JavaScriptSerializer writer = new JavaScriptSerializer();
        public readonly ConcurrentQueue<Dictionary<string, object>> Inbox = new ConcurrentQueue<Dictionary<string, object>>();
        public volatile bool Closed;

        public void Start()
        {
            var t = new Thread(ReadLoop);
            t.IsBackground = true;
            t.Name = "stdin";
            t.Start();
        }

        void ReadLoop()
        {
            var reader = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
            try
            {
                var header = new byte[4];
                while (ReadExact(header, 4))
                {
                    int length = BitConverter.ToInt32(header, 0);
                    if (length <= 0 || length > 16 * 1024 * 1024) break;
                    var body = new byte[length];
                    if (!ReadExact(body, length)) break;
                    var message = reader.DeserializeObject(Encoding.UTF8.GetString(body)) as Dictionary<string, object>;
                    if (message != null) Inbox.Enqueue(message);
                }
            }
            catch (Exception e)
            {
                Log.Write("stdin: " + e.Message);
            }
            Closed = true;
        }

        bool ReadExact(byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int n = input.Read(buffer, offset, count - offset);
                if (n <= 0) return false;
                offset += n;
            }
            return true;
        }

        public void Send(Dictionary<string, object> message)
        {
            try
            {
                byte[] body = Encoding.UTF8.GetBytes(writer.Serialize(message));
                lock (writeGate)
                {
                    output.Write(BitConverter.GetBytes(body.Length), 0, 4);
                    output.Write(body, 0, body.Length);
                    output.Flush();
                }
            }
            catch (Exception e)
            {
                Log.Write("stdout: " + e.Message);
            }
        }
    }

    static class Json
    {
        public static Dictionary<string, object> Msg(string type, params object[] pairs)
        {
            var d = new Dictionary<string, object>();
            d["type"] = type;
            for (int i = 0; i + 1 < pairs.Length; i += 2) d[(string)pairs[i]] = pairs[i + 1];
            return d;
        }

        public static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
        }

        public static int Int(Dictionary<string, object> d, string key, int fallback)
        {
            object v;
            if (d.TryGetValue(key, out v) && v != null)
            {
                try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); } catch { }
            }
            return fallback;
        }

        public static bool Bool(Dictionary<string, object> d, string key, bool fallback)
        {
            object v;
            return d.TryGetValue(key, out v) && v is bool ? (bool)v : fallback;
        }

        public static string[] Strs(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v)) return null;
            var arr = v as object[];
            if (arr == null) return null;
            return arr.Select(x => x == null ? "" : x.ToString()).ToArray();
        }
    }

    // ------------------------------------------------------------------
    // Layered overlay window
    // ------------------------------------------------------------------

    sealed class Overlay : NativeWindow
    {
        public event Action<int, int> Pressed, Moved, Released, RightClicked;

        IntPtr memDc, dib, oldBitmap;
        int capW, capH;
        public Bitmap Surface;
        bool visible;
        bool capturing;
        readonly IntPtr handCursor = Win32.LoadCursor(IntPtr.Zero, new IntPtr(32649));

        public Overlay()
        {
            var cp = new CreateParams
            {
                Caption = "Killing Duck",
                X = -32000,
                Y = -32000,
                Width = 1,
                Height = 1,
                Style = unchecked((int)0x80000000),                // WS_POPUP
                ExStyle = 0x00080000 | 0x00000080 | 0x08000000     // LAYERED | TOOLWINDOW | NOACTIVATE
            };
            CreateHandle(cp);
        }

        public void EnsureSurface(int w, int h)
        {
            if (w <= capW && h <= capH && Surface != null) return;
            FreeSurface();
            capW = Math.Max(w, capW);
            capH = Math.Max(h, capH);

            var bi = new Win32.BITMAPINFO();
            bi.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(Win32.BITMAPINFOHEADER));
            bi.bmiHeader.biWidth = capW;
            bi.bmiHeader.biHeight = -capH;   // top-down
            bi.bmiHeader.biPlanes = 1;
            bi.bmiHeader.biBitCount = 32;

            IntPtr screen = Win32.GetDC(IntPtr.Zero);
            memDc = Win32.CreateCompatibleDC(screen);
            Win32.ReleaseDC(IntPtr.Zero, screen);

            IntPtr bits;
            dib = Win32.CreateDIBSection(memDc, ref bi, 0, out bits, IntPtr.Zero, 0);
            oldBitmap = Win32.SelectObject(memDc, dib);
            Surface = new Bitmap(capW, capH, capW * 4, PixelFormat.Format32bppPArgb, bits);
        }

        void FreeSurface()
        {
            if (Surface != null) Surface.Dispose();
            Surface = null;
            if (memDc != IntPtr.Zero)
            {
                Win32.SelectObject(memDc, oldBitmap);
                Win32.DeleteObject(dib);
                Win32.DeleteDC(memDc);
                memDc = IntPtr.Zero;
            }
        }

        public void Present(int x, int y, int w, int h)
        {
            var dst = new Win32.POINT(x, y);
            var size = new Win32.SIZE(w, h);
            var src = new Win32.POINT(0, 0);
            var blend = new Win32.BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            Win32.UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref size, memDc, ref src, 0, ref blend, 2);
            if (!visible)
            {
                Win32.ShowWindow(Handle, 4);   // SW_SHOWNOACTIVATE
                visible = true;
            }
        }

        public void Hide()
        {
            if (!visible) return;
            Win32.ShowWindow(Handle, 0);
            visible = false;
        }

        // Sit directly above the Chrome window in the z-order, so the duck is
        // covered by whatever covers Chrome, without being "always on top".
        public void KeepAbove(IntPtr target)
        {
            IntPtr next = Win32.GetWindow(Handle, Win32.GW_HWNDNEXT);
            for (int i = 0; i < 64 && next != IntPtr.Zero && next != target && !Win32.IsWindowVisible(next); i++)
                next = Win32.GetWindow(next, Win32.GW_HWNDNEXT);
            if (next == target) return;

            IntPtr prev = Win32.GetWindow(target, Win32.GW_HWNDPREV);
            IntPtr after = prev;
            if (prev == IntPtr.Zero || (Win32.GetWindowLong(prev, Win32.GWL_EXSTYLE) & Win32.WS_EX_TOPMOST) != 0)
                after = Win32.HWND_TOP;
            if (after == Handle) return;
            Win32.SetWindowPos(Handle, after, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
        }

        static int X(IntPtr lp) { return (short)((long)lp & 0xFFFF); }
        static int Y(IntPtr lp) { return (short)(((long)lp >> 16) & 0xFFFF); }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case 0x0021:  // WM_MOUSEACTIVATE: never take focus from Chrome
                    m.Result = new IntPtr(3);
                    return;
                case 0x0020:  // WM_SETCURSOR
                    Win32.SetCursor(handCursor);
                    m.Result = new IntPtr(1);
                    return;
                case 0x0201:  // WM_LBUTTONDOWN
                    Win32.SetCapture(Handle);
                    capturing = true;
                    if (Pressed != null) Pressed(X(m.LParam), Y(m.LParam));
                    return;
                case 0x0200:  // WM_MOUSEMOVE
                    if (Moved != null) Moved(X(m.LParam), Y(m.LParam));
                    return;
                case 0x0202:  // WM_LBUTTONUP
                    capturing = false;
                    Win32.ReleaseCapture();
                    if (Released != null) Released(X(m.LParam), Y(m.LParam));
                    return;
                case 0x0215:  // WM_CAPTURECHANGED
                    if (capturing)
                    {
                        capturing = false;
                        if (Released != null)
                        {
                            Point p = Cursor.Position;
                            Win32.RECT r;
                            Win32.GetWindowRect(Handle, out r);
                            Released(p.X - r.Left, p.Y - r.Top);
                        }
                    }
                    break;
                case 0x0205:  // WM_RBUTTONUP
                    if (RightClicked != null) RightClicked(X(m.LParam), Y(m.LParam));
                    return;
            }
            base.WndProc(ref m);
        }
    }

    // ------------------------------------------------------------------
    // Pixel art: the duck is composed from parts, then outlined
    // ------------------------------------------------------------------

    enum HeadPose { Normal = 0, Windup = 1, Strike = 2 }
    enum Eyes { Open = 0, Closed = 1, Tired = 2 }
    enum Legs { Stand = 0, WalkA = 1, WalkB = 2, Dangle = 3 }
    enum Wing { Folded = 0, Up = 1, Down = 2 }

    struct Pose
    {
        public HeadPose Head;
        public bool Quack;
        public Eyes Eyes;
        public Legs Legs;
        public Wing Wing;
        public bool Bob;
        public bool Sit;
        public int Gear;     // 0 none, 1 headband, 2 headband + shades

        public int Key
        {
            get
            {
                return (int)Head | (Quack ? 1 : 0) << 2 | (int)Eyes << 3 | (int)Legs << 5 |
                       (int)Wing << 8 | (Bob ? 1 : 0) << 10 | (Sit ? 1 : 0) << 11 | Gear << 12;
            }
        }
    }

    static class Art
    {
        public const int W = 24, H = 18;
        public const double AnchorX = 9.5;              // between the feet
        public const double StrikeReach = 24 - AnchorX; // beak tip during a peck
        public const int StrikeBeakRow = 7;             // beak centre line during a peck

        public static readonly Color Ink = Hex("352D24");
        public static readonly Color Cream = Hex("FFF8E7");
        public static readonly Color Red = Hex("DC2626");
        public static readonly Color Yellow = Hex("F5CF45");

        static readonly Dictionary<char, Color> Palette = new Dictionary<char, Color>
        {
            { 'K', Ink },
            { 'Y', Yellow },
            { 'S', Hex("E2B437") },
            { 'L', Hex("FFE36E") },
            { 'W', Hex("E0A82A") },
            { 'O', Hex("F28B27") },
            { 'o', Hex("D96F1C") },
            { 'F', Hex("F07E1E") },
            { 'E', Hex("29251F") },
            { 'H', Color.White },
            { 'M', Hex("8B2F1F") },
            { 'R', Red },
            { 'G', Hex("1E1B18") },
            { 'P', Hex("F59E9E") }
        };

        sealed class Part
        {
            public readonly int Row;
            public readonly string[] Lines;
            public Part(int row, params string[] lines) { Row = row; Lines = lines; }
        }

        // Facing right. '.' is transparent; the outline is added afterwards.
        static readonly Part Body = new Part(7,
            ".YY........YYYYY",
            ".YYY.......YYYYY",
            "..YYYYYYYYYYYYYY",
            "..YYYYYYYYYYYYYYY",
            "..YYYYYYYYYYYYYYY",
            "..YYYYYYYYYYYYYYY",
            "...SYYYYYYYYYYYS",
            ".....SSSSSSSSS");

        static readonly Part WingFolded = new Part(9,
            "....WWWWW",
            "...WWWWWWWW",
            "....WWWWWWWW",
            "......WWWWW");

        static readonly Part WingUp = new Part(4,
            "..WW",
            "..WWW",
            "...WWWW",
            "....WWWWW",
            ".....WWWWWW",
            "......WWWWW");

        static readonly Part WingDown = new Part(10,
            "....WWWWWWW",
            "...WWWWWWWW",
            "..WWWWWWW",
            "..WWWW");

        static readonly Part Head = new Part(2,
            "...........LLLLL",
            "..........LLLLLLL",
            "..........LLLLLLL",
            "..........LLLLLLL",
            "..........LLLLLLL",
            "...........LLLLL");

        static readonly Part EyesOpen = new Part(4,
            "..............HE",
            "..............EE");
        static readonly Part EyesClosed = new Part(5,
            "..............EE");
        static readonly Part EyesTired = new Part(4,
            "..............SS",
            "..............EE");
        static readonly Part Blush = new Part(6,
            ".............P");

        static readonly Part BeakClosed = new Part(5,
            ".................OOO",
            ".................oo");
        static readonly Part BeakOpen = new Part(5,
            ".................OOO",
            ".................M",
            "................ooo");

        static readonly Part Headband = new Part(3,
            "........RRRRRRRRR",
            ".......RR");
        static readonly Part Shades = new Part(4,
            "...........GGGHGG",
            ".............GGGG");

        static readonly Part[] LegParts =
        {
            new Part(15, ".......F...F", ".......FFF.FFF"),     // stand
            new Part(15, ".......F...FFF", ".......FFF"),       // walk A
            new Part(15, "......FFF..F", "...........FFF"),     // walk B
            new Part(15, "........F..F", "........F..F")        // dangle
        };

        static readonly Dictionary<long, Bitmap> Cache = new Dictionary<long, Bitmap>();

        public static Color Hex(string hex)
        {
            return Color.FromArgb(
                int.Parse(hex.Substring(0, 2), NumberStyles.HexNumber),
                int.Parse(hex.Substring(2, 2), NumberStyles.HexNumber),
                int.Parse(hex.Substring(4, 2), NumberStyles.HexNumber));
        }

        public static void ClearCache()
        {
            foreach (var b in Cache.Values) b.Dispose();
            Cache.Clear();
        }

        public static Bitmap Frame(Pose pose, int scale, bool left)
        {
            long key = ((long)pose.Key << 8) | ((long)scale << 1) | (left ? 1L : 0L);
            Bitmap bmp;
            if (Cache.TryGetValue(key, out bmp)) return bmp;
            bmp = Render(Compose(pose), scale, left);
            Cache[key] = bmp;
            return bmp;
        }

        static char[,] Compose(Pose p)
        {
            var g = new char[H, W];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    g[y, x] = '.';

            int body = p.Sit ? 2 : (p.Bob ? 1 : 0);
            int hx = 0, hy = 0;
            if (p.Head == HeadPose.Windup) { hx = -1; hy = -1; }
            else if (p.Head == HeadPose.Strike) { hx = 3; hy = 1; }
            hy += body;

            if (!p.Sit) Stamp(g, LegParts[(int)p.Legs], 0, 0);
            Stamp(g, Body, 0, body);
            Stamp(g, p.Wing == Wing.Up ? WingUp : p.Wing == Wing.Down ? WingDown : WingFolded, 0, body);
            Stamp(g, Head, hx, hy);
            Stamp(g, p.Eyes == Eyes.Closed ? EyesClosed : p.Eyes == Eyes.Tired ? EyesTired : EyesOpen, hx, hy);
            Stamp(g, Blush, hx, hy);
            Stamp(g, p.Quack ? BeakOpen : BeakClosed, hx, hy);
            if (p.Gear >= 1) Stamp(g, Headband, hx, hy);
            if (p.Gear >= 2) Stamp(g, Shades, hx, hy);

            Outline(g);
            return g;
        }

        static void Stamp(char[,] g, Part part, int dx, int dy)
        {
            for (int r = 0; r < part.Lines.Length; r++)
            {
                int y = part.Row + r + dy;
                if (y < 0 || y >= H) continue;
                string line = part.Lines[r];
                for (int c = 0; c < line.Length; c++)
                {
                    if (line[c] == '.') continue;
                    int x = c + dx;
                    if (x >= 0 && x < W) g[y, x] = line[c];
                }
            }
        }

        static void Outline(char[,] g)
        {
            var add = new List<Point>();
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    if (g[y, x] != '.') continue;
                    if (Filled(g, x - 1, y) || Filled(g, x + 1, y) || Filled(g, x, y - 1) || Filled(g, x, y + 1))
                        add.Add(new Point(x, y));
                }
            foreach (var p in add) g[p.Y, p.X] = 'K';
        }

        static bool Filled(char[,] g, int x, int y)
        {
            return x >= 0 && y >= 0 && x < W && y < H && g[y, x] != '.' && g[y, x] != 'K';
        }

        static Bitmap Render(char[,] g, int s, bool left)
        {
            var bmp = new Bitmap(W * s, H * s, PixelFormat.Format32bppPArgb);
            using (var gr = Graphics.FromImage(bmp))
            {
                gr.Clear(Color.Transparent);
                var brushes = new Dictionary<char, SolidBrush>();
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                    {
                        char c = g[y, x];
                        if (c == '.') continue;
                        SolidBrush b;
                        if (!brushes.TryGetValue(c, out b))
                        {
                            b = new SolidBrush(Palette[c]);
                            brushes[c] = b;
                        }
                        int px = left ? (W - 1 - x) : x;
                        gr.FillRectangle(b, px * s, y * s, s, s);
                    }
                foreach (var b in brushes.Values) b.Dispose();
            }
            return bmp;
        }

    }

    // Tiny 5-pixel-tall font for speech bubbles.
    static class PixelFont
    {
        static readonly Dictionary<char, string[]> Glyphs = new Dictionary<char, string[]>();

        static PixelFont()
        {
            Add('A', ".#.", "#.#", "###", "#.#", "#.#");
            Add('B', "##.", "#.#", "##.", "#.#", "##.");
            Add('C', ".##", "#..", "#..", "#..", ".##");
            Add('D', "##.", "#.#", "#.#", "#.#", "##.");
            Add('E', "###", "#..", "##.", "#..", "###");
            Add('F', "###", "#..", "##.", "#..", "#..");
            Add('G', ".##", "#..", "#.#", "#.#", ".##");
            Add('H', "#.#", "#.#", "###", "#.#", "#.#");
            Add('I', "###", ".#.", ".#.", ".#.", "###");
            Add('J', "..#", "..#", "..#", "#.#", ".#.");
            Add('K', "#.#", "#.#", "##.", "#.#", "#.#");
            Add('L', "#..", "#..", "#..", "#..", "###");
            Add('M', "#...#", "##.##", "#.#.#", "#...#", "#...#");
            Add('N', "#..#", "##.#", "#.##", "#..#", "#..#");
            Add('O', ".#.", "#.#", "#.#", "#.#", ".#.");
            Add('P', "##.", "#.#", "##.", "#..", "#..");
            Add('Q', ".#.", "#.#", "#.#", "##.", ".##");
            Add('R', "##.", "#.#", "##.", "#.#", "#.#");
            Add('S', ".##", "#..", ".#.", "..#", "##.");
            Add('T', "###", ".#.", ".#.", ".#.", ".#.");
            Add('U', "#.#", "#.#", "#.#", "#.#", "###");
            Add('V', "#.#", "#.#", "#.#", "#.#", ".#.");
            Add('W', "#...#", "#...#", "#.#.#", "##.##", "#...#");
            Add('X', "#.#", "#.#", ".#.", "#.#", "#.#");
            Add('Y', "#.#", "#.#", ".#.", ".#.", ".#.");
            Add('Z', "###", "..#", ".#.", "#..", "###");
            Add('0', "###", "#.#", "#.#", "#.#", "###");
            Add('1', ".#.", "##.", ".#.", ".#.", "###");
            Add('2', "##.", "..#", ".#.", "#..", "###");
            Add('3', "##.", "..#", ".#.", "..#", "##.");
            Add('4', "#.#", "#.#", "###", "..#", "..#");
            Add('5', "###", "#..", "##.", "..#", "##.");
            Add('6', ".##", "#..", "###", "#.#", "###");
            Add('7', "###", "..#", ".#.", ".#.", ".#.");
            Add('8', "###", "#.#", "###", "#.#", "###");
            Add('9', "###", "#.#", "###", "..#", "##.");
            Add('!', "#", "#", "#", ".", "#");
            Add('?', "##.", "..#", ".#.", "...", ".#.");
            Add('+', "...", ".#.", "###", ".#.", "...");
            Add(' ', "..", "..", "..", "..", "..");
        }

        static void Add(char c, params string[] rows) { Glyphs[c] = rows; }

        static string[] Glyph(char c)
        {
            string[] g;
            return Glyphs.TryGetValue(char.ToUpperInvariant(c), out g) ? g : Glyphs['?'];
        }

        public static int Measure(string text)
        {
            int w = 0;
            foreach (char c in text) w += Glyph(c)[0].Length + 1;
            return Math.Max(0, w - 1);
        }

        public static void Draw(Graphics g, string text, int x, int y, int s, Brush brush)
        {
            foreach (char c in text)
            {
                string[] rows = Glyph(c);
                for (int r = 0; r < rows.Length; r++)
                    for (int k = 0; k < rows[r].Length; k++)
                        if (rows[r][k] == '#') g.FillRectangle(brush, x + k * s, y + r * s, s, s);
                x += (rows[0].Length + 1) * s;
            }
        }

        public static void DrawOutlined(Graphics g, string text, int x, int y, int s, Brush fill, Brush outline)
        {
            int o = Math.Max(1, s / 2);
            Draw(g, text, x - o, y, s, outline);
            Draw(g, text, x + o, y, s, outline);
            Draw(g, text, x, y - o, s, outline);
            Draw(g, text, x, y + o, s, outline);
            Draw(g, text, x, y, s, fill);
        }
    }

    // ------------------------------------------------------------------
    // Sounds, synthesised at start-up (no audio files to ship)
    // ------------------------------------------------------------------

    static class Sfx
    {
        public static bool On = true;
        public static SoundPlayer Quack, Peck, Pop;
        const int Rate = 22050;

        public static void Init()
        {
            try
            {
                Quack = Load(MakeQuack(), 0.55);
                Peck = Load(MakePeck(), 0.35);
                Pop = Load(MakePop(), 0.5);
            }
            catch (Exception e)
            {
                Log.Write("sfx: " + e.Message);
            }
        }

        public static void Play(SoundPlayer p)
        {
            if (!On || p == null) return;
            try { p.Play(); } catch { }
        }

        static SoundPlayer Load(float[] samples, double gain)
        {
            var p = new SoundPlayer(new MemoryStream(Wav(samples, gain)));
            p.Load();
            return p;
        }

        static byte[] Wav(float[] s, double gain)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            int bytes = s.Length * 2;
            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            w.Write(36 + bytes);
            w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16);
            w.Write((short)1);
            w.Write((short)1);
            w.Write(Rate);
            w.Write(Rate * 2);
            w.Write((short)2);
            w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write(bytes);
            float peak = 0.0001f;
            foreach (float v in s) peak = Math.Max(peak, Math.Abs(v));
            foreach (float v in s) w.Write((short)(v / peak * gain * 32767));
            w.Flush();
            return ms.ToArray();
        }

        sealed class BandPass
        {
            readonly double b0, b2, a1, a2;
            double x1, x2, y1, y2;

            public BandPass(double freq, double q)
            {
                double w = 2 * Math.PI * freq / Rate, alpha = Math.Sin(w) / (2 * q), a0 = 1 + alpha;
                b0 = alpha / a0;
                b2 = -alpha / a0;
                a1 = -2 * Math.Cos(w) / a0;
                a2 = (1 - alpha) / a0;
            }

            public double Run(double x)
            {
                double y = b0 * x + b2 * x2 - a1 * y1 - a2 * y2;
                x2 = x1; x1 = x; y2 = y1; y1 = y;
                return y;
            }
        }

        // A nasal buzz with a falling pitch, shaped by two formants.
        static float[] MakeQuack()
        {
            const double len = 0.19;
            int n = (int)(Rate * len);
            var s = new float[n];
            var f1 = new BandPass(1000, 4);
            var f2 = new BandPass(2300, 6);
            var rnd = new Random(7);
            double phase = 0;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / Rate, p = t / len;
                double f0 = 330 + 190 * Math.Exp(-p * 3.0) + 12 * Math.Sin(2 * Math.PI * 32 * t);
                phase += f0 / Rate;
                double frac = phase - Math.Floor(phase);
                double src = (frac < 0.3 ? 1.0 : -0.43) + (2 * frac - 1) * 0.5 + (rnd.NextDouble() - 0.5) * 0.15;
                double y = f1.Run(src) + f2.Run(src) * 0.6 + src * 0.08;
                double env = Math.Min(1, t / 0.012) * (p < 0.7 ? 1 : (1 - p) / 0.3);
                s[i] = (float)(y * env);
            }
            return s;
        }

        static float[] MakePeck()
        {
            const double len = 0.05;
            int n = (int)(Rate * len);
            var s = new float[n];
            var bp = new BandPass(2200, 3);
            var rnd = new Random(3);
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / Rate;
                double v = Math.Sin(2 * Math.PI * 1300 * t) * 0.6 + bp.Run(rnd.NextDouble() * 2 - 1) * 1.5;
                s[i] = (float)(v * Math.Exp(-t * 90));
            }
            return s;
        }

        static float[] MakePop()
        {
            const double len = 0.22;
            int n = (int)(Rate * len);
            var s = new float[n];
            var rnd = new Random(5);
            double phase = 0;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / Rate, p = t / len;
                phase += (900 * Math.Exp(-p * 2.2) + 120) / Rate;
                double tone = Math.Sin(2 * Math.PI * phase) * Math.Exp(-p * 4);
                double noise = (rnd.NextDouble() * 2 - 1) * Math.Exp(-t * 40);
                s[i] = (float)(tone * 0.8 + noise * 0.5);
            }
            return s;
        }
    }

    // ------------------------------------------------------------------
    // Grabbing a picture of the tab, so it can shatter when it dies
    // ------------------------------------------------------------------

    static class TabPicture
    {
        public static Bitmap Take(IntPtr hwnd, Rectangle relTab)
        {
            try
            {
                Win32.RECT wr;
                Win32.GetWindowRect(hwnd, out wr);
                int w = wr.Right - wr.Left, h = Math.Min(wr.Bottom - wr.Top, relTab.Bottom + 2);
                Rectangle crop = Rectangle.Intersect(relTab, new Rectangle(0, 0, w, h));
                if (w <= 0 || h <= 0 || crop.Width <= 0 || crop.Height <= 0) return null;

                // PrintWindow draws only Chrome, so the duck is not in the picture.
                using (var full = new Bitmap(w, h, PixelFormat.Format24bppRgb))
                {
                    bool ok;
                    using (var g = Graphics.FromImage(full))
                    {
                        IntPtr dc = g.GetHdc();
                        try { ok = Win32.PrintWindow(hwnd, dc, 2); }
                        finally { g.ReleaseHdc(dc); }
                    }
                    if (ok)
                    {
                        Bitmap tab = full.Clone(crop, PixelFormat.Format32bppPArgb);
                        if (!IsBlack(tab)) return tab;
                        tab.Dispose();
                    }
                }

                using (var shot = new Bitmap(crop.Width, crop.Height, PixelFormat.Format24bppRgb))
                {
                    using (var g = Graphics.FromImage(shot))
                        g.CopyFromScreen(wr.Left + crop.X, wr.Top + crop.Y, 0, 0, crop.Size);
                    return shot.Clone(new Rectangle(0, 0, shot.Width, shot.Height), PixelFormat.Format32bppPArgb);
                }
            }
            catch (Exception e)
            {
                Log.Write("picture: " + e.Message);
                return null;
            }
        }

        static bool IsBlack(Bitmap b)
        {
            for (int i = 0; i < 24; i++)
            {
                Color c = b.GetPixel((i * 37) % b.Width, (i * 13) % b.Height);
                if (c.R > 8 || c.G > 8 || c.B > 8) return false;
            }
            return true;
        }
    }

    // ------------------------------------------------------------------
    // The game
    // ------------------------------------------------------------------

    enum Act { Idle, Walk, Job, Celebrate, Stunned, Sleep, Slide }
    enum Phase { Locate, Approach, Peck }

    struct PeckStep
    {
        public HeadPose Head;
        public double Duration;
        public int Event;   // 0 none, 1 alert, 2 warning peck, 3 killing peck
        public PeckStep(HeadPose head, double duration, int ev) { Head = head; Duration = duration; Event = ev; }
    }

    sealed class Job
    {
        public string Id;
        public int Index;
        public string Title;
        public string[] Titles;
        public string Reason;
        public Phase Phase;
        public double T;
        public int Side;
        public StripWatcher.Search Search;
        public bool Forced;
        public Point Target;
        public Rectangle TabRect;
        public Bitmap Picture;
        public PeckStep[] Steps;
        public int Step;
        public double StepT;
        public bool Pecked;

        public bool Hunt { get { return Reason == "hunt"; } }
    }

    sealed class Particle
    {
        public int Kind;          // 0 pixel, 1 tab shard, 2 text
        public double X, Y, VX, VY, Gravity, Rot, VR, Life, Max, Size, Drag;
        public Color Color;
        public Bitmap Image;
        public string Text;
    }

    sealed class Arc
    {
        public double X0, X1, Y0, Height, Duration, T;
    }

    sealed class DuckApp : ApplicationContext
    {
        static readonly PeckStep[] QuickPeck =
        {
            new PeckStep(HeadPose.Windup, 0.13, 0),
            new PeckStep(HeadPose.Strike, 0.10, 3),
            new PeckStep(HeadPose.Normal, 0.20, 0)
        };

        // Hunting is slower on purpose: the "!" and two warning pecks give
        // the user time to click the duck and spare the tab.
        static readonly PeckStep[] HuntPeck =
        {
            new PeckStep(HeadPose.Normal, 0.45, 1),
            new PeckStep(HeadPose.Windup, 0.16, 0),
            new PeckStep(HeadPose.Strike, 0.08, 2),
            new PeckStep(HeadPose.Normal, 0.35, 0),
            new PeckStep(HeadPose.Windup, 0.16, 0),
            new PeckStep(HeadPose.Strike, 0.08, 2),
            new PeckStep(HeadPose.Normal, 0.40, 0),
            new PeckStep(HeadPose.Windup, 0.28, 0),
            new PeckStep(HeadPose.Strike, 0.12, 3),
            new PeckStep(HeadPose.Normal, 0.20, 0)
        };

        readonly Overlay overlay;
        readonly StripWatcher watcher = new StripWatcher();
        readonly NativeHost host;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly Random rng = new Random();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        double T, lastT;

        // Layout, in overlay pixels. The overlay covers the tab strip row.
        Strip strip;
        IntPtr homeHwnd;
        int ox, oy, ow, oh, stripH, floorY, walkL, walkR;
        int scale = 2;
        double hiddenFor;
        long lastSignature;

        // The duck. (dx, dy) is the point between its feet.
        double dx, dy, vx, vy;
        int facing = 1;
        Act act = Act.Idle;
        double actT, actDur = 2, walkTarget, speedNow, lean, slideMax;
        Arc arc;
        double quackUntil, idleSince, nextChatter = 25, napUntil, sweatAt;
        bool greeted, hinted;

        // State reported by the extension.
        string mood = "happy";
        int tabCount, level = 1;
        bool gotState;

        string bubble;
        double bubbleUntil;

        readonly Queue<Job> jobs = new Queue<Job>();
        Job job;

        readonly List<Particle> parts = new List<Particle>();

        // Mouse
        bool pressed, dragging;
        int pressX, pressY, mouseX, mouseY;
        double grabDX, grabDY, dragVX, lastMoveT;
        int lastMoveX;
        int hoverTab = -1;
        readonly List<double> clicks = new List<double>();

        double K { get { return scale / 2.0; } }

        public DuckApp()
        {
            Sfx.Init();

            overlay = new Overlay();
            overlay.Pressed += OnPressed;
            overlay.Moved += OnMoved;
            overlay.Released += OnReleased;
            overlay.RightClicked += OnRightClicked;
            BuildMenu();

            watcher.Start();

            host = new NativeHost();
            host.Start();
            host.Send(Json.Msg("ready", "version", Program.Version));

            timer.Interval = 15;
            timer.Tick += delegate { Tick(); };
            timer.Start();
        }

        // --------------------------------------------------------------
        // Main loop
        // --------------------------------------------------------------

        void Tick()
        {
            double now = clock.Elapsed.TotalSeconds;
            double dt = Math.Min(0.05, now - lastT);
            lastT = now;
            T = now;

            try
            {
                Dictionary<string, object> m;
                while (host.Inbox.TryDequeue(out m)) Handle(m);
                if (host.Closed)
                {
                    Quit(null);
                    return;
                }

                PickCandidate();
                bool visible = Layout();

                if (job == null && jobs.Count > 0) StartJob(jobs.Dequeue());
                if (job != null && job.Phase == Phase.Locate) Locate(dt);

                if (!visible)
                {
                    hiddenFor += dt;
                    if (job != null && job.Phase != Phase.Locate && hiddenFor > 2.5) Abort("hidden");
                    overlay.Hide();
                    return;
                }
                hiddenFor = 0;

                UpdateDuck(dt);
                UpdateParticles(dt);
                overlay.KeepAbove(homeHwnd);
                Render();
            }
            catch (Exception e)
            {
                Log.Write("tick: " + e);
            }
        }

        void PickCandidate()
        {
            IntPtr fg = Win32.GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == overlay.Handle) return;
            // Extension popups and menus are owned by the browser window.
            IntPtr root = Win32.GetAncestor(fg, Win32.GA_ROOTOWNER);
            if (root != IntPtr.Zero && Chrome.IsChromeWindow(root)) watcher.Candidate = root;
        }

        bool Layout()
        {
            if (T < napUntil) return false;
            Strip s = watcher.Home;
            if (s == null || !s.Valid) return false;
            IntPtr h = s.Hwnd;
            if (!Win32.IsWindow(h) || !Win32.IsWindowVisible(h) || Win32.IsIconic(h) || Win32.IsCloaked(h)) return false;
            Win32.RECT wr;
            if (!Win32.GetWindowRect(h, out wr)) return false;

            strip = s;
            ox = wr.Left + s.Region.X;
            oy = wr.Top + s.Region.Y;
            ow = s.Region.Width;
            stripH = s.Region.Height;
            floorY = stripH - 1;

            int newScale = Math.Max(2, stripH / 19);
            if (newScale != scale)
            {
                scale = newScale;
                Art.ClearCache();
            }

            int margin = 10 * scale;
            walkL = s.WalkLeft - s.Region.X + margin;
            walkR = s.WalkRight - s.Region.X - margin;
            if (walkR < walkL) walkR = walkL;

            bool tall = dragging || dy > floorY + 1 || parts.Any(p => p.Kind == 1 || p.Y > stripH);
            oh = stripH + (tall ? 240 : 6 * scale);

            if (h != homeHwnd)
            {
                homeHwnd = h;
                Spawn();
            }
            return true;
        }

        // --------------------------------------------------------------
        // Messages from the extension
        // --------------------------------------------------------------

        void Handle(Dictionary<string, object> m)
        {
            string type = Json.Str(m, "type");
            if (type == "state")
            {
                string newMood = Json.Str(m, "mood") ?? "happy";
                int newCount = Json.Int(m, "tabCount", tabCount);
                int newLevel = Json.Int(m, "level", level);
                if (gotState && newCount != tabCount) Wake();
                if (gotState && newLevel > level)
                {
                    Say("LEVEL UP!", 2.5);
                    Sfx.Play(Sfx.Quack);
                    Confetti(dx, floorY - 12 * scale, 24);
                }
                if (newMood != mood) nextChatter = Math.Min(nextChatter, T + 3);
                mood = newMood;
                tabCount = newCount;
                level = newLevel;
                Sfx.On = Json.Bool(m, "sound", Sfx.On);
                string[] titles = Json.Strs(m, "titles");
                watcher.ProfileTitles = titles ?? new string[0];
                if (!gotState) watcher.Poke();
                gotState = true;
            }
            else if (type == "kill")
            {
                jobs.Enqueue(new Job
                {
                    Id = Json.Str(m, "jobId"),
                    Index = Json.Int(m, "index", -1),
                    Title = Json.Str(m, "title") ?? "",
                    Titles = Json.Strs(m, "titles") ?? new string[0],
                    Reason = Json.Str(m, "reason") ?? "user"
                });
            }
            else if (type == "close-failed")
            {
                Say("HUH?", 1.5);
            }
            else if (type == "say")
            {
                string text = Json.Str(m, "text");
                if (!string.IsNullOrEmpty(text)) Say(text, 2.5);
            }
        }


        // --------------------------------------------------------------
        // Kill jobs
        // --------------------------------------------------------------


        void StartJob(Job j)
        {
            job = j;
            job.Phase = Phase.Locate;
            job.T = 0;
            Wake();
            if (T < napUntil) Abort("napping");
        }

        // Make sure the duck is in the window that holds the tab.
        void Locate(double dt)
        {
            job.T += dt;
            Strip s = watcher.Home;
            if (s != null && s.Valid && job.Titles.Length > 0 &&
                StripWatcher.MatchScore(s, job.Titles) >= StripWatcher.Needed(job.Titles.Length, s.Tabs.Count))
            {
                if (strip == null || strip.Hwnd != s.Hwnd) return;   // wait for Layout to move the duck there
                job.Phase = Phase.Approach;
                job.T = 0;
                act = Act.Job;
                actT = 0;
                if (job.Hunt) Say("!", 1.0);
                return;
            }
            if (job.Search == null && !job.Forced)
            {
                job.Search = watcher.Find(job.Titles);
            }
            else if (job.Search != null && job.Search.Done)
            {
                if (job.Search.Result == IntPtr.Zero)
                {
                    Abort("not-found");
                    return;
                }
                watcher.ForceHome(job.Search.Result);
                job.Search = null;
                job.Forced = true;
            }
            if (job.T > 3) Abort("not-found");
        }

        TabInfo FindTab()
        {
            if (strip == null) return null;
            var tabs = strip.Tabs;
            if (job.Index >= 0 && job.Index < tabs.Count && StripWatcher.NameMatches(tabs[job.Index].Name, job.Title))
                return tabs[job.Index];
            var byName = tabs.FirstOrDefault(t => StripWatcher.NameMatches(t.Name, job.Title));
            if (byName != null) return byName;
            return job.Index >= 0 && job.Index < tabs.Count && tabs.Count == job.Titles.Length ? tabs[job.Index] : null;
        }

        Rectangle ToOverlay(Rectangle rel)
        {
            return new Rectangle(rel.X - strip.Region.X, rel.Y - strip.Region.Y, rel.Width, rel.Height);
        }

        Point PeckPoint(TabInfo tab)
        {
            Rectangle r = ToOverlay(tab.Close.IsEmpty ? tab.Rect : tab.Close);
            return new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
        }

        void Approach(double dt)
        {
            TabInfo tab = FindTab();
            if (tab == null)
            {
                if (job.T > 1.5) Abort("not-found");
                return;
            }

            Rectangle tr = ToOverlay(tab.Rect);
            Point target = PeckPoint(tab);
            if (tr.Width < 6 || target.X < 0 || target.X > ow)
            {
                Abort("hidden");
                return;
            }

            double reach = Art.StrikeReach * scale;
            if (job.Side == 0)
            {
                int side = target.X >= dx ? 1 : -1;
                if (side == 1 && target.X - reach < 10 * scale) side = -1;
                if (side == -1 && target.X + reach > ow - 10 * scale) side = 1;
                job.Side = side;
            }

            double stand = target.X - job.Side * reach;
            double d = stand - dx;
            if (Math.Abs(d) <= 1.0)
            {
                dx = stand;
                facing = job.Side;
                speedNow = 0;
                BeginPeck(tab, tr, target);
                return;
            }

            double speed;
            if (job.Hunt) speed = Math.Abs(d) > 260 * K ? 700 * K : 115 * K;
            else speed = Math.Max(260 * K, Math.Min(1100 * K, Math.Abs(d) * 4));

            double move = Math.Min(Math.Abs(d), speed * dt);
            dx += Math.Sign(d) * move;
            facing = Math.Sign(d);
            speedNow = move / Math.Max(dt, 0.001);
            if (speedNow > 380 * K && rng.NextDouble() < dt * 30) Dust(dx - facing * 6 * scale, floorY);
        }

        void BeginPeck(TabInfo tab, Rectangle tr, Point target)
        {
            job.Phase = Phase.Peck;
            job.Target = target;
            job.TabRect = tr;
            job.Picture = TabPicture.Take(strip.Hwnd, tab.Rect);
            job.Steps = job.Hunt ? HuntPeck : QuickPeck;
            job.Step = -1;
            NextStep();
        }

        void Peck(double dt)
        {
            job.StepT += dt;
            if (job.StepT >= job.Steps[job.Step].Duration) NextStep();
        }

        void NextStep()
        {
            job.Step++;
            job.StepT = 0;
            if (job.Step >= job.Steps.Length)
            {
                FinishJob();
                return;
            }
            int ev = job.Steps[job.Step].Event;
            if (ev == 1) Say("!", 1.4);
            else if (ev == 2) Hit(false);
            else if (ev == 3) Hit(true);
        }

        void Hit(bool kill)
        {
            Sfx.Play(kill ? Sfx.Pop : Sfx.Peck);
            Sparks(job.Target.X, job.Target.Y, kill ? 12 : 5);
            if (!kill) return;
            job.Pecked = true;
            Shatter();
            FloatText("+1", job.Target.X, job.Target.Y - 4 * scale, Art.Red);
            host.Send(Json.Msg("pecked", "jobId", job.Id));
        }

        void FinishJob()
        {
            if (job.Picture != null) job.Picture.Dispose();
            job = null;
            act = Act.Celebrate;
            actT = 0;
            actDur = 0.9;
            quackUntil = T + 0.5;
            idleSince = T;
            Hop(5 * scale, 0.3);
            Sfx.Play(Sfx.Quack);
        }

        void Abort(string reason)
        {
            if (job == null) return;
            host.Send(Json.Msg("abort", "jobId", job.Id, "reason", reason));
            if (job.Picture != null) job.Picture.Dispose();
            job = null;
            if (reason == "spared")
            {
                Stun(1.3);
                Say("SPARED", 1.6);
            }
            else
            {
                SetIdle(1.5);
                if (reason != "napping") Say("?", 1.2);
            }
        }

        // --------------------------------------------------------------
        // Duck behaviour
        // --------------------------------------------------------------

        void Spawn()
        {
            dx = walkL + rng.NextDouble() * (walkR - walkL);
            dy = floorY;
            vx = vy = 0;
            arc = null;
            SetIdle(1.5);
            Puff(dx, floorY - 8 * scale, 14);
            idleSince = T;
            if (!greeted)
            {
                greeted = true;
                Say("QUACK!", 1.8);
                quackUntil = T + 0.4;
            }
        }

        void SetIdle(double duration = 2)
        {
            act = Act.Idle;
            actT = 0;
            actDur = duration;
        }

        void Wake()
        {
            idleSince = T;
            if (act == Act.Sleep)
            {
                SetIdle(1);
                Say("!", 0.8);
            }
        }

        void Stun(double duration)
        {
            act = Act.Stunned;
            actT = 0;
            actDur = duration;
            Feathers(dx, floorY - 10 * scale, 6);
        }

        void Hop(double height, double duration)
        {
            arc = new Arc { X0 = dx, X1 = dx, Y0 = floorY, Height = height, Duration = duration };
        }

        double WalkSpeed()
        {
            double units = mood == "worried" ? 27 : mood == "stressed" ? 35 : mood == "exhausted" ? 14 : 21;
            return units * scale;
        }

        void UpdateDuck(double dt)
        {
            if (!dragging && arc == null && act != Act.Job && act != Act.Slide) dx = Clamp(dx, walkL, walkR);
            speedNow = 0;

            if (dragging)
            {
                UpdateDrag(dt);
                return;
            }
            if (arc != null)
            {
                UpdateArc(dt);
                return;
            }
            if (Math.Abs(dy - floorY) > 0.5)
            {
                ReturnToFloor(dt);
                if (act != Act.Slide) return;
            }

            actT += dt;
            switch (act)
            {
                case Act.Idle: Idle(); break;
                case Act.Walk: Walk(dt); break;
                case Act.Job:
                    if (job == null) SetIdle();
                    else
                    {
                        job.T += dt;
                        if (job.Phase == Phase.Approach) Approach(dt);
                        else if (job.Phase == Phase.Peck) Peck(dt);
                    }
                    break;
                case Act.Celebrate:
                    // Waddle off, so the duck is not left standing on the
                    // next tab's close button.
                    if (actT > actDur)
                    {
                        double away = -facing * R(45, 120) * K;
                        walkTarget = Clamp(dx + away, walkL, walkR);
                        if (Math.Abs(walkTarget - dx) < 20 * K) walkTarget = Clamp(dx - away, walkL, walkR);
                        act = Act.Walk;
                        actT = 0;
                    }
                    break;
                case Act.Stunned:
                    if (actT > actDur) SetIdle();
                    break;
                case Act.Sleep:
                    if (rng.NextDouble() < dt * 0.7)
                        FloatText("Z", HeadX() + facing * 2 * scale, floorY - 14 * scale, Art.Cream, 1.8, -12 * K);
                    break;
                case Act.Slide: Slide(dt); break;
            }

            if (mood == "stressed" || mood == "exhausted")
            {
                if (T > sweatAt && act != Act.Sleep)
                {
                    sweatAt = T + 2 + rng.NextDouble() * 2;
                    Sweat();
                }
            }
            Chatter();
        }

        void Idle()
        {
            if (T - idleSince > 150 && job == null)
            {
                act = Act.Sleep;
                actT = 0;
                return;
            }
            if (actT < actDur) return;
            double r = rng.NextDouble();
            if (r < 0.62)
            {
                walkTarget = walkL + rng.NextDouble() * (walkR - walkL);
                act = Act.Walk;
                actT = 0;
            }
            else if (r < 0.74)
            {
                Hop(4 * scale, 0.3);
                SetIdle(1 + rng.NextDouble() * 2);
            }
            else
            {
                if (rng.NextDouble() < 0.5) facing = -facing;
                SetIdle(1 + rng.NextDouble() * 3);
            }
        }

        void Walk(double dt)
        {
            double d = walkTarget - dx;
            if (Math.Abs(d) < 1)
            {
                SetIdle(1 + rng.NextDouble() * 3);
                return;
            }
            facing = Math.Sign(d);
            double move = Math.Min(Math.Abs(d), WalkSpeed() * dt);
            dx += facing * move;
            speedNow = move / Math.Max(dt, 0.001);
        }

        void UpdateArc(double dt)
        {
            arc.T += dt;
            double p = Math.Min(1, arc.T / arc.Duration);
            dx = arc.X0 + (arc.X1 - arc.X0) * p;
            dy = arc.Y0 + (floorY - arc.Y0) * p - 4 * arc.Height * p * (1 - p);
            if (arc.X1 != arc.X0) facing = Math.Sign(arc.X1 - arc.X0);
            if (p >= 1)
            {
                arc = null;
                dy = floorY;
                Dust(dx - 4 * scale, floorY);
                Dust(dx + 4 * scale, floorY);
            }
        }

        // After being dropped: fall onto the strip, or flap back up to it.
        void ReturnToFloor(double dt)
        {
            if (dy < floorY)
            {
                vy += 2000 * K * dt;
                dy += vy * dt;
                if (dy >= floorY)
                {
                    dy = floorY;
                    if (vy > 500 * K) Dust(dx, floorY);
                    vy = 0;
                }
            }
            else
            {
                dy = Math.Max(floorY, dy - 320 * K * dt);
                vy = 0;
            }
        }

        void Slide(double dt)
        {
            dx += vx * dt;
            vx *= Math.Exp(-2.4 * dt);
            if (dx < walkL)
            {
                dx = walkL;
                vx = Math.Abs(vx) * 0.6;
                Bonk();
            }
            else if (dx > walkR)
            {
                dx = walkR;
                vx = -Math.Abs(vx) * 0.6;
                Bonk();
            }
            if (Math.Abs(vx) > 1) facing = Math.Sign(vx);
            speedNow = Math.Abs(vx);
            if (speedNow > 300 * K && rng.NextDouble() < dt * 20) Dust(dx, floorY);
            if (Math.Abs(vx) < 25 * K)
            {
                vx = 0;
                if (slideMax > 1300 * K) Stun(1.2);
                else SetIdle();
            }
        }

        void Bonk()
        {
            Sfx.Play(Sfx.Peck);
            Sparks(dx + facing * 8 * scale, floorY - 9 * scale, 4);
        }

        void Chatter()
        {
            if (T < nextChatter || bubble != null || job != null) return;
            if (act != Act.Idle && act != Act.Walk) return;

            bool happy = mood == "happy";
            nextChatter = T + (happy ? 70 + rng.NextDouble() * 60 : 28 + rng.NextDouble() * 20);

            string line;
            if (!hinted)
            {
                hinted = true;
                line = "DRAG ME ONTO A TAB!";
            }
            else if (mood == "worried") line = rng.Next(2) == 0 ? tabCount + " TABS?" : "CLOSE SOME?";
            else if (mood == "stressed") line = rng.Next(2) == 0 ? tabCount + " TABS!" : "TOO MANY!";
            else if (mood == "exhausted") line = rng.Next(2) == 0 ? "BREAK TIME!" : "HELP ME!";
            else line = "QUACK";

            Say(line, 2.6);
            if (line == "QUACK") quackUntil = T + 0.3;
        }

        void Say(string text, double seconds)
        {
            bubble = text;
            bubbleUntil = T + seconds;
        }

        // --------------------------------------------------------------
        // Mouse: click to quack (or spare a tab), drag to aim, fling to slide
        // --------------------------------------------------------------

        Rectangle DuckBounds()
        {
            double anchor = facing > 0 ? Art.AnchorX : Art.W - Art.AnchorX;
            int left = (int)Math.Round(dx - anchor * scale);
            int top = (int)Math.Round(dy - Art.H * scale);
            return new Rectangle(left, top, Art.W * scale, Art.H * scale);
        }

        double HeadX()
        {
            return dx + facing * (13 - Art.AnchorX) * scale;
        }

        void OnPressed(int x, int y)
        {
            if (!DuckBounds().Contains(x, y)) return;
            pressed = true;
            dragging = false;
            pressX = x;
            pressY = y;
            mouseX = x;
            mouseY = y;
            grabDX = dx - x;
            grabDY = dy - y;
            lastMoveX = x;
            lastMoveT = clock.Elapsed.TotalSeconds;
            dragVX = 0;
        }

        void OnMoved(int x, int y)
        {
            if (!pressed) return;
            if (!dragging && (Math.Abs(x - pressX) > 5 || Math.Abs(y - pressY) > 5)) StartDrag();
            if (!dragging) return;

            double now = clock.Elapsed.TotalSeconds;
            double dtm = now - lastMoveT;
            if (dtm > 0.004)
            {
                dragVX = dragVX * 0.5 + (x - lastMoveX) / dtm * 0.5;
                lastMoveX = x;
                lastMoveT = now;
            }
            mouseX = x;
            mouseY = y;
        }

        void OnReleased(int x, int y)
        {
            if (!pressed) return;
            pressed = false;
            if (!dragging)
            {
                Clicked();
                return;
            }
            dragging = false;
            if (clock.Elapsed.TotalSeconds - lastMoveT > 0.08) dragVX = 0;   // held still before letting go

            int tab = hoverTab;
            hoverTab = -1;
            if (tab >= 0 && Math.Abs(dragVX) < 900 * K)
            {
                DropOn(tab);
                SetIdle(3);
                return;
            }
            if (Math.Abs(dragVX) > 250 * K)
            {
                vx = Clamp(dragVX, -2400 * K, 2400 * K);
                slideMax = Math.Abs(vx);
                act = Act.Slide;
                actT = 0;
                Say("WHEE!", 1.0);
                quackUntil = T + 0.4;
            }
            else SetIdle();
        }

        void StartDrag()
        {
            dragging = true;
            arc = null;
            if (job != null && job.Phase != Phase.Locate && !job.Pecked) Abort("spared");
            act = Act.Idle;
            idleSince = T;
            quackUntil = T + 0.3;
            Sfx.Play(Sfx.Quack);
        }

        void UpdateDrag(double dt)
        {
            double tx = mouseX + grabDX, ty = mouseY + grabDY;
            double follow = Math.Min(1, dt * 25);
            dx += (tx - dx) * follow;
            dy += (ty - dy) * follow;
            dx = Clamp(dx, 8 * scale, ow - 8 * scale);
            dy = Clamp(dy, Art.H * scale * 0.7, floorY + 160 * K);
            facing = dragVX > 40 ? 1 : dragVX < -40 ? -1 : facing;

            hoverTab = -1;
            if (strip != null && mouseY < stripH + 40 * K)
            {
                for (int i = 0; i < strip.Tabs.Count; i++)
                {
                    Rectangle r = ToOverlay(strip.Tabs[i].Rect);
                    if (r.Width > 8 && mouseX >= r.Left + 6 && mouseX <= r.Right - 6)
                    {
                        hoverTab = i;
                        break;
                    }
                }
            }
        }

        void DropOn(int index)
        {
            TabInfo tab = strip.Tabs[index];
            Say("ON IT!", 1.2);
            Sfx.Play(Sfx.Quack);
            quackUntil = T + 0.3;
            host.Send(Json.Msg("drop", "index", index, "name", tab.Name, "names", strip.Names()));
        }

        void Clicked()
        {
            idleSince = T;
            if (job != null && job.Phase != Phase.Locate && !job.Pecked)
            {
                Abort("spared");
                return;
            }
            if (act == Act.Sleep)
            {
                Wake();
                Hop(5 * scale, 0.3);
                return;
            }

            clicks.Add(T);
            clicks.RemoveAll(c => T - c > 2);
            if (clicks.Count >= 5)
            {
                clicks.Clear();
                Say("STOP IT!", 1.5);
                Stun(1.0);
                Sfx.Play(Sfx.Quack);
                return;
            }

            Sfx.Play(Sfx.Quack);
            quackUntil = T + 0.35;
            Say("QUACK!", 1.0);
            if (arc == null) Hop(4 * scale, 0.28);
        }

        void BuildMenu()
        {
            menu.ShowImageMargin = false;
            var quack = new ToolStripMenuItem("Quack!");
            quack.Click += delegate { Clicked(); };
            var sound = new ToolStripMenuItem("Sound");
            sound.Click += delegate
            {
                Sfx.On = !Sfx.On;
                host.Send(Json.Msg("setting", "sound", Sfx.On));
            };
            var nap = new ToolStripMenuItem("Nap for 30 minutes");
            nap.Click += delegate
            {
                napUntil = T + 30 * 60;
                if (job != null && !job.Pecked) Abort("napping");
                host.Send(Json.Msg("nap", "minutes", 30));
            };
            var quit = new ToolStripMenuItem("Send the duck away until Chrome restarts");
            quit.Click += delegate { Quit("user"); };
            menu.Items.AddRange(new ToolStripItem[] { quack, sound, nap, new ToolStripSeparator(), quit });
            menu.Opening += delegate { sound.Checked = Sfx.On; };
        }

        void OnRightClicked(int x, int y)
        {
            if (DuckBounds().Contains(x, y)) menu.Show(Cursor.Position);
        }

        void Quit(string reason)
        {
            if (reason != null) host.Send(Json.Msg("bye", "reason", reason));
            timer.Stop();
            overlay.Hide();
            ExitThread();
        }

        // --------------------------------------------------------------
        // Particles
        // --------------------------------------------------------------

        void UpdateParticles(double dt)
        {
            for (int i = parts.Count - 1; i >= 0; i--)
            {
                Particle p = parts[i];
                p.Life += dt;
                if (p.Life >= p.Max)
                {
                    if (p.Image != null) p.Image.Dispose();
                    parts.RemoveAt(i);
                    continue;
                }
                p.VY += p.Gravity * dt;
                if (p.Drag > 0)
                {
                    double f = Math.Exp(-p.Drag * dt);
                    p.VX *= f;
                    p.VY *= f;
                }
                p.X += p.VX * dt;
                p.Y += p.VY * dt;
                p.Rot += p.VR * dt;
            }
            if (parts.Count > 400) parts.RemoveRange(0, parts.Count - 400);
        }

        void AddPixel(double x, double y, double vx, double vy, double gravity, double life, double size, Color c, double drag = 0)
        {
            parts.Add(new Particle { Kind = 0, X = x, Y = y, VX = vx, VY = vy, Gravity = gravity, Max = life, Size = size, Color = c, Drag = drag });
        }

        double R(double a, double b) { return a + rng.NextDouble() * (b - a); }

        void Sparks(double x, double y, int n)
        {
            for (int i = 0; i < n; i++)
            {
                double a = R(0, Math.PI * 2), v = R(60, 220) * K;
                AddPixel(x, y, Math.Cos(a) * v, Math.Sin(a) * v, 300 * K, R(0.18, 0.35), scale,
                    i % 2 == 0 ? Color.White : Art.Yellow, 4);
            }
        }

        void Dust(double x, double y)
        {
            AddPixel(x + R(-2, 2) * scale, y - scale, R(-30, 30) * K, R(-40, -10) * K, 0, R(0.25, 0.45), scale,
                Color.FromArgb(170, 220, 210, 190), 3);
        }

        void Puff(double x, double y, int n)
        {
            for (int i = 0; i < n; i++)
            {
                double a = R(0, Math.PI * 2), v = R(40, 120) * K;
                AddPixel(x, y, Math.Cos(a) * v, Math.Sin(a) * v * 0.5, 0, R(0.35, 0.6), scale * 2,
                    Color.FromArgb(200, 255, 255, 255), 5);
            }
        }

        void Feathers(double x, double y, int n)
        {
            for (int i = 0; i < n; i++)
                AddPixel(x, y, R(-90, 90) * K, R(-140, -40) * K, 160 * K, R(0.7, 1.1), scale, Art.Yellow, 2);
        }

        void Confetti(double x, double y, int n)
        {
            Color[] colors = { Art.Red, Art.Yellow, Color.White, Art.Hex("6EC6FF"), Art.Hex("7BD389") };
            for (int i = 0; i < n; i++)
                AddPixel(x, y, R(-200, 200) * K, R(-260, -60) * K, 500 * K, R(0.8, 1.3), scale, colors[i % colors.Length], 1.5);
        }

        void Sweat()
        {
            double hx = dx - facing * 1 * scale;
            AddPixel(hx, floorY - 16 * scale, -facing * 30 * K, -40 * K, 400 * K, 0.6, scale, Art.Hex("6EC6FF"));
        }

        void FloatText(string text, double x, double y, Color color, double life = 1.0, double vy = 0)
        {
            parts.Add(new Particle
            {
                Kind = 2,
                Text = text,
                X = x,
                Y = y,
                VY = vy == 0 ? -34 * K : vy,
                VX = 0,
                Max = life,
                Color = color
            });
        }

        // The tab breaks into pieces that fly off and fall down the page.
        void Shatter()
        {
            Rectangle r = job.TabRect;
            Bitmap img = job.Picture;
            if (img == null)
            {
                Confetti(job.Target.X, job.Target.Y, 20);
                return;
            }
            int cols = (int)Clamp(r.Width / (10 * scale), 4, 12), rows = 2;
            int cw = (int)Math.Ceiling(img.Width / (double)cols), ch = (int)Math.Ceiling(img.Height / (double)rows);
            for (int cy = 0; cy < rows; cy++)
                for (int cx = 0; cx < cols; cx++)
                {
                    var rect = new Rectangle(cx * cw, cy * ch, Math.Min(cw, img.Width - cx * cw), Math.Min(ch, img.Height - cy * ch));
                    if (rect.Width <= 0 || rect.Height <= 0) continue;
                    double px = r.X + rect.X + rect.Width / 2.0, py = r.Y + rect.Y + rect.Height / 2.0;
                    double away = (px - job.Target.X) / Math.Max(1, r.Width);
                    parts.Add(new Particle
                    {
                        Kind = 1,
                        Image = img.Clone(rect, PixelFormat.Format32bppPArgb),
                        X = px,
                        Y = py,
                        VX = away * 420 * K + R(-70, 70) * K - facing * 40 * K,
                        VY = R(-280, -90) * K,
                        Gravity = 1300 * K,
                        VR = R(-540, 540),
                        Max = R(1.0, 1.4)
                    });
                }
            Confetti(job.Target.X, job.Target.Y, 10);
        }

        // --------------------------------------------------------------
        // Drawing
        // --------------------------------------------------------------

        Pose CurrentPose()
        {
            var p = new Pose();
            p.Gear = level >= 3 ? 2 : level >= 2 ? 1 : 0;
            bool blink = (T % 3.7) < 0.12;
            p.Eyes = blink ? Eyes.Closed : mood == "exhausted" ? Eyes.Tired : Eyes.Open;
            p.Quack = T < quackUntil;

            bool airborne = dragging || arc != null || Math.Abs(dy - floorY) > 0.5;
            if (airborne)
            {
                p.Legs = Legs.Dangle;
                p.Wing = ((int)(T * 12) % 2 == 0) ? Wing.Up : Wing.Down;
                if (dragging) p.Quack = p.Quack || ((int)(T * 3) % 3 == 0);
                return p;
            }

            switch (act)
            {
                case Act.Sleep:
                    p.Sit = true;
                    p.Eyes = Eyes.Closed;
                    return p;
                case Act.Stunned:
                    p.Eyes = Eyes.Closed;
                    return p;
                case Act.Celebrate:
                    p.Quack = true;
                    p.Wing = ((int)(T * 10) % 2 == 0) ? Wing.Up : Wing.Folded;
                    return p;
                case Act.Slide:
                    p.Wing = Wing.Up;
                    p.Legs = Legs.Dangle;
                    p.Quack = true;
                    return p;
                case Act.Job:
                    if (job != null && job.Phase == Phase.Peck && job.Step >= 0 && job.Step < job.Steps.Length)
                    {
                        p.Head = job.Steps[job.Step].Head;
                        p.Eyes = Eyes.Open;
                        return p;
                    }
                    break;
            }

            if (speedNow > 2)
            {
                double rate = Clamp(speedNow / (3.0 * scale), 6, 22);
                int f = (int)(T * rate) % 4;
                p.Legs = f == 0 ? Legs.WalkA : f == 2 ? Legs.WalkB : Legs.Stand;
                p.Bob = f == 1 || f == 3;
                if (speedNow > 380 * K) p.Wing = Wing.Up;
            }
            return p;
        }

        void Render()
        {
            Pose pose = CurrentPose();
            lean = 0;
            if (pose.Head == HeadPose.Strike && job != null)
            {
                double beakY = floorY - Art.H * scale + Art.StrikeBeakRow * scale;
                lean = Clamp(job.Target.Y - beakY, -3 * scale, 3 * scale);
            }

            long sig = pose.Key;
            sig = sig * 31 + (long)Math.Round(dx);
            sig = sig * 31 + (long)Math.Round(dy + lean);
            sig = sig * 31 + facing;
            sig = sig * 31 + ox;
            sig = sig * 31 + oy;
            sig = sig * 31 + ow;
            sig = sig * 31 + oh;
            sig = sig * 31 + (bubble != null ? bubble.GetHashCode() : 0);
            sig = sig * 31 + hoverTab;
            if (bubble != null && T > bubbleUntil) bubble = null;
            bool animating = parts.Count > 0 || act == Act.Stunned;
            if (!animating && sig == lastSignature) return;
            lastSignature = sig;

            overlay.EnsureSurface(ow, oh);
            using (var g = Graphics.FromImage(overlay.Surface))
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.SmoothingMode = SmoothingMode.None;
                g.SetClip(new Rectangle(0, 0, ow, oh));

                if (dragging && hoverTab >= 0 && strip != null && hoverTab < strip.Tabs.Count) DrawTarget(g, strip.Tabs[hoverTab]);
                DrawShadow(g);
                DrawDuck(g, pose);
                if (act == Act.Stunned) DrawStars(g);
                DrawParticles(g);
                DrawBubble(g);
            }
            overlay.Present(ox, oy, ow, oh);
        }

        void DrawShadow(Graphics g)
        {
            double lift = Math.Max(0, floorY - dy);
            double f = Clamp(1 - lift / (30.0 * scale), 0.3, 1);
            int w = (int)(13 * scale * f), h = Math.Max(2, (int)(3 * scale * f));
            using (var b = new SolidBrush(Color.FromArgb((int)(70 * f), 20, 12, 8)))
                g.FillEllipse(b, (int)(dx - w / 2.0), floorY - h + scale / 2, w, h);
        }

        void DrawDuck(Graphics g, Pose pose)
        {
            Bitmap f = Art.Frame(pose, scale, facing < 0);
            Rectangle b = DuckBounds();
            int y = b.Y + (int)Math.Round(lean);
            if (act == Act.Slide && arc == null && Math.Abs(dy - floorY) < 0.5)
                y -= (int)(Math.Abs(Math.Sin(T * 22)) * 2 * scale * Math.Min(1, Math.Abs(vx) / (800 * K)));
            g.DrawImage(f, new Rectangle(b.X, y, f.Width, f.Height));
        }

        void DrawStars(Graphics g)
        {
            double cx = HeadX(), cy = floorY - 16 * scale;
            using (var fill = new SolidBrush(Art.Yellow))
            using (var ink = new SolidBrush(Art.Ink))
            {
                for (int i = 0; i < 3; i++)
                {
                    double a = T * 6 + i * Math.PI * 2 / 3;
                    int x = (int)(cx + Math.Cos(a) * 7 * scale), y = (int)(cy + Math.Sin(a) * 2 * scale);
                    g.FillRectangle(ink, x - scale * 2, y - scale, scale * 5, scale * 3);
                    g.FillRectangle(ink, x - scale, y - scale * 2, scale * 3, scale * 5);
                    g.FillRectangle(fill, x - scale, y, scale * 3, scale);
                    g.FillRectangle(fill, x, y - scale, scale, scale * 3);
                }
            }
        }

        void DrawTarget(Graphics g, TabInfo tab)
        {
            Rectangle r = ToOverlay(tab.Rect);
            r.Inflate(-2, -2);
            int t = scale, len = 6 * scale;
            using (var b = new SolidBrush(Art.Red))
            using (var tint = new SolidBrush(Color.FromArgb(50, 220, 38, 38)))
            {
                g.FillRectangle(tint, r);
                g.FillRectangle(b, r.Left, r.Top, len, t);
                g.FillRectangle(b, r.Left, r.Top, t, len);
                g.FillRectangle(b, r.Right - len, r.Top, len, t);
                g.FillRectangle(b, r.Right - t, r.Top, t, len);
                g.FillRectangle(b, r.Left, r.Bottom - t, len, t);
                g.FillRectangle(b, r.Left, r.Bottom - len, t, len);
                g.FillRectangle(b, r.Right - len, r.Bottom - t, len, t);
                g.FillRectangle(b, r.Right - t, r.Bottom - len, t, len);
            }
        }

        void DrawParticles(Graphics g)
        {
            foreach (Particle p in parts)
            {
                double fade = Clamp((p.Max - p.Life) / (p.Max * 0.4), 0, 1);
                if (p.Kind == 0)
                {
                    int a = (int)(p.Color.A * fade);
                    using (var b = new SolidBrush(Color.FromArgb(a, p.Color)))
                        g.FillRectangle(b, (int)(p.X - p.Size / 2), (int)(p.Y - p.Size / 2), (int)p.Size, (int)p.Size);
                }
                else if (p.Kind == 1 && p.Image != null)
                {
                    GraphicsState state = g.Save();
                    g.TranslateTransform((float)p.X, (float)p.Y);
                    g.RotateTransform((float)p.Rot);
                    int w = p.Image.Width, h = p.Image.Height;
                    if (fade < 1)
                    {
                        using (var ia = new ImageAttributes())
                        {
                            var cm = new ColorMatrix { Matrix33 = (float)fade };
                            ia.SetColorMatrix(cm);
                            g.DrawImage(p.Image, new Rectangle(-w / 2, -h / 2, w, h), 0, 0, w, h, GraphicsUnit.Pixel, ia);
                        }
                    }
                    else
                    {
                        g.DrawImage(p.Image, new Rectangle(-w / 2, -h / 2, w, h));
                    }
                    g.Restore(state);
                }
                else if (p.Kind == 2)
                {
                    int s = scale;
                    int w = PixelFont.Measure(p.Text) * s;
                    using (var fill = new SolidBrush(Color.FromArgb((int)(255 * fade), p.Color)))
                    using (var ink = new SolidBrush(Color.FromArgb((int)(255 * fade), Art.Ink)))
                        PixelFont.DrawOutlined(g, p.Text, (int)(p.X - w / 2.0), (int)p.Y, s, fill, ink);
                }
            }
        }

        void DrawBubble(Graphics g)
        {
            if (bubble == null) return;
            int s = scale;
            int tw = PixelFont.Measure(bubble) * s, th = 5 * s, pad = 2 * s;
            int bw = tw + pad * 2 + 2 * s, bh = th + pad * 2 + s;

            Rectangle duck = DuckBounds();
            bool right = facing < 0;   // put the bubble behind the duck's head
            int bx = right ? duck.Right - 3 * s : duck.Left + 3 * s - bw;
            if (bx < 0 || bx + bw > ow)
            {
                right = !right;
                bx = right ? duck.Right - 3 * s : duck.Left + 3 * s - bw;
            }
            bx = (int)Clamp(bx, 0, Math.Max(0, ow - bw));
            int by = (int)Clamp(duck.Top + s, 0, Math.Max(0, stripH - bh));

            using (var ink = new SolidBrush(Art.Ink))
            using (var paper = new SolidBrush(Art.Cream))
            {
                // Pixel-rounded box: an ink frame with the corners knocked off.
                g.FillRectangle(ink, bx + s, by, bw - 2 * s, bh);
                g.FillRectangle(ink, bx, by + s, bw, bh - 2 * s);
                g.FillRectangle(paper, bx + s, by + s, bw - 2 * s, bh - 2 * s);
                PixelFont.Draw(g, bubble, bx + s + pad, by + s + pad - s / 2, s, ink);
            }
        }

        static double Clamp(double v, double lo, double hi)
        {
            return v < lo ? lo : v > hi ? hi : v;
        }
    }
}
