using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

// ===================== 组合动作（宏）：一键连发几条指令 =====================
//
// 为什么值得做：面板上一个按钮只能干一件事。而"睡前"其实是三件事——暂停播放、把电视切过去、
// 关显示器；"开始剪片子"是四件事——开播放器、开素材目录、把主屏交给电视、把音量调低。
// 没有宏的话他要么按四下，要么在手机上做一个"连点"（但手机一锁屏/切后台就断了，
// 而"睡前"这个动作按完就是把手机放下）。
//
// 所以宏**跑在电脑上**：按一次，手机可以立刻死掉，剩下的步骤照样走完。
//
// 步骤的形状是刻意做小的：一步 = 一句 /cmd 的查询串（"c=key&k=playpause"），
// 或者一句 "wait=400"（等 400 毫秒）。不做成第二种"动作语言"，因为那样电脑上就得有第二份
// "act 是什么意思"的解释表 —— 手机 fire() 一份、编辑器试触发一份、宏再来一份，三份早晚对不上
// （CONTRIBUTING 里"同一件事只许有一处夹边界"是同一个道理）。
// 现在宏能用的动作 = 手机和 curl 能用的动作，一条不多一条不少。
//
// 安全边界要说清：宏**不新增任何能力** —— 每一步走的都是同一个 Dispatch，受同一套
// 口令/设备凭据、锁屏守卫、run 白名单约束。它改变的是"一次点击造成多少动作"，
// 所以 confirm 这种"人手滑"的护栏在宏这里更该当回事（手机上按按钮照旧会先问）。
static class MacroRun
{
    static readonly object Lk = new object();
    static volatile bool Busy = false;
    static volatile bool StopReq = false;
    static string Cur = "";
    static int CurI = 0, CurN = 0;
    static long StartAt = 0;

    const int MaxSeconds = 180;         // 整段最长三分钟（每步之间才检查，所以是"别一直占着"，不是硬闹钟）

    // 单步的耐心。20 秒是很宽的数字：正常一步是毫秒级（发个键、切个屏），
    // 慢的那些（改显示配置带 DDC 重试、救屏）实测也就几秒。
    // 超过 20 秒没回音的，基本都是"永远不会回"的那一类。
    const int StepSeconds = 20;

    // 上一步的结果，留在内存里给 /macrostatus 看。
    // 为什么要留：进度本来是走 SSE 推的，而"睡前"这种宏按完就是把手机扣下 —— 屏一锁，
    // SSE 没了，第 3 步失败就没人知道，只剩本机日志。界面上至少能回看"最后一次跑到哪、成了没"。
    static string Last = "";

