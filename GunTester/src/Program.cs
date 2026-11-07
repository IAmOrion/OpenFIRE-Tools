// GunTester - standalone light gun test screen for OpenFIRE (and other absolute-mouse) guns.
// Reads each gun separately via Windows Raw Input, the same way MAME does.
// Recoil / rumble are produced by the gun's own firmware in normal mode, exactly as in games.
//
// Keys:  Esc = quit   C = clear counters and log   G = toggle grid   H = toggle help line

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

namespace GunTester
{
    internal static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            // Real pixels on high-DPI / 4K screens, so the crosshair lines up with the gun.
            try { if (!SetProcessDpiAwarenessContext(new IntPtr(-4))) SetProcessDPIAware(); }
            catch { try { SetProcessDPIAware(); } catch { } }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TestForm());
        }
    }

    // ------------------------------------------------------------------ Native

    internal static class Native
    {
        public const int WM_INPUT = 0x00FF;
        public const int WM_INPUT_DEVICE_CHANGE = 0x00FE;
        public const uint RID_INPUT = 0x10000003;
        public const uint RIDI_DEVICENAME = 0x20000007;
        public const uint RIM_TYPEMOUSE = 0;
        public const uint RIM_TYPEKEYBOARD = 1;
        public const uint RIDEV_INPUTSINK = 0x00000100;
        public const uint RIDEV_DEVNOTIFY = 0x00002000;
        public const int GIDC_ARRIVAL = 1;
        public const int GIDC_REMOVAL = 2;

        public const ushort MOUSE_MOVE_ABSOLUTE = 0x01;
        public const ushort MOUSE_VIRTUAL_DESKTOP = 0x02;

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTDEVICELIST
        {
            public IntPtr hDevice;
            public uint dwType;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[] pRawInputDeviceList, ref uint puiNumDevices, uint cbSize);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetProductString(SafeFileHandle hidDeviceObject, byte[] buffer, uint bufferLength);

        public static string GetDeviceName(IntPtr hDevice)
        {
            uint size = 0;
            GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref size);
            if (size == 0) return null;
            IntPtr buf = Marshal.AllocHGlobal((int)size * 2);
            try
            {
                if (GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, buf, ref size) == unchecked((uint)-1)) return null;
                return Marshal.PtrToStringUni(buf);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        public static string GetProductString(string devicePath)
        {
            if (string.IsNullOrEmpty(devicePath)) return null;
            try
            {
                // Access 0 = query only; works on mouse/keyboard collections that Windows holds open.
                using (var h = CreateFile(devicePath, 0, 3 /*FILE_SHARE_READ|WRITE*/, IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0, IntPtr.Zero))
                {
                    if (h.IsInvalid) return null;
                    var buf = new byte[512];
                    if (!HidD_GetProductString(h, buf, (uint)buf.Length)) return null;
                    string s = Encoding.Unicode.GetString(buf);
                    int z = s.IndexOf('\0');
                    if (z >= 0) s = s.Substring(0, z);
                    s = s.Trim();
                    return s.Length > 0 ? s : null;
                }
            }
            catch { return null; }
        }
    }

    internal static class Util
    {
        public static double Clamp(double v, double lo, double hi) { return v < lo ? lo : (v > hi ? hi : v); }
        public static float Clamp(float v, float lo, float hi) { return v < lo ? lo : (v > hi ? hi : v); }
    }

    // ------------------------------------------------------------------ Gun state

    internal sealed class Gun
    {
        public string Key;              // VID&PID (shared by all interfaces of one gun)
        public string Label;            // product string, or VID/PID
        public Color Colour;
        public int Order;               // display slot
        public bool Connected = true;
        public bool SeenAbsolute;
        public bool IsOpenFire;

        public double X = 0.5, Y = 0.5;  // 0..1 across the screen
        public bool HasPosition;
        public bool Left, Right, Middle, X1, X2;
        public readonly HashSet<Keys> KeysDown = new HashSet<Keys>();

        public int Shots;               // trigger (left button) presses
        public int OffscreenShots;      // trigger presses while at an edge
        public int RightClicks;
        public DateTime LastInput = DateTime.MinValue;
        public DateTime LastFlash = DateTime.MinValue;   // trigger flash
        public readonly HashSet<IntPtr> Handles = new HashSet<IntPtr>();

        public bool AtEdge
        {
            get { return HasPosition && (X <= 0.002 || X >= 0.998 || Y <= 0.002 || Y >= 0.998); }
        }
    }

    // ------------------------------------------------------------------ Form

    internal sealed class TestForm : Form
    {
        static readonly Regex VidPidRx = new Regex(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Color[] SpareColours = { Color.LimeGreen, Color.Gold, Color.Violet, Color.Orange };

        readonly Dictionary<IntPtr, string> handleToKey = new Dictionary<IntPtr, string>();
        readonly Dictionary<string, Gun> guns = new Dictionary<string, Gun>();
        readonly HashSet<IntPtr> ignoredHandles = new HashSet<IntPtr>();   // non-gun devices
        readonly List<string> log = new List<string>();
        readonly Timer frameTimer = new Timer();
        bool showGrid = true, showHelp = true;
        int nextOrder;

        public TestForm()
        {
            Text = "Gun Tester";
            // Use the icon embedded in the .exe (set with /win32icon when compiling) for the window / taskbar.
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = Screen.PrimaryScreen.Bounds;
            BackColor = Color.FromArgb(14, 14, 18);
            DoubleBuffered = true;
            KeyPreview = true;
            TopMost = true;
            ShowInTaskbar = true;

            frameTimer.Interval = 15;
            frameTimer.Tick += (s, e) => Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            var devs = new[]
            {
                new Native.RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = Native.RIDEV_INPUTSINK | Native.RIDEV_DEVNOTIFY, hwndTarget = Handle }, // mouse
                new Native.RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x06, dwFlags = Native.RIDEV_INPUTSINK | Native.RIDEV_DEVNOTIFY, hwndTarget = Handle }, // keyboard
            };
            if (!Native.RegisterRawInputDevices(devs, (uint)devs.Length, (uint)Marshal.SizeOf<Native.RAWINPUTDEVICE>()))
                AddLog("ERROR: could not register for raw input (" + Marshal.GetLastWin32Error() + ")");
            ScanExisting();
            frameTimer.Start();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Cursor.Hide();
            Activate();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Cursor.Show();
            base.OnFormClosed(e);
        }

        // Set from raw input just before the matching WM_KEYDOWN arrives, so keys sent
        // by a gun (e.g. Start / Coin mapped to keys) never trigger the tester's shortcuts.
        bool lastKeyFromGun;

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                if (!lastKeyFromGun) Close();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (lastKeyFromGun) { e.Handled = true; return; }
            if (e.KeyCode == Keys.C)
            {
                foreach (var g in guns.Values) { g.Shots = 0; g.OffscreenShots = 0; g.RightClicks = 0; }
                log.Clear();
                AddLog("Counters and log cleared");
            }
            else if (e.KeyCode == Keys.G) showGrid = !showGrid;
            else if (e.KeyCode == Keys.H) showHelp = !showHelp;
            base.OnKeyDown(e);
        }

        void AddLog(string msg)
        {
            log.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + msg);
            while (log.Count > 10) log.RemoveAt(0);
        }

        // ---------------------------------------------------- device identification

        void ScanExisting()
        {
            uint count = 0;
            uint sz = (uint)Marshal.SizeOf<Native.RAWINPUTDEVICELIST>();
            Native.GetRawInputDeviceList(null, ref count, sz);
            if (count == 0) return;
            var list = new Native.RAWINPUTDEVICELIST[count];
            if (Native.GetRawInputDeviceList(list, ref count, sz) == unchecked((uint)-1)) return;
            foreach (var d in list)
            {
                if (d.dwType != Native.RIM_TYPEMOUSE && d.dwType != Native.RIM_TYPEKEYBOARD) continue;
                Identify(d.hDevice, false, true);
            }
            int n = guns.Values.Count(g => g.IsOpenFire);
            AddLog(n == 0 ? "No OpenFIRE guns found yet - move or fire a gun"
                          : n + " OpenFIRE gun(s) found");
        }

        // Returns the gun for this handle, or null if the device is not (yet) a gun.
        Gun Identify(IntPtr h, bool absoluteSeen, bool quiet)
        {
            // Handle 0 = input injected by software (remote control tools, macros), not a real device.
            if (h == IntPtr.Zero) return null;
            string key;
            Gun known;
            if (handleToKey.TryGetValue(h, out key) && guns.TryGetValue(key, out known))
            {
                if (absoluteSeen) known.SeenAbsolute = true;
                return known;
            }
            if (!absoluteSeen && ignoredHandles.Contains(h)) return null;

            string name = Native.GetDeviceName(h) ?? "";
            var m = VidPidRx.Match(name);
            string vid = m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
            string pid = m.Success ? m.Groups[2].Value.ToUpperInvariant() : null;
            bool isOpenFire = vid == "F143";

            // A device is treated as a gun if it is OpenFIRE (VID F143) or sends absolute mouse positions.
            if (!isOpenFire && !absoluteSeen)
            {
                ignoredHandles.Add(h);
                return null;
            }
            ignoredHandles.Remove(h);

            key = m.Success ? vid + "&" + pid : "H" + h.ToInt64().ToString("X");
            handleToKey[h] = key;

            Gun gun;
            if (!guns.TryGetValue(key, out gun))
            {
                string product = Native.GetProductString(name);
                gun = new Gun
                {
                    Key = key,
                    IsOpenFire = isOpenFire,
                    Label = product ?? (m.Success ? "VID " + vid + " PID " + pid : "Absolute mouse"),
                    Order = nextOrder++,
                };
                gun.Colour = PickColour(gun, pid);
                guns[key] = gun;
                if (!quiet) AddLog("Connected: " + gun.Label);
                else AddLog("Found: " + gun.Label);
            }
            else if (!gun.Connected)
            {
                gun.Connected = true;
                AddLog("Reconnected: " + gun.Label);
            }
            else if (gun.Label.StartsWith("VID ") || gun.Label == "Absolute mouse")
            {
                // Earlier interface had no product string; try this one.
                string product = Native.GetProductString(name);
                if (product != null) gun.Label = product;
            }
            gun.Handles.Add(h);
            if (absoluteSeen) gun.SeenAbsolute = true;
            return gun;
        }

        Color PickColour(Gun g, string pid)
        {
            string l = g.Label.ToUpperInvariant();
            bool p1 = Regex.IsMatch(l, @"\bP\s?1\b") || pid == "0001";
            bool p2 = Regex.IsMatch(l, @"\bP\s?2\b") || pid == "0002";
            if (p1 && !p2) return Color.FromArgb(255, 70, 70);
            if (p2 && !p1) return Color.FromArgb(80, 150, 255);
            int spare = guns.Count % SpareColours.Length;
            return SpareColours[spare];
        }

        // ---------------------------------------------------- raw input

        protected override void WndProc(ref Message msg)
        {
            if (msg.Msg == Native.WM_INPUT) HandleInput(msg.LParam);
            else if (msg.Msg == Native.WM_INPUT_DEVICE_CHANGE) HandleDeviceChange((int)msg.WParam, msg.LParam);
            base.WndProc(ref msg);
        }

        void HandleDeviceChange(int change, IntPtr h)
        {
            if (change == Native.GIDC_ARRIVAL)
            {
                Identify(h, false, false);
            }
            else if (change == Native.GIDC_REMOVAL)
            {
                ignoredHandles.Remove(h);
                string key;
                Gun gun;
                if (!handleToKey.TryGetValue(h, out key)) return;
                handleToKey.Remove(h);
                if (!guns.TryGetValue(key, out gun)) return;
                gun.Handles.Remove(h);
                if (gun.Handles.Count == 0 && gun.Connected)
                {
                    gun.Connected = false;
                    gun.Left = gun.Right = gun.Middle = gun.X1 = gun.X2 = false;
                    gun.KeysDown.Clear();
                    AddLog("DISCONNECTED: " + gun.Label + "  (after " + gun.Shots + " shots)");
                }
            }
        }

        void HandleInput(IntPtr hRaw)
        {
            int headerSize = 8 + 2 * IntPtr.Size;
            uint size = 0;
            Native.GetRawInputData(hRaw, Native.RID_INPUT, IntPtr.Zero, ref size, (uint)headerSize);
            if (size == 0) return;
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (Native.GetRawInputData(hRaw, Native.RID_INPUT, buf, ref size, (uint)headerSize) != size) return;
                uint type = (uint)Marshal.ReadInt32(buf, 0);
                IntPtr hDev = Marshal.ReadIntPtr(buf, 8);
                int o = headerSize;

                if (type == Native.RIM_TYPEMOUSE)
                {
                    ushort flags = (ushort)Marshal.ReadInt16(buf, o + 0);
                    ushort btn = (ushort)Marshal.ReadInt16(buf, o + 4);
                    int lx = Marshal.ReadInt32(buf, o + 12);
                    int ly = Marshal.ReadInt32(buf, o + 16);
                    bool absolute = (flags & Native.MOUSE_MOVE_ABSOLUTE) != 0;

                    var gun = Identify(hDev, absolute, false);
                    if (gun == null) return;
                    gun.LastInput = DateTime.Now;
                    if (!gun.Connected) { gun.Connected = true; AddLog("Input resumed: " + gun.Label); }

                    if (absolute)
                    {
                        Rectangle area = (flags & Native.MOUSE_VIRTUAL_DESKTOP) != 0
                            ? SystemInformation.VirtualScreen
                            : Screen.PrimaryScreen.Bounds;
                        double sx = area.Left + lx / 65535.0 * area.Width;
                        double sy = area.Top + ly / 65535.0 * area.Height;
                        // Convert to this window's 0..1 space.
                        gun.X = Util.Clamp((sx - Bounds.Left) / Math.Max(1, Bounds.Width - 1), 0, 1);
                        gun.Y = Util.Clamp((sy - Bounds.Top) / Math.Max(1, Bounds.Height - 1), 0, 1);
                        gun.HasPosition = true;
                    }

                    if ((btn & 0x0001) != 0)
                    {
                        gun.Left = true; gun.Shots++; gun.LastFlash = DateTime.Now;
                        if (gun.AtEdge) gun.OffscreenShots++;
                    }
                    if ((btn & 0x0002) != 0) gun.Left = false;
                    if ((btn & 0x0004) != 0) { gun.Right = true; gun.RightClicks++; }
                    if ((btn & 0x0008) != 0) gun.Right = false;
                    if ((btn & 0x0010) != 0) gun.Middle = true;
                    if ((btn & 0x0020) != 0) gun.Middle = false;
                    if ((btn & 0x0040) != 0) gun.X1 = true;
                    if ((btn & 0x0080) != 0) gun.X1 = false;
                    if ((btn & 0x0100) != 0) gun.X2 = true;
                    if ((btn & 0x0200) != 0) gun.X2 = false;
                }
                else if (type == Native.RIM_TYPEKEYBOARD)
                {
                    ushort kflags = (ushort)Marshal.ReadInt16(buf, o + 2);
                    ushort vkey = (ushort)Marshal.ReadInt16(buf, o + 6);
                    var gun = Identify(hDev, false, false);
                    lastKeyFromGun = gun != null;
                    if (gun == null || vkey == 0 || vkey == 0xFF) return;
                    gun.LastInput = DateTime.Now;
                    var k = (Keys)vkey;
                    if ((kflags & 0x01) != 0) gun.KeysDown.Remove(k); else gun.KeysDown.Add(k);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        // ---------------------------------------------------- drawing

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            int W = ClientSize.Width, H = ClientSize.Height;
            float unit = Math.Max(8f, H / 90f);   // scales text with screen size

            if (showGrid) DrawGrid(g, W, H, unit);

            var shown = guns.Values.Where(x => x.SeenAbsolute || x.IsOpenFire).OrderBy(x => x.Order).ToList();

            // Panels: up to two on top row (left / right), extras stacked below.
            using (var f = new Font("Segoe UI", unit * 1.15f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var fb = new Font("Segoe UI", unit * 1.5f, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                for (int i = 0; i < shown.Count; i++)
                {
                    bool right = i % 2 == 1;
                    int row = i / 2;
                    DrawPanel(g, shown[i], right, row, W, unit, f, fb);
                }
                DrawLog(g, W, H, unit, f);
                if (showHelp)
                {
                    string help = "Esc quit    C clear counters    G grid on/off    H hide this line";
                    var sz = g.MeasureString(help, f);
                    using (var b = new SolidBrush(Color.FromArgb(120, 200, 200, 200)))
                        g.DrawString(help, f, b, (W - sz.Width) / 2, unit * 0.6f);
                }
                if (shown.Count == 0)
                {
                    string t = "Waiting for a gun - move or fire one";
                    var sz = g.MeasureString(t, fb);
                    g.DrawString(t, fb, Brushes.Gainsboro, (W - sz.Width) / 2, H * 0.40f);
                }
            }

            foreach (var gun in shown) DrawCrosshair(g, gun, W, H, unit);
        }

        void DrawGrid(Graphics g, int W, int H, float unit)
        {
            using (var minor = new Pen(Color.FromArgb(38, 38, 48), 1f))
            using (var major = new Pen(Color.FromArgb(70, 70, 86), 1.5f))
            using (var border = new Pen(Color.FromArgb(110, 110, 130), 2f))
            using (var target = new Pen(Color.FromArgb(150, 150, 170), 2f))
            {
                for (int i = 1; i < 20; i++)
                {
                    var p = i % 2 == 0 ? major : minor;
                    float x = W * i / 20f, y = H * i / 20f;
                    g.DrawLine(p, x, 0, x, H);
                    g.DrawLine(p, 0, y, W, y);
                }
                g.DrawRectangle(border, 1, 1, W - 3, H - 3);

                // Targets: centre, edge midpoints, corners (inset 5%).
                float r = unit * 2.2f;
                float[] xs = { 0.05f, 0.5f, 0.95f };
                float[] ys = { 0.05f, 0.5f, 0.95f };
                foreach (var fx in xs)
                    foreach (var fy in ys)
                    {
                        float cx = W * fx, cy = H * fy;
                        g.DrawEllipse(target, cx - r, cy - r, r * 2, r * 2);
                        g.DrawEllipse(target, cx - r / 3, cy - r / 3, r * 2 / 3, r * 2 / 3);
                        g.DrawLine(target, cx - r * 1.5f, cy, cx + r * 1.5f, cy);
                        g.DrawLine(target, cx, cy - r * 1.5f, cx, cy + r * 1.5f);
                    }
            }
        }

        void DrawCrosshair(Graphics g, Gun gun, int W, int H, float unit)
        {
            if (!gun.HasPosition || !gun.Connected) return;
            float cx = (float)(gun.X * (W - 1)), cy = (float)(gun.Y * (H - 1));
            float r = unit * 2.8f;
            bool flash = (DateTime.Now - gun.LastFlash).TotalMilliseconds < 120;
            var col = flash ? Color.White : gun.Colour;
            using (var pen = new Pen(col, flash ? 4f : 3f))
            using (var shadow = new Pen(Color.FromArgb(160, 0, 0, 0), 6f))
            {
                foreach (var p in new[] { shadow, pen })
                {
                    g.DrawEllipse(p, cx - r, cy - r, r * 2, r * 2);
                    g.DrawLine(p, cx - r * 1.8f, cy, cx - r * 0.35f, cy);
                    g.DrawLine(p, cx + r * 0.35f, cy, cx + r * 1.8f, cy);
                    g.DrawLine(p, cx, cy - r * 1.8f, cx, cy - r * 0.35f);
                    g.DrawLine(p, cx, cy + r * 0.35f, cx, cy + r * 1.8f);
                }
            }
            if (flash)
                using (var b = new SolidBrush(Color.FromArgb(150, gun.Colour)))
                    g.FillEllipse(b, cx - r * 0.6f, cy - r * 0.6f, r * 1.2f, r * 1.2f);

            using (var f = new Font("Segoe UI", unit * 1.2f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var b = new SolidBrush(gun.Colour))
            {
                string tag = ShortTag(gun);
                float tx = cx + r * 1.2f, ty = cy + r * 1.0f;
                var sz = g.MeasureString(tag, f);
                if (tx + sz.Width > W) tx = cx - r * 1.2f - sz.Width;
                if (ty + sz.Height > H) ty = cy - r * 1.0f - sz.Height;
                g.DrawString(tag, f, b, tx, ty);
            }

            if (gun.AtEdge)
            {
                using (var f = new Font("Segoe UI", unit * 1.3f, FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    string t = "OFF-SCREEN";
                    var sz = g.MeasureString(t, f);
                    float tx = Util.Clamp(cx - sz.Width / 2, 4, W - sz.Width - 4);
                    float ty = Util.Clamp(cy - r * 2.2f - sz.Height, 4, H - sz.Height - 4);
                    g.DrawString(t, f, Brushes.Orange, tx, ty);
                }
            }
        }

        static string ShortTag(Gun gun)
        {
            var m = Regex.Match(gun.Label, @"\bP\s?(\d)\b", RegexOptions.IgnoreCase);
            return m.Success ? "P" + m.Groups[1].Value : gun.Label;
        }

        void DrawPanel(Graphics g, Gun gun, bool right, int row, int W, float unit, Font f, Font fb)
        {
            float pw = unit * 30f, lh = f.Height * 1.15f;
            float ph = fb.Height * 1.3f + lh * 6.4f;
            float px = right ? W - pw - unit * 1.5f : unit * 1.5f;
            float py = unit * 3f + row * (ph + unit);

            using (var bg = new SolidBrush(Color.FromArgb(215, 22, 22, 28)))
            using (var edge = new Pen(gun.Connected ? gun.Colour : Color.DimGray, 2f))
            {
                g.FillRectangle(bg, px, py, pw, ph);
                g.DrawRectangle(edge, px, py, pw, ph);
            }

            float x = px + unit, y = py + unit * 0.5f;
            using (var cb = new SolidBrush(gun.Connected ? gun.Colour : Color.DimGray))
                g.DrawString(gun.Label, fb, cb, x, y);
            y += fb.Height * 1.3f;

            // Status line
            string status;
            Brush sb;
            if (!gun.Connected) { status = "DISCONNECTED"; sb = Brushes.OrangeRed; }
            else if (gun.LastInput == DateTime.MinValue) { status = "Connected - no input yet"; sb = Brushes.Gainsboro; }
            else
            {
                double s = (DateTime.Now - gun.LastInput).TotalSeconds;
                status = "Last input " + (s < 1 ? "just now" : s.ToString("0") + "s ago");
                sb = s > 10 ? Brushes.Orange : Brushes.Gainsboro;
            }
            g.DrawString(status, f, sb, x, y); y += lh;

            // Counters
            g.DrawString("Shots " + gun.Shots + "    Off-screen shots " + gun.OffscreenShots + "    Right clicks " + gun.RightClicks,
                f, Brushes.Gainsboro, x, y); y += lh;

            // Position
            string pos = gun.HasPosition ? string.Format("X {0,5:0.0}%   Y {1,5:0.0}%", gun.X * 100, gun.Y * 100) : "No position yet";
            g.DrawString(pos + (gun.AtEdge ? "   EDGE" : ""), f, gun.AtEdge ? Brushes.Orange : Brushes.Gainsboro, x, y); y += lh * 1.25f;

            // Mouse buttons as lamps
            float bx = x;
            bx = Lamp(g, "Trigger (L)", gun.Left, bx, y, unit, f, gun.Colour);
            bx = Lamp(g, "Right", gun.Right, bx, y, unit, f, gun.Colour);
            bx = Lamp(g, "Middle", gun.Middle, bx, y, unit, f, gun.Colour);
            y += lh * 1.35f;
            bx = x;
            bx = Lamp(g, "Back (X1)", gun.X1, bx, y, unit, f, gun.Colour);
            bx = Lamp(g, "Fwd (X2)", gun.X2, bx, y, unit, f, gun.Colour);
            y += lh * 1.35f;

            string keys = gun.KeysDown.Count == 0 ? "-" : string.Join("  ", gun.KeysDown.Select(KeyName));
            g.DrawString("Keys: " + keys, f, gun.KeysDown.Count > 0 ? Brushes.White : Brushes.Gray, x, y);
        }

        static string KeyName(Keys k)
        {
            switch (k)
            {
                case Keys.Return: return "Enter";
                case Keys.ShiftKey: return "Shift";
                case Keys.ControlKey: return "Ctrl";
                case Keys.Menu: return "Alt";
                case Keys.Escape: return "Esc";
                case Keys.Space: return "Space";
                default:
                    if (k >= Keys.D0 && k <= Keys.D9) return ((int)k - (int)Keys.D0).ToString();
                    return k.ToString();
            }
        }

        static float Lamp(Graphics g, string label, bool on, float x, float y, float unit, Font f, Color c)
        {
            var sz = g.MeasureString(label, f);
            float w = sz.Width + unit * 1.2f, h = sz.Height + unit * 0.3f;
            using (var b = new SolidBrush(on ? c : Color.FromArgb(45, 45, 55)))
                g.FillRectangle(b, x, y, w, h);
            g.DrawString(label, f, on ? Brushes.Black : Brushes.Gray, x + unit * 0.6f, y + unit * 0.15f);
            return x + w + unit * 0.6f;
        }

        void DrawLog(Graphics g, int W, int H, float unit, Font f)
        {
            if (log.Count == 0) return;
            float lh = f.Height * 1.1f;
            float w = unit * 46f, h = lh * log.Count + unit;
            float x = (W - w) / 2, y = H - h - unit * 1.5f;
            using (var bg = new SolidBrush(Color.FromArgb(200, 22, 22, 28)))
                g.FillRectangle(bg, x, y, w, h);
            float ty = y + unit * 0.5f;
            foreach (var line in log)
            {
                Brush b = line.Contains("DISCONNECTED") || line.Contains("ERROR") ? Brushes.OrangeRed : Brushes.Silver;
                g.DrawString(line, f, b, x + unit * 0.8f, ty);
                ty += lh;
            }
        }
    }
}
