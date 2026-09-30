using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

// 极简 JSON 读写：只够配置文件用，不追求兼容所有合法 JSON。
// 面板定义整棵树按原样存取（Dictionary/List），这样加字段不用改 C#。
static class Json
{
    public static object Parse(string s)
    {
        int i = 0;
        object v = Value(s, ref i);
        Skip(s, ref i);
        return v;
    }

    static void Skip(string s, ref int i) { while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++; }

    static object Value(string s, ref int i)
    {
        Skip(s, ref i);
        if (i >= s.Length) throw new FormatException("json 意外结束");
        char c = s[i];
        if (c == '{') return Obj(s, ref i);
        if (c == '[') return Arr(s, ref i);
        if (c == '"') return Str(s, ref i);
        if (c == 't') { Want(s, ref i, "true"); return true; }
        if (c == 'f') { Want(s, ref i, "false"); return false; }
        if (c == 'n') { Want(s, ref i, "null"); return null; }
        return Num(s, ref i);
    }

    static void Want(string s, ref int i, string word)
    {
        if (i + word.Length > s.Length || s.Substring(i, word.Length) != word) throw new FormatException("json 坏了：" + word);
        i += word.Length;
    }

    static Dictionary<string, object> Obj(string s, ref int i)
    {
        var d = new Dictionary<string, object>(StringComparer.Ordinal);
        i++; Skip(s, ref i);
        if (i < s.Length && s[i] == '}') { i++; return d; }
        while (i < s.Length)
        {
            Skip(s, ref i);
            string k = Str(s, ref i);
            Skip(s, ref i);
            if (i >= s.Length || s[i] != ':') throw new FormatException("json 缺冒号");
            i++;
            d[k] = Value(s, ref i);
            Skip(s, ref i);
            if (i < s.Length && s[i] == ',') { i++; continue; }
            if (i < s.Length && s[i] == '}') { i++; return d; }
            throw new FormatException("json 缺逗号或右括号");
        }
        throw new FormatException("json 没闭合");
    }

    static List<object> Arr(string s, ref int i)
    {
        var a = new List<object>();
        i++; Skip(s, ref i);
        if (i < s.Length && s[i] == ']') { i++; return a; }
        while (i < s.Length)
        {
            a.Add(Value(s, ref i));
            Skip(s, ref i);
            if (i < s.Length && s[i] == ',') { i++; continue; }
            if (i < s.Length && s[i] == ']') { i++; return a; }
            throw new FormatException("数组没闭合");
        }
        throw new FormatException("数组没闭合");
    }

