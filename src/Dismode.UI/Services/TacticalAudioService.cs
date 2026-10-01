using System.IO;
using System.Runtime.InteropServices;

namespace Dismode.UI.Services;

public sealed class TacticalAudioService
{
    private const uint SndAsync = 0x0001;
    private const uint SndNodefault = 0x0002;
    private const uint SndMemory = 0x0004;

    private static readonly Lazy<TacticalAudioService> s_instance = new(() => new TacticalAudioService());
    public static TacticalAudioService Instance => s_instance.Value;

    private readonly byte[] _sessionStartSfx;
    private readonly byte[] _sessionStopSfx;
    private readonly byte[] _clickSfx;
    private readonly byte[] _toggleSfx;
    private readonly byte[] _successSfx;
    private readonly byte[] _warningSfx;

    public bool IsEnabled { get; set; } = true;

    private TacticalAudioService()
    {
        _sessionStartSfx = SynthesizeSessionStartWav();
        _sessionStopSfx = SynthesizeSessionStopWav();
        _clickSfx = SynthesizeClickWav();
        _toggleSfx = SynthesizeToggleWav();
        _successSfx = SynthesizeSuccessWav();
        _warningSfx = SynthesizeWarningWav();
    }

    public void PlaySessionStart() => Play(_sessionStartSfx);
    public void PlaySessionStop() => Play(_sessionStopSfx);
    public void PlayClick() => Play(_clickSfx);
    public void PlayToggle() => Play(_toggleSfx);
    public void PlaySuccess() => Play(_successSfx);
    public void PlayWarning() => Play(_warningSfx);

    private void Play(byte[] wavData)
    {
        if (!IsEnabled || wavData.Length == 0)
        {
            return;
        }

        try
        {
            _ = PlaySound(wavData, nint.Zero, SndAsync | SndMemory | SndNodefault);
        }
        catch
        {
            // Ignoruj błędy podsystemu audio, aby nie zakłócać działania UI
        }
    }

    private static byte[] SynthesizeSessionStartWav()
    {
        // Cyber engage: rising frequency 350Hz -> 900Hz with power ramp and exponential decay
        const int sampleRate = 44100;
        const double duration = 0.22;
        int sampleCount = (int)(sampleRate * duration);
        short[] samples = new short[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            double t = (double)i / sampleRate;
            double progress = t / duration;
            double freq = 350.0 + (550.0 * Math.Pow(progress, 1.5));
            double env = progress < 0.15
                ? progress / 0.15
                : Math.Exp(-5.0 * (progress - 0.15));

            double sample = Math.Sin(2.0 * Math.PI * freq * t) * env;
            samples[i] = (short)(sample * short.MaxValue * 0.45);
        }

        return CreateWavFromSamples(samples, sampleRate);
    }

    private static byte[] SynthesizeSessionStopWav()
    {
        // Disengage: descending resolve sweep 650Hz -> 280Hz
        const int sampleRate = 44100;
        const double duration = 0.26;
        int sampleCount = (int)(sampleRate * duration);
        short[] samples = new short[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            double t = (double)i / sampleRate;
            double progress = t / duration;
            double freq = 650.0 - (370.0 * progress);
            double env = progress < 0.1
                ? progress / 0.1
                : Math.Exp(-4.0 * (progress - 0.1));

            double sample = Math.Sin(2.0 * Math.PI * freq * t) * env;
            samples[i] = (short)(sample * short.MaxValue * 0.40);
        }

        return CreateWavFromSamples(samples, sampleRate);
    }

    private static byte[] SynthesizeClickWav()
    {
        // Subtle tactical micro-click
        const int sampleRate = 44100;
        const double duration = 0.018;
        int sampleCount = (int)(sampleRate * duration);
        short[] samples = new short[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            double t = (double)i / sampleRate;
            double progress = t / duration;
            double env = Math.Exp(-20.0 * progress);
            double sample = Math.Sin(2.0 * Math.PI * 1400.0 * t) * env;
            samples[i] = (short)(sample * short.MaxValue * 0.25);
        }

        return CreateWavFromSamples(samples, sampleRate);
    }

    private static byte[] SynthesizeToggleWav()
    {
        // Tactile short blip
        const int sampleRate = 44100;
        const double duration = 0.035;
        int sampleCount = (int)(sampleRate * duration);
        short[] samples = new short[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            double t = (double)i / sampleRate;
            double progress = t / duration;
            double env = Math.Sin(progress * Math.PI);
            double sample = Math.Sin(2.0 * Math.PI * 880.0 * t) * env;
            samples[i] = (short)(sample * short.MaxValue * 0.30);
        }

        return CreateWavFromSamples(samples, sampleRate);
    }

    private static byte[] SynthesizeSuccessWav()
    {
        // Futuristic double chime (D5 -> A5)
        const int sampleRate = 44100;
        const double duration = 0.28;
        int sampleCount = (int)(sampleRate * duration);
        short[] samples = new short[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            double t = (double)i / sampleRate;
            double sample;
            if (t < 0.12)
            {
                double env1 = Math.Exp(-8.0 * (t / 0.12));
                sample = Math.Sin(2.0 * Math.PI * 587.33 * t) * env1;
            }
            else
            {
                double t2 = t - 0.12;
                double env2 = Math.Exp(-6.0 * (t2 / 0.16));
                sample = Math.Sin(2.0 * Math.PI * 880.0 * t2) * env2;
            }

            samples[i] = (short)(sample * short.MaxValue * 0.35);
        }

        return CreateWavFromSamples(samples, sampleRate);
    }

    private static byte[] SynthesizeWarningWav()
    {
        // Tactical alert tone
        const int sampleRate = 44100;
        const double duration = 0.20;
        int sampleCount = (int)(sampleRate * duration);
        short[] samples = new short[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            double t = (double)i / sampleRate;
            double progress = t / duration;
            double env = Math.Sin(progress * Math.PI);
            double sample = (Math.Sin(2.0 * Math.PI * 260.0 * t) + 0.5 * Math.Sin(2.0 * Math.PI * 520.0 * t)) / 1.5 * env;
            samples[i] = (short)(sample * short.MaxValue * 0.40);
        }

        return CreateWavFromSamples(samples, sampleRate);
    }

    private static byte[] CreateWavFromSamples(short[] samples, int sampleRate)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        int subChunk2Size = samples.Length * sizeof(short);
        int chunkSize = 36 + subChunk2Size;

        // RIFF header
        writer.Write("RIFF"u8.ToArray());
        writer.Write(chunkSize);
        writer.Write("WAVE"u8.ToArray());

        // fmt chunk
        writer.Write("fmt "u8.ToArray());
        writer.Write(16); // subChunk1Size (PCM = 16)
        writer.Write((short)1); // AudioFormat (1 = PCM)
        writer.Write((short)1); // NumChannels (1 = Mono)
        writer.Write(sampleRate);
        writer.Write(sampleRate * sizeof(short)); // ByteRate
        writer.Write((short)sizeof(short)); // BlockAlign
        writer.Write((short)16); // BitsPerSample

        // data chunk
        writer.Write("data"u8.ToArray());
        writer.Write(subChunk2Size);
        foreach (short sample in samples)
        {
            writer.Write(sample);
        }

        return stream.ToArray();
    }

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", SetLastError = true)]
    private static extern bool PlaySound(byte[]? pszSound, nint hmod, uint fdwSound);
}
