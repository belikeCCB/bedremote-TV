using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;

// ===================== 两边一起放同一首歌 =====================
//
// 场景：人躺床上，画面和声音本来走 HDMI 到电视；他想让**手机耳机也播同一份**，
// 两份声音叠在一起（他要的是"声场变宽"，不是晚半秒的回声）。
//
// 三条被划掉的路线，别再走回头：
//   * 不装任何第三方软件（VoiceMeeter 一类被明确否掉）；
//   * 不把电脑的音频流转发给手机（"音频不是传输的"）—— 两台各自播自己那一份；
//   * 不依赖手机麦克风 / 安全上下文（他那台鸿蒙手机大概率拿不到权限）。
//
// 于是架构只有一种合理形状：**手机是主，电脑是从，所有校正都作用在电脑这一侧**。
// 电脑这一侧完全在我们手里（我们自己的播放页 + 自己的音频管线），想延后多少毫秒都行；
// 手机那一侧能做的只有"播"和"报我播到几秒"，别的一律不指望它。
//
// 对时为什么不需要 NTP：手机的每次上报都由**服务器盖上自己的钟**（Dj.Seen），
// 而电脑那个播放页和服务器在同一台机器上、用的是同一个钟 —— 所以电脑端只要
// "手机报的位置 + 服务器戳的时间 - 手机当时在什么时间" 就能外推到"手机现在在哪"。
// 剩下的误差是"手机→服务器"这一段单向延迟，它基本是个常数，
// 正好被"现在重合了"那一下标定吸收掉（见 Offset）。

static class Dj
{
    // ---- 手机报上来的状态（手机是主，所以这份是"参考"） ----
    static public double Pos = -1;          // 手机报的播放位置（秒）
    static public long Stamp = 0;           // 服务器收到那一刻（Tk()）
    static public double PcPos = -1;        // 电脑页报的位置，只给手机显示差值用
    static public double PcRate = 1;        // 它当时的倍速（追帧时不是 1.0，外推必须带上）
    static public long PcStamp = 0;
    // 两边播的是同一个 URL（都由这台电脑发出去），所以"同一份内容"不用靠运气。
    // 这里存的就是那个 URL（/media?f=…），谁拿到它都能直接喂给自己的 <audio>。
    static public string Src = "";
    static public string Title = "";        // 只给人看的名字（列表里那一条）
    static public bool Playing = false;
    // 直播流（电台的 HLS 常常是 live）没有可比的"第几秒"：两边各挂在直播时间轴的尾巴上，
    // 拿位置去追只会越追越歪。所以直播只做"一起播 / 一起停"，不做位置校正。
    static public bool Live = false;
    static public double Vol = 1.0;         // 电脑这一份的音量（压低电视、把主声交给耳机，靠它）
    static public long BeatN = 0;           // 收到过多少次上报（掉线检测用）

    static readonly object Lk = new object();

    // 手机这一份到耳朵，比电脑那一份早/晚多少毫秒 —— 一个"这台电视 + 这副耳机"的常数。
    // 电脑侧能精确知道自己什么时候把声音送出去，但送出去之后经过 HDMI、电视处理、
    // 几米空气、再加耳机自己的延迟，这一段**没有任何软件能测**（耳机塞在耳朵里，
    // 麦克风听不见它），所以只能由耳朵标定一次。
    static public int Offset = Config.DjOffset;

    static long Now { get { return Program.NowMs(); } }

    // 手机"现在"在放第几秒 —— 外推：报的时候是几点、过了多久、有没有在播
    static public double PhoneNow()
    {
        lock (Lk)
        {
            if (Pos < 0) return -1;
            if (!Playing) return Pos;                       // 暂停了就不往前推
            double dt = (Now - Stamp) / 1000.0;
            if (dt < 0) dt = 0;
            if (dt > 3) return -1;                          // 太久没心跳：手机页可能已经关了，别拿旧位置去追
            return Pos + dt;
        }
    }

    // 电脑该跟着走到的目标位置 = 手机现在的位置 + 标定偏移
    static public double Target()
    {
        lock (Lk) { if (Live) return -1; }          // 直播：没有"该跟到第几秒"这回事
        double p = PhoneNow();
        if (p < 0) return -1;
        return Math.Max(0, p + Offset / 1000.0);
    }

    static public void Beat(string pos, string playing, string src, string title, string live)
    {
        double p;
        if (!double.TryParse(pos, NumberStyles.Float, CultureInfo.InvariantCulture, out p) || p < 0) return;
        lock (Lk)
        {
            Pos = p; Stamp = Now; Playing = (playing == "1"); Live = (live == "1");
            if (!string.IsNullOrEmpty(src)) Src = src;
            if (!string.IsNullOrEmpty(title)) Title = title;
            BeatN++;
        }
    }

