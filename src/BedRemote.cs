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
    static bool _locked = false;

    // 近似判据：锁屏界面和 Ctrl+Alt+Del 界面都由 LogonUI.exe 承载，它在跑就说明
    // 键盘鼠标注不进桌面。比 OpenInputDesktop 好在控制台进程里也能稳定拿到。
    // 注意：UAC 提权弹窗不经过 LogonUI，那种窗口要另外看 Consent.exe。
    public static bool LockedNow()
    {
        if ((DateTime.Now - _lockChecked).TotalMilliseconds < 500) return _locked;
        _lockChecked = DateTime.Now;
        try
        {
            _locked = Process.GetProcessesByName("LogonUI").Length > 0
                   || Process.GetProcessesByName("Consent").Length > 0;
        }
        catch { _locked = false; }
        return _locked;
    }

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

sealed class Sse
{
    public readonly NetworkStream Stream;
    public readonly Guid Id;
    public volatile bool Dead;
    public Sse(NetworkStream s) { Stream = s; Id = Guid.NewGuid(); }
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
    const string Version = "bedremote 0.6.0";

    static void Help()
    {
        Console.WriteLine(Version);
        Console.WriteLine("用法: bedremote.exe [--port=8765] [--token=xxxx] [--help] [--version]");
        Console.WriteLine("配置文件: 程序同目录下的 bedremote.json（端口、令牌、面板按钮都在里面）");
        Console.WriteLine("面板编辑器: 浏览器打开 http://127.0.0.1:<端口>/edit");
        Console.WriteLine("按屏控制: /cmd?c=mons 列屏；c=blank&mt=<屏id>[&sec=20][&m=ddc] 让某块屏无信号；c=wake&mt=<屏id> 接回来");
    }

    static string LastWin = "\u0000";

    static int Main(string[] args)
    {
        // 故意不把控制台设成 UTF-8：这台机是 936 代码页，设了反而满屏乱码，
        // 让 .NET 按控制台自己的代码页转换即可。
        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        Config.Load(exeDir);

        int cliPort = 0; string cliToken = null;
        foreach (var a in args)
        {
            if (a.StartsWith("--port=")) int.TryParse(a.Substring(7), out cliPort);
            else if (a.StartsWith("--token=")) cliToken = a.Substring(8);
            else if (a == "--help" || a == "-h") { Help(); return 0; }
            else if (a == "--version" || a == "-v") { Console.WriteLine(Version); return 0; }
        }
        Port = Config.Port;
        Token = Config.Token ?? "";
        if (cliPort > 0) Port = cliPort;
        if (cliToken != null) Token = cliToken;
        Awake = Config.KeepAwake;
        Started = DateTime.Now.Ticks;

        WwwDir = Path.Combine(exeDir, "www");
        DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "bed-remote");
        try { Directory.CreateDirectory(DataDir); } catch { }

        var ips = LocalIPv4();
        string suffix = Token.Length > 0 ? "/?t=" + Token : "";

        var listener = new TcpListener(IPAddress.Any, Port);
        try { listener.Start(); }
        catch (SocketException)
        {
            // 最常见的场景：run.cmd 被点了第二次，端口已经被自己占着。
            // 这里绝不能抛出（未处理异常 → 窗口一闪就没了，只剩一句"提示异常"），
            // 而且他点这个脚本通常就是想要手机地址，顺手再打一遍。
            Console.WriteLine("");
            Console.WriteLine("  端口 " + Port + " 已被占用 —— bedremote 很可能已经在运行了。");
            Console.WriteLine("  ----------------------------------------------------------------");
            foreach (var ip in ips)
                Console.WriteLine("  手机直接打开:  http://" + ip + ":" + Port + suffix);
            Console.WriteLine("  ----------------------------------------------------------------");
            Console.WriteLine("  想重启：先双击 stop.cmd，再双击 run.cmd。");
            Console.WriteLine("  手机不想输地址：双击 pair.cmd，屏幕出二维码，手机扫一下就直连。");
            Console.WriteLine("  端口是别的程序占的：bedremote.exe --port=8888");
            return 3;
        }

