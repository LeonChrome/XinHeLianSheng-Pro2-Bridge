using NAudio.CoreAudioApi;
using NAudio.Wave;

const int sampleRate = 48000;
const int seconds = 2;

using var enumerator = new MMDeviceEnumerator();
MMDevice? device = enumerator
    .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
    .FirstOrDefault(candidate =>
        candidate.FriendlyName.Contains(
            "DualSense Wireless Controller",
            StringComparison.OrdinalIgnoreCase));
if (device == null)
{
    throw new InvalidOperationException(
        "Active DualSense render endpoint was not found.");
}

WaveFormat format = device.AudioClient.MixFormat;
Console.WriteLine(
    "endpoint=" +
    device.FriendlyName +
    " mix_format=" +
    device.AudioClient.MixFormat +
    " requested_format=" +
    format);
if (format.SampleRate != sampleRate ||
    format.Channels != 4 ||
    format.BitsPerSample != 32)
{
    throw new InvalidOperationException(
        "Unexpected DualSense mix format: " + format);
}

byte[] pcm = new byte[sampleRate * seconds * format.BlockAlign];
double leftPhase = 0;
double rightPhase = 0;
for (int frame = 0; frame < sampleRate * seconds; frame++)
{
    float rearLeft = (float)(Math.Sin(leftPhase) * 0.34);
    float rearRight = (float)(Math.Sin(rightPhase) * 0.34);
    int offset = frame * format.BlockAlign;
    BitConverter.TryWriteBytes(pcm.AsSpan(offset + 8, 4), rearLeft);
    BitConverter.TryWriteBytes(pcm.AsSpan(offset + 12, 4), rearRight);
    leftPhase += 2 * Math.PI * 140 / sampleRate;
    rightPhase += 2 * Math.PI * 330 / sampleRate;
}

using var source = new RawSourceWaveStream(
    new MemoryStream(pcm, writable: false),
    format);
using var output = new WasapiOut(
    device,
    AudioClientShareMode.Shared,
    true,
    20);
var stopped = new TaskCompletionSource(
    TaskCreationOptions.RunContinuationsAsynchronously);
output.PlaybackStopped += (_, args) =>
{
    if (args.Exception != null)
    {
        stopped.TrySetException(args.Exception);
    }
    else
    {
        stopped.TrySetResult();
    }
};
output.Init(source);
var playbackWatch = System.Diagnostics.Stopwatch.StartNew();
output.Play();
await stopped.Task.WaitAsync(TimeSpan.FromSeconds(seconds + 5));
playbackWatch.Stop();
if (playbackWatch.Elapsed < TimeSpan.FromSeconds(seconds * 0.75))
{
    throw new InvalidOperationException(
        "DualSense audio playback was compressed: elapsed=" +
        playbackWatch.Elapsed.TotalSeconds.ToString("F3") +
        "s expected_about=" +
        seconds +
        "s");
}

Console.WriteLine(
    "v60_haptic_audio_smoke: passed endpoint=" +
    device.FriendlyName +
    " format=" +
    format +
    " elapsed_seconds=" +
    playbackWatch.Elapsed.TotalSeconds.ToString("F3"));