    static public void SetVol(string v)
    {
        double x;
        if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out x)) return;
        if (x < 0) x = 0; if (x > 1) x = 1;
        lock (Lk) { Vol = x; }
    }

    static public void PcBeat(string pos, string rate)
    {
        double p, r;
        if (!double.TryParse(pos, NumberStyles.Float, CultureInfo.InvariantCulture, out p) || p < 0) return;
        // 追帧时电脑是 1.04 倍速在跑的 —— 外推不按它真实的倍速走，手机上看到的差值就是假的
        if (!double.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out r) || r <= 0 || r > 4) r = 1;
        lock (Lk) { PcPos = p; PcRate = r; PcStamp = Now; }
    }

    static public void Stop()
    {
        lock (Lk) { Playing = false; Pos = -1; PcPos = -1; }
    }

    // 给电脑播放页轮询的：目标位置 + 该不该播 + 在放什么
    static public string StateJson()
    {
        double t = Target();
        bool play; string src, title; double vol; bool live;
        lock (Lk) { play = Playing; src = Src; title = Title; vol = Vol; live = Live; }
        var sb = new StringBuilder();
        sb.Append("{\"target\":").Append(t < 0 ? "-1" : t.ToString("0.000", CultureInfo.InvariantCulture));
        sb.Append(",\"play\":").Append(play ? "true" : "false");
        sb.Append(",\"live\":").Append(live ? "true" : "false");
        sb.Append(",\"off\":").Append(Offset);
        sb.Append(",\"vol\":").Append(vol.ToString("0.00", CultureInfo.InvariantCulture));
        sb.Append(",\"src\":\"").Append(Program.Json(src)).Append("\"");
        sb.Append(",\"title\":\"").Append(Program.Json(title)).Append("\"");
        lock (Lk)
        {
            sb.Append(",\"beat\":").Append(BeatN);
            sb.Append(",\"age\":").Append(Pos < 0 ? -1 : Math.Max(0, (Now - Stamp) / 1000));
        }
        sb.Append('}');
        return sb.ToString();
    }

    // 给手机显示的：两边差多少毫秒（手机位置 - 电脑位置，减掉标定偏移才是"该不该再调"）
    static public string GapJson()
    {
        double ph = PhoneNow(), pc = -1;
        bool live;
        lock (Lk) { live = Live; if (PcPos >= 0 && Now - PcStamp < 3000) pc = PcPos + (Now - PcStamp) / 1000.0 * PcRate; }
        if (live) return "{\"gap\":0,\"live\":true}";      // 直播不比位置，手机上显示"两边都在播"就够了
        if (ph < 0 || pc < 0) return "{\"gap\":-99999}";
        int gapMs = (int)Math.Round((pc - Offset / 1000.0 - ph) * 1000.0);
        return "{\"gap\":" + gapMs + ",\"phone\":" + ph.ToString("0.00", CultureInfo.InvariantCulture) +
               ",\"pc\":" + pc.ToString("0.00", CultureInfo.InvariantCulture) + ",\"off\":" + Offset + "}";
    }

    static public void SetOffset(int ms)
    {
        if (ms < -2000) ms = -2000;
        if (ms > 2000) ms = 2000;
        Offset = ms;
        string err; Config.SetDjOffset(ms, out err);
        Program.Log("[同播] 偏移标定为 " + ms + " 毫秒（这台电视 + 这副耳机的常数，换耳机再点一次）");
    }
}

// ===================== 把音乐文件发给两边 =====================
//
// 两边播的是**同一个 URL**（都由这台电脑发出去），所以"同一份内容"这件事不用靠运气。
// 要能 seek 就得支持 HTTP Range —— 浏览器对 `<audio>` 拖进度条、以及电脑端被命令
// "跳到 12.382 秒"，都会发带 Range 的请求；不支持 206 的话手机上根本拖不动。
static class Media
{
    static readonly string[] Ext = { ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".flac", ".mka", ".mp4", ".m4v", ".mkv", ".webm" };

    static string Mime(string f)
    {
        string e = Path.GetExtension(f).ToLowerInvariant();
        if (e == ".mp3") return "audio/mpeg";
        if (e == ".m4a" || e == ".mp4" || e == ".m4v") return "audio/mp4";
        if (e == ".aac") return "audio/aac";
        if (e == ".ogg" || e == ".opus") return "audio/ogg";
        if (e == ".wav") return "audio/wav";
        if (e == ".flac" || e == ".mka") return "audio/flac";
        if (e == ".mkv") return "video/x-matroska";
        if (e == ".webm") return "video/webm";
        return "application/octet-stream";
    }

