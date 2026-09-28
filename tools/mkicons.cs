// 生成 PWA 图标（www\icon-192.png、www\icon-512.png）—— 和界面/托盘上那张"绿底一张床"是同一个画法，
// 只是按尺寸放大。为什么要生成文件而不是运行时画：manifest 里的图标必须是真 PNG，浏览器要下载它。
// 重新生成：
//   csc -nologo -target:exe -platform:x64 -r:System.Drawing.dll -out:tools\mkicons.exe tools\mkicons.cs
//   tools\mkicons.exe www
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

static class MkIcons
{
    static void Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : "www";
        int[] sizes = new int[] { 192, 512 };
        for (int i = 0; i < sizes.Length; i++)
        {
            int n = sizes[i];
            string path = Path.Combine(dir, "icon-" + n + ".png");
            using (var bmp = new Bitmap(n, n))
            {
                Draw(bmp);
                bmp.Save(path, ImageFormat.Png);
            }
            Console.WriteLine("wrote " + path + " " + new FileInfo(path).Length + " bytes");
        }
    }

    static void Draw(Bitmap bmp)
    {
        float k = bmp.Width / 32f;                       // 所有坐标都按 32 的设计网格缩放
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using (var bg = new SolidBrush(Color.FromArgb(24, 140, 90)))
                g.FillEllipse(bg, 0, 0, bmp.Width - 1, bmp.Height - 1);
            using (var w = new SolidBrush(Color.White))
            {
                g.FillRectangle(w, 7 * k, 15 * k, 19 * k, 5 * k);        // 床垫
                g.FillRectangle(w, 8 * k, 20 * k, 3 * k, 5 * k);         // 前腿
                g.FillRectangle(w, 22 * k, 20 * k, 3 * k, 5 * k);        // 后腿
                g.FillRectangle(w, 8 * k, 10 * k, 6 * k, 5 * k);         // 枕头
                g.FillRectangle(w, 2 * k, 15 * k, 5 * k, 2 * k);         // 床头板
            }
        }
    }
}
