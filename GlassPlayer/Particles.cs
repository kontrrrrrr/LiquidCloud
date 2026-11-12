using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace GlassPlayer;

/// <summary>Lightweight particle background. Draws at ~30 fps and stops completely when not running.</summary>
public class ParticleLayer : FrameworkElement
{
    struct P { public double X, Y, Vx, Vy, R, Phase, A; }

    P[] ps = Array.Empty<P>();
    readonly Random rnd = new();
    readonly DispatcherTimer timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    DateTime last = DateTime.UtcNow;
    double t;
    string shape = "Dots";
    double speed = 1, size = 2.5;
    SolidColorBrush brush = Brushes.White;
    Pen pen = new(Brushes.White, 1);

    public ParticleLayer()
    {
        IsHitTestVisible = false;
        timer.Tick += (_, _) => Step();
        SizeChanged += (_, _) => Reseed();
    }

    public void SetColor(Color c)
    {
        brush = new SolidColorBrush(c); brush.Freeze();
        pen = new Pen(brush, 1.2); pen.Freeze();
        InvalidateVisual();
    }

    public void Configure(int count, double size, double speed, string shape)
    {
        this.size = size; this.speed = speed; this.shape = shape;
        ps = new P[Math.Clamp(count, 0, 400)];
        Reseed();
    }

    void Reseed()
    {
        for (int i = 0; i < ps.Length; i++) Seed(ref ps[i], true);
        InvalidateVisual();
    }

    public bool Running
    {
        set
        {
            if (value == timer.IsEnabled) return;
            if (value) { last = DateTime.UtcNow; timer.Start(); }
            else timer.Stop();
        }
    }

    double Rand(double a, double b) => a + rnd.NextDouble() * (b - a);

    void Seed(ref P p, bool anywhere)
    {
        double w = Math.Max(ActualWidth, 1), h = Math.Max(ActualHeight, 1);
        p.X = Rand(0, w);
        p.R = Rand(0.5, 1.5) * size;
        p.Phase = Rand(0, Math.PI * 2);
        p.A = Rand(0.25, 0.8);
        switch (shape)
        {
            case "Snow":
                p.Y = anywhere ? Rand(0, h) : -10;
                p.Vx = Rand(-8, 8); p.Vy = Rand(18, 45);
                break;
            case "Bubbles":
                p.Y = anywhere ? Rand(0, h) : h + 10;
                p.Vx = Rand(-6, 6); p.Vy = -Rand(15, 40);
                p.R *= 2.2;
                break;
            case "Stars":
                p.Y = Rand(0, h);
                p.Vx = Rand(-3, 3); p.Vy = Rand(-3, 3);
                break;
            default: // Dots
                p.Y = Rand(0, h);
                p.Vx = Rand(-14, 14); p.Vy = Rand(-14, 14);
                break;
        }
    }

    void Step()
    {
        var now = DateTime.UtcNow;
        double dt = Math.Min((now - last).TotalSeconds, 0.1) * speed;
        last = now;
        t += dt;
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        for (int i = 0; i < ps.Length; i++)
        {
            ref var p = ref ps[i];
            double sway = shape is "Snow" or "Bubbles" ? Math.Sin(t * 1.3 + p.Phase) * 12 : 0;
            p.X += (p.Vx + sway) * dt;
            p.Y += p.Vy * dt;
            if (shape == "Snow" && p.Y > h + 10) Seed(ref p, false);
            else if (shape == "Bubbles" && p.Y < -20) Seed(ref p, false);
            else
            {
                if (p.X < -10) p.X = w + 10; else if (p.X > w + 10) p.X = -10;
                if (p.Y < -10) p.Y = h + 10; else if (p.Y > h + 10) p.Y = -10;
            }
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        foreach (var p in ps)
        {
            double a = p.A;
            if (shape == "Stars") a *= 0.55 + 0.45 * Math.Sin(t * 2.2 + p.Phase);
            dc.PushOpacity(Math.Clamp(a, 0, 1));
            var c = new Point(p.X, p.Y);
            if (shape == "Bubbles") dc.DrawEllipse(null, pen, c, p.R, p.R);
            else if (shape == "Stars")
            {
                dc.DrawEllipse(brush, null, c, p.R * 0.6, p.R * 0.6);
                dc.DrawLine(pen, new Point(p.X - p.R * 1.6, p.Y), new Point(p.X + p.R * 1.6, p.Y));
                dc.DrawLine(pen, new Point(p.X, p.Y - p.R * 1.6), new Point(p.X, p.Y + p.R * 1.6));
            }
            else dc.DrawEllipse(brush, null, c, p.R, p.R);
            dc.Pop();
        }
    }
}
