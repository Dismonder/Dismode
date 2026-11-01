using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using Dismode.UI.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Dismode.UnitTests;

[TestClass]
public sealed class TacticalAudioServiceTests
{
    [TestMethod]
    public async Task PanelActionsSilenceNestedAsyncSoundsWithoutMutingOtherActions()
    {
        Assert.IsFalse(TacticalAudioService.IsPanelActionSilent);
        TaskCompletionSource entered = new();
        TaskCompletionSource release = new();
        Task action = Task.Run(async () =>
        {
            using IDisposable silence = TacticalAudioService.SilencePanelAction();
            Assert.IsTrue(TacticalAudioService.IsPanelActionSilent);
            using (TacticalAudioService.SilencePanelAction())
            {
                await Task.Yield();
                Assert.IsTrue(TacticalAudioService.IsPanelActionSilent);
            }
            entered.SetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(TacticalAudioService.IsPanelActionSilent);
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(TacticalAudioService.IsPanelActionSilent);
        release.SetResult();
        await action.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(TacticalAudioService.IsPanelActionSilent);
    }

    [TestMethod]
    [DataRow("SessionStart", 450)]
    [DataRow("SessionStop", 450)]
    [DataRow("Click", 30)]
    [DataRow("Toggle", 80)]
    [DataRow("Success", 500)]
    [DataRow("Warning", 450)]
    public void SynthesizedSoundIsDeterministicAndSafe(string name, int maximumMilliseconds)
    {
        MethodInfo? synthesizer = typeof(TacticalAudioService).GetMethod(
            $"Synthesize{name}Wav", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(synthesizer);
        byte[] wav = (byte[])synthesizer.Invoke(null, null)!;
        CollectionAssert.AreEqual(wav, (byte[])synthesizer.Invoke(null, null)!);

        Assert.IsTrue(wav.Length > 44);
        Assert.AreEqual("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.AreEqual(wav.Length - 8, ReadInt32(wav, 4));
        Assert.AreEqual("WAVEfmt ", Encoding.ASCII.GetString(wav, 8, 8));
        Assert.AreEqual(16, ReadInt32(wav, 16));
        Assert.AreEqual((short)1, ReadInt16(wav, 20));
        Assert.AreEqual((short)1, ReadInt16(wav, 22));
        Assert.AreEqual(44100, ReadInt32(wav, 24));
        Assert.AreEqual(88200, ReadInt32(wav, 28));
        Assert.AreEqual((short)2, ReadInt16(wav, 32));
        Assert.AreEqual((short)16, ReadInt16(wav, 34));
        Assert.AreEqual("data", Encoding.ASCII.GetString(wav, 36, 4));
        Assert.AreEqual(wav.Length - 44, ReadInt32(wav, 40));
        Assert.AreEqual(0, (wav.Length - 44) % 2);

        int sampleCount = (wav.Length - 44) / 2;
        double milliseconds = sampleCount * 1000.0 / 44100;
        Assert.IsTrue(milliseconds >= 10 && milliseconds <= maximumMilliseconds);
        double peak = 0;
        double sum = 0;
        double sumSquares = 0;
        double previous = 0;
        for (int i = 0; i < sampleCount; i++)
        {
            double sample = ReadInt16(wav, 44 + (2 * i)) / 32768.0;
            Assert.IsTrue(double.IsFinite(sample));
            Assert.IsTrue(Math.Abs(sample - previous) < 0.15, $"{name}: discontinuity at {i}");
            peak = Math.Max(peak, Math.Abs(sample));
            sum += sample;
            sumSquares += sample * sample;
            previous = sample;
        }

        Assert.IsTrue(peak <= Math.Pow(10, -6.0 / 20));
        Assert.IsTrue(Math.Abs(sum / sampleCount) < 0.0001);
        double rms = Math.Sqrt(sumSquares / sampleCount);
        Assert.IsTrue(rms is >= 0.09 and <= 0.13, $"{name}: RMS {rms}");
        Assert.IsTrue(Math.Abs((int)ReadInt16(wav, 44)) <= 1);
        Assert.IsTrue(Math.Abs((int)ReadInt16(wav, wav.Length - 2)) <= 1);
        Console.WriteLine($"{name}: {milliseconds:F2} ms, peak {20 * Math.Log10(peak):F2} dBFS, RMS {rms:F4}, DC {sum / sampleCount:E2}");

        // Eksport tylko na zadanie testera, bez plikow w aplikacji.
        string? exportDirectory = Environment.GetEnvironmentVariable("DISMODE_SFX_EXPORT_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(exportDirectory))
        {
            Directory.CreateDirectory(exportDirectory);
            File.WriteAllBytes(Path.Combine(exportDirectory, $"{name}.wav"), wav);
        }
    }

    private static short ReadInt16(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset, 2));

    private static int ReadInt32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));

    [TestMethod]
    public void PinForPlaybackCopiesDataToPinnedObjectHeap()
    {
        byte[] source = [1, 2, 3, 4];

        byte[] pinned = TacticalAudioService.PinForPlayback(source);

        CollectionAssert.AreEqual(source, pinned);
        Assert.AreNotSame(source, pinned);
        // The pinned object heap is reported as the highest generation.
        Assert.AreEqual(GC.MaxGeneration, GC.GetGeneration(pinned));
    }
}
