// 提权通行证（Windows 计划任务）
//
// 要解决的问题：从手机上点开一个"要管理员权限"的程序 → 电脑弹 UAC 确认框 →
// 那个框是高权限窗口，我们这个普通权限进程送不进输入 → 远程当场"点不动"。
//
// 通行证的做法：一次性（要一次 UAC）注册一个计划任务，任务的执行人设成 **BUILTIN\Users**
// 组 + RunLevel=HighestAvailable。这是 Windows 自带的机制：任务管理器里"以最高权限运行"的任务，
// 如果它的主体是 Users 组，那么**普通权限进程也能 schtasks /run 触发它，而且不再弹框**。
// 于是：办证时点一次"是"，以后每次从手机上启动这个程序都不问。
//
// 为什么默认关、为什么要写进 SECURITY：这张证本质上是一个**定向的 UAC 绕过**。
// 能碰到 bedremote 的人 = 能免确认以管理员身份启动"证上写的那一个程序"。
// 所以：只对配置里逐条点名的条目生效、任务名用 ASCII（不过程编码那关）、
// 一条证只对应一条命令、随时能撕、状态在界面上看得见。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

static class Passes
{
    const string Prefix = "bedremote-run-";

    // 一张证
    public class Pass
    {
        public string Name = "";          // bedremote.json 里 run 白名单的那个名字（给人看的）
        public string Cmd = "";           // 要跑的命令（原样，和 run 白名单里一致）
        public string Task = "";          // 计划任务名（ASCII，见文件头）
        public bool Registered;           // 任务在不在系统里
        public bool Stale;                // 命令改过了，证上的内容已经过时
    }

    static readonly object Lk = new object();
    static List<Pass> list = null;

    static string MapFile(string dir) { return Path.Combine(dir, "passes.map"); }

    // 任务名：bedremote-run-<序号>，序号按"名字第一次出现的顺序"分配并且**记在映射文件里**，
    // 这样改名/删条目不会让别的证换任务名（换了就得重办）。
    static int SlotFor(Dictionary<string, string> map, string name, int[] next)
    {
        string t;
        if (map.TryGetValue(name, out t) && t.StartsWith(Prefix))
        {
            int n;
            if (int.TryParse(t.Substring(Prefix.Length), out n) && n > 0) return n;
        }
        int slot = ++next[0];
        map[name] = Prefix + slot;
        return slot;
    }

