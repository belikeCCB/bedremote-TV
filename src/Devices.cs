using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

// ===================== 已配对的设备（可以一台一台踢掉） =====================
//
// 为什么需要它：口令是**一把共用钥匙** —— 你给过朋友一次二维码，他就永远能进来，
// 而且你在电脑上根本分不清"现在这台是谁"。设备凭据解决的就是这个：
// 手机第一次带着正确口令扫码进来时，我们记下它自己那串随机 id（存在它自己的
// localStorage 里），以后它每次请求都带着这串 id，不用再输口令；
// 你哪天想让它进不来，在名单里点一下「踢掉」就行 —— 只踢它这一台，别的设备不受影响。
//
// 说清楚它挡不了什么：id 存在那台手机的浏览器里，**换了浏览器/清了站点数据就要重新配对**
// （这是应该的，不是 bug）；同网段抓包能抄到 id（所以它等价于一个不能过期的口令 ——
// 介意就改口令，改口令会让所有设备都要重新扫一次码，这正是要的效果）。
static class Devs
{
    class Rec { public string Id = ""; public string Name = ""; public long Added = 0; public long Last = 0; }

    static readonly Dictionary<string, Rec> Map = new Dictionary<string, Rec>(StringComparer.Ordinal);
    static readonly object Lk = new object();
    static string File_ = "";
    static bool Dirty = false;

    const int Max = 50;                       // 一个局域网里不该有 50 台手机，超了就丢最久没来的

    static long Tk() { return DateTime.Now.Ticks; }

    // 设备编号是手机自己生成的随机串（浏览器里没有可信的"设备指纹"可用，只能让它自己存）。
    // 我们只认 8~64 位的 [A-Za-z0-9_-]：太短的当垃圾丢掉，免得谁都能拿 "a" 往名单里灌。
    static public bool Shape(string id)
    {
        if (id == null || id.Length < 8 || id.Length > 64) return false;
        for (int i = 0; i < id.Length; i++)
        {
            char c = id[i];
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_') continue;
            return false;
        }
        return true;
    }

    static public void Init(string dataDir)
    {
        File_ = Path.Combine(dataDir, "devices.json");
        try
        {
            if (!File.Exists(File_)) return;
            object root = Json.Parse(File.ReadAllText(File_, Encoding.UTF8));
            foreach (object o in Json.Arr(root))
            {
                var r = Json.Obj(o);
                string id = Json.Str(Json.Get(r, "id"), "");
                if (id.Length == 0) continue;
                var rec = new Rec();
                rec.Id = id;
                if (!Shape(rec.Id)) continue;
                rec.Name = Json.Str(Json.Get(r, "name"), "");
                // 文件里存的是"秒"（人读得懂、手改也不会炸），内存里存 Tick。
                // 这两头必须成对换算：以前 Init 直接把秒塞进 Tick 字段，于是每次重启
                // 再落盘就再除一次 1e7，配对的日期两三次之后变成 0（回归测试抓到的）。
                rec.Added = (long)Json.Num(Json.Get(r, "added"), 0) * TimeSpan.TicksPerSecond;
                rec.Last = (long)Json.Num(Json.Get(r, "last"), 0) * TimeSpan.TicksPerSecond;
                Map[id] = rec;
                while (Map.Count > Max) DropOldest();      // 手改过的文件塞进来太多行，丢最久没来的
            }
            Program.Log("[设备] 读到 " + Map.Count + " 台已配对设备");
        }
        catch (Exception ex) { Program.Log("[设备] 名单读失败（当空的处理）：" + ex.Message); }
    }

    // 带对了口令的请求顺手把这台设备记下来 —— 这就是"配对"那一刻，不需要额外交互
    static public void Pair(string id, string name)
    {
        if (string.IsNullOrEmpty(id)) return;
        lock (Lk)
        {
            Rec r;
            if (Map.TryGetValue(id, out r))
            {
                r.Last = Tk();
                if (!string.IsNullOrEmpty(name)) r.Name = Trim(name);
                return;
            }
            r = new Rec();
            r.Id = id; r.Name = Trim(string.IsNullOrEmpty(name) ? "未命名设备" : name);
            r.Added = Tk(); r.Last = r.Added;
            Map[id] = r;
            Dirty = true;
            // 上限在这儿也要生效。以前只有启动读文件时裁剪，于是运行期"每次换个 id 就挤进来一条"，
            // 名单能长到无限：界面上根本没法看，文件也一路写盘。满了就丢最久没来的（不丢新来的）。
            int evicted = 0;
            while (Map.Count > Max) { DropOldest(); evicted++; }
            Program.Log("[设备] 配对了新设备：「" + r.Name + "」 " + id.Substring(0, Math.Min(8, id.Length)) + "…"
                + (evicted > 0 ? "（名单超过 " + Max + " 台，挤掉了 " + evicted + " 台最久没来的）" : ""));
            Save();
        }
    }