    static string Str(string s, ref int i)
    {
        if (s[i] != '"') throw new FormatException("期望字符串");
        i++;
        var sb = new StringBuilder();
        while (i < s.Length)
        {
            char c = s[i++];
            if (c == '"') return sb.ToString();
            if (c != '\\') { sb.Append(c); continue; }
            if (i >= s.Length) break;
            char e = s[i++];
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case '/': sb.Append('/'); break;
                case '\\': sb.Append('\\'); break;
                case '"': sb.Append('"'); break;
                case 'u':
                    if (i + 4 > s.Length) throw new FormatException("\\u 截断");
                    sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber));
                    i += 4;
                    break;
                default: sb.Append(e); break;
            }
        }
        throw new FormatException("字符串没闭合");
    }

    static object Num(string s, ref int i)
    {
        int st = i;
        while (i < s.Length && "+-.eE0123456789".IndexOf(s[i]) >= 0) i++;
        double d;
        if (!double.TryParse(s.Substring(st, i - st), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
            throw new FormatException("数字坏了：" + s.Substring(st, i - st));
        return d;
    }

    public static string Write(object v)
    {
        var sb = new StringBuilder();
        Emit(v, sb, 0);
        return sb.ToString();
    }

    static bool Pretty = false;
    public static string WritePretty(object v)
    {
        Pretty = true;
        try { var sb = new StringBuilder(); Emit(v, sb, 0); return sb.ToString(); }
        finally { Pretty = false; }
    }

    static void NL(StringBuilder sb, int depth)
    {
        if (!Pretty) return;
        sb.Append('\n');
        for (int i = 0; i < depth; i++) sb.Append("  ");
    }

    static void Emit(object v, StringBuilder sb, int depth)
    {
        if (v == null) { sb.Append("null"); return; }
        if (v is string) { Esc((string)v, sb); return; }
        if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
        if (v is double) { sb.Append(((double)v).ToString("R", CultureInfo.InvariantCulture)); return; }
        if (v is long) { sb.Append(((long)v).ToString(CultureInfo.InvariantCulture)); return; }
        if (v is int) { sb.Append(((int)v).ToString(CultureInfo.InvariantCulture)); return; }
        var d = v as Dictionary<string, object>;
        if (d != null)
        {
            sb.Append('{');
            bool first = true;
            foreach (var kv in d)
            {
                if (!first) sb.Append(',');
                NL(sb, depth + 1);
                first = false;
                Esc(kv.Key, sb); sb.Append(Pretty ? ": " : ":"); Emit(kv.Value, sb, depth + 1);
            }
            if (!first) NL(sb, depth);
            sb.Append('}');
            return;
        }
        var a = v as List<object>;
        if (a != null)
        {
            sb.Append('[');
            for (int i = 0; i < a.Count; i++)
            {
                if (i > 0) sb.Append(',');
                NL(sb, depth + 1);
                Emit(a[i], sb, depth + 1);
            }
            if (a.Count > 0) NL(sb, depth);
            sb.Append(']');
            return;
        }
        Esc(Convert.ToString(v, CultureInfo.InvariantCulture), sb);
    }

    static void Esc(string s, StringBuilder sb)
    {
        sb.Append('"');
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
        sb.Append('"');
    }

    // ---- 取值小工具（面板树里到处用，缺字段就当默认值，别抛） ----

    public static Dictionary<string, object> Obj(object v)
    {
        var d = v as Dictionary<string, object>;
        return d ?? new Dictionary<string, object>(StringComparer.Ordinal);
    }
    public static List<object> Arr(object v)
    {
        var a = v as List<object>;
        return a ?? new List<object>();
    }
    public static string Str(object v, string d)
    {
        var s = v as string;
        return s == null ? d : s;
    }
    public static double Num(object v, double d)
    {
        var n = v as double?;
        return n.HasValue ? n.Value : d;
    }
    public static bool Bool(object v, bool d)
    {
        var b = v as bool?;
        if (b.HasValue) return b.Value;
        var s = v as string;                       // 兼容早期版本写出的 "True"/"False"
        if (s != null)
        {
            if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return d;
    }
    public static object Get(Dictionary<string, object> d, string k)
    {
        object v; return d != null && d.TryGetValue(k, out v) ? v : null;
    }
}

static class Config
{
    public static int Port = 8765;
    public static string Token = "";
    public static bool KeepAwake = true;
    public static bool DenyWhenLocked = true;
    // DDC/CI 熄灭某块屏。**默认关**：有些面板"软关"之后连 DDC 通道一起断，
    // 命令叫不醒它，只能人起身拔线重插（本机主屏实测就是这么坑）。要开自己去 json 里打开。
    public static bool AllowDdcOff = false;
    /// <summary>
    /// 关掉一块屏之后多少秒自动接回来；0 = 永不（默认）。
    /// 这个必须是**服务端**策略：以前是手机页带 sec=20 过来，服务端照着办，
    /// 结果手机页面是旧版就一直"关不住"（实测日志：off:已摘掉，将在 20 秒后自动接回 → [auto-wake]）。
    /// 客户端的 sec 参数只在服务端也开了的时候才生效。
    /// </summary>
    public static int AutoWakeSec = 0;
    /// <summary>
    /// 「丢到电脑」开关：手机把一个 http(s) 链接推过来，电脑用默认浏览器打开。
    /// 默认开 —— 它比"手机能控制你的鼠标键盘"轻得多，但确实是"局域网里任何设备都能让这台电脑开网页"，
    /// 所以留了这个闸，并且每一次打开都写进日志（界面上看得见是谁丢的）。
    /// </summary>
    public static bool AllowOpen = true;
    /// <summary>
    /// 同一个端口上再吃一路 HTTPS（自签证书）。默认开。
    /// 不是为了"加密"——局域网里那层加密意义有限；是为了手机浏览器肯把
    /// 陀螺仪、剪贴板、屏幕常亮这几样敏感能力交出来（非安全上下文里它们根本不触发）。
    /// 证书生成失败会自动退回纯明文，服务不会起不来。
    /// </summary>
    public static bool Https = true;
    /// <summary>
    /// 用**自建根 CA** 签证书，而不是一张裸自签。
    /// 差别只在一件事上：Chrome 注册 Service Worker 要求证书被**信任**（绕过警告页不算），
    /// 所以想装成 App、想让安卓分享面板里出现 bedremote，就得开这个，然后在手机上装一次
    /// `http://&lt;电脑&gt;:8765/ca.crt`。默认关 —— 装根证书是个安全决定，得他自己做。
    /// </summary>
    public static bool Ca = false;
    public static Dictionary<string, string> Run = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// 哪些 run 条目走"提权通行证"（计划任务，见 src\Passes.cs）。
    /// 这里只是**点名**；证要在界面里办一次（一次 UAC），没办证的条目按普通方式跑、该弹框还是弹框。
    /// 例：`"elevatedRun": ["某某软件"]`
    /// </summary>
    public static List<string> ElevatedRun = new List<string>();
    public static Dictionary<string, object> Panels = null;
    public static Dictionary<string, object> Gamepad = null;

    // 多机场景下"192.168.1.7"和"ESWIN"这种名字人记不住，所以机器和每块屏都能起别名。
    // 屏名按 uid 存（设备路径里那个稳定号），换接口/换分辨率不会张冠李戴。
    public static string Name = "";
    public static Dictionary<string, string> ScreenNames = new Dictionary<string, string>();
    // 「两边一起放同一首歌」用得到：能从 /media 发出去的目录，和那次耳朵标定的固定偏移（毫秒）。
    // 偏移**可以为负**（耳机比电视慢就填正、快就填负），所以它不能走 SetNum —— SetNum 把负数当"删掉这一项"。
    public static List<string> MusicDirs = new List<string>();
    public static int DjOffset = 0;
    /// <summary>
    /// 组合动作：名字 -> 步骤。步骤 = 一句 /cmd 查询串（"c=key&k=playpause"）或一句等待（"wait=400"）。
    /// 一个宏最多 32 步（再多就不是"顺手一键"，是在配置文件里写脚本了，那种东西该用计划任务）。
    /// </summary>
    public const int MaxMacroSteps = 32;
    public static Dictionary<string, List<string>> Macros = new Dictionary<string, List<string>>(StringComparer.Ordinal);

    public static string NameOrMachine()
    {
        if (!string.IsNullOrEmpty(Name)) return Name;
        try { return Environment.MachineName; } catch { return "bedremote"; }
    }

    public static string ScreenName(string uid, string fallback)
    {
        string v;
        if (uid != null && ScreenNames != null && ScreenNames.TryGetValue(uid, out v) && !string.IsNullOrEmpty(v)) return v;
        return fallback;
    }
    public static string Path = "";
    public static bool Loaded = false;

    const string DefaultJson = @"{
  ""port"": 8765,
  ""token"": """",
  ""keepAwake"": true,
  ""denyWhenLocked"": true,
  ""ddcOff"": false,
  ""autoWakeSec"": 0,
  ""allowOpen"": true,
  ""https"": true,
  ""ca"": false,
  ""run"": { ""声音设置"": ""start ms-settings:sound"", ""显示设置"": ""start ms-settings:display"", ""切屏面板"": ""start DisplaySwitch.exe"" },
  ""panels"": {
    ""tabs"": [
      {
        ""name"": ""遥控"",
        ""groups"": [
          { ""title"": ""播放"", ""cols"": 4, ""buttons"": [
            { ""label"": ""⏯"", ""sub"": ""系统播放键"", ""act"": ""key"", ""key"": ""playpause"" },
            { ""label"": ""空格"", ""sub"": ""网页播放/暂停"", ""act"": ""key"", ""key"": ""space"" },
            { ""label"": ""←"", ""sub"": ""后退"", ""act"": ""key"", ""key"": ""left"" },
            { ""label"": ""→"", ""sub"": ""前进"", ""act"": ""key"", ""key"": ""right"" },
            { ""label"": ""⏮"", ""sub"": ""上一首"", ""act"": ""key"", ""key"": ""prevtrack"" },
            { ""label"": ""⏭"", ""sub"": ""下一首"", ""act"": ""key"", ""key"": ""nexttrack"" },
            { ""label"": ""F"", ""sub"": ""播放器全屏"", ""act"": ""key"", ""key"": ""f"" },
            { ""label"": ""F11"", ""sub"": ""浏览器全屏"", ""act"": ""key"", ""key"": ""f11"" }
          ]},
          { ""title"": ""音量"", ""cols"": 3, ""buttons"": [
            { ""label"": ""－"", ""sub"": ""音量减"", ""act"": ""key"", ""key"": ""voldown"" },
            { ""label"": ""🔇"", ""sub"": ""静音"", ""act"": ""key"", ""key"": ""mute"" },
            { ""label"": ""＋"", ""sub"": ""音量加"", ""act"": ""key"", ""key"": ""volup"" }
          ]},
          { ""title"": ""窗口与切屏"", ""cols"": 4, ""buttons"": [
            { ""label"": ""Alt+Tab"", ""sub"": ""切窗口"", ""act"": ""combo"", ""mods"": ""alt"", ""key"": ""tab"" },
            { ""label"": ""Win+Tab"", ""sub"": ""任务视图"", ""act"": ""combo"", ""mods"": ""win"", ""key"": ""tab"" },
            { ""label"": ""Win+D"", ""sub"": ""显示桌面"", ""act"": ""combo"", ""mods"": ""win"", ""key"": ""d"" },
            { ""label"": ""Win+↑"", ""sub"": ""最大化"", ""act"": ""combo"", ""mods"": ""win"", ""key"": ""up"" },
            { ""label"": ""→ 电视屏"", ""act"": ""screen"", ""n"": ""tv"" },
            { ""label"": ""→ 主屏"", ""act"": ""screen"", ""n"": ""0"" },
            { ""label"": ""Esc"", ""act"": ""key"", ""key"": ""esc"" },
            { ""label"": ""截图"", ""act"": ""key"", ""key"": ""printscreen"" }
          ]},
          { ""title"": ""整机"", ""cols"": 3, ""buttons"": [
            { ""label"": ""声音"", ""sub"": ""Windows 里点下一个"", ""act"": ""run"", ""name"": ""声音设置"" },
            { ""label"": ""防睡眠"", ""act"": ""awake"" },
            { ""label"": ""关显示器"", ""sub"": ""动鼠标唤醒"", ""act"": ""power"", ""confirm"": ""关闭显示器？动一下鼠标就回来。"" },
            { ""label"": ""重载页面"", ""act"": ""reload"" }
          ]}
        ]
      }
    ]
  }
}";

    public static void Load(string exeDir)
    {
        Path = System.IO.Path.Combine(exeDir, "bedremote.json");
        string text;
        try
        {
            if (System.IO.File.Exists(Path)) text = System.IO.File.ReadAllText(Path);
            else { text = DefaultJson; System.IO.File.WriteAllText(Path, DefaultJson, new UTF8Encoding(false)); }
        }
        catch { text = DefaultJson; }

        try
        {
            var root = Json.Obj(Json.Parse(text));
            Port = (int)Json.Num(Json.Get(root, "port"), Port);
            Token = Json.Str(Json.Get(root, "token"), "");
            KeepAwake = Json.Bool(Json.Get(root, "keepAwake"), KeepAwake);
            DenyWhenLocked = Json.Bool(Json.Get(root, "denyWhenLocked"), DenyWhenLocked);
            AllowDdcOff = Json.Bool(Json.Get(root, "ddcOff"), AllowDdcOff);
            AllowOpen = Json.Bool(Json.Get(root, "allowOpen"), AllowOpen);
            Https = Json.Bool(Json.Get(root, "https"), Https);
            Ca = Json.Bool(Json.Get(root, "ca"), Ca);
            AutoWakeSec = (int)Json.Num(Json.Get(root, "autoWakeSec"), AutoWakeSec);
            if (AutoWakeSec < 0) AutoWakeSec = 0;
            if (AutoWakeSec > 3600) AutoWakeSec = 3600;
            Run = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in Json.Obj(Json.Get(root, "run"))) Run[kv.Key] = Json.Str(kv.Value, "");
            ElevatedRun = new List<string>();
            object er = Json.Get(root, "elevatedRun");
            if (er != null)
                foreach (var o in Json.Arr(er))
                {
                    string nm = Json.Str(o, "").Trim();
                    if (nm.Length > 0 && !ElevatedRun.Contains(nm)) ElevatedRun.Add(nm);
                }
            Panels = Json.Obj(Json.Get(root, "panels"));
            // 配置里没写 panels（或者写了个空的）**不等于"手机上一个按钮都不要"** ——
            // 那样手改配置的人打开页面会看到「遥控」整页消失，只会以为程序坏了。
            // 缺就回内置那套；真想清空，去 /edit 里删，那里有明确语义。
            object tabs0 = Json.Get(Panels, "tabs");
            if (tabs0 == null || Json.Arr(tabs0).Count == 0)
            {
                var defRoot = Json.Obj(Json.Parse(DefaultJson));
                Panels = Json.Obj(Json.Get(defRoot, "panels"));
            }
            Gamepad = Json.Obj(Json.Get(root, "gamepad"));
            Name = Json.Str(Json.Get(root, "name"), "").Trim();
            ScreenNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            object sn = Json.Get(root, "screenNames");
            if (sn != null)
                foreach (var kv in Json.Obj(sn))
                {
                    string nm = Json.Str(kv.Value, "").Trim();
                    if (kv.Key.Length > 0 && nm.Length > 0) ScreenNames[kv.Key] = nm.Length > 40 ? nm.Substring(0, 40) : nm;
                }
            // 同播：允许被 /media 发出去的歌放在哪些目录 + 那次标定的固定偏移
            MusicDirs = new List<string>();
            object md = Json.Get(root, "musicDirs");
            if (md != null)
                foreach (var o in Json.Arr(md))
                {
                    string d = Json.Str(o, "").Trim();
                    if (d.Length > 0 && !MusicDirs.Contains(d)) MusicDirs.Add(d);
                }
            DjOffset = (int)Json.Num(Json.Get(root, "djOffset"), 0);
            if (DjOffset < -2000) DjOffset = -2000;
            if (DjOffset > 2000) DjOffset = 2000;
            // 组合动作（宏）：名字 -> 一串步骤。步骤就是一句 /cmd 的查询串，或者 "wait=毫秒"。
            // 故意不做成"另一种动作语言"：这样电脑上 Dispatch 里那条动作表是**唯一一份**，
            // 宏能用的动作 = 手机/脚本能用的动作，不会两边各长出一套语义（见 src\Macros.cs 顶部）。
            Macros = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            object mx = Json.Get(root, "macros");
            if (mx != null)
                foreach (var kv in Json.Obj(mx))
                {
                    string nm = kv.Key.Trim();
                    if (nm.Length == 0) continue;
                    if (nm.Length > 40) nm = nm.Substring(0, 40);
                    var steps = new List<string>();
                    foreach (object o in Json.Arr(kv.Value))
                    {
                        string s = Json.Str(o, "").Trim();
                        if (s.Length == 0) continue;
                        if (s.Length > 400) s = s.Substring(0, 400);        // 一行步骤不该比一条命令长
                        if (steps.Count < MaxMacroSteps) steps.Add(s);
                    }
                    if (steps.Count > 0 && !Macros.ContainsKey(nm)) Macros[nm] = steps;
                }
            Loaded = true;
        }
        catch (Exception ex)
        {
            Error = "配置解析失败，已退回内置面板：" + ex.Message;
            var root = Json.Obj(Json.Parse(DefaultJson));
            Panels = Json.Obj(Json.Get(root, "panels"));
            Gamepad = new Dictionary<string, object>();
            Name = "";
            ScreenNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Macros = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        }
        if (Port < 1 || Port > 65535) Port = 8765;
    }

    public static string Error = null;

    // 只替换 panels 段，其它字段保持文件里的原样（用户在编辑器里只该看到面板）
    public static bool SavePanels(string panelsJson, out string err)
    {
        err = null;
        try
        {
            var p = Json.Obj(Json.Parse(panelsJson));
            string text = System.IO.File.Exists(Path) ? System.IO.File.ReadAllText(Path) : DefaultJson;
            var root = Json.Obj(Json.Parse(text));
            root["panels"] = p;
            Panels = p;
            System.IO.File.WriteAllText(Path, Json.WritePretty(root), new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex) { err = ex.Message; return false; }
    }

    public static string PanelsJson() { return Json.Write(Panels ?? new Dictionary<string, object>()); }

    static int CountWidgets(object arr)
    {
        if (arr == null) return 0;
        try { return Json.Arr(arr).Count; } catch { return 0; }
    }

    // 改一个顶层键的公共流程：读文件 → 解析 → 只动那一个键 → 写回（UTF-8 无 BOM）。
    // val 传 null 表示删掉这个键，回到内置默认。
    static bool Mutate(string key, object val, out string err)
    {
        err = null;
        try
        {
            string text = System.IO.File.Exists(Path) ? System.IO.File.ReadAllText(Path) : DefaultJson;
            var root = Json.Obj(Json.Parse(text));
            if (val == null) root.Remove(key); else root[key] = val;
            System.IO.File.WriteAllText(Path, Json.WritePretty(root), new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex) { err = ex.Message; return false; }
    }

    public static bool SetName(string name, out string err)
    {
        name = (name ?? "").Trim();
        if (name.Length > 40) name = name.Substring(0, 40);
        Name = name;
        return Mutate("name", name.Length == 0 ? null : (object)name, out err);
    }

    // 给某块屏起别名；传空字符串 = 删掉别名，回到显示器的出厂型号名。
    public static bool SetScreenName(string uid, string alias, out string err)
    {
        err = null;
        if (string.IsNullOrEmpty(uid)) { err = "没有这块屏的编号"; return false; }
        alias = (alias ?? "").Trim();
        if (alias.Length > 40) alias = alias.Substring(0, 40);
        if (alias.Length == 0) ScreenNames.Remove(uid); else ScreenNames[uid] = alias;
        var obj = new Dictionary<string, object>();
        foreach (var kv in ScreenNames) obj[kv.Key] = kv.Value;
        return Mutate("screenNames", obj.Count == 0 ? null : (object)obj, out err);
    }

    // 手机当手柄的布局：存这里只是为了换手机/重装还能拿回来，电脑端不解释它的内容
    // （只有手机页读它）。坐标一律是 0..1 的比例，所以不同尺寸/横竖屏都能套。
    public static string GamepadJson() { return Json.Write(Gamepad ?? new Dictionary<string, object>()); }

    public static bool SaveGamepad(string layoutJson, out string err)
    {
        err = null;
        try
        {
            var g = Json.Obj(Json.Parse(layoutJson ?? "{}"));
            // 收得紧一点：这个键是远程可写的，塞个几百 KB 进去就会把配置文件撑爆。
            // 现在的形状是 profiles:{名字:{widgets:[...]}}，老形状是顶层 widgets:[...]，两种都限。
            int max = 0;
            object profs = Json.Get(g, "profiles");
            if (profs != null)
            {
                var ps = Json.Obj(profs);
                if (ps.Count > 24) { err = "预设太多（" + ps.Count + " > 24）"; return false; }
                foreach (var kv in ps) max = Math.Max(max, CountWidgets(Json.Get(Json.Obj(kv.Value), "widgets")));
            }
            max = Math.Max(max, CountWidgets(Json.Get(g, "widgets")));
            if (max > 64) { err = "一套布局里控件太多（" + max + " > 64）"; return false; }
            string text = System.IO.File.Exists(Path) ? System.IO.File.ReadAllText(Path) : DefaultJson;
            var root = Json.Obj(Json.Parse(text));
            root["gamepad"] = g;
            Gamepad = g;
            System.IO.File.WriteAllText(Path, Json.WritePretty(root), new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex) { err = ex.Message; return false; }
    }

    // 口令：界面里改了要**立刻生效**，所以除了写文件还要让运行时跟着换（见 Program.ApplyToken）。
    // 这里故意不删键：token 为空也把它写成 ""，这样 bedremote.example.json 里那一行对得上，
    // 想知道"口令在哪配"的人打开配置文件一眼就能看到。
    public static bool SetToken(string tok, out string err)
    {
        tok = (tok ?? "").Trim();
        Token = tok;
        return Mutate("token", (object)tok, out err);
    }

    // 编辑器保存组合动作用这个：整段替换 macros 键（跟 SavePanels 一个道理，只做字符串手术的话
    // 手改过的其它键会被冲掉）。校验放在这里，别等到跑的时候才炸。
    public static bool SetMacros(string macrosJson, out string err)
    {
        err = null;
        try
        {
            var raw = Json.Obj(Json.Parse(string.IsNullOrEmpty(macrosJson) ? "{}" : macrosJson));
            var next = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var kv in raw)
            {
                string nm = kv.Key.Trim();
                if (nm.Length == 0) continue;
                if (nm.Length > 40) { err = "动作名太长了（" + nm + "，最多 40 字）"; return false; }
                // 值必须真的是数组。不查这一条的话，一份形状错的东西（把数组写成了对象）会被当成
                // "这个动作没有步骤"静默丢掉 —— 而整段是**替换**语义，结果就是"存了一次坏数据，
                // 把原来能用的动作全清了"，还回一个 ok:true。清空的正确写法是传 {}。
                if (!(kv.Value is List<object>)) { err = "「" + nm + "」的步骤必须是数组，比如 [\"c=mons\",\"wait=400\"]"; return false; }
                var steps = new List<string>();
                foreach (object o in Json.Arr(kv.Value))
                {
                    if (!(o is string)) { err = "「" + nm + "」的每一步必须是字符串"; return false; }
                    string s = Json.Str(o, "").Trim();
                    if (s.Length == 0) continue;
                    if (s.Length > 400) { err = "「" + nm + "」有一步太长（最多 400 字）"; return false; }
                    if (steps.Count >= MaxMacroSteps) { err = "「" + nm + "」步骤太多（上限 " + MaxMacroSteps + " 步）"; return false; }
                    steps.Add(s);
                }
                if (steps.Count == 0) continue;            // 空动作直接丢掉，别留个按了没反应的按钮
                next[nm] = steps;
            }
            if (next.Count > 40) { err = "组合动作太多（" + next.Count + " > 40）"; return false; }
            // 传进来有东西、结果一条都不合法 = 坏数据，不是"他想清空"：原来的一个字节都不许动。
            if (next.Count == 0 && raw.Count > 0) { err = "这份组合动作里没有一条是合法的，原来的没动"; return false; }
            string text = System.IO.File.Exists(Path) ? System.IO.File.ReadAllText(Path) : DefaultJson;
            var root = Json.Obj(Json.Parse(text));
            if (next.Count == 0) root.Remove("macros");
            else
            {
                var obj = new Dictionary<string, object>();
                foreach (var kv in next)
                {
                    var arr = new List<object>();
                    foreach (string s in kv.Value) arr.Add(s);
                    obj[kv.Key] = arr;
                }
                root["macros"] = obj;
            }
            System.IO.File.WriteAllText(Path, Json.WritePretty(root), new UTF8Encoding(false));
            Macros = next;
            return true;
        }
        catch (Exception ex) { err = ex.Message; return false; }
    }

    // 图形界面改开关用这个：解析 → 只换那一个键 → 写回，别在界面里做字符串手术。
    public static bool SetBool(string key, bool val, out string err)
    {
        err = null;
        try
        {
            string text = System.IO.File.Exists(Path) ? System.IO.File.ReadAllText(Path) : DefaultJson;
            var root = Json.Obj(Json.Parse(text));
            root[key] = val;
            if (key == "denyWhenLocked") DenyWhenLocked = val;
            else if (key == "keepAwake") KeepAwake = val;
            else if (key == "ddcOff") AllowDdcOff = val;
            else if (key == "allowOpen") AllowOpen = val;
            else if (key == "https") Https = val;
            else if (key == "ca") Ca = val;
            System.IO.File.WriteAllText(Path, Json.WritePretty(root), new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex) { err = ex.Message; return false; }
    }

    // 加/改一条 run 白名单（「往手机上加个软件」向导用，省得用户手改 JSON）
    public static bool SetRun(string name, string cmd, out string err)
    {
        err = null;
        try
        {
            string text = System.IO.File.Exists(Path) ? System.IO.File.ReadAllText(Path) : DefaultJson;
            var root = Json.Obj(Json.Parse(text));
            var run = Json.Obj(Json.Get(root, "run"));
            run[name] = cmd;
            root["run"] = run;
            Run[name] = cmd;
            System.IO.File.WriteAllText(Path, Json.WritePretty(root), new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex) { err = ex.Message; return false; }
    }

    // 往手机面板里加一个 run 按钮：分组固定叫「我的软件」（没有就建一个），页签不存在就新建页签。
    public static bool AddRunButton(string tabName, string label, string runName, string sub, out string err)
    {
        err = null;
        try
        {
            string text = System.IO.File.Exists(Path) ? System.IO.File.ReadAllText(Path) : DefaultJson;
            var root = Json.Obj(Json.Parse(text));
            var panels = Json.Obj(Json.Get(root, "panels"));
            var tabs = Json.Arr(Json.Get(panels, "tabs"));
            Dictionary<string, object> tab = null;
            for (int i = 0; i < tabs.Count; i++)
            {
                var t = Json.Obj(tabs[i]);
                if (string.Equals(Json.Str(Json.Get(t, "name"), ""), tabName, StringComparison.OrdinalIgnoreCase)) { tab = t; break; }
            }
            if (tab == null) { tab = new Dictionary<string, object>(); tab["name"] = tabName; tabs.Add(tab); }
            var groups = Json.Arr(Json.Get(tab, "groups"));
            Dictionary<string, object> grp = null;
            for (int i = 0; i < groups.Count; i++)
            {
                var g = Json.Obj(groups[i]);
                if (string.Equals(Json.Str(Json.Get(g, "title"), ""), "我的软件", StringComparison.OrdinalIgnoreCase)) { grp = g; break; }
            }
            if (grp == null)
            {
                grp = new Dictionary<string, object>();
                grp["title"] = "我的软件"; grp["cols"] = 2; grp["buttons"] = new List<object>();
                groups.Add(grp);
            }
            var btns = Json.Arr(Json.Get(grp, "buttons"));
            var b = new Dictionary<string, object>();
            b["label"] = label;
            if (!string.IsNullOrEmpty(sub)) b["sub"] = sub;
            b["act"] = "run"; b["name"] = runName;
            btns.Add(b);
            grp["buttons"] = btns;
            tab["groups"] = groups;
            panels["tabs"] = tabs;
            root["panels"] = panels;
            Panels = panels;
            System.IO.File.WriteAllText(Path, Json.WritePretty(root), new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex) { err = ex.Message; return false; }
    }

    // 数字项（如 autoWakeSec）同上；传负数表示"删掉这一项，回到内置默认"
    public static bool SetNum(string key, int val, out string err)
    {
        err = null;
        try
        {
            string text = System.IO.File.Exists(Path) ? System.IO.File.ReadAllText(Path) : DefaultJson;
            var root = Json.Obj(Json.Parse(text));
            if (val < 0) root.Remove(key); else root[key] = val;
            if (key == "autoWakeSec" && val >= 0) AutoWakeSec = val;
            System.IO.File.WriteAllText(Path, Json.WritePretty(root), new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex) { err = ex.Message; return false; }
    }

    // 字符串数组项（目前只有 elevatedRun）：界面里"点名"用，不让用户手改 JSON。
    public static bool SetList(string key, List<string> items, out string err)
    {
        err = null;
        try
        {
            string text = System.IO.File.Exists(Path) ? System.IO.File.ReadAllText(Path) : DefaultJson;
            var root = Json.Obj(Json.Parse(text));
            var arr = new List<object>();
            for (int i = 0; i < items.Count; i++)
                if (items[i] != null && items[i].Trim().Length > 0) arr.Add(items[i].Trim());
            root[key] = arr;
            if (key == "elevatedRun")
            {
                ElevatedRun = new List<string>();
                for (int i = 0; i < arr.Count; i++) ElevatedRun.Add((string)arr[i]);
            }
            if (key == "musicDirs")
            {
                MusicDirs = new List<string>();
                for (int i = 0; i < arr.Count; i++) MusicDirs.Add((string)arr[i]);
            }
            System.IO.File.WriteAllText(Path, Json.WritePretty(root), new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex) { err = ex.Message; return false; }
    }

    // 同播的标定偏移：毫秒，可正可负，所以单独一个 setter（SetNum 把负数解释成"删掉这一项"）
    public static bool SetDjOffset(int ms, out string err)
    {
        err = null;
        if (ms < -2000) ms = -2000;
        if (ms > 2000) ms = 2000;
        try
        {
            string text = System.IO.File.Exists(Path) ? System.IO.File.ReadAllText(Path) : DefaultJson;
            var root = Json.Obj(Json.Parse(text));
            root["djOffset"] = ms;
            System.IO.File.WriteAllText(Path, Json.WritePretty(root), new UTF8Encoding(false));
            DjOffset = ms;
            return true;
        }
        catch (Exception ex) { err = ex.Message; return false; }
    }
}