    public static List<Pass> All(string dir, IList<string> wanted)
    {
        lock (Lk)
        {
            var outp = new List<Pass>();
            var map = ReadMap(dir);
            var next = new int[] { MaxSlot(map) };
            bool changed = false;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < wanted.Count; i++)
            {
                string name = wanted[i];
                if (name == null || name.Trim().Length == 0) continue;
                if (seen.Contains(name)) continue;
                seen.Add(name);
                string cmd;
                if (!Program.RunCmd(name, out cmd) || cmd.Length == 0)
                {
                    // 点名了但白名单里没有 —— 也要显示出来，否则用户以为没生效
                    var bad = new Pass();
                    bad.Name = name; bad.Cmd = ""; bad.Task = ""; bad.Registered = false; bad.Stale = false;
                    outp.Add(bad);
                    continue;
                }
                var p = new Pass();
                p.Name = name; p.Cmd = cmd;
                p.Task = Prefix + SlotFor(map, name, next);
                if (map.Count != next[0]) changed = true;
                p.Registered = TaskExists(p.Task);
                p.Stale = p.Registered && !CmdMatches(p.Task, cmd);
                outp.Add(p);
            }
            if (changed) WriteMap(dir, map);
            list = outp;
            return outp;
        }
    }

    static int MaxSlot(Dictionary<string, string> map)
    {
        int mx = 0;
        foreach (var kv in map)
        {
            if (!kv.Value.StartsWith(Prefix)) continue;
            int n;
            if (int.TryParse(kv.Value.Substring(Prefix.Length), out n) && n > mx) mx = n;
        }
        return mx;
    }

    static Dictionary<string, string> ReadMap(string dir)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string f = MapFile(dir);
            if (!File.Exists(f)) return d;
            foreach (var raw in File.ReadAllLines(f, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
        }
        catch { }
        return d;
    }

    static void WriteMap(string dir, Dictionary<string, string> map)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var sb = new StringBuilder();
            foreach (var kv in map) sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\n');
            File.WriteAllText(MapFile(dir), sb.ToString(), new UTF8Encoding(false));
        }
        catch { }
    }

    // ---------- 查 ----------
    static int Run(string file, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var p = Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit(6000);
                return p.HasExited ? p.ExitCode : 99;
            }
        }
        catch { return 98; }
    }

    public static bool TaskExists(string task)
    {
        if (string.IsNullOrEmpty(task)) return false;
        return Run("schtasks.exe", "/query /tn \"" + task + "\"") == 0;
    }

    // 证上的命令还等于现在配置里的命令吗？（读任务的 Action 参数比对）
    static bool CmdMatches(string task, string cmd)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", "/query /tn \"" + task + "\" /xml")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
            };
            using (var p = Process.Start(psi))
            {
                string xml = p.StandardOutput.ReadToEnd();
                p.WaitForExit(6000);
                if (xml.Length == 0) return true;      // 读不到就当没变，别乱报"过时"
                return xml.IndexOf(Esc(cmd), StringComparison.Ordinal) >= 0;
            }
        }
        catch { return true; }
    }

    // ---------- 办 / 撕 ----------
    static string XmlFor(string cmd)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n");
        sb.Append("<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n");
        sb.Append("  <RegistrationInfo><Description>bedremote 提权通行证</Description></RegistrationInfo>\r\n");
        sb.Append("  <Triggers />\r\n");                                    // 没有触发器：只能手动 start
        sb.Append("  <Principals><Principal id=\"Author\">\r\n");
        sb.Append("    <GroupId>S-1-5-32-545</GroupId>");                    // BUILTIN\Users —— 免弹框的关键
        sb.Append("\r\n    <RunLevel>HighestAvailable</RunLevel>\r\n");
        sb.Append("  </Principal></Principals>\r\n");
        sb.Append("  <Settings>\r\n");
        sb.Append("    <MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>\r\n");
        sb.Append("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n");
        sb.Append("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n");
        sb.Append("    <AllowHardTerminate>true</AllowHardTerminate>\r\n");
        sb.Append("    <StartWhenAvailable>false</StartWhenAvailable>\r\n");
        sb.Append("    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n");
        sb.Append("    <Priority>7</Priority>\r\n");
        sb.Append("    <RestartOnFailure><Interval>PT1M</Interval><Count>0</Count></RestartOnFailure>\r\n");
        sb.Append("  </Settings>\r\n");
        sb.Append("  <Actions Context=\"Author\"><Exec>\r\n");
        sb.Append("    <Command>C:\\Windows\\System32\\cmd.exe</Command>\r\n");
        // 白名单里存的本来就是给 cmd /c 用的命令行（"start ms-settings:sound"、"mpv --loop ..."），
        // 所以这里照抄同样的语义，不要再套一层 start —— 套了会把 "start xxx" 当成文件名。
        sb.Append("    <Arguments>/c " + Esc(cmd) + "</Arguments>\r\n");
        sb.Append("  </Exec></Actions>\r\n");
        sb.Append("</Task>\r\n");
        return sb.ToString();
    }

    static string Esc(string s)
    {
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }

    // 办证：一次 UAC 把**所有**要办的任务一起注册（多条也只点一次"是"）。
    // 返回 null = 成功；否则是给人看的原因。
    public static string Issue(string dir, IList<string> wanted, Action<string> log)
    {
        var ps = All(dir, wanted);
        var todo = new List<Pass>();
        for (int i = 0; i < ps.Count; i++)
            if (ps[i].Cmd.Length > 0 && (!ps[i].Registered || ps[i].Stale)) todo.Add(ps[i]);
        if (todo.Count == 0) return null;

        string tmp = Path.Combine(dir, "passes");
        try { Directory.CreateDirectory(tmp); } catch { }
        var sb = new StringBuilder();
        for (int i = 0; i < todo.Count; i++)
        {
            var p = todo[i];
            if (p.Task.Length == 0) p.Task = Prefix + "x";
            string xf = Path.Combine(tmp, p.Task + ".xml");
            try { File.WriteAllText(xf, XmlFor(p.Cmd), new UnicodeEncoding(true, false)); }
            catch (Exception ex) { return "写任务定义失败：" + ex.Message; }
            if (i > 0) sb.Append(" && ");
            sb.Append("schtasks /create /f /tn \"").Append(p.Task).Append("\" /xml \"").Append(xf).Append("\"");
        }
        string whole = sb.ToString();
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c " + whole + " & pause")
            {
                UseShellExecute = true,
                Verb = "runas"
            };
            var pr = Process.Start(psi);
            if (pr != null) { try { pr.WaitForExit(120000); } catch { } }
        }
        catch (Exception ex) { return "你没点「是」，或者系统不让（" + ex.Message + "）—— 证没办成，什么都没改。"; }
        try { Directory.Delete(tmp, true); } catch { }
        list = null;
        if (log != null) log("[通行证] 已注册 " + todo.Count + " 条");
        return null;
    }

    public static string Revoke(string dir, IList<string> wanted, Action<string> log)
    {
        var ps = All(dir, wanted);
        var sb = new StringBuilder();
        int n = 0;
        for (int i = 0; i < ps.Count; i++)
        {
            if (ps[i].Task.Length == 0 || !ps[i].Registered) continue;
            if (n > 0) sb.Append(" & ");
            sb.Append("schtasks /delete /f /tn \"").Append(ps[i].Task).Append("\"");
            n++;
        }
        if (n == 0) return null;
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c " + sb.ToString() + " & pause")
            { UseShellExecute = true, Verb = "runas" };
            var pr = Process.Start(psi);
            if (pr != null) { try { pr.WaitForExit(120000); } catch { } }
        }
        catch (Exception ex) { return "撕证没成（" + ex.Message + "）"; }
        list = null;
        if (log != null) log("[通行证] 已删除 " + n + " 条");
        return null;
    }

    // 触发：这就是"手机上点一下、电脑以管理员身份开起来、不弹框"的那一步。
    public static bool Trigger(string task, out string how)
    {
        how = "";
        if (string.IsNullOrEmpty(task)) { how = "这条没有任务名"; return false; }
        int rc = Run("schtasks.exe", "/run /tn \"" + task + "\"");
        if (rc == 0) return true;
        how = "schtasks /run 返回 " + rc + "（任务不在了？或者被组策略挡了）";
        return false;
    }
}
