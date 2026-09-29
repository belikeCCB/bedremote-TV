using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// bedremote - 手机当键鼠，画面走 HDMI 到电视。
// 只用 .NET Framework 4 自带能力，无需安装任何东西，无需管理员。

static class W32
{
    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT { public uint uMsg; public ushort wParamL; public ushort wParamH; }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public INPUTUNION u; }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFOEX lpi);
    [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint esFlags);

    [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr n);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern IntPtr SetClipboardData(uint fmt, IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint fmt);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr h);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr h);

    public delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr data);

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    public const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    public const uint MI_MOVE = 0x0001, MI_LEFTDOWN = 0x0002, MI_LEFTUP = 0x0004, MI_RIGHTDOWN = 0x0008,
                      MI_RIGHTUP = 0x0010, MI_MIDDLEDOWN = 0x0020, MI_MIDDLEUP = 0x0040,
                      MI_WHEEL = 0x0800, MI_HWHEEL = 0x1000, MI_ABSOLUTE = 0x8000, MI_VIRTUALDESK = 0x4000;
    public const uint KI_EXT = 0x0001, KI_UP = 0x0002, KI_UNICODE = 0x0004;

    const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001;
    const uint CF_UNICODETEXT = 13, GMEM_MOVEABLE = 0x0002;
    const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    static int CBSize { get { return Marshal.SizeOf(typeof(INPUT)); } }

    static INPUT Mouse(uint flags, int dx, int dy, uint data)
    {
        var i = new INPUT(); i.type = INPUT_MOUSE;
        i.u.mi.dx = dx; i.u.mi.dy = dy; i.u.mi.mouseData = data; i.u.mi.dwFlags = flags;
        return i;
    }
    static INPUT Key(ushort vk, ushort scan, uint flags)
    {
        var i = new INPUT(); i.type = INPUT_KEYBOARD;
        i.u.ki.wVk = vk; i.u.ki.wScan = scan; i.u.ki.dwFlags = flags;
        return i;
    }
    public static void Send(INPUT[] arr) { SendInput((uint)arr.Length, arr, CBSize); }

    public static void MoveRelative(int dx, int dy)
    {
        // 不能直接发 MOUSEEVENTF_MOVE：那会走"提高指针精确度"的加速曲线，
        // 实测 +60 变成 +134，而且快慢不一致，触控板完全不可控。
        // 所以改成：读当前落点 -> 加上位移 -> 用绝对坐标放到那里，1:1 且自动跟随实体鼠标。
        POINT cur = CursorPos();
        RECT v = VirtualDesktop();
        int nx = cur.X + dx, ny = cur.Y + dy;
        if (nx < v.Left) nx = v.Left; if (nx > v.Right - 1) nx = v.Right - 1;
        if (ny < v.Top) ny = v.Top; if (ny > v.Bottom - 1) ny = v.Bottom - 1;
        MoveAbsolutePhysical(nx, ny);
    }

    public static void MoveAbsolutePhysical(int px, int py)
    {
        RECT v = VirtualDesktop();
        int w = v.Right - v.Left, h = v.Bottom - v.Top;
        if (w < 1) w = 1; if (h < 1) h = 1;
        double fx = (px - v.Left + 0.5) / w, fy = (py - v.Top + 0.5) / h;
        MoveAbsoluteVirtual(fx, fy);
    }

    public static void MoveAbsoluteVirtual(double normX, double normY)
    {
        int dx = (int)Math.Round(normX * 65535.0), dy = (int)Math.Round(normY * 65535.0);
        if (dx < 0) dx = 0; if (dx > 65535) dx = 65535;
        if (dy < 0) dy = 0; if (dy > 65535) dy = 65535;
        Send(new[] { Mouse(MI_MOVE | MI_ABSOLUTE | MI_VIRTUALDESK, dx, dy, 0) });
    }

    public static void Button(uint down, uint up, string how)
    {
        if (how == "down") Send(new[] { Mouse(down, 0, 0, 0) });
        else if (how == "up") Send(new[] { Mouse(up, 0, 0, 0) });
        else if (how == "dbl")
        {
            Send(new[] { Mouse(down, 0, 0, 0), Mouse(up, 0, 0, 0), Mouse(down, 0, 0, 0), Mouse(up, 0, 0, 0) });
        }
        else { Send(new[] { Mouse(down, 0, 0, 0) }); Thread.Sleep(24); Send(new[] { Mouse(up, 0, 0, 0) }); }
    }

    public static void Wheel(int d, int h)
    {
        var list = new List<INPUT>();
        if (d != 0) list.Add(Mouse(MI_WHEEL, 0, 0, unchecked((uint)d)));
        if (h != 0) list.Add(Mouse(MI_HWHEEL, 0, 0, unchecked((uint)h)));
        if (list.Count > 0) Send(list.ToArray());
    }

    public static void Vk(uint vk, bool ext, bool up)
    {
        uint f = (ext ? KI_EXT : 0) | (up ? KI_UP : 0);
        Send(new[] { Key((ushort)vk, 0, f) });
    }

    public static void TapVk(uint vk, bool ext)
    {
        uint f = ext ? KI_EXT : 0;
        Send(new[] { Key((ushort)vk, 0, f), Key((ushort)vk, 0, f | KI_UP) });
    }

    public static void TypeUnicode(string s)
    {
        var list = new List<INPUT>();
        foreach (char c in s)
        {
            list.Add(Key(0, (ushort)c, KI_UNICODE));
            list.Add(Key(0, (ushort)c, KI_UNICODE | KI_UP));
        }
        const int CHUNK = 24;
        for (int i = 0; i < list.Count; i += CHUNK)
        {
            int n = Math.Min(CHUNK, list.Count - i);
            var part = new INPUT[n]; Array.Copy(list.ToArray(), i, part, 0, n);
            Send(part);
            if (i + n < list.Count) Thread.Sleep(4);
        }
    }

    public static string ClipboardGet()
    {
        try
        {
            if (!OpenClipboard(IntPtr.Zero)) return null;
            try
            {
                IntPtr h = GetClipboardData(CF_UNICODETEXT);
                if (h == IntPtr.Zero) return null;
                return Marshal.PtrToStringUni(h);
            }
            finally { CloseClipboard(); }
        }
        catch { return null; }
    }

    public static bool ClipboardSet(string text)
    {
        try
        {
            if (!OpenClipboard(IntPtr.Zero)) return false;
            try
            {
                EmptyClipboard();
                byte[] bytes = Encoding.Unicode.GetBytes(text + "\0");
                IntPtr h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
                if (h == IntPtr.Zero) return false;
                IntPtr dst = GlobalLock(h);
                if (dst == IntPtr.Zero) { GlobalFree(h); return false; }
                Marshal.Copy(bytes, 0, dst, bytes.Length);
                GlobalUnlock(h);
                if (SetClipboardData(CF_UNICODETEXT, h) == IntPtr.Zero) { GlobalFree(h); return false; }
                return true;
            }
            finally { CloseClipboard(); }
        }
        catch { return false; }
    }

    static DateTime _lockChecked = DateTime.MinValue;

    // 近似判据：锁屏界面和 Ctrl+Alt+Del 界面都由 LogonUI.exe 承载，它在跑就说明
    // 输入桌面已经不是你的桌面了，注入会被系统丢掉。
    // UAC 提权确认框不经过 LogonUI，它由 Consent.exe 承载 —— 那是另一种"点不动"：
    // 窗口本身在普通桌面上看得见，但它是**高权限窗口**，我们这种普通权限进程往里送输入
    // 会被 UIPI 直接丢掉。两者的解法完全不同，所以这里必须分开报，别拿"先去解锁"糊弄。
    static string _lockWhy = "";
    public static string LockWhy()
    {
        if ((DateTime.Now - _lockChecked).TotalMilliseconds < 500) return _lockWhy;
        _lockChecked = DateTime.Now;
        try
        {
            bool consent = Process.GetProcessesByName("Consent").Length > 0;
            bool logon = Process.GetProcessesByName("LogonUI").Length > 0;
            _lockWhy = consent ? "uac" : (logon ? "lock" : "");
        }
        catch { _lockWhy = ""; }
        return _lockWhy;
    }
    public static bool LockedNow() { return LockWhy().Length > 0; }

    public static void KeepAwake(bool on)
    {
        SetThreadExecutionState(on ? (ES_CONTINUOUS | ES_SYSTEM_REQUIRED) : ES_CONTINUOUS);
    }

    public static void MonitorOff()
    {
        const uint WM_SYSCOMMAND = 0x0112, SC_MONITORPOWER = 0xF170;
        PostMessage(new IntPtr(0xffff), WM_SYSCOMMAND, new IntPtr(unchecked((int)SC_MONITORPOWER)), new IntPtr(2));
    }

    public static POINT CursorPos() { POINT p; GetCursorPos(out p); return p; }

    public static string ForegroundTitle()
    {
        try
        {
            IntPtr h = GetForegroundWindow(); if (h == IntPtr.Zero) return "";
            var sb = new StringBuilder(512);
            GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }
        catch { return ""; }
    }

    public class Mon { public int Left, Top, Right, Bottom; public bool Primary; public string Device; }

    static System.Windows.Forms.Screen[] ToScreens()
    {
        return System.Windows.Forms.Screen.AllScreens;
    }

    public static List<Mon> Monitors()
    {
        var list = new List<Mon>();
        try
        {
            foreach (var sc in ToScreens())
                list.Add(new Mon
                {
                    Left = sc.Bounds.Left, Top = sc.Bounds.Top,
                    Right = sc.Bounds.Right, Bottom = sc.Bounds.Bottom,
                    Primary = sc.Primary, Device = sc.DeviceName
                });
        }
        catch { }
        return list;
    }

    public static RECT VirtualDesktop()
    {
        return new RECT
        {
            Left = GetSystemMetrics(SM_XVIRTUALSCREEN),
            Top = GetSystemMetrics(SM_YVIRTUALSCREEN),
            Right = GetSystemMetrics(SM_XVIRTUALSCREEN) + GetSystemMetrics(SM_CXVIRTUALSCREEN),
            Bottom = GetSystemMetrics(SM_YVIRTUALSCREEN) + GetSystemMetrics(SM_CYVIRTUALSCREEN)
        };
    }
}

static class Keys
{
    public class K { public uint Vk; public bool Ext; public K(uint v, bool e) { Vk = v; Ext = e; } }
    static readonly Dictionary<string, K> Map = new Dictionary<string, K>(StringComparer.OrdinalIgnoreCase)
    {
        { "enter", new K(0x0D, false) }, { "return", new K(0x0D, false) },
        { "esc", new K(0x1B, false) }, { "escape", new K(0x1B, false) },
        { "tab", new K(0x09, false) }, { "space", new K(0x20, false) },
        { "backspace", new K(0x08, false) }, { "back", new K(0x08, false) },
        { "del", new K(0x2E, false) }, { "delete", new K(0x2E, false) },
        { "ins", new K(0x2D, false) },
        { "up", new K(0x26, true) }, { "down", new K(0x28, true) },
        { "left", new K(0x25, true) }, { "right", new K(0x27, true) },
        { "home", new K(0x24, true) }, { "end", new K(0x23, true) },
        { "pgup", new K(0x21, true) }, { "prior", new K(0x21, true) },
        { "pgdn", new K(0x22, true) }, { "next", new K(0x22, true) },
        { "caps", new K(0x14, false) },
        { "win", new K(0x5B, false) }, { "lwin", new K(0x5B, false) }, { "rwin", new K(0x5C, true) },
        { "menu", new K(0x5D, true) }, { "apps", new K(0x5D, true) },
        { "printscreen", new K(0x2C, true) }, { "pause", new K(0x13, false) },
        { "playpause", new K(0xB3, false) }, { "play", new K(0xB3, false) },
        { "nexttrack", new K(0xB0, false) }, { "prevtrack", new K(0xA1, false) },
        { "stoptrack", new K(0xB2, false) },
        { "mute", new K(0xAD, false) }, { "voldown", new K(0xAE, false) }, { "volup", new K(0xAF, false) },
        { "browserback", new K(0xA6, false) }, { "browserforward", new K(0xA7, false) },
        { "browserrefresh", new K(0xA8, false) }, { "search", new K(0x46, false) },
    };

    static K F(int n) { return new K((uint)(0x70 + n - 1), false); }
    static Keys() { for (int i = 1; i <= 12; i++) Map["f" + i] = F(i); }

    public static bool Has(string name) { return Map.ContainsKey(name); }
    public static K Get(string name) { K k; return Map.TryGetValue(name, out k) ? k : null; }

    public static bool CharVk(string k, out uint vk)
    {
        vk = 0;
        if (k == null || k.Length != 1) return false;
        char c = char.ToUpperInvariant(k[0]);
        if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) { vk = c; return true; }
        return false;
    }

    public static uint ModVk(string m)
    {
        switch (m.ToLowerInvariant())
        {
            case "ctrl": return 0x11;
            case "alt": return 0x12;
            case "shift": return 0x10;
            case "win": return 0x5B;
            default: return 0;
        }
    }
    public static bool ModExt(string m) { return string.Equals(m, "alt", StringComparison.OrdinalIgnoreCase) || string.Equals(m, "win", StringComparison.OrdinalIgnoreCase); }
}

// ---------- 手柄的"按住"状态 ----------
// 手机当手柄和当鼠标有个本质区别：手指按住的每一秒都必须有人替它维持状态，
// 而"抬手"那个事件是最容易丢的（锁屏、切后台、WiFi 抖一下、页面被系统回收）。
// 丢一次的后果不是"少一个事件"，是游戏里角色一直朝前跑 —— 所以这里不按下发/抬发，
// 而是每帧上报"我现在按住了哪些"，服务端自己差分；再配一个心跳看门狗兜底。
static class Held
{
    class Holder
    {
        public readonly Dictionary<string, Keys.K> Ks = new Dictionary<string, Keys.K>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, uint> Mb = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        public int Next;                       // Environment.TickCount 意义上的过期时刻
    }

