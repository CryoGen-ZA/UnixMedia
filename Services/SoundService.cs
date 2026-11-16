using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace UnixMedia.Services;

public enum UiSound { Focus, Select, Intro }

/// <summary>
/// Plays short UI sounds with no extra dependencies.
/// Sounds are synthesised at startup, or loaded from &lt;data&gt;/sounds/focus.wav and select.wav if present.
/// Playback: winmm PlaySound on Windows, afplay on macOS, pw-play / paplay / aplay on Linux.
/// </summary>
public static class SoundService
{
    private const int SampleRate = 44100;

    private static readonly Dictionary<UiSound, string> Files = new();
    private static readonly Dictionary<UiSound, byte[]> Data = new();
    private static readonly Dictionary<UiSound, GCHandle> Pinned = new();
    private static string? _linuxPlayer;
    private static Process? _current;
    private static bool _initialized;

    public static bool Enabled { get; set; } = true;

    public static string CustomSoundDirectory => Path.Combine(TileStore.DataDirectory, "sounds");

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            var cache = Path.Combine(Path.GetTempPath(), $"unixmedia-sounds-{Environment.UserName}");
            Directory.CreateDirectory(cache);

            foreach (var sound in Enum.GetValues<UiSound>())
            {
                var name = sound.ToString().ToLowerInvariant() + ".wav";
                var custom = Path.Combine(CustomSoundDirectory, name);

                byte[] wav;
                string path;
                if (File.Exists(custom))
                {
                    wav = File.ReadAllBytes(custom);
                    path = custom;
                }
                else
                {
                    wav = sound switch
                    {
                        UiSound.Focus => SynthFocus(),
                        UiSound.Select => SynthSelect(),
                        _ => SynthIntro(),
                    };
                    path = Path.Combine(cache, name);
                    File.WriteAllBytes(path, wav);
                }

                Files[sound] = path;
                Data[sound] = wav;
                if (OperatingSystem.IsWindows())
                    Pinned[sound] = GCHandle.Alloc(wav, GCHandleType.Pinned); // must stay put while winmm plays it
            }

