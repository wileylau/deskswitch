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
using Windows.UI.Notifications;
using Windows.Data.Xml.Dom;
using Microsoft.Win32;

class TrayApp : ApplicationContext
{
    private readonly NotifyIcon tray;
    private readonly Timer timer;
    private int lastDesktop = -1;
    private string lastIconLabel;
    private IntPtr mouseHookHandle = IntPtr.Zero;
    private LowLevelMouseProc mouseHookProc;

    private DesktopMenuForm menuForm;
    private Point lastMenuLocation;

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
    }

    [ComImport, Guid("53F5CA0B-158F-4124-900C-057158060B27"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IVirtualDesktopManagerInternal
    {
        [PreserveSig] int GetCount(out uint pCount);
        [PreserveSig] int MoveViewToDesktop(IntPtr pView, IntPtr pDesktop);
        [PreserveSig] int CanViewMoveDesktops(IntPtr pView, out bool pfCanViewMoveDesktops);
        [PreserveSig] int GetCurrentDesktop(out IntPtr desktop);
        [PreserveSig] int GetDesktops(out IntPtr ppDesktops);
    }

    [ComImport, Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IObjectArray
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, ref Guid riid, out IntPtr ppv);
    }

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
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
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
    private const ushort VK_D = 0x44;
    private const ushort VK_F4 = 0x73;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int RemoveDesktopDelegate(IntPtr self, IntPtr pRemove, IntPtr pFallback);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetDesktopNameDelegate(IntPtr self, IntPtr pDesktop, IntPtr hstring);

    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("shell32.dll")] private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string AppID);
    [DllImport("combase.dll")] private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string sourceString, uint length, out IntPtr hstring);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr hstring);
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
        public string TopProcess = "";
        public List<string> Titles = new List<string>();
        public List<string> Processes = new List<string>();
    }

    public class DesktopRowData
    {
        public string Name;
        public string DesktopName;
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

        ShowToast();
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
            string rawTitle = (info != null && info.TopWindow != IntPtr.Zero && info.Titles.Count > 0)
                ? info.Titles[0] : "(empty)";
            bool tooLong = rawTitle.Length > 40;
            if (!tooLong && info != null)
            {
                foreach (var t in info.Titles)
                {
                    if (t.Length > 40) { tooLong = true; break; }
                }
            }
            string topTitle = rawTitle;
            if (tooLong && info != null && !string.IsNullOrWhiteSpace(info.TopProcess))
                topTitle = info.TopProcess;
            if (topTitle.Length > 55) topTitle = topTitle.Substring(0, 52) + "...";
            if (name.Length > 28) name = name.Substring(0, 25) + "...";

            items.Add(new DesktopRowData
            {
                Name = name + (d == currentDesktopGuid ? "  (current)" : ""),
                DesktopName = name,
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
            menuForm.NewDesktopClicked += (s, e) =>
            {
                if (menuForm != null) menuForm.Hide();
                UninstallMouseHook();
                SendNewDesktop();
                var t = new System.Windows.Forms.Timer { Interval = 400 };
                t.Tick += (ss, ee) => { t.Stop(); t.Dispose(); ReopenMenu(); };
                t.Start();
            };
            menuForm.ItemDeleted += idx =>
            {
                if (menuForm != null) menuForm.Hide();
                UninstallMouseHook();
                DeleteDesktop(idx);
                var t = new System.Windows.Forms.Timer { Interval = 400 };
                t.Tick += (ss, ee) => { t.Stop(); t.Dispose(); ReopenMenu(); };
                t.Start();
            };
            menuForm.RenameCommitted += (idx, newName) =>
            {
                RenameDesktop(idx, newName);
                lastDesktop = -1;
                lastIconLabel = null;
                RefreshCurrent();
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
        lastMenuLocation = menuForm.Location;

        menuForm.Show();
        try { menuForm.Activate(); } catch { }
        InstallMouseHook();
    }

    private void SendCloseDesktop()
    {
        INPUT[] inputs = new INPUT[6];
        inputs[0] = KeyInput(VK_CONTROL, false);
        inputs[1] = KeyInput(VK_LWIN, false);
        inputs[2] = KeyInput(VK_F4, false);
        inputs[3] = KeyInput(VK_F4, true);
        inputs[4] = KeyInput(VK_LWIN, true);
        inputs[5] = KeyInput(VK_CONTROL, true);
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
    }

    private void SendNewDesktop()
    {
        INPUT[] inputs = new INPUT[6];
        inputs[0] = KeyInput(VK_CONTROL, false);
        inputs[1] = KeyInput(VK_LWIN, false);
        inputs[2] = KeyInput(VK_D, false);
        inputs[3] = KeyInput(VK_D, true);
        inputs[4] = KeyInput(VK_LWIN, true);
        inputs[5] = KeyInput(VK_CONTROL, true);
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
    }

    private void ReopenMenu()
    {
        if (menuForm == null) return;
        var items = BuildItems();
        menuForm.Populate(items);
        menuForm.Location = lastMenuLocation;
        menuForm.Show();
        try { menuForm.Activate(); } catch { }
        InstallMouseHook();
    }

    private IntPtr GetInternalManagerPtr()
    {
        try
        {
            var shellType = Type.GetTypeFromCLSID(new Guid("c2f03a33-21f5-47fa-b4bb-156362a2f239"));
            var shell = Activator.CreateInstance(shellType) as IServiceProvider;
            Guid service = new Guid("c5e0cdca-7b6e-41b2-9fc4-d93975cc467b");
            Guid iid = new Guid("53f5ca0b-158f-4124-900c-057158060b27");
            IntPtr ptr;
            int hr = shell.QueryService(ref service, ref iid, out ptr);
            if (hr == 0 && ptr != IntPtr.Zero) return ptr;
        }
        catch { }
        return IntPtr.Zero;
    }

    private void DeleteDesktop(int index)
    {
        var desktops = GetDesktops();
        if (index < 0 || index >= desktops.Count) return;
        if (desktops.Count <= 1) return;

        IntPtr mgrPtr = GetInternalManagerPtr();
        if (mgrPtr == IntPtr.Zero) return;
        try
        {
            var mgr = (IVirtualDesktopManagerInternal)Marshal.GetTypedObjectForIUnknown(mgrPtr, typeof(IVirtualDesktopManagerInternal));
            IntPtr arrPtr;
            int hr = mgr.GetDesktops(out arrPtr);
            if (hr != 0 || arrPtr == IntPtr.Zero) return;
            try
            {
                var arr = (IObjectArray)Marshal.GetTypedObjectForIUnknown(arrPtr, typeof(IObjectArray));
                uint count;
                arr.GetCount(out count);
                if (index >= count) return;

                Guid vdIid = new Guid("3f07f4be-b107-441a-af0f-39d82529072c");
                IntPtr targetPtr;
                arr.GetAt((uint)index, ref vdIid, out targetPtr);
                if (targetPtr == IntPtr.Zero) return;

                int fbIndex = (index == 0) ? 1 : 0;
                IntPtr fallbackPtr;
                arr.GetAt((uint)fbIndex, ref vdIid, out fallbackPtr);
                if (fallbackPtr == IntPtr.Zero) { Marshal.Release(targetPtr); return; }

                IntPtr vtable = Marshal.ReadIntPtr(mgrPtr);
                IntPtr fn = Marshal.ReadIntPtr(vtable, 13 * IntPtr.Size);
                var del = (RemoveDesktopDelegate)Marshal.GetDelegateForFunctionPointer(fn, typeof(RemoveDesktopDelegate));
                del(mgrPtr, targetPtr, fallbackPtr);

                Marshal.Release(targetPtr);
                Marshal.Release(fallbackPtr);
            }
            finally
            {
                Marshal.Release(arrPtr);
            }
        }
        finally
        {
            Marshal.Release(mgrPtr);
        }
    }

    private void RenameDesktop(int index, string newName)
    {
        var desktops = GetDesktops();
        if (index < 0 || index >= desktops.Count) return;

        IntPtr mgrPtr = GetInternalManagerPtr();
        if (mgrPtr == IntPtr.Zero) return;
        try
        {
            var mgr = (IVirtualDesktopManagerInternal)Marshal.GetTypedObjectForIUnknown(mgrPtr, typeof(IVirtualDesktopManagerInternal));
            IntPtr arrPtr;
            int hr = mgr.GetDesktops(out arrPtr);
            if (hr != 0 || arrPtr == IntPtr.Zero) return;
            try
            {
                var arr = (IObjectArray)Marshal.GetTypedObjectForIUnknown(arrPtr, typeof(IObjectArray));
                Guid vdIid = new Guid("3f07f4be-b107-441a-af0f-39d82529072c");
                IntPtr pDesktop;
                arr.GetAt((uint)index, ref vdIid, out pDesktop);
                if (pDesktop == IntPtr.Zero) return;

                IntPtr hstr;
                WindowsCreateString(newName, (uint)newName.Length, out hstr);
                IntPtr vtable = Marshal.ReadIntPtr(mgrPtr);
                IntPtr fn = Marshal.ReadIntPtr(vtable, 16 * IntPtr.Size);
                var del = (SetDesktopNameDelegate)Marshal.GetDelegateForFunctionPointer(fn, typeof(SetDesktopNameDelegate));
                del(mgrPtr, pDesktop, hstr);
                WindowsDeleteString(hstr);
                Marshal.Release(pDesktop);
            }
            finally
            {
                Marshal.Release(arrPtr);
            }
        }
        finally
        {
            Marshal.Release(mgrPtr);
        }
    }

    private string PromptRename(string current)
    {
        using (var f = new Form())
        {
            f.Text = "Rename desktop";
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.StartPosition = FormStartPosition.CenterScreen;
            f.ClientSize = new Size(300, 90);
            f.TopMost = true;
            f.MaximizeBox = false;
            f.MinimizeBox = false;

            var tb = new TextBox { Text = current, Left = 10, Top = 10, Width = 260 };
            var ok = new Button { Text = "OK", Left = 120, Top = 45, Width = 75, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", Left = 205, Top = 45, Width = 75, DialogResult = DialogResult.Cancel };

            f.Controls.Add(tb);
            f.Controls.Add(ok);
            f.Controls.Add(cancel);
            f.AcceptButton = ok;
            f.CancelButton = cancel;

            if (f.ShowDialog() == DialogResult.OK)
                return tb.Text.Trim();
            return null;
        }
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

    private void ShowToast()
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID("DeskSwitcher");
            var template = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
            var textNodes = template.GetElementsByTagName("text");
            textNodes[0].AppendChild(template.CreateTextNode("DeskSwitcher"));
            textNodes[1].AppendChild(template.CreateTextNode("DeskSwitcher is running. Pin this icon to the taskbar so it stays visible."));
            var toast = new ToastNotification(template);
            ToastNotificationManager.CreateToastNotifier("DeskSwitcher").Show(toast);
        }
        catch { }
    }

    private void RefreshCurrent()
    {
        int idx = GetCurrentDesktopIndex();
        string label = "?";
        string tooltip = "Desktop ?";
        if (idx >= 0)
        {
            var desktops = GetDesktops();
            if (idx < desktops.Count)
            {
                string name = GetDesktopName(desktops[idx], idx);
                string defaultName = "Desktop " + (idx + 1);
                if (name == defaultName)
                    label = (idx + 1).ToString();
                else if (!string.IsNullOrWhiteSpace(name))
                    label = name.Substring(0, 1).ToUpperInvariant();
                else
                    label = (idx + 1).ToString();
                tooltip = name;
            }
            else
            {
                label = (idx + 1).ToString();
                tooltip = "Desktop " + (idx + 1);
            }
        }

        if (idx == lastDesktop && label == lastIconLabel) 
        {
            return;
        }
        lastDesktop = idx;
        lastIconLabel = label;
        tray.Icon = MakeIcon(label);
        tray.Text = tooltip;
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
                if (info.TopWindow == IntPtr.Zero)
                {
                    info.TopWindow = hWnd;
                    try
                    {
                        uint pid;
                        GetWindowThreadProcessId(hWnd, out pid);
                        if (pid != 0)
                        {
                            using (var p = Process.GetProcessById((int)pid))
                                info.TopProcess = p.ProcessName;
                        }
                    }
                    catch { }
                }
                info.Titles.Add(title);

                try
                {
                    uint pid2;
                    GetWindowThreadProcessId(hWnd, out pid2);
                    if (pid2 != 0)
                    {
                        string pname = null;
                        try { using (var p = Process.GetProcessById((int)pid2)) pname = p.ProcessName; } catch { }
                        if (!string.IsNullOrWhiteSpace(pname) && !info.Processes.Contains(pname))
                            info.Processes.Add(pname);
                    }
                }
                catch { }
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
    public delegate void ItemDeletedHandler(int index);
    public event ItemClickedHandler ItemClicked;
    public event ItemDeletedHandler ItemDeleted;
    public event Action<int, string> RenameCommitted;
    public event EventHandler ExitClicked;
    public event EventHandler NewDesktopClicked;

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
    private Panel newDesktopRow;
    private TextBox editBox;
    private Label editLabel;
    private int editIndex = -1;

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
        if (newDesktopRow != null) { Controls.Remove(newDesktopRow); newDesktopRow.Dispose(); newDesktopRow = null; }

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

        newDesktopRow = CreateNewDesktopRow(rowWidth);
        newDesktopRow.Location = new Point(padding, y);
        newDesktopRow.Width = rowWidth;
        Controls.Add(newDesktopRow);
        y += newDesktopRow.Height + (int)(2 * dpiScale);

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
        name.DoubleClick += (s, e) => { BeginRename(data.Index, name, data.DesktopName); };

        var title = new Label
        {
            Text = data.TopTitle,
            Font = new Font(fontName, 9f, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = secondaryColor,
            Location = new Point((int)(16 * dpiScale), (int)(32 * dpiScale)),
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
        }

        var nameClickTimer = new Timer { Interval = 300 };
        nameClickTimer.Tick += (s, e) =>
        {
            nameClickTimer.Stop();
            if (ItemClicked != null) ItemClicked(data.Index);
        };
        name.Click += (s, e) => { nameClickTimer.Stop(); nameClickTimer.Start(); };
        name.DoubleClick += (s, e) => { nameClickTimer.Stop(); BeginRename(data.Index, name, data.DesktopName); };

        title.Click += (s, e) => { if (ItemClicked != null) ItemClicked(data.Index); };
        row.Click += (s, e) => { if (ItemClicked != null) ItemClicked(data.Index); };
        var del = new Label
        {
            Text = "\u00D7",
            Font = new Font(fontName, 12f, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = secondaryColor,
            Location = new Point(width - (int)(22 * dpiScale), (int)(8 * dpiScale)),
            AutoSize = true,
            BackColor = Color.Transparent,
            Cursor = Cursors.Hand
        };
        row.Controls.Add(del);

        del.MouseEnter += (s, e) => { row.BackColor = hover; };
        del.MouseLeave += (s, e) =>
        {
            if (!row.ClientRectangle.Contains(row.PointToClient(Cursor.Position)))
                row.BackColor = bgColor;
        };
        del.Click += (s, e) =>
        {
            if (ItemDeleted != null) ItemDeleted(data.Index);
        };

        return row;
    }

    private void BeginRename(int index, Label nameLabel, string currentName)
    {
        if (editBox != null) return;
        editIndex = index;
        editLabel = nameLabel;
        nameLabel.Visible = false;

        var row = nameLabel.Parent as Panel;
        editBox = new TextBox
        {
            Text = currentName,
            Font = nameLabel.Font,
            ForeColor = textColor,
            BackColor = bgColor,
            Location = nameLabel.Location,
            Width = Math.Max((int)(120 * dpiScale), row.ClientSize.Width - nameLabel.Left - (int)(8 * dpiScale)),
            Height = (int)(16 * dpiScale),
            BorderStyle = BorderStyle.FixedSingle
        };
        row.Controls.Add(editBox);
        editBox.Focus();
        editBox.SelectAll();

        editBox.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                CommitRename();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                CancelRename();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        };
        editBox.LostFocus += (s, e) => CommitRename();
    }

    private void CommitRename()
    {
        if (editBox == null) return;
        string text = editBox.Text.Trim();
        int idx = editIndex;
        var lbl = editLabel;
        var box = editBox;

        editBox = null;
        editLabel = null;
        editIndex = -1;

        Panel row = (lbl != null) ? (lbl.Parent as Panel) : null;
        if (lbl != null && row != null && row.Controls.Contains(box))
        {
            row.Controls.Remove(box);
            box.Dispose();
            lbl.Visible = true;
            if (!string.IsNullOrWhiteSpace(text))
            {
                string suffix = lbl.Text.Contains("(current)") ? "  (current)" : "";
                lbl.Text = text + suffix;
            }
        }

        if (RenameCommitted != null && !string.IsNullOrWhiteSpace(text))
            RenameCommitted(idx, text);
    }

    private void CancelRename()
    {
        if (editBox == null) return;
        var box = editBox;
        var lbl = editLabel;
        editBox = null;
        editLabel = null;
        editIndex = -1;
        Panel row = (lbl != null) ? (lbl.Parent as Panel) : null;
        if (lbl != null && row != null && row.Controls.Contains(box))
        {
            row.Controls.Remove(box);
            box.Dispose();
            lbl.Visible = true;
        }
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

    private Panel CreateNewDesktopRow(int width)
    {
        int rowH = (int)(36 * dpiScale);
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
        var lbl = new Label
        {
            Text = "New desktop",
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
        row.Click += (s, e) => { if (NewDesktopClicked != null) NewDesktopClicked(this, EventArgs.Empty); };
        lbl.Click += (s, e) => { if (NewDesktopClicked != null) NewDesktopClicked(this, EventArgs.Empty); };

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