        Console.WriteLine("");
        Console.WriteLine("  bedremote 已启动");
        Console.WriteLine("  ----------------------------------------------------------------");
        foreach (var ip in ips)
            Console.WriteLine("  手机浏览器打开:  http://" + ip + ":" + Port + suffix);
        Console.WriteLine("  ----------------------------------------------------------------");
        if (Token.Length > 0)
            Console.WriteLine("  令牌 t=" + Token + "（地址里必须带上，否则控制指令会被拒绝）");
        else
            Console.WriteLine("  未设令牌：局域网里任何设备打开上面地址就能控制你的鼠标键盘。");
        Console.WriteLine("  想加令牌： bedremote.exe --token=xxxx  或改 bedremote.json 里的 token");
        Console.WriteLine("  改按钮：  电脑浏览器打开 http://127.0.0.1:" + Port + "/edit");
        Console.WriteLine("  本机推送文字到手机:  curl \"http://127.0.0.1:" + Port + "/notify?text=任务跑完了\"");
        Console.WriteLine("  问一句并等手机回答:  curl \"http://127.0.0.1:" + Port + "/ask?text=要现在下载吗\"");
        Console.WriteLine("  手机不想输地址:  双击 pair.cmd（或本机开 " + "http://127.0.0.1:" + Port + "/pair）出二维码，手机扫一下就直连");
        if (Config.Error != null)
            Console.WriteLine("  [警告] bedremote.json 解析失败，已退回内置默认面板：" + Config.Error);
        else
            Console.WriteLine("  配置已加载：" + Config.Path + "（run 白名单 " + Config.Run.Count + " 条）");
        Console.WriteLine("  停止: 直接关这个窗口");
        Console.WriteLine("");

        ThreadPool.QueueUserWorkItem(_ => WatchWindow());