    // 心跳窗口：手机默认 33ms 一帧，这里给到 900ms 才判死。
    // 宁可松得慢一点（多跑半秒）也别松得太快 —— 一次 GC 停顿就松键会被当成 bug。
    const int HoldMs = 900;
    static readonly Dictionary<string, Holder> All = new Dictionary<string, Holder>();
    static readonly object Lk = new object();

    static Keys.K Resolve(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var k = Keys.Get(name);
        if (k != null) return k;
        uint mvk = Keys.ModVk(name);              // shift/ctrl/alt/win 不在 Keys.Map 里（它们本来是给 combo 用的），
        if (mvk != 0) return new Keys.K(mvk, Keys.ModExt(name));   // 但手柄的"疾跑键"就是 shift —— 得能按住
        uint vk;
        return Keys.CharVk(name, out vk) ? new Keys.K(vk, false) : null;
    }

    static uint[] MbFlags(string b)
    {
        if (b == "right") return new[] { W32.MI_RIGHTDOWN, W32.MI_RIGHTUP };
        if (b == "mid") return new[] { W32.MI_MIDDLEDOWN, W32.MI_MIDDLEUP };
        if (b == "left") return new[] { W32.MI_LEFTDOWN, W32.MI_LEFTUP };
        return null;
    }

    // 一帧：keys=当前按住的键名（逗号分隔）、mb=按住的鼠标键、dx/dy=这一帧的视角位移。
    // 返回一个短字符串当 ack，方便手机上看到"电脑以为我现在按住了什么"。
    public static string Frame(string who, string keys, string mbs, int dx, int dy)
    {
        var wantK = new List<Keys.K>();
        var seenK = new Dictionary<string, Keys.K>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (keys ?? "").Split(','))
        {
            string n = raw.Trim();
            if (n.Length == 0 || seenK.ContainsKey(n)) continue;
            var k = Resolve(n);
            if (k == null) continue;                 // 不认识的键名直接丢，不让手机把服务端猜一遍
            seenK[n] = k;
            wantK.Add(k);
            if (wantK.Count >= 16) break;            // 十几根手指的游戏不存在，超了就是发疯了
        }

        var wantM = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (mbs ?? "").Split(','))
        {
            string n = raw.Trim();
            if (n.Length == 0) continue;
            if (MbFlags(n) == null) continue;
            wantM[n] = 0;
            if (wantM.Count >= 4) break;
        }

        if (dx > 400) dx = 400; if (dx < -400) dx = -400;
        if (dy > 400) dy = 400; if (dy < -400) dy = -400;

        Holder h;
        lock (Lk)
        {
            if (!All.TryGetValue(who, out h))
            {
                if (All.Count > 24) All.Clear();     // 没有令牌时谁都可能发帧，别让表无限长
                h = new Holder();
                All[who] = h;
            }
            h.Next = Environment.TickCount + HoldMs;

            // 先松掉这一帧里没有的，再按下新出现的：顺序反过来的话，
            // 同一个键换名字（比如 W→D）会留下一个没人认领的按下状态。
            var goneK = new List<string>();
            foreach (var kv in h.Ks) if (!seenK.ContainsKey(kv.Key)) goneK.Add(kv.Key);
            foreach (var n in goneK) { W32.Vk(h.Ks[n].Vk, h.Ks[n].Ext, true); h.Ks.Remove(n); }
            foreach (var kv in seenK)
                if (!h.Ks.ContainsKey(kv.Key)) { W32.Vk(kv.Value.Vk, kv.Value.Ext, false); h.Ks[kv.Key] = kv.Value; }

            var goneM = new List<string>();
            foreach (var n in h.Mb.Keys) if (!wantM.ContainsKey(n)) goneM.Add(n);
            foreach (var n in goneM) { W32.Button(0, MbFlags(n)[1], "up"); h.Mb.Remove(n); }
            foreach (var n in wantM.Keys)
                if (!h.Mb.ContainsKey(n)) { W32.Button(MbFlags(n)[0], 0, "down"); h.Mb[n] = 0; }
        }

        if (dx != 0 || dy != 0) W32.MoveRelative(dx, dy);
        return "held:" + wantK.Count + "+" + wantM.Count;
    }

    // 手机主动收手（抬手、切后台、页面关掉）时立刻叫停，不用等看门狗那 900ms。
    public static void Release(string who)
    {
        Holder h;
        lock (Lk) { if (!All.TryGetValue(who, out h)) return; All.Remove(who); }
        ReleaseAll(h);
    }

    static void ReleaseAll(Holder h)
    {
        foreach (var kv in h.Ks) W32.Vk(kv.Value.Vk, kv.Value.Ext, true);
        foreach (var kv in h.Mb) W32.Button(0, MbFlags(kv.Key)[1], "up");
        h.Ks.Clear(); h.Mb.Clear();
    }

    public static int HeldCount()
    {
        lock (Lk) { int n = 0; foreach (var h in All.Values) n += h.Ks.Count + h.Mb.Count; return n; }
    }

    // 看门狗：心跳断了、或者锁屏了（这时注入会被系统丢掉，键状态就成了假的"以为还按着"），
    // 一律立刻松干净。
    public static void Sweep()
    {
        int now = Environment.TickCount;
        bool allow = Program.InputAllowed();
        List<KeyValuePair<string, Holder>> dead = null;
        lock (Lk)
        {
            foreach (var kv in All)
            {
                // wrap 安全的差值：过期的判断只看"还没到点"是不是负数
                if (!allow || (int)(kv.Value.Next - now) <= 0)
                {
                    if (dead == null) dead = new List<KeyValuePair<string, Holder>>();
                    dead.Add(kv);
                }
            }
            if (dead != null) foreach (var kv in dead) All.Remove(kv.Key);
        }
        if (dead == null) return;
        int n = 0;
        foreach (var kv in dead) { n += kv.Value.Ks.Count + kv.Value.Mb.Count; ReleaseAll(kv.Value); }
        if (n > 0) Program.Log("[手柄] 松开了 " + n + " 个按住的键（" + (allow ? "心跳断了" : "锁屏/拒绝输入") + "）");
    }
}

sealed class Sse
{
    public readonly Stream Stream;            // 明文是 NetworkStream，走了 TLS 就是 SslStream
    public readonly Guid Id;
    public volatile bool Dead;
    public string Dev = "";                   // 这条连接属于哪台设备（踢人的时候按它找，见 Devs）
    public Sse(Stream s) { Stream = s; Id = Guid.NewGuid(); }
}

static class Program
{
    static readonly List<Sse> Clients = new List<Sse>();
    static readonly object ClientsLock = new object();
    static string Token = "";
    static int Port = 8765;
    static string WwwDir = "";
    static string DataDir = "";
    static long Started = 0;
    static volatile bool Awake = true;
    internal const string Version = "bedremote 0.13.0";

    static void Help()
    {
        Log(Version);
        Log("用法: bedremote.exe [--port=8765] [--token=xxxx] [--data=目录] [--help] [--version]");
        Log("配置文件: 程序同目录下的 bedremote.json（端口、令牌、面板按钮都在里面）");
        Log("数据目录: 默认 %LOCALAPPDATA%\\bed-remote（证书/配对名单/通行证），--data= 可以指到别处");
        Log("口令/踢设备: 双击 exe 进界面，「门禁」那一栏（改口令当场生效，不用重启）");
        Log("面板编辑器: 浏览器打开 http://127.0.0.1:<端口>/edit");
        Log("按屏控制: /cmd?c=mons 列屏；c=blank&mt=<屏id>[&sec=20][&m=ddc] 让某块屏无信号；c=wake&mt=<屏id> 接回来");
    }

    static string LastWin = "\u0000";

    // ---------- 给界面用的外壳 ----------
    static TcpListener Listener;
    static volatile bool Serving;
    static int StartErr;                 // 3 = 端口被占
    static volatile bool WatchStarted;
    static volatile bool MatesStarted;
    static volatile bool Relaunching;            // 界面重启监听中：绑定失败先重试，别急着报"端口被占"
    static readonly object SinksLock = new object();
    static readonly List<Action<string>> Sinks = new List<Action<string>>();
    static System.Security.Cryptography.X509Certificates.X509Certificate2 Cert;   // null = 只跑明文
    internal static bool HttpsOn { get { return Cert != null; } }
    // 换了网卡 / DHCP 重新分了 IP：SAN 里得有新地址，否则手机直接报错连"继续访问"都没有。
    internal static void RefreshCert()
    {
        if (!Config.Https || !Serving) return;
        try { Cert = Tls.Ensure(DataDir, LocalIPv4(), Log, Config.Ca); } catch { }
    }

    // 所有输出统一走这里：控制台照打，同时推给界面。图形模式下没有控制台窗口，
    // 日志只活在界面里 —— "谁把屏幕关了"这种事必须看得见，今晚就是靠日志才定位到的。
    internal static void Log(string s)
    {
        try { Console.Out.WriteLine(s); } catch { }
        Action<string>[] arr = null;
        lock (SinksLock) { if (Sinks.Count > 0) arr = Sinks.ToArray(); }
        if (arr != null)
            for (int i = 0; i < arr.Length; i++) { try { arr[i](s); } catch { } }
    }
    internal static void AddSink(Action<string> f) { lock (SinksLock) Sinks.Add(f); }
    internal static bool ServingNow { get { return Serving; } }
    internal static int PortNow { get { return Port; } }
    internal static string TokenNow { get { return Token; } }
    internal static int StartError { get { return StartErr; } }
    internal static string[] IpsNow() { return LocalIPv4().ToArray(); }
    internal static bool AwakeOn { get { return Awake; } }

    internal static void SetAwake(bool on)
    {
        Awake = on;
        try { W32.KeepAwake(on); } catch { }
    }

    // 界面上"正在控制这台电脑的设备"只列别人：本机自己的浏览器不算外部设备
    internal static List<Peer> ExternalPeers()
    {
        lock (Peers)
        {
            var r = new List<Peer>();
            foreach (var kv in Peers) if (!IsMine(kv.Key)) r.Add(kv.Value);
            r.Sort(delegate(Peer a, Peer b) { return b.Last.CompareTo(a.Last); });
            return r;
        }
    }

    internal static bool StartHidden;          // --tray：界面直接缩到托盘（开机自启用）
    internal static bool InGui;                // 服务跑在界面进程里：日志少刷屏、提示别说"关这个窗口"
    internal static bool OpenWizard;           // --wizard：窗口一起来就把"加软件"向导摊开（也方便脚本验收）

    static int Main(string[] args)
    {
        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        Config.Load(exeDir);

        // 双击 = 图形界面（一个窗口 + 托盘）。--console / --service 才是原来的无界面服务模式。
        // --port= 与 --token= 只改参数，不切模式：界面也能跑在别的端口上（测试、多实例）。
        int cliPort = 0; string cliToken = null; string cliData = null; bool gui = true;
        foreach (var a in args)
        {
            if (a.StartsWith("--port=")) { int.TryParse(a.Substring(7), out cliPort); }
            else if (a.StartsWith("--token=")) { cliToken = a.Substring(8); }
            else if (a.StartsWith("--data=")) { cliData = a.Substring(7); }
            else if (a == "--help" || a == "-h") { Help(); return 0; }
            else if (a == "--version" || a == "-v") { Log(Version); return 0; }
            else if (a == "--console" || a == "--no-gui" || a == "--service") gui = false;
            else if (a == "--gui") gui = true;
            else if (a == "--tray") { gui = true; StartHidden = true; }
            else if (a == "--wizard") { gui = true; OpenWizard = true; }
            else gui = false;                       // 不认识的一律按命令行处理，别悄悄进界面
        }
        Port = Config.Port;
        Token = Config.Token ?? "";
        if (cliPort > 0) Port = cliPort;
        if (cliToken != null) Token = cliToken;
        Awake = Config.KeepAwake;
        Started = DateTime.Now.Ticks;

        WwwDir = Path.Combine(exeDir, "www");
        // 数据目录（证书、配对名单、通行证、最后一次回复）默认跟着当前用户。
        // --data= 是给测试和"想插 U 盘随身带"的人留的口子：不指回来就用默认，什么都不变。
        DataDir = string.IsNullOrEmpty(cliData)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "bed-remote")
            : Path.GetFullPath(cliData);
        try { Directory.CreateDirectory(DataDir); } catch { }
        Devs.Init(DataDir);             // 已配对的设备名单：界面一开就要看得见，所以在进界面之前读

