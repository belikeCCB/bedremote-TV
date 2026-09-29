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

    const int MaxSeconds = 180;         // 整个宏最长跑三分钟：再久就是写错了，别让它一直占着

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
        foreach (string s in steps)
            if (s.IndexOf("c=macro", StringComparison.OrdinalIgnoreCase) >= 0)
            { err = "组合动作里不许再放组合动作（会绕圈）：" + s; return null; }

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

    static void Engine(string name, List<string> steps, string by)
    {
        Program.Log("[宏] 「" + name + "」开始（" + steps.Count + " 步，由 " + (string.IsNullOrEmpty(by) ? "local" : by) + " 触发）");
        try
        {
            for (int i = 0; i < steps.Count; i++)
            {
                if (StopReq) { Program.Log("[宏] 「" + name + "」被叫停，后面 " + (steps.Count - i) + " 步不做了"); break; }
                if ((Program.NowMs() - StartAt) / 1000 > MaxSeconds) { Program.Log("[宏] 「" + name + "」超过 " + MaxSeconds + " 秒，剩下的不做了"); break; }
                string step = steps[i];
                lock (Lk) CurI = i + 1;

                if (step.StartsWith("wait=", StringComparison.OrdinalIgnoreCase))
                {
                    int ms;
                    if (!int.TryParse(step.Substring(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out ms) || ms < 0)
                    { Program.Log("[宏] 「" + name + "」第 " + (i + 1) + " 步的等待时间看不懂：" + step); continue; }
                    if (ms > 60000) ms = 60000;
                    int slept = 0;
                    while (slept < ms && !StopReq) { int chunk = ms - slept < 120 ? ms - slept : 120; Thread.Sleep(chunk); slept += chunk; }
                    continue;
                }

                string ack = Program.RunCmd(step, by);
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
        catch (Exception ex) { Program.Log("[宏] 「" + name + "」跑飞了：" + ex.Message); }
        finally
        {
            lock (Lk) { Busy = false; StopReq = false; CurI = 0; CurN = 0; }
            Program.Log("[宏] 「" + name + "」结束");
            Program.Broadcast("{\"e\":\"macro\",\"n\":\"" + Program.Json(name) + "\",\"done\":true}");
        }
    }

    static void Tell(string name, int i, int n, string step, string ack)
    {
        Program.Log("[宏] " + name + " " + i + "/" + n + " " + step + " -> " + (ack.Length == 0 ? "ok" : ack));
        Program.Broadcast("{\"e\":\"macro\",\"n\":\"" + Program.Json(name) + "\",\"i\":" + i + ",\"of\":" + n +
                          ",\"s\":\"" + Program.Json(step) + "\",\"ack\":\"" + Program.Json(ack) + "\"}");
    }
}
