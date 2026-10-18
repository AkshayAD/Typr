using System.Collections.Concurrent;
using Typr.Infrastructure;
using NAudio.Wave;
using Xunit;
using Xunit.Abstractions;
using System.Diagnostics;

namespace Typr.Infrastructure.Tests;

public sealed class ModelReplayFactAttribute : FactAttribute
{
    public ModelReplayFactAttribute()
    {
        if (!Directory.Exists(Environment.GetEnvironmentVariable("TYPR_TEST_MODEL")) ||
            !File.Exists(Environment.GetEnvironmentVariable("TYPR_TEST_WAV")))
            Skip = "Set TYPR_TEST_MODEL and TYPR_TEST_WAV to verified model and synthetic PCM16 mono 16kHz audio.";
    }
}

public sealed class SpeechReplayTests(ITestOutputHelper output)
{
    [ModelReplayFact]
    public async Task RealModelRecognizesPacedAudioCancelsFinalizationAndStartsAFreshGeneration()
    {
        string? previous = Environment.GetEnvironmentVariable("TYPR_REPLAY_WAV");
        string model = Environment.GetEnvironmentVariable("TYPR_TEST_MODEL")!;
        string wave = Environment.GetEnvironmentVariable("TYPR_TEST_WAV")!;
        Environment.SetEnvironmentVariable("TYPR_REPLAY_WAV", wave);
        try
        {
            using var reader = new WaveFileReader(wave);
            using var speech = new SpeechSession(model, savedAudioLimit: 0);
            var captions = new ConcurrentQueue<int>();
            var errors = new ConcurrentQueue<string>();
            speech.CaptionForRecording += (generation, _) => captions.Enqueue(generation);
            speech.Error += errors.Enqueue;
            speech.Prepare(); // Uses the explicit synthetic replay path, never the microphone.
            int first = speech.StartRecording();
            await Task.Delay(reader.TotalTime + TimeSpan.FromMilliseconds(800));
            var stopClock=Stopwatch.StartNew();
            string result = await speech.StopRecordingAsync().WaitAsync(TimeSpan.FromSeconds(30));
            output.WriteLine($"Synthetic replay stop: {stopClock.Elapsed.TotalMilliseconds:F1}ms; final characters={result.Length}.");
            output.WriteLine($"Synthetic replay captured frames={speech.CapturedSampleCount}; fixture frames={reader.Length/2}.");
            Assert.Equal(reader.Length/2,speech.CapturedSampleCount);
            Assert.True(result.Length >= 20, "The real model must return a meaningful synthetic transcript.");
            Assert.Contains(first, captions);

            int cancelled = speech.StartRecording();
            await Task.Delay(700);
            using var cancellation = new CancellationTokenSource();
            var finalization = speech.StopRecordingAsync(cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => finalization);

            int next = speech.StartRecording();
            Assert.NotEqual(cancelled, next);
            Assert.NotEqual(first, next);
            await Task.Delay(reader.TotalTime + TimeSpan.FromMilliseconds(800));
            string recovered = await speech.StopRecordingAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(reader.Length/2,speech.CapturedSampleCount);
            Assert.Equal(result, recovered);
            Assert.Contains(next, captions);
            Assert.Empty(errors);
        }
        finally { Environment.SetEnvironmentVariable("TYPR_REPLAY_WAV", previous); }
    }
}
