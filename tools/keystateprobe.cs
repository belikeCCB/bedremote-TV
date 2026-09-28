// 探针：只看"系统认为哪些键现在是按着的"，不动任何窗口、不发消息。
// 用它验证服务端 frame 的按下/差分/自动松手是不是真的生效（GetAsyncKeyState 读的是全局键态，
// SendInput 注入会更新它）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

static class Probe
{
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);

    static readonly Dictionary<string, int> Vk = new Dictionary<string, int>
    {
        { "W", 0x57 }, { "A", 0x41 }, { "S", 0x53 }, { "D", 0x44 },
        { "SHIFT", 0x10 }, { "CTRL", 0x11 }, { "SPACE", 0x20 },
        { "LBUTTON", 0x01 }, { "RBUTTON", 0x02 },
    };

    static int Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : "probe.log";
        int secs = args.Length > 1 ? int.Parse(args[1]) : 20;
        var held = new Dictionary<string, bool>();
        var sb = new StringBuilder();
        foreach (var k in Vk.Keys) held[k] = (GetAsyncKeyState(Vk[k]) & 0x8000) != 0;
        File.WriteAllText(outPath, "start held=" + Now() + "\n");
        DateTime end = DateTime.Now.AddSeconds(secs);
        while (DateTime.Now < end)
        {
            Thread.Sleep(25);
            foreach (var kv in Vk)
            {
                bool now = (GetAsyncKeyState(kv.Value) & 0x8000) != 0;
                if (now != held[kv.Key])
                {
                    held[kv.Key] = now;
                    File.AppendAllText(outPath, (now ? "DOWN " : "UP   ") + kv.Key + "  " +
                        DateTime.Now.ToString("HH:mm:ss.fff") + "\n");
                }
            }
        }
        File.AppendAllText(outPath, "end " + Now() + "\n");
        return 0;
    }

    static string Now() { return DateTime.Now.ToString("HH:mm:ss.fff"); }
}
