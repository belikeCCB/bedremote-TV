// 认程序：这个 exe 要不要管理员权限、它到底是哪个文件、现在桌面上开着哪些程序。
// 都是给「往手机上加个软件」那个向导用的，也可以单独被诊断代码调用。
//
// 为什么要自动判断"要不要管理员"：让用户去猜"我这个软件会不会弹框"是不讲理的，
// 而 Windows 其实把答案写在两个地方 —— exe 内嵌的 manifest（requestedExecutionLevel）
// 和注册表里的兼容性勾选（RUNASADMIN）。读一下就知道，不用跑一次看它弹不弹。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

static class Apps
{
    public class Item
    {
        public string Title = "";         // 窗口标题
        public string Exe = "";           // 完整路径
        public string Name = "";          // 文件名（不含扩展名）
        public string Why = "";           // 要管理员的原因（"" = 不要）
        public override string ToString()
        {
            string t = Title.Length > 40 ? Title.Substring(0, 40) : Title;
            return t + "　—　" + Name + (Why.Length > 0 ? "　（要管理员）" : "");
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr LoadLibraryEx(string f, IntPtr h, int flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr FindResource(IntPtr h, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll")] static extern IntPtr LoadResource(IntPtr h, IntPtr res);
    [DllImport("kernel32.dll")] static extern IntPtr LockResource(IntPtr data);
    [DllImport("kernel32.dll")] static extern uint SizeofResource(IntPtr h, IntPtr res);
    [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr h);
    const int LOAD_LIBRARY_AS_DATAFILE = 0x00000002;
    static readonly IntPtr RT_MANIFEST = (IntPtr)24;
    static readonly IntPtr CREATEPROCESS_MANIFEST_RESOURCE_ID = (IntPtr)1;

    // 返回 "" = 不需要；否则是原因（写进日志/界面，别只说"要"）
    public static string NeedsAdmin(string exe)
    {
        string m = ManifestLevel(exe);
        if (m == "requireAdministrator") return "它自己的清单里写了「必须管理员」(requireAdministrator)";
        if (m == "highestAvailable") return "它自己的清单里写了「能用多高用多高」(highestAvailable)";
        if (CompatFlag(exe)) return "兼容性设置里被勾了「以管理员身份运行此程序」（去掉这个勾就不用办证）";
        return "";
    }

    public static string ManifestLevel(string exe)
    {
        if (string.IsNullOrEmpty(exe)) return "";
        IntPtr h = IntPtr.Zero;
        try
        {
            h = LoadLibraryEx(exe, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE);
            if (h == IntPtr.Zero) return "";
            IntPtr res = FindResource(h, CREATEPROCESS_MANIFEST_RESOURCE_ID, RT_MANIFEST);
            if (res == IntPtr.Zero) return "";
            IntPtr data = LoadResource(h, res);
            if (data == IntPtr.Zero) return "";
            int len = (int)SizeofResource(h, res);
            if (len <= 0 || len > 65536) return "";
            byte[] buf = new byte[len];
            Marshal.Copy(data, buf, 0, len);
            string txt = Encoding.UTF8.GetString(buf);
            int i = txt.IndexOf("requestedExecutionLevel", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return "";
            string seg = txt.Substring(i, Math.Min(200, txt.Length - i));
            if (seg.IndexOf("requireAdministrator", StringComparison.OrdinalIgnoreCase) >= 0) return "requireAdministrator";
            if (seg.IndexOf("highestAvailable", StringComparison.OrdinalIgnoreCase) >= 0) return "highestAvailable";
            return "";
        }
        catch { return ""; }
        finally { if (h != IntPtr.Zero) { try { FreeLibrary(h); } catch { } } }
    }

    // 兼容性勾选存在注册表的 Layers 里，键值是 exe 的完整路径，数据里含 RUNASADMIN
    public static bool CompatFlag(string exe)
    {
        string[] roots = new string[] { "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags\\Layers" };
        foreach (string rk in roots)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(rk))
                    if (Hit(k, exe)) return true;
            }
            catch { }
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(rk))
                    if (Hit(k, exe)) return true;
            }
            catch { }
        }
        return false;
    }

