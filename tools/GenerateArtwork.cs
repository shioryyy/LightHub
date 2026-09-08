using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
internal static class Assets {
    static void Main(string[] args) {
        Directory.CreateDirectory(args[0]);
        using(var bitmap=new Bitmap(360,460)) using(var g=Graphics.FromImage(bitmap)) {
            g.SmoothingMode=SmoothingMode.AntiAlias; g.Clear(Color.Transparent);
            using(var body=new GraphicsPath()) {
                body.AddBezier(180,25,90,25,63,72,60,160); body.AddBezier(60,160,55,221,48,299,64,355);
                body.AddBezier(64,355,87,440,273,440,296,355); body.AddBezier(296,355,312,299,305,221,300,160); body.AddBezier(300,160,297,72,270,25,180,25);
                using(var brush=new SolidBrush(Color.FromArgb(39,43,46))) g.FillPath(brush,body);
                using(var pen=new Pen(Color.FromArgb(67,73,77),3)) g.DrawPath(pen,body);
            }
            using(var seam=new Pen(Color.FromArgb(12,16,18),3)) { g.DrawLine(seam,180,28,180,173); g.DrawBezier(seam,61,173,113,191,247,191,299,173); }
            using(var wheel=new SolidBrush(Color.FromArgb(12,16,18))) g.FillRectangle(wheel,168,63,24,64);
            using(var ridges=new Pen(Color.FromArgb(91,98,103),2)) for(int y=70;y<122;y+=7) g.DrawLine(ridges,171,y,189,y);
            using(var accent=new SolidBrush(Color.FromArgb(0,164,128))) { g.FillEllipse(accent,174,249,12,12); g.FillEllipse(accent,174,275,12,12); g.FillEllipse(accent,174,301,12,12); }
            using(var sides=new Pen(Color.FromArgb(99,107,112),5)) { g.DrawLine(sides,60,185,57,218); g.DrawLine(sides,57,232,56,264); g.DrawLine(sides,300,185,303,218); g.DrawLine(sides,303,232,304,264); }
            bitmap.Save(Path.Combine(args[0],"gpw.png"),ImageFormat.Png);
        }
    }
}
