using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using System.Diagnostics;
using Microsoft.Win32;

class TrayApp : ApplicationContext
{
    private readonly NotifyIcon tray;
    private readonly Timer timer;
    private int lastDesktop = -1;
    private IntPtr mouseHookHandle = IntPtr.Zero;
    private LowLevelMouseProc mouseHookProc;

    private DesktopMenuForm menuForm;

    [ComImport, Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out bool onCurrentDesktop);
        [PreserveSig] int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);
        [PreserveSig] int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
    }

    private static readonly IVirtualDesktopManager Vdm =
        (IVirtualDesktopManager)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a")));

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    [DllImport("user32.dll")] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    private const uint GW_OWNER = 4;

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public KEYBDINPUT ki;
    }
    [StructLayout(LayoutKind.Sequential, Size = 32)]
    struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
    private const uint INPUT_KEYBOARD = 1;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_RIGHT = 0x27;
    private const ushort VK_LEFT = 0x25;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)] private static extern IntPtr GetModuleHandle(string lpModuleName);
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int x; public int y; }
    [StructLayout(LayoutKind.Sequential)]
    struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData; public uint flags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    class DesktopInfo
    {
        public Guid Id;
        public IntPtr TopWindow;
        public List<string> Titles = new List<string>();
    }

    public class DesktopRowData
    {
        public string Name;
        public string TopTitle;
        public bool IsCurrent;
        public int Index;
    }

    private List<Guid> currentDesktops = new List<Guid>();
    private List<DesktopInfo> currentInfos = new List<DesktopInfo>();
    private Guid currentDesktopGuid = Guid.Empty;

    public TrayApp()
    {
        tray = new NotifyIcon
        {
            Visible = true,
            Text = "Desktop Switcher"
        };
        tray.MouseClick += (s, e) =>
        {
            ShowMenu(Cursor.Position);
        };

        timer = new Timer { Interval = 1000 };
        timer.Tick += (s, e) => RefreshCurrent();
        timer.Start();
        RefreshCurrent();
    }

    private void RefreshData()
    {
        currentDesktops = GetDesktops();
        currentInfos = CollectDesktops(currentDesktops);
        currentDesktopGuid = GetCurrentDesktopGuid();
    }

    private List<DesktopRowData> BuildItems()
    {
        RefreshData();
        var items = new List<DesktopRowData>();
        for (int i = 0; i < currentDesktops.Count; i++)
        {
            var d = currentDesktops[i];
            DesktopInfo info = null;
            foreach (var di in currentInfos) if (di.Id == d) { info = di; break; }

            string name = GetDesktopName(d, i);
            string topTitle = (info != null && info.TopWindow != IntPtr.Zero && info.Titles.Count > 0)
                ? info.Titles[0] : "(empty)";
            if (topTitle.Length > 55) topTitle = topTitle.Substring(0, 52) + "...";
            if (name.Length > 28) name = name.Substring(0, 25) + "...";

            items.Add(new DesktopRowData
            {
                Name = name + (d == currentDesktopGuid ? "  (current)" : ""),
                TopTitle = topTitle,
                IsCurrent = d == currentDesktopGuid,
                Index = i
            });
        }
        return items;
    }

    private void ShowMenu(Point location)
    {
        var items = BuildItems();

        if (menuForm == null)
        {
            menuForm = new DesktopMenuForm(IsDarkMode());
            menuForm.ItemClicked += idx =>
            {
                if (menuForm != null) menuForm.Hide();
                UninstallMouseHook();
                SwitchToDesktop(idx);
            };
            menuForm.ExitClicked += (s, e) =>
            {
                if (menuForm != null) menuForm.Hide();
                UninstallMouseHook();
                ExitThread();
            };
            menuForm.Deactivate += (s, e) =>
            {
                if (menuForm != null && menuForm.Visible)
                {
                    menuForm.Hide();
                    UninstallMouseHook();
                }
            };
        }

        menuForm.Populate(items);

        var screen = Screen.FromPoint(location);
        int w = menuForm.Width;
        int h = menuForm.Height;
        int x = location.X;
        int y;
        if (location.Y >= screen.WorkingArea.Bottom - (int)(40 * (menuForm.DeviceDpi / 96f)))
        {
            y = screen.WorkingArea.Bottom - h;
        }
        else if (location.Y <= screen.WorkingArea.Top + (int)(40 * (menuForm.DeviceDpi / 96f)))
        {
            y = screen.WorkingArea.Top;
        }
        else
        {
            y = location.Y - h;
        }
        if (x + w > screen.WorkingArea.Right) x = screen.WorkingArea.Right - w;
        if (x < screen.WorkingArea.Left) x = screen.WorkingArea.Left;
        if (y + h > screen.WorkingArea.Bottom) y = screen.WorkingArea.Bottom - h;
        if (y < screen.WorkingArea.Top) y = screen.WorkingArea.Top;
        menuForm.Location = new Point(x, y);

        menuForm.Show();
        try { menuForm.Activate(); } catch { }
        InstallMouseHook();
    }

    private void SwitchToDesktop(int targetIndex)
    {
        int current = GetCurrentDesktopIndex();
        if (targetIndex == current) return;
        bool right = targetIndex > current;
        int steps = Math.Abs(targetIndex - current);
        for (int i = 0; i < steps; i++)
        {
            SendKeyCombo(right ? VK_RIGHT : VK_LEFT);
            System.Threading.Thread.Sleep(80);
        }
    }

    private static void SendKeyCombo(ushort arrowKey)
    {
        INPUT[] inputs = new INPUT[6];
        inputs[0] = KeyInput(VK_CONTROL, false);
        inputs[1] = KeyInput(VK_LWIN, false);
        inputs[2] = KeyInput(arrowKey, false);
        inputs[3] = KeyInput(arrowKey, true);
        inputs[4] = KeyInput(VK_LWIN, true);
        inputs[5] = KeyInput(VK_CONTROL, true);
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
    }

    private static INPUT KeyInput(ushort vk, bool keyUp)
    {
        INPUT input = new INPUT();
        input.type = INPUT_KEYBOARD;
        input.ki.wVk = vk;
        input.ki.dwFlags = keyUp ? KEYEVENTF_KEYUP : 0;
        return input;
    }

    private void InstallMouseHook()
    {
        if (mouseHookHandle != IntPtr.Zero) return;
        mouseHookProc = new LowLevelMouseProc(MouseHookCallback);
        mouseHookHandle = SetWindowsHookEx(WH_MOUSE_LL, mouseHookProc, GetModuleHandle(null), 0);
    }

    private void UninstallMouseHook()
    {
        if (mouseHookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(mouseHookHandle);
            mouseHookHandle = IntPtr.Zero;
        }
        mouseHookProc = null;
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && menuForm != null && menuForm.Visible &&
            (wParam.ToInt32() == WM_LBUTTONDOWN || wParam.ToInt32() == WM_RBUTTONDOWN))
        {
            System.Drawing.Point cp = System.Windows.Forms.Cursor.Position;
            if (!menuForm.Bounds.Contains(cp))
            {
                menuForm.Hide();
                UninstallMouseHook();
            }
        }
        return CallNextHookEx(mouseHookHandle, nCode, wParam, lParam);
    }

    private static string GetDesktopName(Guid id, int index)
    {
        try
        {
            string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops\Desktops\{" + id.ToString().ToUpperInvariant() + "}";
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
            {
                if (key != null)
                {
                    var name = key.GetValue("Name") as string;
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }
            }
        }
        catch { }
        return "Desktop " + (index + 1);
    }

    private void RefreshCurrent()
    {
        int idx = GetCurrentDesktopIndex();
        if (idx == lastDesktop)
        {
            return;
        }
        lastDesktop = idx;
        tray.Icon = MakeIcon(idx >= 0 ? (idx + 1).ToString() : "?");
        tray.Text = "Desktop " + (idx >= 0 ? (idx + 1).ToString() : "?");
        if (menuForm != null && menuForm.Visible)
        {
            var items = BuildItems();
            menuForm.Populate(items);
        }
    }

    private static int GetCurrentDesktopIndex()
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops"))
            {
                var ids = (byte[])key.GetValue("VirtualDesktopIDs");
                var cur = (byte[])key.GetValue("CurrentVirtualDesktop");
                if (ids == null || cur == null) return -1;
                var desktops = new List<Guid>();
                for (int i = 0; i + 16 <= ids.Length; i += 16)
                {
                    var b = new byte[16];
                    Array.Copy(ids, i, b, 0, 16);
                    desktops.Add(new Guid(b));
                }
                return desktops.IndexOf(new Guid(cur));
            }
        }
        catch { return -1; }
    }

    private static List<Guid> GetDesktops()
    {
        var list = new List<Guid>();
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops"))
            {
                var ids = (byte[])key.GetValue("VirtualDesktopIDs");
                if (ids == null) return list;
                for (int i = 0; i + 16 <= ids.Length; i += 16)
                {
                    var b = new byte[16];
                    Array.Copy(ids, i, b, 0, 16);
                    list.Add(new Guid(b));
                }
            }
        }
        catch { }
        return list;
    }

    private List<DesktopInfo> CollectDesktops(List<Guid> desktops)
    {
        var result = new Dictionary<Guid, DesktopInfo>();
        foreach (var d in desktops) result[d] = new DesktopInfo { Id = d };

        EnumWindows((hWnd, _) =>
        {
            try
            {
                if (!IsWindowVisible(hWnd)) return true;
                if (GetWindow(hWnd, GW_OWNER) != IntPtr.Zero) return true;
                int len = GetWindowTextLength(hWnd);
                if (len <= 0) return true;
                var sb = new StringBuilder(len + 1);
                GetWindowText(hWnd, sb, sb.Capacity);
                string title = sb.ToString();
                if (string.IsNullOrWhiteSpace(title)) return true;
                if (title.IndexOf("Desktop Switcher", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (title.IndexOf("DeskSwitcher", StringComparison.OrdinalIgnoreCase) >= 0) return true;

                Guid id;
                int hr = Vdm.GetWindowDesktopId(hWnd, out id);
                if (hr != 0) return true;
                if (!result.ContainsKey(id)) result[id] = new DesktopInfo { Id = id };
                var info = result[id];
                if (info.TopWindow == IntPtr.Zero) info.TopWindow = hWnd;
                info.Titles.Add(title);
            }
            catch { }
            return true;
        }, IntPtr.Zero);

        return new List<DesktopInfo>(result.Values);
    }

    private Guid GetCurrentDesktopGuid()
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops"))
            {
                var cur = (byte[])key.GetValue("CurrentVirtualDesktop");
                if (cur != null) return new Guid(cur);
            }
        }
        catch { }
        return Guid.Empty;
    }

    public static Color GetAccentColor()
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
            {
                if (key != null)
                {
                    var v = key.GetValue("AccentColor");
                    if (v is int)
                    {
                        int i = (int)v;
                        byte a = (byte)((i >> 24) & 0xFF);
                        byte r = (byte)(i & 0xFF);
                        byte g = (byte)((i >> 8) & 0xFF);
                        byte b = (byte)((i >> 16) & 0xFF);
                        if (a == 0) a = 255;
                        return Color.FromArgb(a, r, g, b);
                    }
                }
            }
        }
        catch { }
        return SystemColors.Highlight;
    }

    private static bool IsDarkMode()
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                if (key != null)
                {
                    var v = key.GetValue("AppsUseLightTheme");
                    if (v is int) return (int)v == 0;
                }
            }
        }
        catch { }
        return false;
    }

    private Icon MakeIcon(string text)
    {
        string fontName = "Segoe UI";
        foreach (var f in FontFamily.Families)
        {
            if (f.Name == "Segoe UI Variable Display") { fontName = f.Name; break; }
            if (f.Name == "Segoe UI Variable Text" && fontName == "Segoe UI") fontName = f.Name;
        }

        int[] sizes = new int[] { 16, 20, 24, 32, 48 };
        var images = new List<byte[]>();

        foreach (int size in sizes)
        {
            using (var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

                float fontSize = text.Length >= 3 ? size * 0.42f : (text.Length == 2 ? size * 0.55f : size * 0.72f);
                using (var font = new Font(fontName, fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var brush = new SolidBrush(Color.White))
                {
                    var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString(text, font, brush, new RectangleF(0, 0, size, size), sf);
                }

                using (var ms = new System.IO.MemoryStream())
                {
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    images.Add(ms.ToArray());
                }
            }
        }

        using (var ms = new System.IO.MemoryStream())
        using (var bw = new System.IO.BinaryWriter(ms))
        {
            bw.Write((ushort)0);
            bw.Write((ushort)1);
            bw.Write((ushort)images.Count);

            int offset = 6 + 16 * images.Count;
            for (int i = 0; i < images.Count; i++)
            {
                int s = sizes[i];
                bw.Write((byte)(s >= 256 ? 0 : s));
                bw.Write((byte)(s >= 256 ? 0 : s));
                bw.Write((byte)0);
                bw.Write((byte)0);
                bw.Write((ushort)1);
                bw.Write((ushort)32);
                bw.Write((uint)images[i].Length);
                bw.Write((uint)offset);
                offset += images[i].Length;
            }
            foreach (var img in images) bw.Write(img);
            ms.Position = 0;
            return new Icon(ms);
        }
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void ExitThreadCore()
    {
        UninstallMouseHook();
        if (menuForm != null)
        {
            try { menuForm.Dispose(); } catch { }
            menuForm = null;
        }
        timer.Stop();
        tray.Visible = false;
        tray.Dispose();
        base.ExitThreadCore();
    }
}

class DesktopMenuForm : Form
{
    public delegate void ItemClickedHandler(int index);
    public event ItemClickedHandler ItemClicked;
    public event EventHandler ExitClicked;

    private readonly bool dark;
    private bool useBackdrop;
    private Color bgColor;
    private Color hoverColor;
    private Color textColor;
    private Color secondaryColor;
    private Color sepColor;
    private Color accentColor;
    private string fontName;
    private float dpiScale = 1f;
    private int padding = 8;

    private readonly List<Panel> rows = new List<Panel>();
    private Panel separator;
    private Panel exitRow;

    public DesktopMenuForm(bool dark)
    {
        this.dark = dark;

        fontName = "Segoe UI";
        foreach (var f in FontFamily.Families)
        {
            if (f.Name == "Segoe UI Variable Text") { fontName = f.Name; break; }
        }

        if (dark)
        {
            bgColor = Color.FromArgb(31, 31, 31);
            hoverColor = Color.FromArgb(45, 45, 45);
            textColor = Color.FromArgb(255, 255, 255);
            secondaryColor = Color.FromArgb(153, 153, 153);
            sepColor = Color.FromArgb(60, 60, 60);
        }
        else
        {
            bgColor = Color.White;
            hoverColor = Color.FromArgb(237, 237, 237);
            textColor = Color.FromArgb(0, 0, 0);
            secondaryColor = Color.FromArgb(96, 96, 96);
            sepColor = Color.FromArgb(224, 224, 224);
        }
        accentColor = TrayApp.GetAccentColor();

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = bgColor;
        dpiScale = DeviceDpi / 96f;
        padding = (int)(8 * dpiScale);
        ClientSize = new Size((int)(380 * dpiScale), (int)(120 * dpiScale));

        HandleCreated += (s, e) => ApplyDwm();
        Resize += (s, e) => ApplyRegion();
        useBackdrop = false;
        ApplyRegion();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            return cp;
        }
    }

    private void ApplyRegion()
    {
        try
        {
            using (var path = new GraphicsPath())
            {
                int r = 8;
                var rect = new Rectangle(0, 0, Width, Height);
                path.AddArc(rect.X, rect.Y, r * 2, r * 2, 180, 90);
                path.AddArc(rect.Right - r * 2, rect.Y, r * 2, r * 2, 270, 90);
                path.AddArc(rect.Right - r * 2, rect.Bottom - r * 2, r * 2, r * 2, 0, 90);
                path.AddArc(rect.X, rect.Bottom - r * 2, r * 2, r * 2, 90, 90);
                path.CloseFigure();
                this.Region = new Region(path);
            }
        }
        catch { }
    }

    private void ApplyDwm()
    {
        try
        {
            int darkVal = dark ? 1 : 0;
            DwmSetWindowAttribute(Handle, 20, ref darkVal, 4);
            int corner = 2;
            DwmSetWindowAttribute(Handle, 33, ref corner, 4);
            useBackdrop = false;
        }
        catch { useBackdrop = false; }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (useBackdrop) return;
        using (var b = new SolidBrush(bgColor))
            e.Graphics.FillRectangle(b, ClientRectangle);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
            Hide();
            e.Handled = true;
        }
    }

    public void Populate(List<TrayApp.DesktopRowData> items)
    {
        foreach (var r in rows)
        {
            Controls.Remove(r);
            r.Dispose();
        }
        rows.Clear();
        if (separator != null) { Controls.Remove(separator); separator.Dispose(); separator = null; }
        if (exitRow != null) { Controls.Remove(exitRow); exitRow.Dispose(); exitRow = null; }

        int y = padding;
        int rowWidth = ClientSize.Width - padding * 2;

        foreach (var it in items)
        {
            var row = CreateRow(it, rowWidth);
            row.Location = new Point(padding, y);
            row.Width = rowWidth;
            Controls.Add(row);
            rows.Add(row);
            y += row.Height;
        }

        separator = new Panel { Height = 1, Width = rowWidth, Location = new Point(padding, y + 2), BackColor = sepColor };
        Controls.Add(separator);
        y += separator.Height + (int)(6 * dpiScale);

        exitRow = CreateExitRow(rowWidth);
        exitRow.Location = new Point(padding, y);
        exitRow.Width = rowWidth;
        Controls.Add(exitRow);
        y += exitRow.Height;

        ClientSize = new Size(ClientSize.Width, y + padding);
    }

    private Panel CreateRow(TrayApp.DesktopRowData data, int width)
    {
        int rowH = (int)(52 * dpiScale);
        var row = new Panel { Height = rowH, Width = width, Cursor = Cursors.Hand, BackColor = bgColor };
        try
        {
            using (var path = new GraphicsPath())
            {
                int r = 4;
                path.AddArc(0, 0, r * 2, r * 2, 180, 90);
                path.AddArc(width - r * 2, 0, r * 2, r * 2, 270, 90);
                path.AddArc(width - r * 2, rowH - r * 2, r * 2, r * 2, 0, 90);
                path.AddArc(0, rowH - r * 2, r * 2, r * 2, 90, 90);
                path.CloseFigure();
                row.Region = new Region(path);
            }
        }
        catch { }

        if (data.IsCurrent)
        {
            var bar = new Panel { Width = (int)(3 * dpiScale), Height = (int)(20 * dpiScale), Location = new Point((int)(4 * dpiScale), (int)(16 * dpiScale)), BackColor = accentColor };
            row.Controls.Add(bar);
        }

        var name = new Label
        {
            Text = data.Name,
            Font = new Font(fontName, 10.5f, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = textColor,
            Location = new Point((int)(16 * dpiScale), (int)(8 * dpiScale)),
            AutoSize = true,
            BackColor = Color.Transparent
        };
        row.Controls.Add(name);

        var title = new Label
        {
            Text = data.TopTitle,
            Font = new Font(fontName, 9f, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = secondaryColor,
            Location = new Point((int)(16 * dpiScale), (int)(28 * dpiScale)),
            AutoSize = true,
            BackColor = Color.Transparent
        };
        row.Controls.Add(title);

        Color hover = hoverColor;
        row.MouseEnter += (s, e) => { row.BackColor = hover; };
        row.MouseLeave += (s, e) =>
        {
            if (!row.ClientRectangle.Contains(row.PointToClient(Cursor.Position)))
                row.BackColor = bgColor;
        };
        foreach (Control c in row.Controls)
        {
            c.MouseEnter += (s, e) => { row.BackColor = hover; };
            c.MouseLeave += (s, e) =>
            {
                if (!row.ClientRectangle.Contains(row.PointToClient(Cursor.Position)))
                    row.BackColor = bgColor;
            };
            c.Click += (s, e) =>
            {
                if (ItemClicked != null) ItemClicked(data.Index);
            };
        }
        row.Click += (s, e) =>
        {
            if (ItemClicked != null) ItemClicked(data.Index);
        };

        return row;
    }

    private Panel CreateExitRow(int width)
    {
        int exitH = (int)(36 * dpiScale);
        var row = new Panel { Height = exitH, Width = width, Cursor = Cursors.Hand, BackColor = bgColor };
        try
        {
            using (var path = new GraphicsPath())
            {
                int r = 4;
                path.AddArc(0, 0, r * 2, r * 2, 180, 90);
                path.AddArc(width - r * 2, 0, r * 2, r * 2, 270, 90);
                path.AddArc(width - r * 2, exitH - r * 2, r * 2, r * 2, 0, 90);
                path.AddArc(0, exitH - r * 2, r * 2, r * 2, 90, 90);
                path.CloseFigure();
                row.Region = new Region(path);
            }
        }
        catch { }
        var lbl = new Label
        {
            Text = "Exit",
            Font = new Font(fontName, 10.5f, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = textColor,
            Location = new Point((int)(16 * dpiScale), (int)(8 * dpiScale)),
            AutoSize = true,
            BackColor = Color.Transparent
        };
        row.Controls.Add(lbl);

        Color hover = hoverColor;
        row.MouseEnter += (s, e) => { row.BackColor = hover; };
        row.MouseLeave += (s, e) =>
        {
            if (!row.ClientRectangle.Contains(row.PointToClient(Cursor.Position)))
                row.BackColor = bgColor;
        };
        lbl.MouseEnter += (s, e) => { row.BackColor = hover; };
        lbl.MouseLeave += (s, e) =>
        {
            if (!row.ClientRectangle.Contains(row.PointToClient(Cursor.Position)))
                row.BackColor = bgColor;
        };
        row.Click += (s, e) => { if (ExitClicked != null) ExitClicked(this, EventArgs.Empty); };
        lbl.Click += (s, e) => { if (ExitClicked != null) ExitClicked(this, EventArgs.Empty); };

        return row;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MARGINS { public int cxLeftWidth; public int cxRightWidth; public int cyTopHeight; public int cyBottomHeight; }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int pvAttribute, int cbAttribute);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);
}

static class Program
{
    [STAThread]
    static void Main()
    {
        try { TrayApp.SetProcessDPIAware(); } catch { }
        const string mutexName = "Local\\DeskSwitcherSingleInstance";
        bool createdNew;
        System.Threading.Mutex mutex = new System.Threading.Mutex(true, mutexName, out createdNew);

        if (!createdNew)
        {
            DialogResult result = MessageBox.Show(
                "DeskSwitcher is already running.\nKill the existing instance and start a new one?",
                "DeskSwitcher",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result != DialogResult.Yes)
            {
                mutex.Dispose();
                return;
            }

            foreach (Process p in Process.GetProcessesByName("DeskSwitcher"))
            {
                if (p.Id == Process.GetCurrentProcess().Id) continue;
                try { p.Kill(); p.WaitForExit(2000); } catch { }
            }

            mutex.Dispose();
            mutex = new System.Threading.Mutex(true, mutexName, out createdNew);
            if (!createdNew)
            {
                MessageBox.Show("Could not start a new instance.", "DeskSwitcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
                mutex.Dispose();
                return;
            }
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApp());

        try { mutex.ReleaseMutex(); } catch { }
        mutex.Dispose();
    }
}