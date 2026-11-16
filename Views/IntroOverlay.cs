using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using UnixMedia.Services;

namespace UnixMedia.Views;

/// <summary>
/// Full-window startup intro. Particles swirl in to form the "UnixMedia" wordmark, which holds for a moment,
/// then a sweep releases the particles left to right and they swoosh off while the home screen fades in
/// behind them. Any key or click skips it.
/// </summary>
/// <remarks>
/// Every frame is a function of elapsed time (<see cref="IntroScene.Draw"/>), so the animation is
/// frame-rate independent and the UI thread never shares mutable state with the render thread.
/// </remarks>
public sealed class IntroOverlay : Control, ICustomHitTest
{
    private readonly IntroScene _scene = IntroScene.Create();
    private readonly Stopwatch _clock = new();
    private readonly Stopwatch _warmup = new();
    private double _lastWarmFrame;
    private int _steadyFrames;
    private double? _skipAt;
    private bool _finished;

    // Optional frame statistics (UNIXMEDIA_DEBUG=1), for diagnosing stutter.
    private static readonly bool Debug = Environment.GetEnvironmentVariable("UNIXMEDIA_DEBUG") == "1";
    private static volatile int _gpu = -1; // -1 unknown, 0 software, 1 GPU (set from the render thread)
    private int _frames;
    private double _lastFrame, _worstGap;

    /// <summary>Raised once on the UI thread when the intro has completely faded out.</summary>
    public event EventHandler? Finished;

    public IntroOverlay()
    {
        Focusable = false;
        ClipToBounds = true;
    }

    private double Elapsed => _clock.Elapsed.TotalSeconds;

    /// <summary>Fades the intro out quickly and stops its sound. Safe to call repeatedly.</summary>
    public void Skip()
    {
        if (_finished || _skipAt is not null) return;
        _skipAt = Elapsed;
        SoundService.Stop();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RequestFrame();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Skip();
        e.Handled = true;
    }

    // Swallow all pointer input (and hover effects on the tiles underneath) while running.
    public bool HitTest(Point point) => !_finished;

    private void RequestFrame() => TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);

    private void OnFrame(TimeSpan _)
    {
        if (_finished) return;

        // Warm-up: hold on the first frame (plain background) until frames arrive steadily. The window going
        // fullscreen, JIT and GPU shader compilation all stall the first few frames; letting that happen here
        // keeps it out of the animation. The clock and the sound (timed to it) start afterwards.
        if (!_clock.IsRunning)
        {
            if (!_warmup.IsRunning) _warmup.Start();
            var now = _warmup.Elapsed.TotalSeconds;
            _steadyFrames = now - _lastWarmFrame < 0.025 ? _steadyFrames + 1 : 0;
            _lastWarmFrame = now;

            if (_steadyFrames < 5 && now < 0.8)
            {
                InvalidateVisual();
                RequestFrame();
                return;
            }
            _clock.Start();
            SoundService.Play(UiSound.Intro);
        }

        var t = Elapsed;
        if (_frames++ > 0 && Debug && t - _lastFrame > 0.025)
            Console.Error.WriteLine($"[intro] {(t - _lastFrame) * 1000:0} ms gap ending at t={t:0.000}");
        if (_frames > 1) _worstGap = Math.Max(_worstGap, t - _lastFrame);
        _lastFrame = t;

        if (t >= IntroScene.End || Master(t) <= 0)
        {
            if (Debug)
                Console.Error.WriteLine(
                    $"[intro] {_frames} frames in {t:0.00} s ({_frames / t:0} fps), worst gap {_worstGap * 1000:0} ms, " +
                    $"renderer: {(_gpu == 1 ? "GPU" : _gpu == 0 ? "software" : "unknown")}");
            _finished = true;
            IsHitTestVisible = false;
            IsVisible = false;
            Finished?.Invoke(this, EventArgs.Empty);
            return;
        }

        InvalidateVisual();
        RequestFrame();
    }

    /// <summary>Overall opacity: 1 normally, ramping to 0 over a short period after <see cref="Skip"/>.</summary>
    private double Master(double t) =>
        _skipAt is { } s ? Math.Clamp(1 - (t - s) / 0.3, 0, 1) : 1;

    public override void Render(DrawingContext context)
    {
        if (_finished) return;
        var t = _clock.IsRunning ? Elapsed : 0;
        context.Custom(new DrawOp(new Rect(Bounds.Size), _scene, (float)t, (float)Master(t), !_clock.IsRunning));
    }

    private sealed class DrawOp(Rect bounds, IntroScene scene, float time, float master, bool warmup) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;
        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
            using var lease = feature.Lease();
            _gpu = lease.GrContext is null ? 0 : 1;
            scene.Draw(lease.SkCanvas, (float)bounds.Width, (float)bounds.Height, time, master, warmup);
        }
    }
}