        if (gui && Environment.UserInteractive) return Gui.Run();
        return Serve();
    }

    static int Serve()
    {
        var ips = LocalIPv4();
        string suffix = Token.Length > 0 ? "/?t=" + Token : "";
        Cert = Config.Https ? Tls.Ensure(DataDir, ips, Log, Config.Ca) : null;   // null 就退回纯明文，服务照跑

        var listener = new TcpListener(IPAddress.Any, Port);
        // 界面里改了 HTTPS/端口会走 Restart()：旧监听线程可能还没从 Accept 里退干净，
        // 端口就还捏在操作系统手里。这种"被占"是几百毫秒的事，所以重启时重试绑定，
        // 而不是立刻判定"有别的实例在跑"（那会让界面显示一个假的状态）。
        SocketException bindErr = null;
        int tries = Relaunching ? 16 : 1;
        for (int attempt = 0; attempt < tries; attempt++)
        {
            try { listener.Start(); bindErr = null; break; }
            catch (SocketException ex)
            {
                bindErr = ex;
                listener = new TcpListener(IPAddress.Any, Port);
                Thread.Sleep(150);
            }
        }
        Relaunching = false;
        if (bindErr != null)
        {
            // 最常见的场景：run.cmd 被点了第二次，端口已经被自己占着。
            // 这里绝不能抛出（未处理异常 → 窗口一闪就没了，只剩一句"提示异常"），
            // 而且他点这个脚本通常就是想要手机地址，顺手再打一遍。
            StartErr = 3;
            Log("");
            Log("  端口 " + Port + " 已被占用 —— bedremote 很可能已经在运行了。");
            Log("  ----------------------------------------------------------------");
            foreach (var ip in ips)
                Log("  手机直接打开:  http://" + ip + ":" + Port + suffix);
            Log("  ----------------------------------------------------------------");
            if (InGui)
                Log("  这个窗口没有自己的服务：手机连的是另一个实例。屏幕那几个按钮是本机直接调系统 API 的，照样生效。");
            else
            {
                Log("  想重启：先双击 stop.cmd，再双击 run.cmd。");
                Log("  手机不想输地址：双击 pair.cmd，屏幕出二维码，手机扫一下就直连。");
                Log("  端口是别的程序占的：bedremote.exe --port=8888");
            }
            return 3;
        }
        Listener = listener; Serving = true; StartErr = 0;

        if (InGui)
        {
            // 界面模式下这些字都在日志框里，地址栏窗口顶上已经写过了，别刷屏
            string sch = Cert != null ? "https" : "http";
            Log("服务已在端口 " + Port + " 起来。" + (Token.Length > 0 ? "已设令牌。" : "未设令牌：局域网里任何设备打开地址就能控制鼠标键盘。"));
            foreach (var ip in ips) Log("  手机地址:  " + sch + "://" + ip + ":" + Port + suffix);
            if (Cert != null)
                Log("  HTTPS：" + Tls.Status + "。第一次访问手机会警告\"不受信任\"，安卓 Chrome 点 高级 → 继续访问就行；" +
                    "陀螺仪、读剪贴板、屏幕常亮这几样必须有它才拿得到。");
            else if (Config.Https) Log("  HTTPS 没起来：" + Tls.Status + "（现在只有 http://，陀螺仪/剪贴板用不了）");
            if (Config.Error != null) Log("  [警告] bedremote.json 解析失败，已退回内置默认面板：" + Config.Error);
        }
        else
        {
        Log("");
        Log("  bedremote 已启动");
        Log("  ----------------------------------------------------------------");
        string csch = Cert != null ? "https" : "http";
        foreach (var ip in ips)
            Log("  手机浏览器打开:  " + csch + "://" + ip + ":" + Port + suffix);
        Log("  ----------------------------------------------------------------");
        if (Cert != null)
            Log("  同一端口也吃 http://。HTTPS 是自签的（" + Tls.Status + "），手机第一次会警告，点继续访问即可；" +
                "陀螺仪/剪贴板/屏幕常亮要 HTTPS 才有。");
        if (Token.Length > 0)
            Log("  令牌 t=" + Token + "（地址里必须带上，否则控制指令会被拒绝）");
        else
            Log("  未设令牌：局域网里任何设备打开上面地址就能控制你的鼠标键盘。");
        Log("  想加令牌： 界面「门禁」那栏点「随机」再点「应用」，当场生效不用重启");
        if (Devs.Count() > 0)
            Log("  已配对设备 " + Devs.Count() + " 台（带过正确口令的就自动记下了，界面上能一台一台踢掉）");
        Log("  改按钮：  电脑浏览器打开 http://127.0.0.1:" + Port + "/edit");
        Log("  本机推送文字到手机:  curl \"http://127.0.0.1:" + Port + "/notify?text=任务跑完了\"");
        Log("  问一句并等手机回答:  curl \"http://127.0.0.1:" + Port + "/ask?text=要现在下载吗\"");
        Log("  手机不想输地址:  双击 pair.cmd（或本机开 " + "http://127.0.0.1:" + Port + "/pair）出二维码，手机扫一下就直连");
        if (Config.Error != null)
            Log("  [警告] bedremote.json 解析失败，已退回内置默认面板：" + Config.Error);
        else
            Log("  配置已加载：" + Config.Path + "（run 白名单 " + Config.Run.Count + " 条）");
        Log("  停止: 直接关这个窗口");
        Log("");
        }

        if (!WatchStarted)
        {
            WatchStarted = true;
            ThreadPool.QueueUserWorkItem(_ => WatchWindow());
            ThreadPool.QueueUserWorkItem(_ => HoldWatch());
        }

        // 发现用的 UDP 听点跟服务端口无关，重启监听也不用停它；名字/端口/屏数都是现取，
        // 所以改了机器名之后下一次心跳就带上去了。
        if (!MatesStarted)
        {
            MatesStarted = true;
            Mates.Start(delegate { return Config.NameOrMachine(); },
                        delegate { return Port; },
                        delegate { try { return W32.Monitors().Count; } catch { return 0; } },
                        delegate { return Version; },
                        delegate { return HttpsOn; });
        }

        while (true)
        {
            TcpClient c;
            try { c = listener.AcceptTcpClient(); } catch { break; }
            ThreadPool.QueueUserWorkItem(_ => Handle(c, Cert));
        }
        Serving = false;
        Listener = null;
        return 0;
    }

    internal static void StopServer()
    {
        Serving = false;
        var l = Listener;
        if (l != null) { try { l.Stop(); } catch { } }
    }

    internal static void StartInBackground()
    {
        Thread t = new Thread(delegate() { Serve(); });
        t.IsBackground = true;
        t.Start();
    }

    // 界面改了"启动时才读"的开关（HTTPS）之后重新起一次监听。
    // 已经连上的 SSE 不会被踢 —— listener.Stop() 只关监听套接字，旧连接的线程还活着照常广播，
    // 所以手机那边根本感觉不到（真断了 EventSource 也会自己重连）。
    internal static void Restart()
    {
        Relaunching = true;
        StopServer();
        StartInBackground();
    }

    // 向导写完配置后调这个：run 白名单和手机面板都是"每次查/每次推"的，重载就行，不用重启程序。
    // 端口和令牌不行（监听已经起来了），所以重载时把它们按回原值，避免出现"配置说改了但其实没生效"。
    internal static string ReloadConfig()
    {
        try
        {
            int oldPort = Port; string oldToken = Token;
            Config.Load(AppDomain.CurrentDomain.BaseDirectory);
            Port = oldPort; Token = oldToken;
            Awake = Config.KeepAwake;
            Broadcast("{\"e\":\"panel\"}");
            string s = "配置已重新加载：run 白名单 " + Config.Run.Count + " 条，通行证点名 " + Config.ElevatedRun.Count + " 条";
            Log("[reload] " + s + (Config.Error != null ? "（注意：配置文件解析有问题，已退回内置默认：" + Config.Error + "）" : ""));
            return s;
        }
        catch (Exception ex) { return "重载失败：" + ex.Message; }
    }

    // 端口被占时"是谁占着"：只认进程名以 bedremote 开头的，别的程序一律不报（免得界面去杀无辜进程）
    internal static int PortOccupier(int port)
    {
        try
        {
            var psi = new ProcessStartInfo("netstat.exe", "-ano -p tcp")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using (var pr = Process.Start(psi))
            {
                string s = pr.StandardOutput.ReadToEnd();
                try { pr.WaitForExit(2500); } catch { }
                string tail = ":" + port;
                foreach (var raw in s.Split('\n'))
                {
                    var parts = raw.Trim().Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 5) continue;
                    if (parts[0] != "TCP") continue;
                    if (parts[3].IndexOf("LISTENING", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (!parts[1].EndsWith(tail)) continue;
                    int pid;
                    if (!int.TryParse(parts[parts.Length - 1], out pid) || pid <= 0) continue;
                    if (pid == Process.GetCurrentProcess().Id) continue;
                    string name;
                    try { name = Process.GetProcessById(pid).ProcessName; } catch { continue; }
                    if (name == null || !name.StartsWith("bedremote", StringComparison.OrdinalIgnoreCase)) continue;
                    return pid;
                }
            }
        }
        catch { }
        return 0;
    }

    // 界面上的"接管"：把上一个还在跑的旧实例（多半是控制台版）关掉，自己重新占住端口。
    // 只杀进程名是 bedremote* 的，且必须他点了按钮才动手。
    internal static bool Takeover(out string how)
    {
        how = "";
        int pid = PortOccupier(Port);
        if (pid == 0) { how = "没找到占着端口 " + Port + " 的 bedremote 进程（占着的可能是别的程序），什么都没动"; return false; }
        try
        {
            var p = Process.GetProcessById(pid);
            string name = p.ProcessName;      // 名字要在关掉之前取：进程一退出，再读 ProcessName 会抛"进程已退出"
            p.Kill();
            try { p.WaitForExit(3000); } catch { }
            how = "已关掉旧实例 pid " + pid + "（" + name + "）";
        }
        catch (InvalidOperationException) { how = "pid " + pid + " 自己已经退了"; }
        catch (Exception ex) { how = "关不掉 pid " + pid + "：" + ex.Message; return false; }
        StartErr = 0;
        StartInBackground();
        return true;
    }

    // 给配对页用：局域网地址、端口、令牌、当前有几个页面连着。
    // 手机"屏幕"页读这个：物理上插着的每块屏，含当前无信号的那些（好让他能一键接回来）
    static string MonsJson()
    {
        var sb = new StringBuilder();
        sb.Append("{\"e\":\"mons\",\"ddcOff\":").Append(Config.AllowDdcOff ? "true" : "false")
          .Append(",\"autoSec\":").Append(Config.AutoWakeSec).Append(",\"mons\":[");
        var list = Disp.All(true);
        for (int i = 0; i < list.Count; i++)
        {
            var m = list[i];
            if (i > 0) sb.Append(',');
            string raw = string.IsNullOrEmpty(m.Name) ? m.Dev : m.Name;
            sb.Append("{\"uid\":").Append(m.Uid)
              .Append(",\"name\":\"").Append(Json(Config.ScreenName(m.Uid.ToString(), raw))).Append("\"")
              .Append(",\"raw\":\"").Append(Json(raw)).Append("\"")
              .Append(",\"dev\":\"").Append(Json(m.Dev)).Append("\"")
              .Append(",\"act\":").Append(m.Active ? "true" : "false")
              .Append(",\"clone\":").Append(m.Clone ? "true" : "false")
              .Append(",\"i\":").Append(m.Index)
              .Append(",\"prim\":").Append(m.Primary ? "true" : "false")
              .Append(",\"x\":").Append(m.Left).Append(",\"y\":").Append(m.Top)
              .Append(",\"w\":").Append(m.Width).Append(",\"h\":").Append(m.Height)
              .Append(",\"tech\":\"").Append(Json(m.Active ? Disp.TechName(m.Tech) : "")).Append("\"")
              .Append(",\"hz\":\"").Append(Json(m.Active ? m.Hz : "")).Append("\"")
              .Append(",\"ddc\":").Append(Disp.DdcSupported(m) ? "true" : "false")
              .Append("}");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    static readonly List<Timer> WakeTimers = new List<Timer>();

    // "无信号"兜底：N 秒后自动接回来。免得出门/躺下之后某块屏黑着没人管。
    static void ScheduleWake(uint tid, int sec)
    {
        Timer t = null;
        t = new Timer(delegate(object o)
        {
            try
            {
                string how;
                bool ok = Disp.SetActive(tid, true, out how);
                Log("[auto-wake] tid=" + tid + " " + (ok ? "已接回 " : "接回失败 ") + how);
                Broadcast("{\"e\":\"mons\"}");
            }
            catch { }
            finally
            {
                try { t.Dispose(); } catch { }
                lock (WakeTimers) WakeTimers.Remove(t);
            }
        }, null, sec * 1000, Timeout.Infinite);
        lock (WakeTimers) WakeTimers.Add(t);
    }

    // DDC 熄灭之后的兜底：先 DDC 叫醒，面板不应答就软件重插线。人不用起身。
    static void ScheduleDdcRecover(uint tid, int sec)
    {
        Timer t = null;
        t = new Timer(delegate(object o)
        {
            try
            {
                string info;
                Disp.DdcPower(tid, true, out info);
                Thread.Sleep(900);
                if (!Disp.DdcAlive(Disp.Find(tid)))
                {
                    string rh;
                    bool ok = Disp.Rescue(tid, out rh);
                    Log("[ddc-auto] tid=" + tid + " 面板不应答，重插线 " + (ok ? "成功 " : "失败 ") + rh);
                }
                else Log("[ddc-auto] tid=" + tid + " DDC 已叫醒");
                Broadcast("{\"e\":\"mons\"}");
            }
            catch { }
            finally
            {
                try { t.Dispose(); } catch { }
                lock (WakeTimers) WakeTimers.Remove(t);
            }
        }, null, sec * 1000, Timeout.Infinite);
        lock (WakeTimers) WakeTimers.Add(t);
    }

    // ---------- 谁在控制这台电脑 ----------
    // 没设令牌时局域网里任何设备都能动你的鼠标键盘，所以至少要让电脑上看得见"现在有几台、是哪几台、刚才在干什么"。
    internal class Peer { public string Ip = ""; public long First = 0; public long Last = 0; public int Cmds = 0; public string LastCmd = ""; public int Sse = 0; }
    static readonly Dictionary<string, Peer> Peers = new Dictionary<string, Peer>();
    static string[] _myIps = null;

    static bool IsMine(string ip)
    {
        if (ip == null || ip.Length == 0 || ip == "127.0.0.1" || ip == "::1") return true;
        if (_myIps == null) _myIps = LocalIPv4().ToArray();
        // 本机浏览器用自己那个局域网 IP 访问，也不算"外部设备"
        for (int i = 0; i < _myIps.Length; i++) if (_myIps[i] == ip) return true;
        return false;
    }

    static int ExternalCount()   // 调用方需持有 Peers 锁
    {
        int n = 0;
        foreach (var kv in Peers) if (!IsMine(kv.Key)) n++;
        return n;
    }

    // 收到一条控制指令就记下这台设备。只有第一次见到它才打印+推送，
    // 免得触控板连发 move 的时候把 SSE 刷屏。
    static void Touch(string ip, string cmd)
    {
        if (string.IsNullOrEmpty(ip)) return;
        bool fresh; int total = 0;
        lock (Peers)
        {
            Peer p;
            fresh = !Peers.TryGetValue(ip, out p);
            if (fresh) { p = new Peer(); p.Ip = ip; p.First = DateTime.Now.Ticks; Peers[ip] = p; }
            p.Last = DateTime.Now.Ticks;
            p.Cmds++;
            if (!string.IsNullOrEmpty(cmd)) p.LastCmd = cmd;
            total = ExternalCount();
        }
        if (fresh)
        {
            Log("[新设备] " + ip + " 开始控制这台电脑，外部设备共 " + total + " 台" +
                (Token.Length > 0 ? "（已设令牌）" : "（无令牌：它现在能动你的鼠标键盘）"));
            Broadcast(PeersJson());
        }
    }

    // SSE 连着 = 那个页面还开着。断开也推一次，让电脑/手机上的数字立刻变。
    static void NoteSse(string ip, int delta)
    {
        if (string.IsNullOrEmpty(ip)) return;
        lock (Peers)
        {
            Peer p;
            if (!Peers.TryGetValue(ip, out p))
            {
                p = new Peer(); p.Ip = ip; p.First = DateTime.Now.Ticks;
                p.Last = p.First;                      // 不写这行的话 Last=0，ago 会算出负数（int 溢出）
                Peers[ip] = p;
                if (!IsMine(ip))
                    Log("[新设备] " + ip + " 打开了页面，外部设备共 " + ExternalCount() + " 台" +
                        (Token.Length > 0 ? "（已设令牌）" : "（无令牌：它现在能动你的鼠标键盘）"));
            }
            p.Sse += delta;
            if (p.Sse < 0) p.Sse = 0;
        }
        Broadcast(PeersJson());
    }

    static string PeersListJson()
    {
        List<Peer> arr;
        lock (Peers) arr = new List<Peer>(Peers.Values);
        arr.Sort(delegate (Peer a, Peer b) { return b.Last.CompareTo(a.Last); });   // 最近动过的排最前
        var sb = new StringBuilder();
        sb.Append('[');
        for (int i = 0; i < arr.Count; i++)
        {
            var p = arr[i];
            if (i > 0) sb.Append(',');
            long ago = (DateTime.Now.Ticks - p.Last) / TimeSpan.TicksPerSecond;
            long up = (DateTime.Now.Ticks - p.First) / TimeSpan.TicksPerSecond;
            if (ago < 0) ago = 0; if (ago > 31536000) ago = 31536000;      // 别把溢出/负数甩到界面上
            if (up < 0) up = 0; if (up > 31536000) up = 31536000;
            sb.Append("{\"ip\":\"").Append(Json(p.Ip)).Append("\"")
              .Append(",\"ago\":").Append(ago)
              .Append(",\"secs\":").Append(up)
              .Append(",\"cmds\":").Append(p.Cmds)
              .Append(",\"last\":\"").Append(Json(p.LastCmd)).Append("\"")
              .Append(",\"live\":").Append(p.Sse > 0 ? "true" : "false")
              .Append(",\"self\":").Append(IsMine(p.Ip) ? "true" : "false")
              .Append('}');
        }
        sb.Append(']');
        return sb.ToString();
    }

    static string PeersJson()
    {
        lock (Peers) return "{\"e\":\"peers\",\"n\":" + ExternalCount() + ",\"list\":" + PeersListJson() + "}";
    }

    static string AddrJson()
    {
        int online = 0;
        lock (ClientsLock) { online = Clients.Count; }
        var sb = new StringBuilder();
        sb.Append("{\"port\":").Append(Port)
          .Append(",\"name\":\"").Append(Json(Config.NameOrMachine())).Append("\"")
          .Append(",\"token\":\"").Append(Json(Token ?? "")).Append("\"")
          .Append(",\"online\":").Append(online)
          .Append(",\"devs\":").Append(ExternalCount())
          .Append(",\"autoSec\":").Append(Config.AutoWakeSec)
          .Append(",\"ddcOff\":").Append(Config.AllowDdcOff ? "true" : "false")
          .Append(",\"https\":").Append(Cert != null ? "true" : "false")
          .Append(",\"peers\":").Append(PeersListJson())
          .Append(",\"ips\":[");
        var ips = LocalIPv4();
        for (int i = 0; i < ips.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('"').Append(Json(ips[i])).Append('"');
        }
        sb.Append("]}");
        return sb.ToString();
    }

    static string HelloJson()
    {
        var sb = new StringBuilder();
        sb.Append("{\"e\":\"hello\",\"mon\":[");
        var mons = W32.Monitors();
        for (int i = 0; i < mons.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"i\":").Append(i).Append(",\"l\":").Append(mons[i].Left).Append(",\"t\":").Append(mons[i].Top)
              .Append(",\"w\":").Append(mons[i].Right - mons[i].Left).Append(",\"h\":").Append(mons[i].Bottom - mons[i].Top)
              .Append(",\"p\":").Append(mons[i].Primary ? 1 : 0).Append(",\"d\":\"").Append(Json(mons[i].Device)).Append("\"}");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    static string LastLockWhy = "";
    static void WatchWindow()
    {
        while (true)
        {
            try
            {
                string t = W32.ForegroundTitle();
                if (t != LastWin)
                {
                    LastWin = t;
                    Broadcast("{\"e\":\"win\",\"t\":\"" + Json(t) + "\"}");
                }
                // UAC 弹出来的那一刻就推到手机上，不用等他试着点一下才发现"点不动"；
                // 点掉了也推一下，手机那条红条才不会赖着不走。
                string w = W32.LockWhy();
                if (w != LastLockWhy)
                {
                    LastLockWhy = w;
                    if (w.Length > 0) { LastLockedNote = 0; NoteLocked(); }
                    else Broadcast("{\"e\":\"unlock\"}");
                }
                Mates.Tick();       // 心跳到点就往外喊一声（自己排期，没到点什么都不做）
            }
            catch { }
            Thread.Sleep(700);
        }
    }

    // 手柄看门狗：只有真的有人按着键时才干活，所以平时一圈就是查一下表长没长，
    // 120ms 一次也够把"手机没了 → 键卡住"的窗口压在心跳窗口之内。
    static void HoldWatch()
    {
        int tick = 0;
        while (true)
        {
            try { if (Held.HeldCount() > 0) Held.Sweep(); } catch { }
            // 设备名单每来一条请求就更新"最近在线"，攒几秒再落一次盘（写文件不该跟着鼠标跑）
            if (++tick >= 42) { tick = 0; try { Devs.Tick(); } catch { } }
            Thread.Sleep(120);
        }
    }

    // ---------- 多机 ----------
    // 广播被挡（跨网段、AP 隔离、有些交换机干脆不转 255.255.255.255）时的兜底：
    // 往本机所在 /24 的每个地址**单播**发一次"你是谁"，等一秒看谁答。
    // 比 TCP 扫端口准（只有 bedremote 会回这个包，别的东西端口开着也不该被它写配置），也不会弹防火墙框。
    internal static string MatesScan()
    {
        int n = 0;
        foreach (var baseIp in LocalIPv4())
        {
            int dot = baseIp.LastIndexOf('.');
            if (dot < 0) continue;
            string pre = baseIp.Substring(0, dot + 1);
            for (int i = 1; i <= 254; i++)
            {
                string ip = pre + i;
                if (ip == baseIp) continue;
                try { Mates.Poke(new IPEndPoint(IPAddress.Parse(ip), Mates.DPort)); n++; } catch { }
            }
        }
        Thread.Sleep(1300);
        Log("[发现] 单播问了 " + n + " 个地址，名单上现在有 " + (Mates.Count() + 1) + " 台");
        return Mates.ListJson();
    }

    // 「摆一次，推给多台」：把我们这份 panels POST 给同伴的 /panel/save。
    // 目标必须是发现名单里的地址（IsKnown），不能是调用方随手给的一个 URL ——
    // 否则这条命令就成了"拿本机令牌去写陌生服务器"。写的是对方的 panels 一个键，别的配置不动。
    static string PushPanels(string to, string from)
    {
        if (string.IsNullOrEmpty(to)) return "err:没给目标";
        string body = Config.PanelsJson();
        var done = new List<string>();
        int n = 0;
        foreach (var raw in to.Split(','))
        {
            string t = raw.Trim();
            if (t.Length == 0) continue;
            if (t == "*" || t.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var j in Mates.ListJson().Split('{'))
                {
                    int e = j.IndexOf('}');
                    if (e <= 0) continue;
                    string seg = j.Substring(0, e);
                    string ip = FieldIn(seg, "ip"), port = FieldIn(seg, "port");
                    if (ip.Length == 0 || port.Length == 0) continue;
                    if (FieldIn(seg, "self") == "true") continue;
                    done.Add(ip + ":" + port);
                }
                continue;
            }
            done.Add(t);
        }
        if (done.Count > 32) done = done.GetRange(0, 32);
        var sb = new StringBuilder();
        foreach (var t in done)
        {
            int colon = t.LastIndexOf(':');
            string ip = colon > 0 ? t.Substring(0, colon) : t;
            int port = Port;
            if (colon > 0) int.TryParse(t.Substring(colon + 1), out port);
            if (port <= 0) port = Port;
            if (!Mates.IsKnown(ip, port)) { sb.Append(t).Append("=不在发现名单里;"); continue; }
            string res = PushOne(ip, port, body);
            sb.Append(t).Append('=').Append(res).Append(';');
            Log("[推面板] " + ip + ":" + port + " → " + res + "  <- " + (from ?? "?"));
            n++;
        }
        return n == 0 ? "err:没有可推的目标（先 c=scan 找一找）" : "pushed:" + sb;
    }

    static string FieldIn(string seg, string key)
    {
        string k = "\"" + key + "\":";
        int i = seg.IndexOf(k, StringComparison.Ordinal);
        if (i < 0) return "";
        i += k.Length;
        bool q = seg[i] == '"';
        if (q) i++;
        int j = i;
        while (j < seg.Length && (q ? seg[j] != '"' : (char.IsDigit(seg[j]) || seg[j] == '-' || seg[j] == 't' || seg[j] == 'r' || seg[j] == 'u' || seg[j] == 'e'))) j++;
        return seg.Substring(i, j - i);
    }

    static string PushOne(string ip, int port, string panelsJson)
    {
        try
        {
            // 走 http：我们的服务在同一端口上 peek 首字节分流，明文一样能收 ——
            // 这样就不用在 outbound 上处理自签证书（也不去动全局的证书校验）。
            string url = "http://" + ip + ":" + port + "/panel/save" + (Token.Length > 0 ? "?t=" + Uri.EscapeDataString(Token) : "");
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/json; charset=utf-8";
            req.Timeout = 1800;
            req.ReadWriteTimeout = 1800;
            byte[] b = Encoding.UTF8.GetBytes(panelsJson);
            req.ContentLength = b.Length;
            using (var st = req.GetRequestStream()) st.Write(b, 0, b.Length);
            using (var rp = req.GetResponse())
            using (var rd = new StreamReader(rp.GetResponseStream(), Encoding.UTF8))
            {
                string txt = rd.ReadToEnd();
                return txt.IndexOf("\"ok\":true") >= 0 ? "ok" : "resp:" + txt;
            }
        }
        catch (WebException ex)
        {
            try { if (ex.Response != null) ex.Response.Close(); } catch { }
            return "fail:" + ex.Status;
        }
        catch (Exception ex) { return "fail:" + ex.Message; }
    }

    static string RandomToken()
    {
        const string abc = "abcdefghjkmnpqrstuvwxyz23456789";
        var rnd = new Random();
        var sb = new StringBuilder();
        for (int i = 0; i < 4; i++) sb.Append(abc[rnd.Next(abc.Length)]);
        return sb.ToString();
    }

    internal static List<string> LocalIPv4()
    {
        var outList = new List<string>();
        try
        {
            foreach (var a in System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName()).AddressList)
                if (a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)) outList.Add(a.ToString());
        }
        catch { }
        if (outList.Count == 0) outList.Add("127.0.0.1");
        return outList;
    }

    // ---------- HTTP ----------

    internal class Req
    {
        public string Method, Path, Query, Body;
        public Dictionary<string, string> Form = new Dictionary<string, string>();
        public string Remote;
        public string Range;      // 只有音频/视频那条路用得到（拖进度条、跳秒都要 206）
    }

    static void Handle(TcpClient c, System.Security.Cryptography.X509Certificates.X509Certificate2 cert)
    {
        try
        {
            c.NoDelay = true;
            // 同一个端口：peek 一个字节决定这条是 https 还是 http。握手失败就默默关掉，
            // 不上报 —— 端口扫描器和探测请求也走这条路，报了对人没用。
            var s = Tls.Upgrade(c, cert);
            if (s == null) { try { c.Close(); } catch { } return; }
            var br = new BufferedStream(s, 8192);

            string line = ReadLine(br);
            if (line == null) { c.Close(); return; }
            var parts = line.Split(' ');
            var r = new Req();
            r.Method = parts[0];
            string raw = parts.Length > 1 ? parts[1] : "/";
            int q = raw.IndexOf('?');
            r.Path = q < 0 ? raw : raw.Substring(0, q);
            r.Query = q < 0 ? "" : raw.Substring(q + 1);
            try { r.Remote = ((IPEndPoint)c.Client.RemoteEndPoint).Address.ToString(); } catch { }

            int len = 0;
            while (true)
            {
                string h = ReadLine(br);
                if (string.IsNullOrEmpty(h)) break;
                int colon = h.IndexOf(':');
                if (colon > 0 && h.Substring(0, colon).Trim().ToLowerInvariant() == "content-length")
                    int.TryParse(h.Substring(colon + 1).Trim(), out len);
                // 播放器的 Range 头必须留：不支持 206 的话，手机上进度条拖不动、
                // 电脑端被命令"跳到 12.382 秒"也跳不过去（浏览器会重新要整个文件）
                if (colon > 0 && h.Substring(0, colon).Trim().ToLowerInvariant() == "range")
                    r.Range = h.Substring(colon + 1).Trim();
            }
            if (len > 0)
            {
                if (len > 4 * 1024 * 1024) { c.Close(); return; }   // 防呆：面板文件不该超过 4MB
                byte[] body = new byte[len];
                int got = 0;
                while (got < len) { int n = br.Read(body, got, len - got); if (n <= 0) break; got += n; }
                r.Body = Encoding.UTF8.GetString(body, 0, got);
                ParseForm(r.Body, r.Form);
            }
            foreach (var kv in r.Query.Split('&'))
            {
                if (kv.Length == 0) continue;
                int eq = kv.IndexOf('=');
                string k = eq < 0 ? kv : kv.Substring(0, eq);
                string v = eq < 0 ? "" : kv.Substring(eq + 1);
                k = Uri.UnescapeDataString(k);
                v = Uri.UnescapeDataString(v.Replace('+', ' '));
                if (!r.Form.ContainsKey(k)) r.Form[k] = v;
                r.Form["q_" + k] = v;
            }

            if (r.Path == "/favicon.ico") { Write(s, 204, "text/plain", new byte[0], r); c.Close(); return; }

            if (r.Path == "/notify")
            {
                string txt; r.Form.TryGetValue("q_text", out txt); if (txt == null) txt = "";
                string tk; r.Form.TryGetValue("q_t", out tk);
                bool local = r.Remote == "127.0.0.1" || r.Remote == "::1" || r.Remote == "";
                if (local || tk == Token)
                {
                    Broadcast("{\"e\":\"msg\",\"t\":\"" + Json(txt) + "\"}");
                    Log("[notify] " + txt);
                    Write(s, 200, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ok"), r);
                }
                else Write(s, 403, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("403\n"), r);
                c.Close(); return;
            }

            if (r.Path == "/ask")
            {
                string txt; r.Form.TryGetValue("text", out txt); if (txt == null) txt = "";
                string tk; r.Form.TryGetValue("t", out tk);
                bool local2 = r.Remote == "127.0.0.1" || r.Remote == "::1" || r.Remote == "";
                if (!(local2 || tk == Token)) { Write(s, 403, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("403\n"), r); c.Close(); return; }
                int to; if (!int.TryParse(G(r, "timeout", "120"), out to) || to < 1) to = 120;
                if (to > 3600) to = 3600;
                Write(s, 200, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(WaitReply(txt, to)), r);
                c.Close(); return;
            }

            // 静态资源：只允许 www\vendor\ 下面的 js/css，挡掉 .. 防目录穿越。
            if (r.Path.StartsWith("/vendor/"))
            {
                string rel = r.Path.Substring(1);
                if (rel.Contains(".."))
                { Write(s, 400, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("400\n"), r); c.Close(); return; }
                string fp = Path.Combine(WwwDir, rel.Replace('/', '\\'));
                if (File.Exists(fp))
                {
                    string ct = fp.EndsWith(".js") ? "application/javascript; charset=utf-8"
                              : fp.EndsWith(".css") ? "text/css; charset=utf-8" : "application/octet-stream";
                    Write(s, 200, ct, File.ReadAllBytes(fp), r);
                }
                else Write(s, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("404 " + rel + "\n"), r);
                c.Close(); return;
            }

            // PWA / 安卓分享面板 / 根证书下载。
            // 这几个都得能被浏览器直接拿到（注册 SW、取图标、装 CA 的时候手机还没信任这个地址），
            // 所以只有 manifest 和 /share 在设了令牌时要验一下 —— 里面本来就没有秘密。
            if (r.Path == "/sw.js" || r.Path == "/icon-192.png" || r.Path == "/icon-512.png")
            {
                string fp = Path.Combine(WwwDir, r.Path.Substring(1).Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(fp)) { Write(s, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("404\n"), r); c.Close(); return; }
                string ct = r.Path.EndsWith(".js") ? "application/javascript; charset=utf-8" : "image/png";
                Write(s, 200, ct, File.ReadAllBytes(fp), r);
                c.Close(); return;
            }
            if (r.Path == "/ca.crt" || r.Path == "/ca.pem")
            {
                byte[] pem = Tls.CaPem(DataDir);
                if (pem == null)
                {
                    Write(s, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(
                        "现在是自签模式，没有根证书。想装成 App / 用安卓分享面板，就把 bedremote.json 里的 \"ca\" 改成 true 再重启。\n"), r);
                    c.Close(); return;
                }
                Write(s, 200, "application/x-x509-ca-cert", pem, r);
                c.Close(); return;
            }
            if (r.Path == "/manifest.webmanifest")
            {
                bool okM = Authed(r) || r.Remote == "127.0.0.1" || r.Remote == "::1" || r.Remote == "";
                if (!okM) { Write(s, 403, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("403 需要令牌\n"), r); c.Close(); return; }
                Write(s, 200, "application/manifest+json; charset=utf-8", Encoding.UTF8.GetBytes(ManifestJson()), r);
                c.Close(); return;
            }
            if (r.Path == "/share")
            {
                bool okS = Authed(r) || r.Remote == "127.0.0.1" || r.Remote == "::1" || r.Remote == "";
                if (!okS) { Write(s, 403, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("403 需要令牌\n"), r); c.Close(); return; }
                string u = PickSharedUrl(G(r, "url", ""), G(r, "text", ""), G(r, "title", ""));
                string what = u.Length == 0 ? "分享过来的三段文本里没找到 http 链接" : OpenUrl(u, r.Remote);
                Log("[share] " + what + "  <- " + r.Remote);
                Redirect(s, "/?shared=" + Uri.EscapeDataString(what.Length > 70 ? what.Substring(0, 70) : what)
                                  + (Token.Length > 0 ? "&t=" + Uri.EscapeDataString(Token) : ""));
                c.Close(); return;
            }

            // 配对二维码页 + 地址接口。没设令牌时局域网里谁都能看（这样电视浏览器
            // 也能打开 /pair，躺着扫大屏上的码）；一旦设了令牌就只有本机(127.0.0.1)能看，
            // 免得配对页把令牌白送给局域网里的陌生设备。
            if (r.Path == "/pair" || r.Path == "/addr")
            {
                bool local3 = r.Remote == "127.0.0.1" || r.Remote == "::1" || r.Remote == "";
                if (!Authed(r) && !local3)
                {
                    Write(s, 403, "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes("403 设了令牌：配对页只给本机浏览器看（或在地址后面带上 ?t=令牌）。在电脑上打开 http://127.0.0.1:" + Port + "/pair\n"), r);
                    c.Close(); return;
                }
                if (r.Path == "/addr")
                {
                    Write(s, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(AddrJson()), r);
                    c.Close(); return;
                }
                string pf = Path.Combine(WwwDir, "pair.html");
                if (File.Exists(pf)) Write(s, 200, "text/html; charset=utf-8", File.ReadAllBytes(pf), r);
                else Write(s, 500, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("找不到 www\\pair.html"), r);
                c.Close(); return;
            }

            if (!Authed(r))
            {
                // 人手动打开的那几个页面要给一句人话：光秃秃一个"403 bad token"，
                // 人会以为程序坏了，其实只是口令改了/被踢了，要重新扫一次码。
                // 只管页面：/panel、/gamepad 这些是 fetch 拿 JSON 的，塞 HTML 进去会把调用方噎住。
                if (r.Path == "/" || r.Path == "/index.html" || r.Path == "/dj")
                {
                    Write(s, 403, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(ForbiddenHtml(r.Path)), r);
                    c.Close(); return;
                }
                byte[] b = Encoding.UTF8.GetBytes("403 bad token\n");
                Write(s, 403, "text/plain; charset=utf-8", b, r);
                c.Close(); return;
            }

            if (r.Path == "/events") { ServeSse(s, r); c.Close(); return; }

            switch (r.Path)
            {
                case "/":
                case "/index.html":
                    {
                        string f = Path.Combine(WwwDir, "phone.html");
                        if (File.Exists(f)) Write(s, 200, "text/html; charset=utf-8", File.ReadAllBytes(f), r);
                        else Write(s, 500, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("找不到 www\\phone.html"), r);
                        break;
                    }
                case "/health": Write(s, 200, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("ok"), r); break;
                case "/panel":
                    Write(s, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(Config.PanelsJson()), r);
                    break;
                case "/panel/save":
                    {
                        string err;
                        if (Config.SavePanels(r.Body ?? "{}", out err))
                        {
                            Broadcast("{\"e\":\"panel\"}");
                            Log("[panel] 已保存");
                            Write(s, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"ok\":true}"), r);
                        }
                        else Write(s, 400, "application/json; charset=utf-8",
                            Encoding.UTF8.GetBytes("{\"ok\":false,\"err\":\"" + Json(err) + "\"}"), r);
                        break;
                    }
                case "/gamepad":
                    Write(s, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(Config.GamepadJson()), r);
                    break;
                case "/gamepad/save":
                    {
                        // 手机用表单发（j=...），curl 直接甩一段 JSON 当 body 也认
                        string err;
                        if (Config.SaveGamepad(G(r, "j", r.Body ?? "{}"), out err))
                        {
                            Log("[手柄] 布局已保存：" + (r.Remote ?? "?"));
                            Write(s, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"ok\":true}"), r);
                        }
                        else Write(s, 400, "application/json; charset=utf-8",
                            Encoding.UTF8.GetBytes("{\"ok\":false,\"err\":\"" + Json(err) + "\"}"), r);
                        break;
                    }
                case "/dj":
                    {
                        // 电脑这边那个"跟着手机放"的播放页：浏览器打开它，声音照常走 HDMI 到电视
                        string f = Path.Combine(WwwDir, "dj.html");
                        if (File.Exists(f)) Write(s, 200, "text/html; charset=utf-8", File.ReadAllBytes(f), r);
                        else Write(s, 500, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("找不到 www\\dj.html"), r);
                        break;
                    }
                case "/media":
                    {
                        // 把音乐文件发给两边。只认配置里 musicDirs 列出的目录之内的文件，
                        // 路径先规范化再比前缀 —— 不然 "..\..\Windows\xxx" 就成了任意文件读取。
                        string f; r.Form.TryGetValue("f", out f);
                        if (string.IsNullOrEmpty(f)) { Write(s, 400, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("缺 f 参数"), r); break; }
                        string full;
                        if (!Media.InRoots(f, out full))
                        {
                            Log("[同播] 拒绝越界的路径：" + f + "  <- " + (r.Remote ?? "?"));
                            Write(s, 403, "text/plain; charset=utf-8",
                                Encoding.UTF8.GetBytes("这个文件不在允许列表里（要在 bedremote.json 的 musicDirs 目录下）"), r);
                            break;
                        }
                        Media.Serve(s, c, r, full);
                        c.Close(); return;
                    }
                case "/edit":
                    {
                        string f = Path.Combine(WwwDir, "edit.html");
                        if (File.Exists(f)) Write(s, 200, "text/html; charset=utf-8", File.ReadAllBytes(f), r);
                        else Write(s, 500, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("找不到 www\\edit.html"), r);
                        break;
                    }
                case "/status":
                    {
                        var p = W32.CursorPos();
                        var v = W32.VirtualDesktop();
                        var mons = W32.Monitors();
                        int cur = 0;
                        for (int i = 0; i < mons.Count; i++)
                            if (p.X >= mons[i].Left && p.X < mons[i].Right && p.Y >= mons[i].Top && p.Y < mons[i].Bottom) cur = i;
                        var sb = new StringBuilder();
                        sb.Append("{");
                        sb.Append("\"ip\":\"").Append(r.Remote).Append("\",");
                        sb.Append("\"mon\":").Append(mons.Count).Append(",");
                        sb.Append("\"onscreen\":").Append(cur).Append(",");
                        sb.Append("\"cx\":").Append(p.X).Append(",\"cy\":").Append(p.Y).Append(",");
                        sb.Append("\"virtn\":\"").Append(v.Left).Append(",").Append(v.Top).Append(",").Append(v.Right).Append(",").Append(v.Bottom).Append("\",");
                        sb.Append("\"win\":\"").Append(Json(W32.ForegroundTitle())).Append("\",");
                        sb.Append("\"locked\":").Append(W32.LockedNow() ? "true" : "false").Append(",");
                        sb.Append("\"lockWhy\":\"").Append(Json(W32.LockWhy())).Append("\",");
                        sb.Append("\"awake\":").Append(Awake ? "true" : "false").Append(",");
                        sb.Append("\"name\":\"").Append(Json(Config.NameOrMachine())).Append("\",");
                        sb.Append("\"up\":").Append((int)((DateTime.Now.Ticks - Started) / TimeSpan.TicksPerSecond)).Append(",");
                        // 配置健康状况：以前解析失败是静默退回内置默认，没人看得出来
                        sb.Append("\"cfg\":").Append(Config.Loaded ? "true" : "false").Append(",");
                        sb.Append("\"cfgErr\":\"").Append(Json(Config.Error ?? "")).Append("\",");
                        sb.Append("\"runCount\":").Append(Config.Run.Count).Append(",");
                        lock (Peers) sb.Append("\"devs\":").Append(ExternalCount());
                        sb.Append("}");
                        Write(s, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(sb.ToString()), r);
                        break;
                    }
                case "/reply":
                    {
                        string f = Path.Combine(DataDir, "last_reply.txt");
                        string t = File.Exists(f) ? File.ReadAllText(f) : "";
                        Write(s, 200, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(t), r);
                        break;
                    }
                case "/cmd": Dispatch(s, r); break;
                default: Write(s, 404, "text/plain", Encoding.UTF8.GetBytes("not found"), r); break;
            }
            c.Close();
        }
        catch { try { c.Close(); } catch { } }
    }

    // 谁能进来。两条路，顺序不能反：
    //   1) 设备凭据 d —— 这台手机哪次带正确口令进来过，我们就记下它自己那串随机 id；
    //      以后它不用重输口令，而且能在电脑上一台一台踢掉（见 src\Devices.cs）。
    //   2) 口令 t —— 第一次进来（或刚改过口令）走这条；带对了就顺手配对。
    // 口令为空时第 2 条恒成立（家里局域网，少一次输入），这时配对没意义也就不配：
    // 谁都能进的状态下，"名单"管不住任何人。
    static bool Authed(Req r)
    {
        string d;
        if (r.Form.TryGetValue("q_d", out d) && Devs.Shape(d) && Devs.Ok(d)) return true;
        if (Token.Length == 0) return true;
        string t;
        if (!r.Form.TryGetValue("q_t", out t) || t != Token) return false;
        if (r.Form.TryGetValue("q_d", out d) && Devs.Shape(d))
        {
            string dn; r.Form.TryGetValue("q_dn", out dn);
            Devs.Pair(d, dn);
        }
        return true;
    }

    // 把某台设备现在挂着的长连接掐掉（踢人要用）。返回掐了几条。
    // 先发一条 kick 再关：手机页收到就知道"我不是被网断了，是主人把我移出名单了"，
    // 会停掉自动重连、把话讲明白 —— 不然 EventSource 每 0.8 秒重连一次，
    // 一直 403，页面上只是"数字不跳"，人根本看不懂出了什么事。
    static int CloseDevStreams(string id)
    {
        if (string.IsNullOrEmpty(id)) return 0;
        int n = 0;
        byte[] b = Encoding.UTF8.GetBytes("data: {\"e\":\"kick\"}\n\n");
        lock (ClientsLock)
        {
            for (int i = Clients.Count - 1; i >= 0; i--)
            {
                if (Clients[i].Dev != id) continue;
                try { Clients[i].Stream.Write(b, 0, b.Length); Clients[i].Stream.Flush(); } catch { }
                try { Clients[i].Stream.Close(); } catch { }
                Clients[i].Dead = true;
                Clients.RemoveAt(i);
                n++;
            }
        }
        return n;
    }

    // ---------- 口令 / 设备名单：界面和手机页都走这几个口子，别各自抄一份 ----------

    // 给界面用的"随机口令"。8 位、只用不会看错的字（去掉 i o l 和 0 1），
    // 因为人真的会把它念给别人听；局域网里没人爆破，这个长度足够。
    internal static string NewToken()
    {
        const string abc = "abcdefghjkmnpqrstuvwxyz23456789";
        var rnd = new Random(unchecked((int)DateTime.Now.Ticks));
        var sb = new StringBuilder();
        for (int i = 0; i < 8; i++) sb.Append(abc[rnd.Next(abc.Length)]);
        return sb.ToString();
    }

    // 改口令：写进配置文件 + 当场生效，不用重启服务（重启会把他的手机页全断掉，太难看）。
    // 返回 null = 成功，否则是给人看的那句失败原因。
    internal static string ApplyToken(string want)
    {
        want = (want ?? "").Trim();
        if (want.Length > 64) return "口令太长了（最多 64 个字符）";
        string old = Token;
        if (old == want) return null;
        string err;
        if (!Config.SetToken(want, out err)) return "写 bedremote.json 失败：" + err;
        Token = want;
        int cleared = 0;
        if (want.Length > 0) cleared = Devs.Clear();
        Log("[口令] 已" + (want.Length == 0 ? "清掉口令（局域网里任何设备打开地址就能控制）"
                                            : "改成 " + want) +
            "（已写进 bedremote.json，不用重启）" +
            (cleared > 0 ? "；" + cleared + " 台已配对设备作废，要重新扫码" : ""));
        // 还开着的页面：口令变了，它们下次发指令就会 403。先把地址推过去，
        // 界面/手机页上的二维码立刻是新的，省得人还举着旧码扫。
        Broadcast("{\"e\":\"tok\"}");
        return null;
    }

    internal static string DevListJson() { return Devs.ListJson(); }

    // 踢一台设备：先掐它的长连接，再从名单里删。删不掉也照样返回失败，别骗界面。
    // by 是"谁干的"（界面里点的是 local，手机页点的是那台设备自己的 IP），日志要说得清。
    internal static bool KickDev(string id, string by, out string msg)
    {
        msg = "";
        if (!Devs.Shape(id)) { msg = "设备编号不对"; return false; }
        string name = Devs.NameOf(id);
        int closed = CloseDevStreams(id);
        if (!Devs.Kick(id)) { msg = "名单里没有这台设备"; return false; }
        msg = "已踢掉「" + (name.Length > 0 ? name : id) + "」" + (closed > 0 ? "（断开 " + closed + " 个连接）" : "");
        // 尾巴上再带一句 ASCII 的：日志是可以贴到 issue 里的，英文那半句谁都读得懂（测试也靠它）。
        Log("[设备] " + msg + "（由 " + (string.IsNullOrEmpty(by) ? "local" : by) + " 操作） [device kicked: " + id + "]");
        Broadcast(PeersJson());
        return true;
    }

    static void ParseForm(string body, Dictionary<string, string> into)
    {
        if (string.IsNullOrEmpty(body)) return;
        foreach (var kv in body.Split('&'))
        {
            if (kv.Length == 0) continue;
            int eq = kv.IndexOf('=');
            string k = eq < 0 ? kv : kv.Substring(0, eq);
            string v = eq < 0 ? "" : kv.Substring(eq + 1);
            into[Uri.UnescapeDataString(k)] = Uri.UnescapeDataString(v.Replace('+', ' '));
        }
    }

    static string ReadLine(Stream s)
    {
        var sb = new StringBuilder();
        int c;
        while ((c = s.ReadByte()) >= 0)
        {
            if (c == '\n') break;
            if (c != '\r') sb.Append((char)c);
        }
        if (c < 0 && sb.Length == 0) return null;
        return sb.ToString();
    }

    // 单调毫秒钟。不用 Environment.TickCount 是因为它 25 天会绕回，
    // 而"两边差多少毫秒"这种量最怕的就是绕回那一刻算出个负数。
    static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    internal static long NowMs() { return Clock.ElapsedMilliseconds; }

    // 只发头不发体（音频那种流式响应、以及 416 这种空响应要用）
    internal static void WriteHead(Stream s, int code, params string[] extra)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(code).Append(code == 200 ? " OK" : " Partial").Append("\r\n");
        sb.Append("Content-Length: 0\r\n");
        sb.Append("Cache-Control: no-store\r\n");
        for (int i = 0; i < extra.Length; i++) sb.Append(extra[i]).Append("\r\n");
        sb.Append("Connection: close\r\n\r\n");
        byte[] hb = Encoding.ASCII.GetBytes(sb.ToString());
        s.Write(hb, 0, hb.Length); s.Flush();
    }

    static void Write(Stream s, int code, string ctype, byte[] body, Req r)
    {
        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(code).Append(code == 200 ? " OK" : "").Append("\r\n");
        head.Append("Content-Type: ").Append(ctype).Append("\r\n");
        head.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        head.Append("Cache-Control: no-store\r\n");
        head.Append("Access-Control-Allow-Origin: *\r\n");
        head.Append("Connection: close\r\n\r\n");
        byte[] hb = Encoding.ASCII.GetBytes(head.ToString());
        s.Write(hb, 0, hb.Length);
        if (body.Length > 0) s.Write(body, 0, body.Length);
        s.Flush();
    }

    static void ServeSse(Stream s, Req r)
    {
        byte[] hb = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-store\r\n" +
            "Access-Control-Allow-Origin: *\r\nConnection: keep-alive\r\n\r\n");
        s.Write(hb, 0, hb.Length); s.Flush();
        byte[] op = Encoding.UTF8.GetBytes("retry: 800\n\n");
        s.Write(op, 0, op.Length); s.Flush();

        var cli = new Sse(s);
        string dv; if (r.Form.TryGetValue("q_d", out dv) && Devs.Shape(dv)) cli.Dev = dv;
        lock (ClientsLock) Clients.Add(cli);
        NoteSse(r.Remote, 1);
        try
        {
            byte[] hi = Encoding.UTF8.GetBytes("data: " + HelloJson() + "\n\n" +
                "data: {\"e\":\"ver\",\"t\":\"" + Json(Version) + "\"}\n\n" +
                "data: {\"e\":\"win\",\"t\":\"" + Json(W32.ForegroundTitle()) + "\"}\n\n");
            s.Write(hi, 0, hi.Length); s.Flush();
            while (!cli.Dead)
            {
                byte[] ping = Encoding.UTF8.GetBytes(": ping\n\n");
                s.Write(ping, 0, ping.Length); s.Flush();
                Thread.Sleep(4000);
            }
        }
        catch { }
        finally
        {
            lock (ClientsLock) Clients.Remove(cli);
            try { NoteSse(r.Remote, -1); } catch { }
        }
    }

    static void Broadcast(string json)
    {
        byte[] b = Encoding.UTF8.GetBytes("data: " + json + "\n\n");
        lock (ClientsLock)
        {
            for (int i = Clients.Count - 1; i >= 0; i--)
            {
                try { Clients[i].Stream.Write(b, 0, b.Length); Clients[i].Stream.Flush(); }
                catch { Clients[i].Dead = true; Clients.RemoveAt(i); }
            }
        }
    }

    internal static string Json(string s)
    {
        if (s == null) return "";
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            if (c == '"') sb.Append("\\\"");
            else if (c == '\\') sb.Append("\\\\");
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\t') sb.Append("\\t");
            else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    static string G(Req r, string k, string d) { string v; return r.Form.TryGetValue(k, out v) && v.Length > 0 ? v : d; }
    static int GI(Req r, string k, int d) { int v; return int.TryParse(G(r, k, ""), out v) ? v : d; }

    static readonly AutoResetEvent ReplyEvt = new AutoResetEvent(false);
    static readonly object InputLock = new object();
    static volatile string LastReply = "";

    // 推到手机上，然后等人在手机上回一句（脚本可以直接 curl 拿结果）
    static string WaitReply(string text, int timeoutSec)
    {
        LastReply = "";
        while (ReplyEvt.WaitOne(0)) { }
        Broadcast("{\"e\":\"ask\",\"t\":\"" + Json(text) + "\"}");
        Log("[ask] " + text);
        if (!ReplyEvt.WaitOne(timeoutSec * 1000)) return "";
        return LastReply ?? "";
    }

    // ---------- 指令 ----------

    internal static bool IsInputCmd(string c)
    {
        switch (c)
        {
            case "move": case "abs": case "btn": case "wheel": case "key":
            case "combo": case "text": case "paste": case "screen": case "run":
            case "power": case "open": case "frame": case "release": return true;
        }
        return false;
    }

    static long LastLockedNote = 0;

    internal static bool InputAllowed()
    {
        return !Config.DenyWhenLocked || !W32.LockedNow();
    }

    static void NoteLocked()
    {
        long now = DateTime.Now.Ticks;
        if (now - LastLockedNote < TimeSpan.TicksPerSecond * 4) return;
        LastLockedNote = now;
        string why = W32.LockWhy();
        Broadcast("{\"e\":\"locked\",\"t\":\"" + Json(LockMsg(why)) + "\",\"why\":\"" + Json(why) + "\"}");
        Log("[locked:" + (why.Length == 0 ? "?" : why) + "] 输入被系统挡住");
    }

    // 锁屏和 UAC 得分开说：给错建议等于让人对着屏幕发呆。
    static string LockMsg(string why)
    {
        if (why != "uac") return "电脑在锁屏或安全桌面上，注入会被系统丢掉：先把它解锁。";
        return "电脑正在等你点「管理员身份运行」的 是/否。那个确认框是高权限窗口，"
             + "我们这个普通权限进程往里送输入会被 Windows 直接丢掉 —— 手机上点不动不是卡了。"
             + "要么去电脑前点掉；要么以后把 bedremote 以管理员身份跑（界面里有按钮），那样这类程序连弹窗都不会有。";
    }

    // 「丢到电脑」的公共实现：手机页的 c=open 和安卓分享面板的 /share 都走这一个。
    // 远程可触发，所以输入收得很窄：只认 http/https 完整链接、限长、不许空格/引号/管道
    // （file:、\服务器\共享、javascript: 全进不来）。每一次打开都写日志 + 回推一条提示到手机上。
    static string OpenUrl(string raw, string from)
    {
        string u = (raw ?? "").Trim();
        if (!Config.AllowOpen) { Log("[丢到电脑] 被配置挡掉：" + u); return "denied：配置里 allowOpen=false（想用手机开链接就把它改成 true）"; }
        if (u.Length == 0) return "没有链接";
        if (u.Length > 2000) return "链接太长（>2000 字符）";
        bool good = (u.StartsWith("http://") || u.StartsWith("https://")) && u.Length >= 12 && !HasBadChar(u);
        if (!good) { Log("[丢到电脑] 拒绝：" + u); return "只接受 http:// 或 https:// 开头、不含空格和引号的完整链接"; }
        try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); }
        catch (Exception ex) { Log("[丢到电脑] 打不开：" + ex.Message); return "打不开：" + ex.Message; }
        Log("[丢到电脑] " + u + "  <- " + from);
        string show = u.Length > 80 ? u.Substring(0, 80) + "…" : u;
        Broadcast("{\"e\":\"msg\",\"t\":\"电脑已打开：" + Json(show) + "\"}");
        return "opened";
    }

    // 这条 run 条目被点名要走通行证吗（大小写无所谓，白名单本来就是忽略大小写的）
    static bool IsElev(string name)
    {
        for (int i = 0; i < Config.ElevatedRun.Count; i++)
            if (string.Equals(Config.ElevatedRun[i], name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // 跑一条白名单命令。**只接受配置里写好的名字**，绝不接受手机传来的命令行。
    // 被点名要通行证、而且证办好了 → 走计划任务（以管理员身份起来，不弹框）；
    // 否则照普通方式跑 —— 该弹 UAC 还是会弹，但我们会把"这条还没办证"一起推到手机上，
    // 让他知道下次回电脑点一下，以后就不弹了。
    static string DoRun(string name, string from)
    {
        string cmdline;
        if (!Config.Run.TryGetValue(name, out cmdline) || string.IsNullOrEmpty(cmdline))
        {
            // 光回一个 "denied" 等于让人对着墙猜：把收到的名字、白名单条数、
            // 以及配置文件有没有解析失败一起说出来。
            string d = "denied：白名单里没有[" + name + "]，当前已加载 " + Config.Run.Count + " 条" +
                       (Config.Error != null ? "；而且配置文件解析失败（已退回内置默认）：" + Config.Error : "");
            Log("[run] " + d);
            return d;
        }
        if (IsElev(name))
        {
            var ps = Passes.All(DataDir, Config.ElevatedRun);
            for (int i = 0; i < ps.Count; i++)
            {
                if (!string.Equals(ps[i].Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (ps[i].Registered && !ps[i].Stale)
                {
                    string how;
                    if (Passes.Trigger(ps[i].Task, out how))
                    {
                        Log("[run] " + name + " -> 通行证 " + ps[i].Task + "  <- " + from);
                        return "ran-elev:" + name;
                    }
                    Log("[run] 通行证启动失败（" + how + "），落回普通方式");
                    break;
                }
                string note = ps[i].Stale
                    ? "这条的通行证过时了（命令改过）：回电脑上点一次「重新办证」"
                    : "这条还没办通行证，所以刚才那下大概弹了管理员确认框（手机点不动它）。回电脑点一次「办通行证」，以后就不弹了。";
                Log("[run] " + note);
                Broadcast("{\"e\":\"msg\",\"t\":\"" + Json(note) + "\"}");
                break;
            }
        }
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c " + cmdline)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process.Start(psi);
            Log("[run] " + name + " -> " + cmdline);
            return "ran:" + name;
        }
        catch (Exception ex) { return "跑不了：" + ex.Message; }
    }

    // ---------- 通行证给界面用的接口 ----------
    internal static bool RunCmd(string name, out string cmd) { return Config.Run.TryGetValue(name, out cmd); }
    internal static string DataDirPath { get { return DataDir; } }
    internal static List<string> ElevatedNames { get { return Config.ElevatedRun; } }
    internal static List<Passes.Pass> PassList() { return Passes.All(DataDir, Config.ElevatedRun); }
    internal static string PassIssue() { string e = Passes.Issue(DataDir, Config.ElevatedRun, Log); return e == null ? "" : e; }
    internal static string PassRevoke() { string e = Passes.Revoke(DataDir, Config.ElevatedRun, Log); return e == null ? "" : e; }

    // 链接里不许出现的东西：空白/控制字符、引号、反斜杠、反引号、shell 元字符。
    // 正常 URL 一个都不该有（真要出现就该是 %XX 编码过的），所以这一条同时挡住了
    // file: 路径、UNC 共享、和任何"看起来像命令行"的输入。
    static bool HasBadChar(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c <= ' ' || c == '"' || c == '\'' || c == '\\' || c == '`' ||
                c == '&' || c == ';' || c == '|' || c == '<' || c == '>' || c == '$') return true;
        }
        return false;
    }

    // 安卓分享面板递过来的是 title / text / url 三段，各家 App 填得不一样：
    // 浏览器一般填 url+text，B 站、抖音这类常把链接混在 text 的一句话里。所以谁像链接用谁。
    static string PickSharedUrl(string url, string text, string title)
    {
        string[] cand = new string[] { url, text, title };
        for (int i = 0; i < cand.Length; i++)
        {
            string s = cand[i] ?? "";
            int k = s.IndexOf("http", StringComparison.Ordinal);
            while (k >= 0)
            {
                int e = k;
                while (e < s.Length && " \t\r\n\"'<>()（）【】[]".IndexOf(s[e]) < 0) e++;
                string piece = s.Substring(k, e - k).TrimEnd('。', '，', ',', ')', '）', '】', ']', '!', '?', '？', '！', ':', '：', '.');
                if ((piece.StartsWith("http://") || piece.StartsWith("https://")) && piece.Length >= 12) return piece;
                k = s.IndexOf("http", k + 4, StringComparison.Ordinal);
            }
        }
        return "";
    }

    // PWA 清单。代码生成而不是放一个静态文件，是因为要把令牌写进 start_url 和分享动作里 ——
    // 分享面板打开的是个全新的窗口，没人会再手动把 ?t= 带上。
    static string ManifestJson()
    {
        string tk = Token.Length > 0 ? "?t=" + Uri.EscapeDataString(Token) : "";
        var sb = new StringBuilder();
        sb.Append('{');
        sb.Append("\"name\":\"bedremote 床上遥控\",");
        sb.Append("\"short_name\":\"bedremote\",");
        sb.Append("\"start_url\":\"/").Append(tk).Append("\",");
        sb.Append("\"scope\":\"/\",");
        sb.Append("\"display\":\"standalone\",");
        sb.Append("\"orientation\":\"any\",");
        sb.Append("\"background_color\":\"#14161a\",\"theme_color\":\"#14161a\",");
        sb.Append("\"icons\":[{\"src\":\"/icon-192.png\",\"sizes\":\"192x192\",\"type\":\"image/png\"},");
        sb.Append("{\"src\":\"/icon-512.png\",\"sizes\":\"512x512\",\"type\":\"image/png\"}],");
        sb.Append("\"share_target\":{\"action\":\"/share").Append(tk).Append("\",\"method\":\"GET\",");
        sb.Append("\"params\":{\"title\":\"title\",\"text\":\"text\",\"url\":\"url\"}}");
        sb.Append('}');
        return sb.ToString();
    }

    // 口令挡的是"没钥匙的人"，但被挡住的往往是自己人（口令刚改过，他手机里还留着旧地址）。
    // 所以这一页必须说清楚"现在该干什么"，而不是只甩一个 403 让人以为程序坏了。
    static string ForbiddenHtml(string path)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><meta charset=utf-8>");
        sb.Append("<meta name=viewport content='width=device-width,initial-scale=1'>");
        sb.Append("<title>进不去 · bedremote</title>");
        sb.Append("<style>body{background:#14161a;color:#e8e8ee;font:16px/1.7 system-ui,'Microsoft YaHei UI',sans-serif;");
        sb.Append("padding:26px 20px;max-width:620px;margin:0 auto}h1{font-size:20px;margin:0 0 12px}");
        sb.Append("p{color:#b9bcc6;margin:0 0 14px}code{background:#23262c;padding:2px 7px;border-radius:5px;font-family:Consolas,monospace;font-size:14px}");
        sb.Append("strong{color:#ffd9d0}.sm{color:#6b6f78;font-size:12px}</style>");
        sb.Append("<h1>这台电脑加了口令，你手上的地址不带它</h1>");
        sb.Append("<p>两种可能：要么这台电脑刚<strong>改过口令</strong>（改口令会把之前配过的设备全部作废，这是故意的 —— 换锁嘛）；");
        sb.Append("要么它把你这台设备从名单里<strong>踢掉</strong>了。</p>");
        sb.Append("<p>怎么办：在<strong>那台电脑</strong>上打开 <code>http://127.0.0.1:").Append(Port).Append("/pair</code>，");
        sb.Append("屏幕上会出二维码，手机对着扫一次就进来了。以后换口令之前你都不用再扫。</p>");
        sb.Append("<p>想手输地址：<code>").Append(HttpsOn ? "https" : "http").Append("://电脑IP:").Append(Port)
          .Append("/?t=口令</code>（口令在主界面「门禁」那一栏里）。</p>");
        sb.Append("<p class=sm>").Append(Json(Version)).Append("</p>");
        return sb.ToString();
    }

    static void Redirect(Stream s, string loc)    {
        byte[] hb = Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: " + loc +
            "\r\nContent-Length: 0\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        s.Write(hb, 0, hb.Length); s.Flush();
    }

    static void Dispatch(Stream s, Req r)
    {
        string c = G(r, "c", "");
        string ack = "1";
        long inAt = DateTime.Now.Ticks;      // 这条指令进来之前的时刻，c=lag 要用它算"服务端花了多久"
        Touch(r.Remote, c);      // 记账：是哪台设备、什么时候、发了什么
        // 凡是动显示的动作，先存一份刷新率，动完回读；掉速就自动用系统配置恢复。
        bool disp = c == "blank" || c == "wake" || c == "rescue" || c == "primary" ||
                    c == "only" || c == "cyclep" || c == "cycleo" || c == "undo";
        var hzBefore = disp ? Disp.HzSnapshot() : null;
        // 触控板会连发 move，而"读当前坐标->算落点->绝对放置"必须是原子的，
        // 否则两个请求都读到同一个起点，就丢步了。
        lock (InputLock)
        {
        try
        {
            if (IsInputCmd(c) && !InputAllowed())
            {
                ack = "locked";
                NoteLocked();
            }
            else switch (c)
            {
                case "ping":
                    Write(s, 200, "application/json; charset=utf-8",
                        Encoding.UTF8.GetBytes("{\"e\":\"pong\",\"ts\":" + G(r, "ts", "0") + "}"), r);
                    return;

                case "move": W32.MoveRelative(GI(r, "dx", 0), GI(r, "dy", 0)); break;
                case "abs":
                    {
                        double nx, ny;
                        double.TryParse(G(r, "x", ""), out nx); double.TryParse(G(r, "y", ""), out ny);
                        W32.MoveAbsoluteVirtual(nx, ny);
                        break;
                    }
                case "btn":
                    {
                        string b = G(r, "b", "left"), how = G(r, "d", "click");
                        if (b == "right") W32.Button(W32.MI_RIGHTDOWN, W32.MI_RIGHTUP, how);
                        else if (b == "mid") W32.Button(W32.MI_MIDDLEDOWN, W32.MI_MIDDLEUP, how);
                        else W32.Button(W32.MI_LEFTDOWN, W32.MI_LEFTUP, how);
                        break;
                    }
                case "wheel": W32.Wheel(GI(r, "d", 0), GI(r, "h", 0)); break;

                // 手柄的一帧：谁发的就用谁的 IP 记账，锁屏/断心跳时只松它自己那一份。
                case "frame":
                    ack = Held.Frame(r.Remote ?? "?", G(r, "keys", ""), G(r, "mb", ""), GI(r, "dx", 0), GI(r, "dy", 0));
                    break;
                case "release":
                    Held.Release(r.Remote ?? "?");
                    ack = "released";
                    break;

                case "key":
                    {
                        string k = G(r, "k", "");
                        string hd = G(r, "d", "tap");        // down/up 给手柄用；默认还是点一下
                        var kk = Keys.Get(k);
                        if (kk == null && k.Length == 1) { uint cvk; if (Keys.CharVk(k, out cvk)) kk = new Keys.K(cvk, false); }
                        if (kk != null && (hd == "down" || hd == "up")) W32.Vk(kk.Vk, kk.Ext, hd == "up");
                        else if (kk != null) W32.TapVk(kk.Vk, kk.Ext);
                        else if (k.Length == 1) W32.TypeUnicode(k);
                        break;
                    }
                case "combo":
                    {
                        string k = G(r, "k", "");
                        string mods = G(r, "m", "");
                        var list = new List<string>();
                        foreach (var m in mods.Split('+')) if (m.Trim().Length > 0) list.Add(m.Trim());
                        foreach (var m in list) W32.Vk(Keys.ModVk(m), Keys.ModExt(m), false);
                        Thread.Sleep(12);
                        var kk = Keys.Get(k);
                        uint cvk;
                        if (kk != null) W32.TapVk(kk.Vk, kk.Ext);
                        else if (Keys.CharVk(k, out cvk)) W32.TapVk(cvk, false);
                        else if (k.Length == 1) W32.TypeUnicode(k);
                        Thread.Sleep(12);
                        list.Reverse();
                        foreach (var m in list) W32.Vk(Keys.ModVk(m), Keys.ModExt(m), true);
                        break;
                    }
                case "text": W32.TypeUnicode(G(r, "s", "")); break;
                case "paste":
                    {
                        string want = G(r, "s", "");
                        string keep = W32.ClipboardGet();
                        if (W32.ClipboardSet(want))
                        {
                            Thread.Sleep(20);
                            W32.Vk(0x11, false, false); W32.TapVk(0x56, false); W32.Vk(0x11, false, true);
                            Thread.Sleep(90);
                            if (keep != null && G(r, "restore", "1") == "1") { W32.ClipboardSet(keep); }
                        }
                        break;
                    }
                case "screen":
                    {
                        var mons = W32.Monitors();
                        int want = GI(r, "n", -1);
                        if (want < 0 && G(r, "n", "") == "tv")
                        {
                            want = -1;
                            for (int i = 0; i < mons.Count; i++) if (!mons[i].Primary) { want = i; break; }
                            if (want < 0 && mons.Count > 1) want = 1;
                        }
                        if (want >= 0 && want < mons.Count)
                        {
                            var v = W32.VirtualDesktop();
                            int w = v.Right - v.Left, h = v.Bottom - v.Top;
                            double cx = mons[want].Left + (mons[want].Right - mons[want].Left) / 2.0;
                            double cy = mons[want].Top + (mons[want].Bottom - mons[want].Top) / 2.0;
                            W32.MoveAbsoluteVirtual((cx - v.Left) / w, (cy - v.Top) / h);
                            ack = mons[want].Device;
                        }
                        break;
                    }
                case "power": W32.MonitorOff(); break;
                case "mons":
                    Write(s, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(MonsJson()), r);
                    break;
                case "ddcdbg":
                    {
                        uint du;
                        if (!uint.TryParse(G(r, "mt", "0"), out du) || du == 0) { ack = "err:要 mt="; break; }
                        Write(s, 200, "text/plain; charset=utf-8",
                            Encoding.UTF8.GetBytes(Disp.DdcDebug(du)), r);
                        break;
                    }
                case "blank": case "wake":
                    {
                        // 点名单块屏：blank = 让它无信号，wake = 唤醒它。
                        // mt 是屏的 uid（设备路径里的 UID，比 target id 稳）；参数名故意不叫 t —— t 被令牌占着。
                        uint tid;
                        if (!uint.TryParse(G(r, "mt", "0"), out tid) || tid == 0) { ack = "err:没指定是哪块屏(mt=)"; break; }
                        bool on = c == "wake";
                        bool ddc = G(r, "m", "") == "ddc";
                        int sec; if (!int.TryParse(G(r, "sec", "0"), out sec)) sec = 0;

                        if (ddc && !on && !Config.AllowDdcOff)
                        {
                            // 默认关掉这条路：有面板"软关"之后连 DDC 都不应答，命令叫不醒、只能拔线。
                            ack = "err:DDC 熄灭默认禁用（本机实测：关掉后叫不醒，得人起身拔数据线）。" +
                                  "要用：bedremote.json 里把 \"ddcOff\" 改成 true 再重启。或者用「无信号」那套（一定接得回来）。";
                            break;
                        }

                        bool ok; string how;
                        if (ddc)
                        {
                            string dinfo;
                            ok = Disp.DdcPower(tid, on, out dinfo);
                            how = dinfo;
                            if (ok && on)
                            {
                                // 回读面板活性：不应答 = 多半已经"软关到 DDC 都断了"，直接软件重插线兜底
                                Thread.Sleep(900);
                                if (!Disp.DdcAlive(Disp.Find(tid)))
                                {
                                    string rh;
                                    bool rok = Disp.Rescue(tid, out rh);
                                    how += "；面板不应答，已自动重插线：" + (rok ? "成功 " : "失败 ") + rh;
                                    Log("[ddc] tid=" + tid + " 面板不应答，走重插线兜底");
                                }
                            }
                            if (ok && !on)
                            {
                                if (sec <= 0) sec = 20;     // DDC 熄灭一律带兜底，默认 20 秒
                                ScheduleDdcRecover(tid, sec);
                                how += "；" + sec + " 秒后自动叫醒（叫不醒就自动重插线）";
                            }
                        }
                        else
                        {
                            ok = Disp.SetActive(tid, on, out how);
                            if (ok && !on)
                            {
                                // 自动接回是**服务端**策略。客户端传来的 sec 不算数 ——
                                // 手机页是缓存的旧版时会偷偷带 sec=20，害人以为"屏关不住"（实测踩到）。
                                if (Config.AutoWakeSec > 0)
                                {
                                    ScheduleWake(tid, Config.AutoWakeSec);
                                    how += "，按服务端设置 " + Config.AutoWakeSec + " 秒后自动接回";
                                }
                                else if (sec > 0)
                                    how += "；客户端要求 " + sec + " 秒自动接回，但服务端策略是不自动接回（要改就动 bedremote.json 的 autoWakeSec）";
                            }
                        }
                        ack = (ok ? (on ? "on:" : "off:") : "err:") + how;
                        Log("[" + c + "] tid=" + tid + " -> " + ack);
                        Broadcast("{\"e\":\"mons\"}");
                        break;
                    }
                case "primary":
                    {
                        // 只换主屏，一块屏都不关
                        uint tid;
                        if (!uint.TryParse(G(r, "mt", "0"), out tid) || tid == 0) { ack = "err:要 mt="; break; }
                        string how;
                        bool ok = Disp.SetPrimary(tid, out how);
                        ack = (ok ? "primary:" : "err:") + how;
                        Log("[primary] tid=" + tid + " -> " + ack);
                        Broadcast("{\"e\":\"mons\"}");
                        break;
                    }
                case "only":
                    {
                        // 一键"只用这块屏"：它当主屏，其他屏全部无信号。床上/桌前那一下。
                        uint tid;
                        if (!uint.TryParse(G(r, "mt", "0"), out tid) || tid == 0) { ack = "err:要 mt="; break; }
                        string how;
                        bool ok = Disp.OnlyThis(tid, out how);
                        ack = (ok ? "only:" : "err:") + how;
                        Log("[only] tid=" + tid + " -> " + ack);
                        Broadcast("{\"e\":\"mons\"}");
                        break;
                    }
                case "cyclep": case "cycleo":
                    {
                        // 不点名，就是"换下一块"。两块屏时用不上，三块以上才体现价值。
                        string how;
                        bool ok = Disp.Cycle(c == "cycleo", out how);
                        ack = (ok ? "cycle:" : "err:") + how;
                        Log("[" + c + "] -> " + ack);
                        Broadcast("{\"e\":\"mons\"}");
                        break;
                    }
                case "fixhz":
                    {
                        // 只把亮着的屏拉回它该跑的刷新率，绝不碰拓扑/主屏/位置
                        // （restore 会重排原点，掉速想单独修就用这个）
                        Disp.FixRefreshOnly();
                        ack = "fixhz:" + Disp.RatesNow();
                        Log("[fixhz] -> " + ack);
                        Broadcast("{\"e\":\"mons\"}");
                        break;
                    }
                case "restore":
                    {
                        // 应急：恢复显示现场。先按系统数据库里存的配置，读不到就强制重新枚举模式
                        // （这台机上 QDC_DATABASE_CURRENT 读不到，所以第二条才是主力）。
                        string how;
                        bool ok = Disp.Recover(out how);
                        ack = (ok ? "restore:" : "err:") + how;
                        Log("[restore] -> " + ack);
                        Broadcast("{\"e\":\"mons\"}");
                        break;
                    }
                case "undo":
                    {
                        // 显示配置撤销：回到上一步（"只用这块屏"之前的样子）
                        string how;
                        bool ok = Disp.Undo(out how);
                        ack = (ok ? "undo:" : "err:") + how;
                        Log("[undo] -> " + ack);
                        Broadcast("{\"e\":\"mons\"}");
                        break;
                    }
                case "rescue":
                    {
                        // 软件版"拔线重插"：任何屏黑着叫不醒的时候按这个，不用人起身
                        uint tid;
                        if (!uint.TryParse(G(r, "mt", "0"), out tid) || tid == 0) { ack = "err:要 mt="; break; }
                        string how;
                        bool ok = Disp.Rescue(tid, out how);
                        ack = (ok ? "rescued:" : "err:") + how;
                        Log("[rescue] tid=" + tid + " -> " + ack);
                        Broadcast("{\"e\":\"mons\"}");
                        break;
                    }
                case "awake": Awake = G(r, "on", "1") == "1"; W32.KeepAwake(Awake); break;

                // ---- 同播：手机是主、电脑是从，校正只作用在电脑这一侧 ----
                case "tracks": ack = Media.ListJson(); break;
                case "djbeat":
                    // 手机每 250ms 报一次"我在这首歌的第几秒"。服务器当场盖上自己的钟，
                    // 电脑播放页和服务器同一台机器、同一个钟 —— 所以不需要两端对表。
                    Dj.Beat(G(r, "pos", ""), G(r, "play", "0"), G(r, "u", ""), G(r, "n", ""), G(r, "live", "0"));
                    ack = "ok";
                    break;
                case "djvol":
                    Dj.SetVol(G(r, "v", "1"));
                    Broadcast("{\"e\":\"djvol\"}");
                    ack = "ok";
                    break;
                case "djpc":
                    Dj.PcBeat(G(r, "pos", ""), G(r, "rate", "1"));
                    ack = Dj.StateJson();       // 顺手把该跟到的位置带回去，省一次轮询
                    break;
                case "djstate": ack = Dj.StateJson(); break;      // 电脑播放页轮询
                case "djgap": ack = Dj.GapJson(); break;          // 手机显示"两边差多少毫秒"
                case "djoff":
                    {
                        int ms;
                        if (!int.TryParse(G(r, "ms", ""), out ms)) { ack = "bad"; break; }
                        Dj.SetOffset(ms);
                        Broadcast("{\"e\":\"djoff\",\"t\":" + Dj.Offset + "}");
                        ack = "ok:" + Dj.Offset;
                        break;
                    }
                case "djstop":
                    Dj.Stop();
                    Broadcast("{\"e\":\"djstop\"}");
                    ack = "ok";
                    break;

                // ---- 门禁：已配对设备名单 / 踢掉一台 ----
                // 能发这条指令 = 已经过了 Authed，所以"谁在名单里"这件事本身不算秘密。
                // 注意这里**故意没有** c=token：改口令只在电脑界面上做。
                // 一台已配对的手机当然也能动鼠标键盘，但"把别人锁在门外"这个决定该由主人坐在电脑前按下。
                // 目标用 who，不能用 d —— d 是"我是谁"，一条请求里两个 d 会撞车
                // （写错的那次实测：curl 拿 d 当目标，把自己的编号踢掉了，对面那台毫发无伤）。
                case "devs": ack = Devs.ListJson(); break;
                case "kick":
                    {
                        string m;
                        ack = KickDev(G(r, "who", ""), r.Remote, out m) ? "ok:" + m : "err:" + m;
                        break;
                    }

                // ---- 多机：名单 / 改名 / 扫一段 / 把面板推给同伴 ----
                case "mates": ack = Mates.ListJson(); break;                case "scan": ack = MatesScan(); break;
                case "name":
                    {
                        string err;
                        string n = G(r, "n", "").Trim();
                        if (Config.SetName(n, out err))
                        {
                            Log("[改名] 这台电脑现在叫「" + Config.NameOrMachine() + "」  <- " + (r.Remote ?? "?"));
                            Broadcast("{\"e\":\"name\",\"t\":\"" + Json(Config.NameOrMachine()) + "\"}");
                            ack = "name:" + Config.NameOrMachine();
                        }
                        else ack = "err:" + err;
                        break;
                    }
                case "srename":
                    {
                        string err;
                        long uid;
                        if (!long.TryParse(G(r, "mt", ""), out uid)) { ack = "err:没给屏编号"; break; }
                        string n = G(r, "n", "").Trim();
                        if (Config.SetScreenName(uid.ToString(), n, out err))
                        {
                            Log("[改名] 屏 " + uid + " → 「" + (n.Length == 0 ? "（回到型号名）" : n) + "」  <- " + (r.Remote ?? "?"));
                            Broadcast("{\"e\":\"mons\"}");
                            ack = n.Length == 0 ? "cleared" : "ok";
                        }
                        else ack = "err:" + err;
                        break;
                    }
                case "push": ack = PushPanels(G(r, "to", ""), r.Remote); break;

                // 延迟探针：手机发一个自己的时间戳过来，我们原样退回（走 SSE 那条长连接）。
                // 手机用同一个时钟算"发出去→回到手机"，所以不需要两边对表。
                // 这条路和 c=frame 走的是同一条（HTTP 上行 → 这里分发 → SSE 下行），
                // 量出来的就是"我按下到电脑知道"的那一段，不含屏幕刷新。
                case "lag":
                    {
                        double srv = (DateTime.Now.Ticks - inAt) / (double)TimeSpan.TicksPerMillisecond;
                        Broadcast("{\"e\":\"lag\",\"t0\":\"" + Json(G(r, "t0", "")) + "\",\"srv\":"
                                  + srv.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "}");
                        ack = "lag";
                        break;
                    }
                case "notify": Broadcast("{\"e\":\"msg\",\"t\":\"" + Json(G(r, "s", "")) + "\"}"); break;
                case "mon": Broadcast(HelloJson()); break;
                case "reply":
                    {
                        string t = G(r, "s", "");
                        try { File.WriteAllText(Path.Combine(DataDir, "last_reply.txt"), t); } catch { }
                        LastReply = t;
                        Broadcast("{\"e\":\"reply\",\"t\":\"" + Json(t) + "\"}");
                        Log("[reply] " + t);
                        ReplyEvt.Set();
                        break;
                    }
                case "run": ack = DoRun(G(r, "n", ""), r.Remote); break;
                case "open":
                    ack = OpenUrl(G(r, "u", ""), r.Remote);
                    break;
                default: break;
            }
        }
        catch (Exception ex) { ack = "err:" + ex.Message; }
        if (disp && ack.IndexOf("err:") != 0)
        {
            try
            {
                string g = Disp.GuardHz(hzBefore);
                if (g.Length > 0) { ack += " | " + g; Log("[hz-guard] " + g); }
            }
            catch { }
        }
        }
        Write(s, 200, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(ack), r);
    }
}