    // 设备名是**那台设备自己报上来的字符串**（?dn=），它会进本机日志。
    // 所以除了截长度，还要把控制字符去掉：不滤的话 "x\n[口令] 已清掉口令" 这种名字
    // 就能在 bedremote.log 里伪造出一行像模像样的记录 —— 内容伤不了人，
    // 但日志是"这台电脑最近给谁干过什么"的唯一凭据，被掺假比被泄露更难受。
    static string Trim(string s)
    {
        if (string.IsNullOrEmpty(s)) return "未命名设备";
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length && sb.Length < 40; i++)
        {
            char c = s[i];
            if (c < ' ' || c == '\x7f') continue;
            sb.Append(c);
        }
        string r = sb.ToString().Trim();
        return r.Length == 0 ? "未命名设备" : r;
    }

    static public bool Ok(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        lock (Lk)
        {
            Rec r;
            if (!Map.TryGetValue(id, out r)) return false;
            r.Last = Tk();
            Dirty = true;
            return true;
        }
    }

    static public bool Kick(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        lock (Lk)
        {
            if (!Map.Remove(id)) return false;
            Dirty = true;
            Save();
        }
        return true;
    }

    static void DropOldest()
    {
        Rec worst = null;
        foreach (KeyValuePair<string, Rec> kv in Map)
            if (worst == null || kv.Value.Last < worst.Last) worst = kv.Value;
        if (worst != null) Map.Remove(worst.Id);
    }

    static public int Count() { lock (Lk) return Map.Count; }

    // 名单里没有这台设备时返回空串（调用方拿它判断"存不存在"，省一次 Dictionary 往外露）
    static public string NameOf(string id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        lock (Lk) { Rec r; return Map.TryGetValue(id, out r) ? r.Name : ""; }
    }

    // 改口令 = 把之前配出去的凭据全部作废。不然"换锁"只换了新钥匙，
    // 旧钥匙还留在名单里，陌生人拿着改前的口令照样能进来 —— 那这次口令就白改了。
    static public int Clear()
    {
        lock (Lk)
        {
            int n = Map.Count;
            Map.Clear();
            Dirty = true;
            Save();
            return n;
        }
    }

    // 定期落盘：每次请求都写文件太蠢，攒着，名单变了且过了几秒才写
    static public void Tick()
    {
        lock (Lk) { if (!Dirty) return; Save(); }
    }

    static void Save()
    {
        try
        {
            var sb = new StringBuilder("[");
            int n = 0;
            foreach (KeyValuePair<string, Rec> kv in Map)
            {
                if (n > 0) sb.Append(',');
                sb.Append("{\"id\":\"").Append(Program.Json(kv.Value.Id))
                  .Append("\",\"name\":\"").Append(Program.Json(kv.Value.Name))
                  .Append("\",\"added\":").Append(kv.Value.Added / 10000000)
                  .Append(",\"last\":").Append(kv.Value.Last / 10000000).Append('}');
                n++;
            }
            sb.Append(']');
            Config.WriteAtomic(File_, sb.ToString());
            Dirty = false;
        }
        catch (Exception ex) { Program.Log("[设备] 名单写失败：" + ex.Message); }
    }

    static public string ListJson()
    {
        var now = Tk();
        var rows = new List<Rec>();
        lock (Lk) foreach (KeyValuePair<string, Rec> kv in Map) rows.Add(kv.Value);
        rows.Sort(delegate(Rec a, Rec b) { return b.Last.CompareTo(a.Last); });
        var sb = new StringBuilder("[");
        for (int i = 0; i < rows.Count; i++)
        {
            Rec r = rows[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":\"").Append(Program.Json(r.Id))
              .Append("\",\"name\":\"").Append(Program.Json(r.Name))
              .Append("\",\"ago\":").Append(Math.Max(0, (now - r.Last) / 10000000))
              .Append(",\"added\":").Append(r.Added / 10000000).Append('}');
        }
        sb.Append(']');
        return sb.ToString();
    }
}