/// <summary>Particle layout plus the drawing code for one frame of the intro.</summary>
/// <remarks>
/// The particle data is immutable. The vertex buffers and the cached wordmark image are only touched from
/// <see cref="Draw"/>, which runs on the render thread.
/// </remarks>
internal sealed class IntroScene
{
    private const string Text = "UnixMedia";
    private const float RefFontSize = 200f;

    // Timeline (seconds).
    private const float GatherDuration = 0.5f;   // per particle
    private const float GatherSpread = 0.15f;    // max random start delay
    private const float TextInStart = 0.45f, TextInDuration = 0.35f;
    public const float SweepStart = 2.0f;
    private const float SweepDuration = 0.75f;   // time for the release front to cross the word
    private const float ReleaseJitter = 0.06f;
    private const float Life = 1.0f;             // particle lifetime after release
    private const float BackgroundFadeStart = SweepStart + 0.1f, BackgroundFadeDuration = 1.2f;
    public const float End = SweepStart + SweepDuration + ReleaseJitter + Life;

    private const float Trail = 0.035f;          // motion-blur streak length, in seconds of travel

    private static readonly SKColor[] Palette =
    [
        SKColor.Parse("#22D3EE"), // cyan
        SKColor.Parse("#818CF8"), // indigo
        SKColor.Parse("#F472B6"), // pink
    ];

    private readonly SKPath _path;
    private readonly SKRect _textBounds;
    private readonly Particle[] _particles;
    private readonly Dust[] _dust;

    // Render-thread state.
    private readonly SKPoint[] _vertices;
    private readonly SKColor[] _colors;
    private SKImage? _wordmark;
    private float _wordmarkScale;

    private IntroScene(SKPath path, Particle[] particles, Dust[] dust)
    {
        _path = path;
        _textBounds = path.TightBounds;
        _particles = particles;
        _dust = dust;
        _vertices = new SKPoint[particles.Length * 6];
        _colors = new SKColor[particles.Length * 6];
    }

    private readonly record struct Particle(
        float TX, float TY,          // resting position in the wordmark (reference units, centred on 0,0)
        float SX, float SY,          // start offset from the resting position
        float Swirl,                 // radians of rotation applied to the offset while gathering
        float Delay, float Release,
        float V0, float Accel, float Lift,
        float WobbleAmp, float WobbleFreq, float Phase,
        float Size, SKColor Color);

    private readonly record struct Dust(float X, float Y, float VX, float VY, float Size, float Twinkle, float Phase);