            if (OperatingSystem.IsLinux())
                _linuxPlayer = new[] { "pw-play", "paplay", "aplay" }.FirstOrDefault(IsOnPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Sound initialisation failed: {ex.Message}");
            Enabled = false;
        }
    }

    public static void Play(UiSound sound)
    {
        if (!Enabled || !_initialized || !Files.ContainsKey(sound)) return;

        if (OperatingSystem.IsWindows())
        {
            PlayWindows(sound);
            return;
        }

        var player = OperatingSystem.IsMacOS() ? "afplay" : _linuxPlayer;
        if (player is null) return;

        var file = Files[sound];
        Task.Run(() =>
        {
            try
            {
                // A new sound cuts off the previous one so fast navigation doesn't pile up players.
                var previous = _current;
                try { if (previous is { HasExited: false }) previous.Kill(); } catch { }

                var psi = new ProcessStartInfo(player)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                if (player == "aplay") psi.ArgumentList.Add("-q");
                psi.ArgumentList.Add(file);

                var p = Process.Start(psi);
                if (p is null) return;
                p.EnableRaisingEvents = true;
                p.Exited += (_, _) => p.Dispose();
                _current = p;
            }
            catch
            {
                // Missing player or audio device: stay silent.
            }
        });
    }

    /// <summary>Stops the sound that is currently playing, if any.</summary>
    public static void Stop()
    {
        if (!_initialized) return;
        if (OperatingSystem.IsWindows())
        {
            try { PlaySound(IntPtr.Zero, IntPtr.Zero, 0); } catch { }
            return;
        }
        var p = _current;
        Task.Run(() => { try { if (p is { HasExited: false }) p.Kill(); } catch { } });
    }

    // ---------------------------------------------------------------- Windows

    [SupportedOSPlatform("windows")]
    private static void PlayWindows(UiSound sound)
    {
        const uint SND_ASYNC = 0x0001, SND_NODEFAULT = 0x0002, SND_MEMORY = 0x0004;
        try { PlaySound(Pinned[sound].AddrOfPinnedObject(), IntPtr.Zero, SND_ASYNC | SND_NODEFAULT | SND_MEMORY); }
        catch { /* no audio */ }
    }

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW")]
    private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);

    // ---------------------------------------------------------------- synthesis

    /// <summary>Short, soft "tick" for moving focus.</summary>
    private static byte[] SynthFocus() => ToWav(Tone(
        durationMs: 45, volume: 0.22,
        (t, _) => Math.Sin(2 * Math.PI * 1320 * t) * Math.Exp(-t * 90)));

    /// <summary>Two-note rising chime for selecting a tile.</summary>
    private static byte[] SynthSelect() => ToWav(Tone(
        durationMs: 260, volume: 0.3,
        (t, _) =>
        {
            double Note(double freq, double start) =>
                t < start ? 0 : Math.Sin(2 * Math.PI * freq * (t - start)) * Math.Exp(-(t - start) * 14);
            return 0.6 * Note(784, 0) + 0.7 * Note(1175, 0.075); // G5 → D6
        }));

    /// <summary>
    /// Ambient startup sound, timed to the intro animation: a deep pad swells in, a soft chime arpeggio rings
    /// as the logo settles (~0.5 s), and a gentle swoosh carries the transition out (~2 s). Mixed quietly
    /// and passed through a small reverb.
    /// </summary>
    private static byte[] SynthIntro()
    {
        const double length = 3.9;
        var n = (int)(SampleRate * length);
        var buf = new double[n];
        var rnd = new Random(7);

        static double Smooth(double x) { x = Math.Clamp(x, 0, 1); return x * x * (3 - 2 * x); }
        static double Sin(double freq, double t, double phase = 0) => Math.Sin(2 * Math.PI * freq * t + phase);

        // Open D chord (D2 A2 D3 A3 E4); each voice is two slightly detuned sines for a slow chorus shimmer.
        double[] padFreq = [73.42, 110.0, 146.83, 220.0, 329.63];
        double[] padGain = [0.5, 0.35, 0.3, 0.16, 0.08];
        // Chime arpeggio as the wordmark forms: A5 D6 E6 A6.
        (double Freq, double At)[] chimes = [(880.0, 0.5), (1174.66, 0.62), (1318.51, 0.74), (1760.0, 0.88)];

        double lp1 = 0, lp2 = 0;
        for (var i = 0; i < n; i++)
        {
            var t = (double)i / SampleRate;

            var padEnv = Smooth(t / 1.1) * (1 - Smooth((t - 2.1) / 1.6));
            var pad = 0.0;
            for (var v = 0; v < padFreq.Length; v++)
                pad += padGain[v] * 0.5 * (Sin(padFreq[v], t) + Sin(padFreq[v] * 1.003, t, v));

            var airEnv = Smooth((t - 0.3) / 1.0) * (1 - Smooth((t - 1.9) / 1.5));
            var air = (Sin(659.25, t) + Sin(987.77, t)) * (0.6 + 0.4 * Sin(0.7, t)) * airEnv; // E5 B5

            var chime = 0.0;
            foreach (var (freq, at) in chimes)
            {
                if (t < at) continue;
                var u = t - at;
                var bell = Sin(freq, u) + 0.25 * Sin(freq * 2.76, u) * Math.Exp(-u * 6); // inharmonic partial = bell
                chime += bell * Math.Exp(-u * 2.2) * (1 - Math.Exp(-u * 300));             // soft attack, long ring
            }

            // Swoosh: low-passed noise whose brightness rises and falls across the transition.
            var whoosh = 0.0;
            var s = (t - 1.95) / 1.2;
            if (s is > 0 and < 1)
            {
                var sweep = Math.Sin(Math.PI * s);
                var k = 1 - Math.Exp(-2 * Math.PI * (120 + 1000 * sweep * sweep) / SampleRate);
                lp1 += k * (rnd.NextDouble() * 2 - 1 - lp1);
                lp2 += k * (lp1 - lp2);
                whoosh = lp2 * sweep * sweep;
            }

            buf[i] = 0.55 * pad * padEnv + 0.05 * air + 0.2 * chime + 2.1 * whoosh;
        }

        buf = Reverb(buf, wet: 0.3);

        // Normalise to a calm level and fade the edges.
        var peak = buf.Max(Math.Abs);
        var gain = peak > 0 ? 0.216 / peak : 0;
        var fade = SampleRate / 100;
        var samples = new short[n];
        for (var i = 0; i < n; i++)
        {
            var env = Math.Min(1.0, Math.Min((double)i / fade, (double)(n - 1 - i) / fade));
            samples[i] = (short)(Math.Clamp(buf[i] * gain * env, -1, 1) * short.MaxValue);
        }
        return ToWav(samples);
    }

    /// <summary>Small Schroeder reverb (four damped combs into two all-passes).</summary>
    private static double[] Reverb(double[] input, double wet)
    {
        int[] combDelays = [1557, 1617, 1491, 1422];
        int[] allpassDelays = [225, 556];
        var wetBuf = new double[input.Length];

        foreach (var d in combDelays)
        {
            var line = new double[d];
            double damp = 0;
            for (int i = 0, j = 0; i < input.Length; i++, j = (j + 1) % d)
            {
                var y = line[j];
                damp = y * 0.6 + damp * 0.4;
                line[j] = input[i] + damp * 0.8;
                wetBuf[i] += y * 0.25;
            }
        }
        foreach (var d in allpassDelays)
        {
            var line = new double[d];
            for (int i = 0, j = 0; i < wetBuf.Length; i++, j = (j + 1) % d)
            {
                var b = line[j];
                var x = wetBuf[i];
                line[j] = x + b * 0.5;
                wetBuf[i] = b - x * 0.5;
            }
        }

        var output = new double[input.Length];
        for (var i = 0; i < input.Length; i++) output[i] = input[i] * (1 - wet) + wetBuf[i] * wet;
        return output;
    }

    private static short[] Tone(int durationMs, double volume, Func<double, double, double> wave)
    {
        var n = SampleRate * durationMs / 1000;
        var samples = new short[n];
        var fade = SampleRate * 3 / 1000; // 3 ms fade in/out avoids clicks
        for (var i = 0; i < n; i++)
        {
            var t = (double)i / SampleRate;
            var env = Math.Min(1.0, Math.Min((double)i / fade, (double)(n - 1 - i) / fade));
            var v = Math.Clamp(wave(t, (double)i / n) * volume * env, -1, 1);
            samples[i] = (short)(v * short.MaxValue);
        }
        return samples;
    }

    private static byte[] ToWav(short[] samples)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var dataLen = samples.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + dataLen); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(SampleRate); w.Write(SampleRate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataLen);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }

    private static bool IsOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Any(d => File.Exists(Path.Combine(d, exe)));
}
