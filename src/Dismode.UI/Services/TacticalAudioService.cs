using System.IO;
using System.Runtime.InteropServices;

namespace Dismode.UI.Services;

public sealed class TacticalAudioService
{
    private const uint SndAsync = 0x0001;
    private const uint SndNodefault = 0x0002;
    private const uint SndMemory = 0x0004;

    private static readonly Lazy<TacticalAudioService> s_instance = new(() => new TacticalAudioService());
    private static readonly AsyncLocal<int> s_panelSilence = new();
    public static TacticalAudioService Instance => s_instance.Value;

    private readonly byte[] _sessionStartSfx;
    private readonly byte[] _sessionStopSfx;
    private readonly byte[] _clickSfx;
    private readonly byte[] _toggleSfx;
    private readonly byte[] _successSfx;
    private readonly byte[] _warningSfx;

    public bool IsEnabled { get; set; } = true;

    internal static IDisposable SilencePanelAction() => new PanelSilence();
    internal static bool IsPanelActionSilent => s_panelSilence.Value > 0;

    private sealed class PanelSilence : IDisposable
    {
        private readonly int _previous = s_panelSilence.Value;
        internal PanelSilence() => s_panelSilence.Value++;
        public void Dispose() => s_panelSilence.Value = _previous;
    }

    private TacticalAudioService()
    {
        _sessionStartSfx = PinForPlayback(SynthesizeSessionStartWav());
        _sessionStopSfx = PinForPlayback(SynthesizeSessionStopWav());
        _clickSfx = PinForPlayback(SynthesizeClickWav());
        _toggleSfx = PinForPlayback(SynthesizeToggleWav());
        _successSfx = PinForPlayback(SynthesizeSuccessWav());
        _warningSfx = PinForPlayback(SynthesizeWarningWav());
    }

    public void PlaySessionStart() => Play(_sessionStartSfx);
    public void PlaySessionStop() => Play(_sessionStopSfx);
    public void PlayClick() => Play(_clickSfx);
    public void PlayToggle() => Play(_toggleSfx);
    public void PlaySuccess() => Play(_successSfx);
    public void PlayWarning() => Play(_warningSfx);

    /// <summary>
    /// PlaySound with SND_ASYNC|SND_MEMORY keeps reading the buffer after the
    /// call returns, so the data must never move: copy it to the pinned
    /// object heap, which the GC does not compact.
    /// </summary>
    internal static byte[] PinForPlayback(byte[] source)
    {
        byte[] pinned = GC.AllocateUninitializedArray<byte>(
            source.Length,
            pinned: true);
        source.CopyTo(pinned, 0);
        return pinned;
    }

    private void Play(byte[] wavData)
    {
        if (!IsEnabled || IsPanelActionSilent || wavData.Length == 0)
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
        // Cieply wzlot z oddechem szumu i rozwiazaniem w kwincie.
        return SynthesizeWav(0.38, 0.16, true,
            new(0, 0.30, 261.63, 392, 0.70),
            new(0.075, 0.305, 523.25, 523.25, 0.48),
            new(0.13, 0.25, 784, 784, 0.22));
    }

    private static byte[] SynthesizeSessionStopWav()
    {
        // Lagodne opadanie do toniki zamiast alarmowego zjazdu.
        return SynthesizeWav(0.34, 0.10, false,
            new(0, 0.28, 523.25, 392, 0.55),
            new(0.055, 0.285, 261.63, 261.63, 0.65));
    }

    private static byte[] SynthesizeClickWav()
    {
        return SynthesizeWav(0.026, 0.035, false,
            new Voice(0, 0.026, 1050, 720, 1, 0.18));
    }

    private static byte[] SynthesizeToggleWav()
    {
        return SynthesizeWav(0.065, 0.025, true,
            new(0, 0.065, 660, 880, 0.85, 0.25),
            new(0.012, 0.053, 1320, 1320, 0.15, 0.20));
    }

    private static byte[] SynthesizeSuccessWav()
    {
        // Nakladajace sie glosy akordu C-dur, bez przeskoku fazy miedzy nutami.
        return SynthesizeWav(0.46, 0, true,
            new(0, 0.40, 523.25, 523.25, 0.55),
            new(0.045, 0.415, 659.25, 659.25, 0.40),
            new(0.09, 0.37, 784, 784, 0.35));
    }