    public static IntroScene Create()
    {
        var rnd = new Random();
        using var typeface = LoadTypeface();
        using var font = new SKFont(typeface, RefFontSize) { Subpixel = true };
        var path = font.GetTextPath(Text, new SKPoint(0, 0));
        var b = path.TightBounds;
        path.Transform(SKMatrix.CreateTranslation(-b.MidX, -b.MidY));
        b = path.TightBounds;

        // Sample the glyph interiors on a jittered grid.
        const float step = 3.4f;
        var particles = new List<Particle>();
        for (var y = b.Top; y <= b.Bottom; y += step)
        for (var x = b.Left; x <= b.Right; x += step)
        {
            var px = x + (float)(rnd.NextDouble() - 0.5) * step * 0.8f;
            var py = y + (float)(rnd.NextDouble() - 0.5) * step * 0.8f;
            if (!path.Contains(px, py)) continue;

            var nx = (px - b.Left) / b.Width; // 0 at the left edge of the word, 1 at the right
            var angle = rnd.NextDouble() * Math.PI * 2;
            var radius = 250 + rnd.NextDouble() * 650;
            var sparkle = (float)rnd.NextDouble() * 0.3f;

            particles.Add(new Particle(
                TX: px, TY: py,
                SX: (float)(Math.Cos(angle) * radius), SY: (float)(Math.Sin(angle) * radius * 0.6),
                Swirl: (float)((rnd.NextDouble() < 0.5 ? -1 : 1) * (0.8 + rnd.NextDouble() * 0.8)),
                Delay: (float)rnd.NextDouble() * GatherSpread,
                // The sweep front eases in and out, so release each particle when the front reaches it.
                Release: SweepStart + SweepDuration * SweepTimeAt(nx) + (float)rnd.NextDouble() * ReleaseJitter,
                V0: (float)rnd.NextDouble() * 180,
                Accel: 900 + (float)rnd.NextDouble() * 2100,
                Lift: ((float)rnd.NextDouble() - 0.4f) * 900,
                WobbleAmp: 40 + (float)rnd.NextDouble() * 140,
                WobbleFreq: 5 + (float)rnd.NextDouble() * 6,
                Phase: (float)(rnd.NextDouble() * Math.PI * 2),
                Size: 1.6f + (float)rnd.NextDouble() * 1.8f,
                Color: Lerp(GradientAt(nx), SKColors.White, sparkle)));
        }

        var dust = new Dust[140];
        for (var i = 0; i < dust.Length; i++)
            dust[i] = new Dust(
                (float)rnd.NextDouble(), (float)rnd.NextDouble(),
                ((float)rnd.NextDouble() - 0.3f) * 0.03f, ((float)rnd.NextDouble() - 0.5f) * 0.02f,
                0.8f + (float)rnd.NextDouble() * 1.8f,
                1.5f + (float)rnd.NextDouble() * 3f, (float)(rnd.NextDouble() * Math.PI * 2));

        return new IntroScene(path, particles.ToArray(), dust);
    }