    // 只允许配置里列出的目录（musicDirs）之内的文件。路径先规范化再比前缀，
    // 这样 "..\..\" 和大小写、短名都绕不过去。
    static public bool InRoots(string full, out string served)
    {
        served = null;
        var roots = Config.MusicDirs;
        if (roots == null || roots.Count == 0) return false;
        string canon;
        try { canon = Path.GetFullPath(full); } catch { return false; }
        foreach (string raw in roots)
        {
            if (string.IsNullOrEmpty(raw)) continue;
            string root;
            try { root = Path.GetFullPath(raw.TrimEnd('\\', '/')); } catch { continue; }
            if (!Directory.Exists(root)) continue;
            if (!canon.StartsWith(root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(canon)) continue;
            served = canon;
            return true;
        }
        return false;
    }

    static public string ListJson()
    {
        var sb = new StringBuilder("[");
        int n = 0;
        var roots = Config.MusicDirs;
        if (roots != null)
        {
            foreach (string raw in roots)
            {
                if (string.IsNullOrEmpty(raw)) continue;
                string root;
                try { root = Path.GetFullPath(raw.TrimEnd('\\', '/')); } catch { continue; }
                if (!Directory.Exists(root)) continue;
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(root); } catch { continue; }
                foreach (string f in files)
                {
                    string e = Path.GetExtension(f).ToLowerInvariant();
                    bool hit = false;
                    foreach (string x in Ext) if (x == e) { hit = true; break; }
                    if (!hit) continue;
                    if (n > 0) sb.Append(',');
                    sb.Append("{\"n\":\"").Append(Program.Json(Path.GetFileNameWithoutExtension(f))).Append('"');
                    sb.Append(",\"u\":\"/media?f=").Append(Uri.EscapeDataString(f)).Append("\"}");
                    n++;
                    if (n >= 400) break;
                }
                if (n >= 400) break;
            }
        }
        sb.Append(']');
        return sb.ToString();
    }

    // 流式发一个文件，支持 Range。注意这里不能用 Write()：它一次把整个 body 塞进内存，
    // 一首歌十几兆、两边同时拖进度就是几十兆的临时分配。
    static public bool Serve(Stream s, TcpClient c, Program.Req r, string full)
    {
        var fi = new FileInfo(full);
        long total = fi.Length;
        long from = 0, to = total - 1;
        string rng = r.Range;
        if (!string.IsNullOrEmpty(rng) && rng.StartsWith("bytes="))
        {
            string v = rng.Substring(6).Trim();
            int dash = v.IndexOf('-');
            if (dash >= 0)
            {
                long a = 0, b = 0;
                string sa = v.Substring(0, dash).Trim(), sb2 = v.Substring(dash + 1).Trim();
                bool haveA = sa.Length > 0 && long.TryParse(sa, out a);
                bool haveB = sb2.Length > 0 && long.TryParse(sb2, out b);
                if (haveA && haveB) { from = a; to = Math.Min(b, total - 1); }
                else if (haveA) { from = a; to = total - 1; }
                else if (haveB) { from = Math.Max(0, total - b); to = total - 1; }   // 尾部 N 字节
            }
        }
        if (from > to || from >= total)
        {
            Program.WriteHead(s, 416, "Content-Range: bytes */" + total);
            return true;
        }
        bool partial = from > 0 || to < total - 1;
        long len = to - from + 1;
        var head = new StringBuilder();
        head.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
        head.Append("Content-Type: ").Append(Mime(full)).Append("\r\n");
        head.Append("Content-Length: ").Append(len).Append("\r\n");
        head.Append("Accept-Ranges: bytes\r\n");
        if (partial) head.Append("Content-Range: bytes ").Append(from).Append('-').Append(to).Append('/').Append(total).Append("\r\n");
        head.Append("Cache-Control: no-store\r\n");
        head.Append("Connection: close\r\n\r\n");
        byte[] hb = Encoding.ASCII.GetBytes(head.ToString());
        s.Write(hb, 0, hb.Length);

        byte[] buf = new byte[64 * 1024];
        using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024))
        {
            fs.Seek(from, SeekOrigin.Begin);
            long left = len;
            while (left > 0)
            {
                int want = (int)Math.Min(buf.Length, left);
                int got = fs.Read(buf, 0, want);
                if (got <= 0) break;
                try { s.Write(buf, 0, got); } catch { break; }   // 播放器中途关连接是常态，不是错误
                left -= got;
            }
        }
        s.Flush();
        return true;
    }
}