    static bool Hit(RegistryKey k, string exe)
    {
        if (k == null || string.IsNullOrEmpty(exe)) return false;
        foreach (string v in k.GetValueNames())
        {
            if (!string.Equals(v, exe, StringComparison.OrdinalIgnoreCase)) continue;
            string s = Convert.ToString(k.GetValue(v, "")) ?? "";
            if (s.IndexOf("RUNASADMIN", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    // 快捷方式 → 真正的 exe（用户从桌面/开始菜单拖进来的多半是 .lnk）
    public static string Resolve(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return path;
        try
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return path;
            object shell = Activator.CreateInstance(t);
            object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
            object tp = sc.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc, null);
            string s = Convert.ToString(tp) ?? "";
            return s.Length > 0 ? s : path;
        }
        catch { return path; }
    }

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr SendMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    static List<IntPtr> _wins = new List<IntPtr>();

    // 桌面上正开着的、有标题的窗口 → 去重成"程序列表"，供向导里直接挑
    public static List<Item> Running()
    {
        var outp = new List<Item>();
        var byExe = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
        _wins = new List<IntPtr>();
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            if (IsWindowVisible(h)) _wins.Add(h);
            return true;
        }, IntPtr.Zero);
        int mine = Process.GetCurrentProcess().Id;
        for (int i = 0; i < _wins.Count; i++)
        {
            IntPtr h = _wins[i];
            uint pid = 0;
            GetWindowThreadProcessId(h, out pid);
            if ((int)pid == 0 || (int)pid == mine) continue;
            var sb = new StringBuilder(512);
            GetWindowTextW(h, sb, 512);
            string title = sb.ToString().Trim();
            if (title.Length == 0) continue;
            string exe;
            try
            {
                using (Process p = Process.GetProcessById((int)pid))
                {
                    string pn = p.ProcessName;
                    if (string.Equals(pn, "bedremote", StringComparison.OrdinalIgnoreCase)) continue;
                    bool skip = false;
                    for (int s = 0; s < Skip.Length; s++)
                        if (string.Equals(pn, Skip[s], StringComparison.OrdinalIgnoreCase)) { skip = true; break; }
                    if (skip) continue;
                    exe = SafePath(p);
                }
            }
            catch { continue; }
            if (string.IsNullOrEmpty(exe)) continue;
            Item it;
            if (byExe.TryGetValue(exe, out it)) continue;         // 同一个程序开多个窗口只列一次
            it = new Item();
            it.Title = title; it.Exe = exe;
            it.Name = System.IO.Path.GetFileNameWithoutExtension(exe);
            it.Why = NeedsAdmin(exe);
            byExe[exe] = it;
            outp.Add(it);
        }
        outp.Sort(delegate(Item a, Item b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
        return outp;
    }

    static string SafePath(Process p)
    {
        try { if (p.MainModule != null) return p.MainModule.FileName; }
        catch
        {
            // 以管理员身份跑的进程，我们这种普通权限读不到 MainModule —— 但
            // QueryFullProcessImageName(LIMITED_INFORMATION) 通常还是给得出完整路径，
            // 而"正在跑的那个管理员程序"恰恰是最需要被加到手机上的。
        }
        IntPtr h = IntPtr.Zero;
        try
        {
            h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
            if (h == IntPtr.Zero) return "";
            var sb = new StringBuilder(1024);
            int n = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString(0, n) : "";
        }
        catch { return ""; }
        finally { if (h != IntPtr.Zero) { try { CloseHandle(h); } catch { } } }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    // 这些进程列出来只会碍事：它们要么是系统外壳，要么是 UWP 的宿主进程
    // （ApplicationFrameHost 背后真正的商店应用我们拿不到 exe，加了也启动不了）
    static readonly string[] Skip = new string[]
    {
        "explorer", "ApplicationFrameHost", "TextInputHost", "SearchHost", "SearchApp",
        "StartMenuExperienceHost", "RuntimeBroker", "ShellExperienceHost", "Taskmgr",
        "svchost", "rundll32", "conhost", "cmd", "powershell", "pwsh", "wscript", "cscript"
    };
}