    static public string ListJson()
    {
        var sb = new StringBuilder("[");
        int i = 0;
        foreach (KeyValuePair<string, List<string>> kv in Config.Macros)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"name\":\"").Append(Program.Json(kv.Key))
              .Append("\",\"steps\":").Append(kv.Value.Count).Append('}');
            i++;
        }
        sb.Append(']');
        return sb.ToString();
    }

    // 编辑器要的是**整段**（含每一步的内容），上面那个摘要不够它用。
    static public string FullJson()
    {
        var sb = new StringBuilder("{");
        bool first = true;
        foreach (KeyValuePair<string, List<string>> kv in Config.Macros)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(Program.Json(kv.Key)).Append("\":[");
            for (int i = 0; i < kv.Value.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(Program.Json(kv.Value[i])).Append('"');
            }
            sb.Append(']');
        }
        sb.Append('}');
        return sb.ToString();
    }

    // 给界面/手机看"现在有没有在跑、跑到第几步"，进度本身走 SSE 推
    static public string StatusJson()
    {
        var sb = new StringBuilder();
        lock (Lk)
        {
            sb.Append("{\"busy\":").Append(Busy ? "true" : "false")
              .Append(",\"name\":\"").Append(Program.Json(Busy ? Cur : "")).Append("\"")
              .Append(",\"i\":").Append(CurI)
              .Append(",\"n\":").Append(CurN)
              .Append(",\"secs\":").Append(Busy ? Math.Max(0, (Program.NowMs() - StartAt) / 1000) : 0)
              .Append(",\"last\":\"").Append(Program.Json(Last)).Append("\"")
              .Append('}');
        }
        return sb.ToString();
    }

    // 起一个宏：立刻返回，跑在后台线程上（HTTP 请求不能陪着等 —— 手机只有 6 条并发连接，
    // 占着一条等十秒，触控板就会开始排队）。
    static public string Start(string name, string by, out string err)
    {
        err = null;
        List<string> steps;
        if (!Config.Macros.TryGetValue(name ?? "", out steps))
        {
            err = "没有这个组合动作（" + (name ?? "") + "）。电脑上 bedremote.json 的 macros 段里写一个再来。";
            return null;
        }
        // 不许套宏：两个宏互相引用就变成死循环，而线程是池里的，绕起来没人收尸。
        // 这里要**看这一步的 c= 到底是什么**再判断。以前是"整串里含 c=macro 就算嵌套"，
        // 于是一条 notify 的文案里恰好出现这 6 个字符也会被误杀 —— 不危险，但会让人
        // 完全看不懂"我这条怎么存不上"。（wait=400 这种没有 c=，Q 返回空串，自然放行。）
        foreach (string s in steps)
        {
            string c = Q(s, "c");
            if (c == "macro" || c == "macros" || c == "macrostatus" || c == "macrostop")
            { err = "组合动作里不许再放组合动作（会绕圈）：" + s; return null; }
        }

        lock (Lk)
        {
            if (Busy) { err = "上一个组合动作还在跑（" + Cur + " 第 " + CurI + "/" + CurN + " 步）；要它停就发 c=macrostop"; return null; }
            Busy = true; StopReq = false; Cur = name; CurI = 0; CurN = steps.Count; StartAt = Program.NowMs();
        }
        List<string> copy = new List<string>(steps);        // 别和"重载配置"抢同一个 List
        ThreadPool.QueueUserWorkItem(delegate { Engine(name, copy, by); });
        return "started";
    }

    static public string Stop()
    {
        lock (Lk)
        {
            if (!Busy) return "现在没有在跑的组合动作";
            StopReq = true;
            return "已请求停止：" + Cur + "（当前那一步做完就停）";
        }
    }

    // 从一步里取出某个参数的值。只做最小限度的拆解（一步就是 "c=key&k=playpause" 这种查询串），
    // 不做 URL 解码：这里的值是拿来**比动词**的，编码过的东西已经不是合法动词了。
    static string Q(string step, string key)
    {
        if (string.IsNullOrEmpty(step)) return "";
        foreach (string kv in step.Split('&'))
        {
            int eq = kv.IndexOf('=');
            if (eq <= 0) continue;
            if (string.Compare(kv.Substring(0, eq), key, StringComparison.OrdinalIgnoreCase) == 0)
                return kv.Substring(eq + 1);
        }
        return "";
    }

    static void Engine(string name, List<string> steps, string by)
    {
        Program.Log("[宏] 「" + name + "」开始（" + steps.Count + " 步，由 " + (string.IsNullOrEmpty(by) ? "local" : by) + " 触发）");
        try
        {
            for (int i = 0; i < steps.Count; i++)
            {
                if (StopReq) { Done(name, "被叫停，后面 " + (steps.Count - i) + " 步不做了"); break; }
                if ((Program.NowMs() - StartAt) / 1000 > MaxSeconds) { Done(name, "超过 " + MaxSeconds + " 秒，剩下的不做了"); break; }
                string step = steps[i];
                lock (Lk) CurI = i + 1;

                if (step.StartsWith("wait=", StringComparison.OrdinalIgnoreCase))
                {
                    int ms;
                    if (!int.TryParse(step.Substring(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out ms) || ms < 0)
                    { Done(name, "第 " + (i + 1) + " 步的等待时间看不懂：" + step); continue; }
                    if (ms > 60000) ms = 60000;
                    int slept = 0;
                    while (slept < ms && !StopReq) { int chunk = ms - slept < 120 ? ms - slept : 120; Thread.Sleep(chunk); slept += chunk; }
                    continue;
                }

                // 一步最多等 StepSeconds 秒，而且是在**另一条线程**上等。
                // 为什么：一步可能撞进一个永远不返回的调用里（`c=open` 开了台已经关机的机器、
                // 剪贴板被别的进程占着不放），而这段是抱着 Busy 标记跑的 —— 一个宏卡死 =
                // 以后每个宏都被"上一个还在跑"拒掉，整个功能在界面上看着就是坏了，只能重启进程。
                // 现在最坏变成"这一个宏停在这一步"，宏系统本身还活着。
                // 超过期限**不再往下走**：第 2 步都没做完，第 3 步"关显示器"照做，那是更糟的结果。
                string ack;
                {
                    string sq = step;
                    string got = "";
                    var done = new ManualResetEvent(false);
                    // 用一条**真线程**而不是线程池：这条宏自己就是池里跑起来的，
                    // 再往池里塞一步，池子忙的时候那一步排不上队 = 被误判成"卡住超时"。
                    var th = new Thread(delegate ()
                    {
                        try { got = Program.RunCmd(sq, by); }
                        catch (Exception ex) { got = "err:" + ex.Message; }
                        done.Set();
                    });
                    th.IsBackground = true;
                    th.Start();
                    if (!done.WaitOne(TimeSpan.FromSeconds(StepSeconds)))
                    {
                        // 只有"没回音"才停整个宏；普通的 err（比如某一步的键位不对）照旧继续往下走，
                        // 这是这个功能一开始就定下的语义（一步不成不牵连后面）。
                        Tell(name, i + 1, steps.Count, step, "stuck");
                        Done(name, "第 " + (i + 1) + " 步超过 " + StepSeconds + " 秒没回音，剩下的不做了（那一步可能还在后台跑着）");
                        break;
                    }
                    ack = got;
                }
                if (ack == "locked")
                {
                    // 锁屏/安全桌面挡下来了：后面每一步都会一样挡，没必要把日志刷满，直接收工
                    Program.Log("[宏] 「" + name + "」第 " + (i + 1) + " 步被锁屏挡住，剩下的不做了");
                    Tell(name, i + 1, steps.Count, step, "locked");
                    break;
                }
                Tell(name, i + 1, steps.Count, step, ack.Length > 90 ? ack.Substring(0, 90) : ack);
                if (ack.StartsWith("err:")) Program.Log("[宏] 「" + name + "」第 " + (i + 1) + " 步没成（继续往下走）：" + ack);
            }
        }
        catch (Exception ex) { Done(name, "跑飞了：" + ex.Message); }
        finally
        {
            lock (Lk) { Busy = false; StopReq = false; CurI = 0; CurN = 0; }
            Program.Log("[宏] 「" + name + "」结束");
            Program.Broadcast("{\"e\":\"macro\",\"n\":\"" + Program.Json(name) + "\",\"done\":true}");
        }
    }

    // 没走完的几种收场：既进日志，也留在 /macrostatus 里（手机锁屏后没人看 SSE，日志是唯一的证据，
    // 但界面上想回看"昨晚那个宏到底成没成"也得有个地方查）。
    static void Done(string name, string note)
    {
        lock (Lk) Last = name + " " + note;
        Program.Log("[宏] 「" + name + "」" + note);
    }

    static void Tell(string name, int i, int n, string step, string ack)
    {
        string line = name + " " + i + "/" + n + " " + step + " -> " + (ack.Length == 0 ? "ok" : ack);
        lock (Lk) Last = line;                                  // 给 /macrostatus 留着（手机锁屏时只剩这一份）
        Program.Log("[宏] " + line);
        Program.Broadcast("{\"e\":\"macro\",\"n\":\"" + Program.Json(name) + "\",\"i\":" + i + ",\"of\":" + n +
                          ",\"s\":\"" + Program.Json(step) + "\",\"ack\":\"" + Program.Json(ack) + "\"}");
    }
}