        while (true)
        {
            TcpClient c;
            try { c = listener.AcceptTcpClient(); } catch { break; }
            ThreadPool.QueueUserWorkItem(_ => Handle(c));
        }
        return 0;
    }

    // 给配对页用：局域网地址、端口、令牌、当前有几个页面连着。
    // 手机"屏幕"页读这个：物理上插着的每块屏，含当前无信号的那些（好让他能一键接回来）
    static string MonsJson()
    {
        var sb = new StringBuilder();
        sb.Append("{\"e\":\"mons\",\"ddcOff\":").Append(Config.AllowDdcOff ? "true" : "false").Append(",\"mons\":[");
        var list = Disp.All(true);
        for (int i = 0; i < list.Count; i++)
        {
            var m = list[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"uid\":").Append(m.Uid)
              .Append(",\"name\":\"").Append(Json(string.IsNullOrEmpty(m.Name) ? m.Dev : m.Name)).Append("\"")
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
                Console.WriteLine("[auto-wake] tid=" + tid + " " + (ok ? "已接回 " : "接回失败 ") + how);
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
                    Console.WriteLine("[ddc-auto] tid=" + tid + " 面板不应答，重插线 " + (ok ? "成功 " : "失败 ") + rh);
                }
                else Console.WriteLine("[ddc-auto] tid=" + tid + " DDC 已叫醒");
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
    class Peer { public string Ip = ""; public long First = 0; public long Last = 0; public int Cmds = 0; public string LastCmd = ""; public int Sse = 0; }
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
            Console.WriteLine("[新设备] " + ip + " 开始控制这台电脑，外部设备共 " + total + " 台" +
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
                p = new Peer(); p.Ip = ip; p.First = DateTime.Now.Ticks; Peers[ip] = p;
                if (!IsMine(ip))
                    Console.WriteLine("[新设备] " + ip + " 打开了页面，外部设备共 " + ExternalCount() + " 台" +
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
            sb.Append("{\"ip\":\"").Append(Json(p.Ip)).Append("\"")
              .Append(",\"ago\":").Append((int)((DateTime.Now.Ticks - p.Last) / TimeSpan.TicksPerSecond))
              .Append(",\"secs\":").Append((int)((DateTime.Now.Ticks - p.First) / TimeSpan.TicksPerSecond))
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
          .Append(",\"token\":\"").Append(Json(Token ?? "")).Append("\"")
          .Append(",\"online\":").Append(online)
          .Append(",\"devs\":").Append(ExternalCount())
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
            }
            catch { }
            Thread.Sleep(700);
        }
    }

    static string RandomToken()
    {
        const string abc = "abcdefghjkmnpqrstuvwxyz23456789";
        var rnd = new Random();
        var sb = new StringBuilder();
        for (int i = 0; i < 4; i++) sb.Append(abc[rnd.Next(abc.Length)]);
        return sb.ToString();
    }

    static List<string> LocalIPv4()
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

    class Req
    {
        public string Method, Path, Query, Body;
        public Dictionary<string, string> Form = new Dictionary<string, string>();
        public string Remote;
    }

    static void Handle(TcpClient c)
    {
        try
        {
            c.NoDelay = true;
            var s = c.GetStream();
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
                    Console.WriteLine("[notify] " + txt);
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
                            Console.WriteLine("[panel] 已保存");
                            Write(s, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"ok\":true}"), r);
                        }
                        else Write(s, 400, "application/json; charset=utf-8",
                            Encoding.UTF8.GetBytes("{\"ok\":false,\"err\":\"" + Json(err) + "\"}"), r);
                        break;
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
                        sb.Append("\"awake\":").Append(Awake ? "true" : "false").Append(",");
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

    static bool Authed(Req r)
    {
        if (Token.Length == 0) return true;   // 默认不要求令牌：家里局域网，少一次输入
        string t;
        return r.Form.TryGetValue("q_t", out t) && t == Token;
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

    static void ServeSse(NetworkStream s, Req r)
    {
        byte[] hb = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-store\r\n" +
            "Access-Control-Allow-Origin: *\r\nConnection: keep-alive\r\n\r\n");
        s.Write(hb, 0, hb.Length); s.Flush();
        byte[] op = Encoding.UTF8.GetBytes("retry: 800\n\n");
        s.Write(op, 0, op.Length); s.Flush();

        var cli = new Sse(s);
        lock (ClientsLock) Clients.Add(cli);
        NoteSse(r.Remote, 1);
        try
        {
            byte[] hi = Encoding.UTF8.GetBytes("data: " + HelloJson() + "\n\n" +
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

    static string Json(string s)
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
        Console.WriteLine("[ask] " + text);
        if (!ReplyEvt.WaitOne(timeoutSec * 1000)) return "";
        return LastReply ?? "";
    }

    // ---------- 指令 ----------

    static bool IsInputCmd(string c)
    {
        switch (c)
        {
            case "move": case "abs": case "btn": case "wheel": case "key":
            case "combo": case "text": case "paste": case "screen": case "run":
            case "power": return true;
        }
        return false;
    }

    static long LastLockedNote = 0;

    static bool InputAllowed()
    {
        return !Config.DenyWhenLocked || !W32.LockedNow();
    }

    static void NoteLocked()
    {
        long now = DateTime.Now.Ticks;
        if (now - LastLockedNote < TimeSpan.TicksPerSecond * 5) return;
        LastLockedNote = now;
        Broadcast("{\"e\":\"locked\",\"t\":\"电脑在锁屏或安全桌面上，注入会被系统丢掉，先去解锁\"}");
        Console.WriteLine("[locked] 输入被系统挡住（锁屏或安全桌面）");
    }

    static void Dispatch(NetworkStream s, Req r)
    {
        string c = G(r, "c", "");
        string ack = "1";
        Touch(r.Remote, c);      // 记账：是哪台设备、什么时候、发了什么
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

                case "key":
                    {
                        string k = G(r, "k", "");
                        var kk = Keys.Get(k);
                        if (kk != null) W32.TapVk(kk.Vk, kk.Ext);
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
                                    Console.WriteLine("[ddc] tid=" + tid + " 面板不应答，走重插线兜底");
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
                            if (ok && !on && sec > 0 && sec <= 3600) { ScheduleWake(tid, sec); how += "，将在 " + sec + " 秒后自动接回"; }
                        }
                        ack = (ok ? (on ? "on:" : "off:") : "err:") + how;
                        Console.WriteLine("[" + c + "] tid=" + tid + " -> " + ack);
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
                        Console.WriteLine("[primary] tid=" + tid + " -> " + ack);
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
                        Console.WriteLine("[only] tid=" + tid + " -> " + ack);
                        Broadcast("{\"e\":\"mons\"}");
                        break;
                    }
                case "cyclep": case "cycleo":
                    {
                        // 不点名，就是"换下一块"。两块屏时用不上，三块以上才体现价值。
                        string how;
                        bool ok = Disp.Cycle(c == "cycleo", out how);
                        ack = (ok ? "cycle:" : "err:") + how;
                        Console.WriteLine("[" + c + "] -> " + ack);
                        Broadcast("{\"e\":\"mons\"}");
                        break;
                    }
                case "undo":
                    {
                        // 显示配置撤销：回到上一步（"只用这块屏"之前的样子）
                        string how;
                        bool ok = Disp.Undo(out how);
                        ack = (ok ? "undo:" : "err:") + how;
                        Console.WriteLine("[undo] -> " + ack);
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
                        Console.WriteLine("[rescue] tid=" + tid + " -> " + ack);
                        Broadcast("{\"e\":\"mons\"}");
                        break;
                    }
                case "awake": Awake = G(r, "on", "1") == "1"; W32.KeepAwake(Awake); break;
                case "notify": Broadcast("{\"e\":\"msg\",\"t\":\"" + Json(G(r, "s", "")) + "\"}"); break;
                case "mon": Broadcast(HelloJson()); break;
                case "reply":
                    {
                        string t = G(r, "s", "");
                        try { File.WriteAllText(Path.Combine(DataDir, "last_reply.txt"), t); } catch { }
                        LastReply = t;
                        Broadcast("{\"e\":\"reply\",\"t\":\"" + Json(t) + "\"}");
                        Console.WriteLine("[reply] " + t);
                        ReplyEvt.Set();
                        break;
                    }
                case "run":
                    {
                        // 只允许跑 bedremote.json 里 run 段白名单写好的命令名，
                        // 绝不接受手机传来的任意命令行。
                        string name = G(r, "n", "");
                        string cmdline;
                        if (!Config.Run.TryGetValue(name, out cmdline) || string.IsNullOrEmpty(cmdline))
                        {
                            // 光回一个 "denied" 等于让人对着墙猜：把收到的名字、白名单条数、
                            // 以及配置文件有没有解析失败一起说出来。
                            ack = "denied：白名单里没有[" + name + "]，当前已加载 " + Config.Run.Count + " 条" +
                                  (Config.Error != null ? "；而且配置文件解析失败（已退回内置默认）：" + Config.Error : "");
                            Console.WriteLine("[run] " + ack);
                            break;
                        }
                        var psi = new ProcessStartInfo("cmd.exe", "/c " + cmdline)
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        Process.Start(psi);
                        ack = "ran:" + name;
                        Console.WriteLine("[run] " + name + " -> " + cmdline);
                        break;
                    }
                case "reset": Broadcast("{\"e\":\"reset\"}"); break;
                default: break;
            }
        }
        catch (Exception ex) { ack = "err:" + ex.Message; }
        }
        Write(s, 200, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(ack), r);
    }
}