    private static byte[] SynthesizeWarningWav()
    {
        // Dwa miekkie pukniecia: czytelne ostrzezenie bez ostrego buzzera.
        return SynthesizeWav(0.30, 0, false,
            new(0, 0.14, 392, 369.99, 0.75, 0.30),
            new(0.14, 0.16, 329.63, 329.63, 0.65, 0.30));
    }

    private readonly record struct Voice(
        double Start, double Duration, double Frequency, double EndFrequency, double Gain, double Sustain = 0.40);

    private static byte[] SynthesizeWav(double duration, double noiseGain, bool rising, params Voice[] voices)
    {
        const int sampleRate = 44100;
        double[] mix = new double[(int)(duration * sampleRate)];
        double[] fades = new double[mix.Length];
        uint noiseState = 0xD15C0DE;
        double lowPass = 0;
        double softLowPass = 0;
        double highPassState = 0;
        double sum = 0;
        double fadeSum = 0;

        for (int i = 0; i < mix.Length; i++)
        {
            double time = (double)i / sampleRate;
            double sample = 0;
            foreach (Voice voice in voices)
            {
                double localTime = time - voice.Start;
                double progress = localTime / voice.Duration;
                if (progress < 0 || progress >= 1)
                {
                    continue;
                }

                // Calkowanie liniowej zmiany czestotliwosci zachowuje ciaglosc fazy.
                double phase = 2 * Math.PI * ((voice.Frequency * localTime)
                    + ((voice.EndFrequency - voice.Frequency) * localTime * localTime / (2 * voice.Duration)));
                double tone = (0.72 * Math.Sin(phase))
                    + (0.16 * Math.Sin((phase * 1.003) + (0.04 * Math.Sin(2 * Math.PI * 4 * localTime))))
                    + (0.09 * Math.Sin(2 * phase))
                    + (0.03 * Math.Sin(3 * phase));
                sample += tone * Envelope(progress, voice.Sustain) * voice.Gain;
            }

            // Staly PRNG, dwustopniowy dolnoprzepust i odciecie basu zmiekczaja szum.
            noiseState ^= noiseState << 13;
            noiseState ^= noiseState >> 17;
            noiseState ^= noiseState << 5;
            double noise = (noiseState / (double)uint.MaxValue * 2) - 1;
            double sweep = rising ? time / duration : 1 - (time / duration);
            double cutoff = 550 + (1450 * sweep);
            double lowPassCoefficient = 1 - Math.Exp(-2 * Math.PI * cutoff / sampleRate);
            lowPass += lowPassCoefficient * (noise - lowPass);
            softLowPass += lowPassCoefficient * (lowPass - softLowPass);
            highPassState += (1 - Math.Exp(-2 * Math.PI * 350 / sampleRate)) * (softLowPass - highPassState);
            double noiseProgress = time / Math.Min(duration, 0.16);
            sample += (softLowPass - highPassState) * noiseGain * Envelope(noiseProgress, 0.55);

            double fade = SmoothStep(Math.Min(time, (double)(mix.Length - 1 - i) / sampleRate) / 0.004);
            fades[i] = fade;
            mix[i] = sample * fade;
            sum += mix[i];
            fadeSum += fade;
        }

        // Usuwanie DC z zachowaniem zer na brzegach, potem wspolny RMS i zapas szczytowy.
        double dc = sum / fadeSum;
        double peak = 0;
        double sumSquares = 0;
        for (int i = 0; i < mix.Length; i++)
        {
            mix[i] -= dc * fades[i];
            if (!double.IsFinite(mix[i]))
            {
                throw new InvalidOperationException("Nieprawidłowa próbka syntezy audio.");
            }

            peak = Math.Max(peak, Math.Abs(mix[i]));
            sumSquares += mix[i] * mix[i];
        }

        double gain = Math.Min(0.11 / Math.Sqrt(sumSquares / mix.Length), 0.49 / peak);
        short[] samples = new short[mix.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)Math.Round(mix[i] * gain * short.MaxValue);
        }

        return CreateWavFromSamples(samples, sampleRate);
    }

    private static double Envelope(double progress, double sustain)
    {
        if (progress < 0 || progress >= 1)
        {
            return 0;
        }

        if (progress < 0.06)
        {
            return SmoothStep(progress / 0.06);
        }

        if (progress < 0.28)
        {
            return 1 - ((1 - sustain) * SmoothStep((progress - 0.06) / 0.22));
        }

        return progress < 0.55 ? sustain : sustain * SmoothStep((1 - progress) / 0.45);
    }

    private static double SmoothStep(double value)
    {
        value = Math.Clamp(value, 0, 1);
        return value * value * (3 - (2 * value));
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
