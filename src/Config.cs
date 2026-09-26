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
    public static Dictionary<string, string> Run = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public static Dictionary<string, object> Panels = null;
    public static string Path = "";
    public static bool Loaded = false;

    const string DefaultJson = @"{
  ""port"": 8765,
  ""token"": """",
  ""keepAwake"": true,
  ""denyWhenLocked"": true,
  ""ddcOff"": false,
  ""run"": { ""声音设置"": ""start ms-settings:sound"" },
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
            Run = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in Json.Obj(Json.Get(root, "run"))) Run[kv.Key] = Json.Str(kv.Value, "");
            Panels = Json.Obj(Json.Get(root, "panels"));
            Loaded = true;
        }
        catch (Exception ex)
        {
            Error = "配置解析失败，已退回内置面板：" + ex.Message;
            var root = Json.Obj(Json.Parse(DefaultJson));
            Panels = Json.Obj(Json.Get(root, "panels"));
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
}