    /// <summary>The app's own font (Inter Bold) so the intro matches the home screen title.</summary>
    private static SKTypeface LoadTypeface()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://Avalonia.Fonts.Inter/Assets/Inter-Bold.ttf"));
            if (SKTypeface.FromStream(stream) is { } tf) return tf;
        }
        catch
        {
            // Fall through to a system font.
        }
        return SKTypeface.FromFamilyName(null, SKFontStyle.Bold) ?? SKTypeface.Default;
    }

    // ---------------------------------------------------------------- frame

    public void Draw(SKCanvas c, float w, float h, float t, float master, bool warmup = false)
    {
        if (w <= 0 || h <= 0 || master <= 0) return;

        var background = master * (1 - Smoother((t - BackgroundFadeStart) / BackgroundFadeDuration));
        var textIn = Smooth((t - TextInStart) / TextInDuration);

        using var paint = new SKPaint { IsAntialias = true };

        // Backdrop: the same gradient as the main window, plus a soft glow behind the word.
        if (background > 0)
        {
            using (var bg = SKShader.CreateLinearGradient(
                       new SKPoint(0, 0), new SKPoint(w, h),
                       [SKColor.Parse("#0B1220"), SKColor.Parse("#111827"), SKColor.Parse("#1E1B4B")],
                       [0f, 0.6f, 1f], SKShaderTileMode.Clamp))
            {
                paint.Shader = bg;
                paint.Color = SKColors.White.WithAlpha(ToByte(background));
                c.DrawRect(0, 0, w, h, paint);
            }

            using (var halo = SKShader.CreateRadialGradient(
                       new SKPoint(w / 2, h / 2), Math.Max(w, h) * 0.45f,
                       [SKColor.Parse("#4F46E5").WithAlpha(ToByte(0.22f * textIn)), SKColors.Transparent],
                       SKShaderTileMode.Clamp))
            {
                paint.Shader = halo;
                paint.Color = SKColors.White.WithAlpha(ToByte(background));
                c.DrawRect(0, 0, w, h, paint);
            }
            paint.Shader = null;

            // Drifting, twinkling dust in screen space.
            foreach (var d in _dust)
            {
                var x = Wrap(d.X + d.VX * t) * w;
                var y = Wrap(d.Y + d.VY * t) * h;
                var a = 0.35f * (0.5f + 0.5f * MathF.Sin(t * d.Twinkle + d.Phase)) * background * Smooth(t / 0.4f);
                paint.Color = SKColors.White.WithAlpha(ToByte(a));
                c.DrawCircle(x, y, d.Size, paint);
            }
        }

        // Fit the word to roughly 62% of the width (or 22% of the height on narrow screens).
        var scale = Math.Min(w * 0.62f / _textBounds.Width, h * 0.22f / _textBounds.Height);
        var deviceScale = scale * c.TotalMatrix.ScaleX; // includes the display's DPI scaling
        c.Save();
        c.Translate(w / 2, h / 2);
        c.Scale(scale);

        DrawWordmark(c, paint, t, textIn * master, deviceScale);
        DrawParticles(c, paint, t, textIn, master);

        if (warmup)
        {
            // Draw later stages of the animation into a single pixel: invisible, but it builds the cached
            // wordmark and makes the GPU compile every shader the animation will need.
            c.Save();
            var inv = 1 / scale;
            c.ClipRect(new SKRect(-0.5f * inv, -0.5f * inv, 0.5f * inv, 0.5f * inv));
            DrawWordmark(c, paint, SweepStart + 0.3f, 1, deviceScale);
            DrawParticles(c, paint, 0.3f, 0, 1);
            DrawParticles(c, paint, SweepStart + 0.4f, 1, 1);
            c.Restore();
        }

        c.Restore();
    }

    private void DrawWordmark(SKCanvas c, SKPaint paint, float t, float alpha, float deviceScale)
    {
        if (alpha <= 0) return;

        // Release front, in reference units. Everything left of it has turned into particles.
        const float soft = 40f;
        var progress = Math.Clamp((t - SweepStart) / SweepDuration, 0, 1);
        // The front starts just left of the word and ends just past it, so nothing lingers at either edge.
        var front = t < SweepStart
            ? _textBounds.Left - 10 * soft
            : _textBounds.Left - soft + SweepEase(progress) * (_textBounds.Width + 2 * soft);
        if (front - soft > _textBounds.Right) return;

        var image = GetWordmark(deviceScale, out var rect);

        // One draw: the cached image, masked by a soft-edged gradient that follows the front.
        using var imageShader = image.ToShader(
            SKShaderTileMode.Decal, SKShaderTileMode.Decal,
            new SKSamplingOptions(SKFilterMode.Linear),
            SKMatrix.CreateScaleTranslation(rect.Width / image.Width, rect.Height / image.Height, rect.Left, rect.Top));
        using var mask = SKShader.CreateLinearGradient(
            new SKPoint(front - soft, 0), new SKPoint(front + soft, 0),
            [SKColors.Transparent, SKColors.White], SKShaderTileMode.Clamp);
        using var masked = SKShader.CreateCompose(imageShader, mask, SKBlendMode.DstIn);

        paint.Reset();
        paint.IsAntialias = true;
        paint.Shader = masked;
        paint.Color = SKColors.White.WithAlpha(ToByte(alpha));
        c.DrawRect(rect, paint);
        paint.Shader = null;
    }

    /// <summary>
    /// The glowing wordmark rendered once at the current device resolution. Re-blurring the glow every frame
    /// is the most expensive part of the intro, so it's cached and only rebuilt if the size changes.
    /// </summary>
    private SKImage GetWordmark(float deviceScale, out SKRect rect)
    {
        rect = _textBounds;
        rect.Inflate(80, 80); // room for the glow

        if (_wordmark is not null && Math.Abs(deviceScale - _wordmarkScale) < 0.01f * _wordmarkScale)
            return _wordmark;

        var ps = Math.Min(deviceScale, 8192f / rect.Width);
        var info = new SKImageInfo(
            Math.Max(1, (int)MathF.Ceiling(rect.Width * ps)),
            Math.Max(1, (int)MathF.Ceiling(rect.Height * ps)),
            SKColorType.Rgba8888, SKAlphaType.Premul);

        using var surface = SKSurface.Create(info);
        var c = surface.Canvas;
        c.Clear(SKColors.Transparent);
        c.Scale(ps);
        c.Translate(-rect.Left, -rect.Top);

        using var paint = new SKPaint { IsAntialias = true };
        using (var blur = SKImageFilter.CreateBlur(16, 16))
        {
            paint.ImageFilter = blur;
            paint.Color = SKColor.Parse("#8B5CF6").WithAlpha(ToByte(0.7f));
            c.DrawPath(_path, paint);
            paint.ImageFilter = null;
        }
        using (var fill = SKShader.CreateLinearGradient(
                   new SKPoint(_textBounds.Left, 0), new SKPoint(_textBounds.Right, 0),
                   Palette, null, SKShaderTileMode.Clamp))
        {
            paint.Shader = fill;
            paint.Color = SKColors.White;
            c.DrawPath(_path, paint);
        }

        _wordmark?.Dispose();
        _wordmark = surface.Snapshot();
        _wordmarkScale = deviceScale;
        return _wordmark;
    }

    /// <summary>
    /// All particles in a single draw call: each one is a short quad from where it was <see cref="Trail"/>
    /// seconds ago to where it is now, bright at the head and fading at the tail (cheap motion blur).
    /// </summary>
    private void DrawParticles(SKCanvas c, SKPaint paint, float t, float textIn, float master)
    {
        var k = 0;
        foreach (ref readonly var p in _particles.AsSpan())
        {
            float alpha, width;
            if (t < p.Release)
            {
                var g = (t - p.Delay) / GatherDuration;
                if (g <= 0) continue;
                // Bright while flying in, then dimmer once the solid text takes over.
                alpha = Smooth(g * 1.4f) * (1 - 0.65f * textIn) * master;
                width = p.Size;
            }
            else
            {
                var u = t - p.Release;
                if (u >= Life) continue;
                var fade = MathF.Pow(1 - u / Life, 1.5f);
                var flash = 1 + 0.4f * MathF.Exp(-u * 8); // gentle brightening as it breaks off
                alpha = Math.Min(1, 0.38f * fade * flash) * master;
                width = p.Size * (1 - 0.4f * u / Life);
            }
            if (alpha <= 0.004f) continue;

            var (hx, hy) = Position(p, t);
            var (tx, ty) = Position(p, t - Trail);

            // Direction of travel; a resting particle becomes a small square.
            float dx = hx - tx, dy = hy - ty;
            var len = MathF.Sqrt(dx * dx + dy * dy);
            if (len < 1e-3f) { dx = 1; dy = 0; } else { dx /= len; dy /= len; }
            var hw = width * 0.5f;
            float nx = -dy * hw, ny = dx * hw;
            hx += dx * hw; hy += dy * hw;
            tx -= dx * hw; ty -= dy * hw;

            var head = p.Color.WithAlpha(ToByte(alpha));
            var tail = p.Color.WithAlpha(ToByte(alpha * (len < 1 ? 1 : 0.15f)));

            _vertices[k] = new SKPoint(tx + nx, ty + ny); _colors[k++] = tail;
            _vertices[k] = new SKPoint(hx + nx, hy + ny); _colors[k++] = head;
            _vertices[k] = new SKPoint(hx - nx, hy - ny); _colors[k++] = head;
            _vertices[k] = new SKPoint(tx + nx, ty + ny); _colors[k++] = tail;
            _vertices[k] = new SKPoint(hx - nx, hy - ny); _colors[k++] = head;
            _vertices[k] = new SKPoint(tx - nx, ty - ny); _colors[k++] = tail;
        }

        // Unused slots become zero-area (invisible) triangles.
        Array.Clear(_vertices, k, _vertices.Length - k);
        Array.Clear(_colors, k, _colors.Length - k);
        if (k == 0) return;

        paint.Reset();
        paint.IsAntialias = true;
        paint.Color = SKColors.White;
        paint.BlendMode = SKBlendMode.Plus; // overlapping particles add up to light
        using var vertices = SKVertices.CreateCopy(SKVertexMode.Triangles, _vertices, _colors);
        c.DrawVertices(vertices, SKBlendMode.Dst, paint); // Dst = use the vertex colours as-is
        paint.BlendMode = SKBlendMode.SrcOver;
    }

    /// <summary>Where a particle is at time <paramref name="t"/>: swirling in, resting in the word, or swooshing off.</summary>
    private static (float X, float Y) Position(in Particle p, float t)
    {
        if (t < p.Release)
        {
            var g = Math.Clamp((t - p.Delay) / GatherDuration, 0, 1);
            var e = 1 - MathF.Pow(1 - g, 3); // ease-out cubic: fast in, soft landing
            var (ox, oy) = Rotate(p.SX * (1 - e), p.SY * (1 - e), p.Swirl * (1 - e));
            return (p.TX + ox + MathF.Sin(t * 3 + p.Phase) * 0.6f, p.TY + oy);
        }

        // Accelerating swoosh to the right with turbulence. Starts from rest so the break-off is smooth.
        var u = t - p.Release;
        var x = p.TX + MathF.Sin(p.Release * 3 + p.Phase) * 0.6f + p.V0 * u + 0.5f * p.Accel * u * u;
        var y = p.TY - p.Lift * u * u + p.WobbleAmp * u * u * MathF.Sin(p.WobbleFreq * u + p.Phase);
        return (x, y);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Sweep front easing (sine in-out): fraction of the word crossed at a given fraction of the sweep time.</summary>
    private static float SweepEase(float x) => (1 - MathF.Cos(MathF.PI * x)) / 2;

    /// <summary>Inverse of <see cref="SweepEase"/>: fraction of the sweep time at which the front reaches <paramref name="nx"/>.</summary>
    private static float SweepTimeAt(float nx) => MathF.Acos(1 - 2 * Math.Clamp(nx, 0, 1)) / MathF.PI;

    private static (float, float) Rotate(float x, float y, float angle)
    {
        var (s, co) = MathF.SinCos(angle);
        return (x * co - y * s, x * s + y * co);
    }

    private static float Smooth(float x)
    {
        x = Math.Clamp(x, 0, 1);
        return x * x * (3 - 2 * x);
    }

    private static float Smoother(float x)
    {
        x = Math.Clamp(x, 0, 1);
        return x * x * x * (x * (x * 6 - 15) + 10);
    }

    private static float Wrap(float v) => v - MathF.Floor(v);

    private static byte ToByte(float a) => (byte)Math.Clamp(a * 255f, 0, 255);

    private static SKColor GradientAt(float t)
    {
        t = Math.Clamp(t, 0, 1) * (Palette.Length - 1);
        var i = Math.Min((int)t, Palette.Length - 2);
        return Lerp(Palette[i], Palette[i + 1], t - i);
    }

    private static SKColor Lerp(SKColor a, SKColor b, float t) => new(
        (byte)(a.Red + (b.Red - a.Red) * t),
        (byte)(a.Green + (b.Green - a.Green) * t),
        (byte)(a.Blue + (b.Blue - a.Blue) * t),
        (byte)(a.Alpha + (b.Alpha - a.Alpha) * t));
}
