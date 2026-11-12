using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace GlassPlayer;

/// <summary>
/// Reads the real output level of your speakers/headphones from Windows (Core Audio peak meter).
/// It's the level of everything Windows is playing, which while LiquidCloud plays is mostly the song.
/// </summary>
sealed class AudioMeter
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioMeterInformation
    {
        int GetPeakValue(out float peak);
    }

    IAudioMeterInformation? meter;
    DateTime lastTry = DateTime.MinValue;

    public float Peak()
    {
        try
        {
            if (meter == null)
            {
                if ((DateTime.UtcNow - lastTry).TotalSeconds < 5) return 0;
                lastTry = DateTime.UtcNow;
                var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                en.GetDefaultAudioEndpoint(0 /* render */, 1 /* multimedia */, out var dev);
                var iid = typeof(IAudioMeterInformation).GUID;
                dev.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var o);
                meter = (IAudioMeterInformation)o;
            }
            meter.GetPeakValue(out var p);
            return p;
        }
        catch { meter = null; return 0; } // device changed or unavailable: try again in a few seconds
    }
}

/// <summary>Level bars under the cover, driven by the real output level. Runs only while visible and playing.</summary>
public class LevelBars : FrameworkElement
{
    readonly AudioMeter meter = new();
    readonly DispatcherTimer timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) }; // 20 fps is plenty for level bars
    readonly double[] bars = new double[28];
    readonly double[] speed = new double[28];
    readonly Random rnd = new();
    double level;
    Brush fill = Brushes.White;

    public LevelBars()
    {
        IsHitTestVisible = false;
        for (int i = 0; i < speed.Length; i++) speed[i] = 0.18 + rnd.NextDouble() * 0.5;
        timer.Tick += (_, _) => Step();
        IsVisibleChanged += (_, _) => Running = running;
    }

    bool running;
    public bool Running
    {
        get => running;
        set
        {
            running = value;
            bool on = value && IsVisible;
            if (on && !timer.IsEnabled) timer.Start();
            else if (!on && timer.IsEnabled) { timer.Stop(); Array.Clear(bars); InvalidateVisual(); }
        }
    }

    public void SetBrush(Brush b) { fill = b; InvalidateVisual(); }

    void Step()
    {
        double p = Math.Sqrt(Math.Clamp(meter.Peak(), 0, 1)); // perceptual curve
        level = p > level ? p : level * 0.88 + p * 0.12;
        int n = bars.Length;
        for (int i = 0; i < n; i++)
        {
            // each bar reacts to the same real level with its own speed and shape, so the row breathes like a spectrum
            double center = 1 - Math.Abs(i - (n - 1) / 2.0) / (n / 2.0);
            double target = level * (0.35 + 0.65 * center) * (0.75 + 0.25 * rnd.NextDouble());
            bars[i] += (target - bars[i]) * speed[i];
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        int n = bars.Length;
        double gap = 3, bw = (w - gap * (n - 1)) / n;
        for (int i = 0; i < n; i++)
        {
            double bh = Math.Max(2, bars[i] * h);
            dc.DrawRoundedRectangle(fill, null, new Rect(i * (bw + gap), (h - bh) / 2, bw, bh), bw / 2, bw / 2);
        }
    }
}
