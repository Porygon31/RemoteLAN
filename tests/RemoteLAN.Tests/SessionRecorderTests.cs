using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using RemoteLAN.Recording;
using Vortice.MediaFoundation;

namespace RemoteLAN.Tests;

public class SessionRecorderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("RemoteLAN_RecorderTests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] MakeRedOverBlueJpeg(int width, int height)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.FillRectangle(Brushes.Red, 0, 0, width, height / 2);
            g.FillRectangle(Brushes.Blue, 0, height / 2, width, height - height / 2);
        }
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Jpeg);
        return ms.ToArray();
    }

    private async Task<string> RecordAsync(params byte[][] frames)
    {
        string path = Path.Combine(_dir, $"{Guid.NewGuid():N}.mp4");
        var recorder = new SessionRecorder(path);
        foreach (byte[] frame in frames)
        {
            recorder.AddFrame(frame);
            await Task.Delay(30);
        }
        recorder.Stop();
        await recorder.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        return path;
    }

    private static byte[][] Repeat(byte[] frame, int count) => Enumerable.Repeat(frame, count).ToArray();

    /// <summary>Decodes the last frame the file yields, as an independent check of what a player would show.</summary>
    private static (int Width, int Height, Color Top, Color Bottom) ReadBackFrame(string path)
    {
        MediaFactory.MFStartup().CheckError();
        try
        {
            using var attributes = MediaFactory.MFCreateAttributes(1);
            attributes.Set(SourceReaderAttributeKeys.EnableVideoProcessing, 1u);
            using var reader = MediaFactory.MFCreateSourceReaderFromURL(path, attributes);

            using var requested = MediaFactory.MFCreateMediaType();
            requested.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            requested.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
            reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, requested);

            using var actual = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
            ulong size = actual.GetUInt64(MediaTypeAttributeKeys.FrameSize);
            int width = (int)(size >> 32);
            int height = (int)(size & 0xFFFFFFFF);

            for (int attempt = 0; attempt < 30; attempt++)
            {
                using IMFSample? sample = reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None, out _, out SourceReaderFlag flags, out _);
                if (sample == null)
                {
                    if ((flags & SourceReaderFlag.EndOfStream) != 0) break;
                    continue;
                }

                using IMFMediaBuffer buffer = sample.ConvertToContiguousBuffer();
                buffer.Lock(out IntPtr data, out _, out _);
                try
                {
                    int stride = (int)actual.GetUInt32(MediaTypeAttributeKeys.DefaultStride);
                    Color Pixel(int y)
                    {
                        // A negative stride means the decoder hands rows over bottom-up.
                        int row = stride < 0 ? height - 1 - y : y;
                        int value = Marshal.ReadInt32(data, row * Math.Abs(stride) + width / 2 * 4);
                        return Color.FromArgb(255, Color.FromArgb(value));
                    }
                    return (width, height, Pixel(height / 4), Pixel(height * 3 / 4));
                }
                finally
                {
                    buffer.Unlock();
                }
            }
            throw new InvalidOperationException("No frame could be decoded.");
        }
        finally
        {
            MediaFactory.MFShutdown();
        }
    }

    [Fact]
    public async Task Recording_ProducesPlayableVideo_TheRightWayUp()
    {
        string path = await RecordAsync(Repeat(MakeRedOverBlueJpeg(640, 360), 15));

        var frame = ReadBackFrame(path);

        Assert.Equal(640, frame.Width);
        Assert.Equal(360, frame.Height);
        Assert.True(frame.Top.R > 150 && frame.Top.B < 100, $"top should be red, was {frame.Top}");
        Assert.True(frame.Bottom.B > 150 && frame.Bottom.R < 100, $"bottom should be blue, was {frame.Bottom}");
    }

    [Fact]
    public async Task RemoteResolutionChange_KeepsInitialVideoSize()
    {
        var frames = Repeat(MakeRedOverBlueJpeg(640, 360), 8)
            .Concat(Repeat(MakeRedOverBlueJpeg(1280, 720), 8))
            .ToArray();

        string path = await RecordAsync(frames);

        var frame = ReadBackFrame(path);
        Assert.Equal(640, frame.Width);
        Assert.Equal(360, frame.Height);
    }

    [Fact]
    public async Task CorruptFrame_IsSkipped()
    {
        byte[] good = MakeRedOverBlueJpeg(320, 240);
        string path = await RecordAsync(good, new byte[] { 1, 2, 3 }, good, good, good, good, good, good);

        Assert.Equal(320, ReadBackFrame(path).Width);
    }

    [Fact]
    public async Task NoFrames_FailsAndLeavesNoFile()
    {
        string path = Path.Combine(_dir, "empty.mp4");
        var recorder = new SessionRecorder(path);

        recorder.Stop();

        await Assert.ThrowsAnyAsync<Exception>(() => recorder.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task UnwritablePath_FailsCompletionInsteadOfThrowingToCaller()
    {
        string path = Path.Combine(_dir, "missing-folder", "video.mp4");
        var recorder = new SessionRecorder(path);

        recorder.AddFrame(MakeRedOverBlueJpeg(320, 240));
        recorder.Stop();

        await Assert.ThrowsAnyAsync<Exception>(() => recorder.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task AddFrameAfterStop_IsIgnored()
    {
        string path = Path.Combine(_dir, "late.mp4");
        var recorder = new SessionRecorder(path);
        recorder.AddFrame(MakeRedOverBlueJpeg(320, 240));
        recorder.Stop();
        recorder.Stop();

        recorder.AddFrame(MakeRedOverBlueJpeg(320, 240));

        await recorder.Completion.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData(1920, 1080, 1920, 1080)]
    [InlineData(1365, 767, 1364, 766)]
    [InlineData(5120, 1440, 4096, 1152)]
    [InlineData(1440, 2560, 1296, 2304)]
    public void VideoSize_StaysEvenAndWithinEncoderLimits(int width, int height, int expectedWidth, int expectedHeight)
    {
        Assert.Equal((expectedWidth, expectedHeight), SessionRecorder.VideoSize(width, height));
    }

    [Theory]
    [InlineData(@"..\..\Windows\evil")]
    [InlineData("C:/Windows/evil")]
    [InlineData("")]
    public void BuildFileName_IsAlwaysASimpleFileName(string remoteName)
    {
        string name = RecordingLocation.BuildFileName(remoteName, new DateTime(2026, 11, 3, 10, 15, 30));

        Assert.Equal(name, Path.GetFileName(name));
        Assert.DoesNotContain("..", name);
        Assert.Contains("20261103_101530", name);
        Assert.EndsWith(".mp4", name);
    }

    [Fact]
    public void BuildFileName_LimitsTheRemoteNameLength()
    {
        string name = RecordingLocation.BuildFileName(new string('x', 500), DateTime.Now);

        Assert.True(name.Length < 100, $"length was {name.Length}");
    }

    [Fact]
    public void BuildFileName_CannotBePredicted()
    {
        var now = DateTime.Now;

        Assert.NotEqual(RecordingLocation.BuildFileName("PC", now), RecordingLocation.BuildFileName("PC", now));
    }
}
