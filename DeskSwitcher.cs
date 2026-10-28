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
    private SummaryForm summary;
    private int lastDesktop = -1;

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
    private const uint GW_OWNER = 4;

    public TrayApp()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Exit", null, (s, e) => ExitThread());

        tray = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Visible = true,
            Text = "Desktop Switcher"
        };
        tray.MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowSummary();
        };

        timer = new Timer { Interval = 1000 };
        timer.Tick += (s, e) => RefreshCurrent();
        timer.Start();
        RefreshCurrent();
    }

    private void RefreshCurrent()
    {
        int idx = GetCurrentDesktopIndex();
        if (idx == lastDesktop) return;
        lastDesktop = idx;
        tray.Icon = MakeIcon(idx >= 0 ? (idx + 1).ToString() : "?");
        tray.Text = "Desktop " + (idx >= 0 ? (idx + 1).ToString() : "?");
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
        var icon = Icon.FromHandle(hicon);
        // keep bitmap alive by not disposing? Icon.FromHandle doesn't own; leak acceptable for tray updates
        return icon;
    }

    private void ShowSummary()
    {
        if (summary == null || summary.IsDisposed)
        {
            summary = new SummaryForm(this);
            summary.Show();
        }
        else
        {
            summary.RefreshData();
            summary.Activate();
        }
    }

    public Dictionary<Guid, List<string>> CollectWindows()
    {
        var desktops = GetDesktops();
        var result = new Dictionary<Guid, List<string>>();
        foreach (var d in desktops) result[d] = new List<string>();

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

                Guid id;
                int hr = Vdm.GetWindowDesktopId(hWnd, out id);
                if (hr != 0) return true;
                if (!result.ContainsKey(id)) result[id] = new List<string>();
                result[id].Add(title);
            }
            catch { }
            return true;
        }, IntPtr.Zero);

        return result;
    }

    public List<Guid> Desktops { get { return GetDesktops(); } }
    public Guid CurrentDesktopGuid
    {
        get
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
    }

    protected override void ExitThreadCore()
    {
        timer.Stop();
        tray.Visible = false;
        tray.Dispose();
        base.ExitThreadCore();
    }
}

class SummaryForm : Form
{
    private readonly TrayApp app;
    private TextBox box;

    public SummaryForm(TrayApp app)
    {
        this.app = app;
        Text = "Desktops Summary";
        Width = 520;
        Height = 420;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;

        box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 10f)
        };
        var btn = new Button { Text = "Refresh", Dock = DockStyle.Bottom, Height = 30 };
        btn.Click += (s, e) => RefreshData();
        Controls.Add(box);
        Controls.Add(btn);
        RefreshData();
    }

    public void RefreshData()
    {
        var desktops = app.Desktops;
        var windows = app.CollectWindows();
        var cur = app.CurrentDesktopGuid;
        var sb = new StringBuilder();
        for (int i = 0; i < desktops.Count; i++)
        {
            var d = desktops[i];
            string mark = d == cur ? "  <-- current" : "";
            sb.AppendLine("Desktop " + (i + 1).ToString() + mark);
            List<string> wins;
            if (windows.TryGetValue(d, out wins) && wins.Count > 0)
            {
                foreach (var w in wins.OrderBy(x => x))
                    sb.AppendLine("   - " + w);
            }
            else
            {
                sb.AppendLine("   (no windows)");
            }
            sb.AppendLine();
        }
        foreach (var kv in windows)
        {
            if (desktops.Contains(kv.Key)) continue;
            string label = kv.Key == Guid.Empty ? "All desktops (pinned/system)" : "Unknown desktop";
            sb.AppendLine(label);
            foreach (var w in kv.Value.OrderBy(x => x)) sb.AppendLine("   - " + w);
            sb.AppendLine();
        }
        box.Text = sb.ToString();
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
