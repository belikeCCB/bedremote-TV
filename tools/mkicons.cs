// 生成 bedremote 的全部图像资产 —— 一处画法，三处用：
//   www\icon-192.png、www\icon-512.png      PWA/安卓主屏幕图标（manifest 要真 PNG，浏览器要下载它）
//   assets\bedremote.ico                   **编进 exe 的图标**（-win32icon），没有它资源管理器里就是个空白程序图标
//   docs\social-preview.png                GitHub 仓库的分享卡片（1280x640，Settings 页面里上传那张）
// 界面/托盘上那张也是同一个画法（`src\Gui.cs` 的 AppIcon），只是运行时现画；改样子请同时改那一份。
//
// 重新生成（-codepage:65001 **不能省**：这个文件里有中文字面量，默认代码页会把它们读成乱码，
// 生成的图上一行豆腐块 —— 而图是二进制的，CI 看不出来，只能靠肉眼看输出）：
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe -nologo -target:exe -platform:x64 `
//     -codepage:65001 -r:System.Drawing.dll -out:tools\mkicons.exe tools\mkicons.cs
//   tools\mkicons.exe
// 输出路径都是相对**当前目录**的，请在仓库根目录跑。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

static class MkIcons
{
    // 设计网格是 32x32，所有形状按这个网格缩放（跟 Gui.cs 的 AppIcon 对齐）
    static readonly Color Green = Color.FromArgb(24, 140, 90);
    static readonly Color Ink = Color.FromArgb(20, 22, 26);        // 跟页面背景同色

    static void Main(string[] args)
    {
        int[] png = new int[] { 192, 512 };
        for (int i = 0; i < png.Length; i++)
        {
            string path = Path.Combine("www", "icon-" + png[i] + ".png");
            using (var bmp = new Bitmap(png[i], png[i]))
            {
                DrawBed(bmp);
                bmp.Save(path, ImageFormat.Png);
            }
            Say(path);
        }

        string ico = Path.Combine("assets", "bedremote.ico");
        WriteIco(ico);

        string card = Path.Combine("docs", "social-preview.png");
        WriteCard(card);
    }

    static void Say(string path)
    {
        Console.WriteLine("wrote " + path + "  " + new FileInfo(path).Length + " bytes");
    }

    // ---------- 那个"绿底一张床" ----------
    static void DrawBed(Bitmap bmp)
    {
        float k = bmp.Width / 32f;
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using (var bg = new SolidBrush(Green))
                g.FillEllipse(bg, 0, 0, bmp.Width - 1, bmp.Height - 1);
            using (var w = new SolidBrush(Color.White))
            {
                g.FillRectangle(w, 7 * k, 15 * k, 19 * k, 5 * k);      // 床垫
                g.FillRectangle(w, 8 * k, 20 * k, 3 * k, 5 * k);       // 前腿
                g.FillRectangle(w, 22 * k, 20 * k, 3 * k, 5 * k);      // 后腿
                g.FillRectangle(w, 8 * k, 10 * k, 6 * k, 5 * k);       // 枕头
                g.FillRectangle(w, 2 * k, 15 * k, 5 * k, 2 * k);       // 床头板
            }
        }
    }

    // ---------- .ico ----------
    // 为什么要自己拼：System.Drawing 只能存**单张**图标，而 Explorer 要的是一份
    // 16/32/48/256 都在的容器 —— 少一档就被迫缩放，小尺寸上糊成一团绿。
    // 小尺寸走经典 BMP 条目（32bpp BGRA + 1bpp 掩码），256 走 PNG 条目（Vista 以后支持，
    // 一张 256 的 PNG 才几 KB，BMP 那份要 1MB）。
    static void WriteIco(string path)
    {
        int[] small = new int[] { 16, 32, 48 };
        var parts = new List<byte[]>();
        var sizes = new List<int>();
        for (int i = 0; i < small.Length; i++)
        {
            using (var b = new Bitmap(small[i], small[i])) { DrawBed(b); parts.Add(BmpEntry(b)); }
            sizes.Add(small[i]);
        }
        using (var big = new Bitmap(256, 256)) { DrawBed(big); parts.Add(PngOf(big)); }
        sizes.Add(256);

        string dir = Path.GetDirectoryName(path);
        if (dir.Length > 0 && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write((ushort)0);                    // 保留
            bw.Write((ushort)1);                    // 类型：图标
            bw.Write((ushort)parts.Count);
            int offset = 6 + 16 * parts.Count;      // 头 + 目录表
            for (int i = 0; i < parts.Count; i++)
            {
                int s = sizes[i];
                bw.Write((byte)(s >= 256 ? 0 : s));     // 宽高 0 = 256
                bw.Write((byte)(s >= 256 ? 0 : s));
                bw.Write((byte)0);                      // 调色板颜色数
                bw.Write((byte)0);                      // 保留
                bw.Write((ushort)1);                    // 颜色平面
                bw.Write((ushort)32);                   // 位深
                bw.Write((uint)parts[i].Length);
                bw.Write((uint)offset);
                offset += parts[i].Length;
            }
            for (int i = 0; i < parts.Count; i++) bw.Write(parts[i]);
        }
        Say(path);
    }

    static byte[] PngOf(Bitmap bmp)
    {
        using (var ms = new MemoryStream())
        {
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }

    static byte[] BmpEntry(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        int maskStride = ((w + 31) / 32) * 4;
        int xorLen = w * h * 4;
        var outBytes = new byte[40 + xorLen + maskStride * h];

        // BITMAPINFOHEADER：高度写 2 倍（XOR 图像 + AND 掩码各 h 行），这是 .ico 的规矩
        Put(outBytes, 0, 40);                 // 头大小
        Put(outBytes, 4, w);
        Put(outBytes, 8, h * 2);
        outBytes[12] = 1; outBytes[13] = 0;   // 平面数
        outBytes[14] = 32; outBytes[15] = 0;  // 位深
        Put(outBytes, 20, xorLen + maskStride * h);

        // 像素：BGRA、自下而上
        int p = 40;
        for (int y = h - 1; y >= 0; y--)
            for (int x = 0; x < w; x++)
            {
                Color c = bmp.GetPixel(x, y);
                outBytes[p++] = c.B;
                outBytes[p++] = c.G;
                outBytes[p++] = c.R;
                outBytes[p++] = c.A;
            }
        // AND 掩码全 0：透明由 alpha 决定（Windows 早就不靠这层了，但结构必须在）
        return outBytes;
    }

    static void Put(byte[] b, int at, int v)
    {
        b[at] = (byte)(v & 255);
        b[at + 1] = (byte)((v >> 8) & 255);
        b[at + 2] = (byte)((v >> 16) & 255);
        b[at + 3] = (byte)((v >> 24) & 255);
    }

    // ---------- GitHub 分享卡片 ----------
    static void WriteCard(string path)
    {
        int W = 1280, H = 640;
        using (var bmp = new Bitmap(W, H))
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.Clear(Ink);
            using (var thin = new Pen(Color.FromArgb(40, 255, 255, 255), 2f))
                g.DrawLine(thin, 96, H - 108, W - 96, H - 108);

            using (var disc = new Bitmap(220, 220))
            {
                DrawBed(disc);
                g.DrawImage(disc, 96, 150);
            }

            string cn = "躺着把电脑用完";
            // 两行短句而不是一个长句：1280 宽的画布上，一行 27px 的英文超过约 62 个字符就出界
            // （第一版就被切掉了尾巴，"…to the TV ov"）。
            string en1 = "Phone as keyboard, mouse, gamepad and panel.";
            string en2 = "The picture goes to the TV over HDMI - this is not remote desktop.";
            Font fTitle = Pick("Segoe UI", "Arial", 112f, FontStyle.Bold);
            Font fCn = PickCn(54f, FontStyle.Bold);
            Font fEn = Pick("Segoe UI", "Arial", 26f, FontStyle.Regular);
            try
            {
                using (var b = new SolidBrush(Color.White))
                    Line(g, "bedremote", fTitle, b, 340, 120, W);
                using (var gr = new SolidBrush(Green))
                    Line(g, cn, fCn, gr, 342, 268, W);
                using (var dim = new SolidBrush(Color.FromArgb(178, 185, 198)))
                {
                    Line(g, en1, fEn, dim, 342, 372, W);
                    Line(g, en2, fEn, dim, 342, 408, W);
                }
                using (var dim = new SolidBrush(Color.FromArgb(120, 130, 145)))
                    Line(g, "Windows 10 / 11  ·  one file, no install  ·  MIT", fEn, dim, 342, 462, W);
            }
            finally
            {
                fTitle.Dispose(); fCn.Dispose(); fEn.Dispose();
            }
            string dir = Path.GetDirectoryName(path);
            if (dir.Length > 0 && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            bmp.Save(path, ImageFormat.Png);
        }
        Say(path);
    }

    // 画一行字，并且**当场检查它有没有出界**。第一版卡片那行英文被切掉了尾巴
    // （"…to the TV ov"），而图是二进制文件，CI 看不出来，只能靠人眼 —— 所以让生成器自己报。
    static void Line(Graphics g, string s, Font f, Brush b, float x, float y, int canvasW)
    {
        SizeF sz = g.MeasureString(s, f);
        if (x + sz.Width > canvasW - 24)
            Console.WriteLine("  !! 出界：" + s + "  需要 " + (int)(x + sz.Width) + "px，画布只有 " + canvasW + "px");
        g.DrawString(s, f, b, x, y);
    }

    // 中文那行不能直接用 Segoe UI（没有汉字会吐豆腐块），也不硬指望某一台机器装了哪个中文字体：
    // 按顺序试，找不到就退回 GenericSansSerif —— 宁可字丑，不要在别人机器上直接抛异常。
    static Font PickCn(float size, FontStyle style)
    {
        string[] cands = new string[] { "Microsoft YaHei UI", "Microsoft YaHei", "PingFang SC", "Noto Sans CJK SC", "SimHei" };
        for (int i = 0; i < cands.Length; i++)
        {
            try
            {
                var fam = new FontFamily(cands[i]);
                if (fam.IsStyleAvailable(style)) return new Font(fam, size, style, GraphicsUnit.Pixel);
            }
            catch { }
        }
        return new Font(SystemFonts.DefaultFont.FontFamily, size, style, GraphicsUnit.Pixel);
    }

    static Font Pick(string first, string second, float size, FontStyle style)
    {
        try { return new Font(new FontFamily(first), size, style, GraphicsUnit.Pixel); }
        catch { }
        try { return new Font(new FontFamily(second), size, style, GraphicsUnit.Pixel); }
        catch { return new Font(SystemFonts.DefaultFont.FontFamily, size, style, GraphicsUnit.Pixel); }
    }
}
