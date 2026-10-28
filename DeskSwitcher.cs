using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

class TrayApp : ApplicationContext
{
    private readonly NotifyIcon tray;
    private readonly Timer timer;
    private int lastDesktop = -1;
    private IntPtr mouseHookHandle = IntPtr.Zero;
    private LowLevelMouseProc mouseHookProc;

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

    public TrayApp()
    {
        var menu = new ContextMenuStrip();
        menu.AutoClose = true;
        menu.Closed += (s, e) => UninstallMouseHook();
        menu.Items.Add("Exit", null, (s, e) => ExitThread());

        tray = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Visible = true,
            Text = "Desktop Switcher"
        };
        tray.MouseClick += (s, e) =>
        {
            RebuildMenu();
            if (e.Button == MouseButtons.Left)
            {
                tray.ContextMenuStrip.Show(Cursor.Position);
                InstallMouseHook();
            }
        };

        timer = new Timer { Interval = 1000 };
        timer.Tick += (s, e) => RefreshCurrent();
        timer.Start();
        RefreshCurrent();
        RebuildMenu();
    }

    private void RebuildMenu()
    {
        var menu = tray.ContextMenuStrip;
        menu.Items.Clear();

        var desktops = GetDesktops();
        var infos = CollectDesktops(desktops);
        var cur = GetCurrentDesktopGuid();

        for (int i = 0; i < desktops.Count; i++)
        {
            var d = desktops[i];
            DesktopInfo info = null;
            foreach (var di in infos) if (di.Id == d) { info = di; break; }

            string name = GetDesktopName(d, i);
            string topTitle = (info != null && info.TopWindow != IntPtr.Zero && info.Titles.Count > 0)
                ? info.Titles[0] : "(empty)";
            if (topTitle.Length > 45) topTitle = topTitle.Substring(0, 42) + "...";

            string text = name + (d == cur ? "  (current)" : "") + "  -  " + topTitle;
            var item = new ToolStripMenuItem(text);
            int targetIndex = i;
            item.Click += (s, e) => { tray.ContextMenuStrip.Close(); SwitchToDesktop(targetIndex); };
            if (d == cur) item.Font = new Font(item.Font, System.Drawing.FontStyle.Bold);
            menu.Items.Add(item);
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (s, e) => ExitThread());

        // show menu at cursor on left click
        // ContextMenuStrip shows automatically on right click; for left click show manually
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
        if (nCode >= 0 && tray.ContextMenuStrip.Visible &&
            (wParam.ToInt32() == WM_LBUTTONDOWN || wParam.ToInt32() == WM_RBUTTONDOWN))
        {
            System.Drawing.Point cp = System.Windows.Forms.Cursor.Position;
            if (!tray.ContextMenuStrip.Bounds.Contains(cp))
            {
                tray.ContextMenuStrip.Close();
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
        if (idx == lastDesktop) return;
        lastDesktop = idx;
        tray.Icon = MakeIcon(idx >= 0 ? (idx + 1).ToString() : "?");
        tray.Text = "Desktop " + (idx >= 0 ? (idx + 1).ToString() : "?");
        RebuildMenu();
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

    private Icon MakeIcon(string text)
    {
        var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(0, 0, 0, 0));
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (var font = new Font("Segoe UI", 16f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(Color.White))
            using (var bg = new SolidBrush(Color.FromArgb(200, 0, 120, 215)))
            {
                g.FillEllipse(bg, 2, 2, 28, 28);
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(text, font, brush, new RectangleF(0, 0, 32, 32), sf);
            }
        }
        var hicon = bmp.GetHicon();
        return Icon.FromHandle(hicon);
    }

    protected override void ExitThreadCore()
    {
        UninstallMouseHook();
        timer.Stop();
        tray.Visible = false;
        tray.Dispose();
        base.ExitThreadCore();
    }
}

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApp());
    }
}
