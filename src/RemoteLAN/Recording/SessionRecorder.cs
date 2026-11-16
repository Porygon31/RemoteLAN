using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Vortice.MediaFoundation;

namespace RemoteLAN.Recording;

/// <summary>
/// Encodes the JPEG frames of a session into an H.264 MP4 file on a dedicated thread.
/// Any failure surfaces through <see cref="Completion"/> and never disturbs the session itself.
/// </summary>
public sealed class SessionRecorder
{
    private const int FramesPerSecond = 15; // Rate of AgentServer.StreamScreenLoopAsync
    private const long SampleDuration = TimeSpan.TicksPerSecond / FramesPerSecond;
    private const int MaxVideoWidth = 4096; // Media Foundation H.264 encoder limits
    private const int MaxVideoHeight = 2304;
    private const double BitsPerPixel = 0.15;

    // shortcut: frames dropped when the encoder falls 2s behind, raise the bound if videos stutter
    private readonly BlockingCollection<(byte[] Jpeg, long Timestamp)> _queue = new(FramesPerSecond * 2);
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string OutputPath { get; }

    /// <summary>Completes once the file is playable; faults if no valid file could be produced.</summary>
    public Task Completion => _completion.Task;

    public SessionRecorder(string outputPath)
    {
        OutputPath = outputPath;

        // Foreground: exiting the app must wait for the file to be finalized
        var worker = new Thread(Run) { IsBackground = false, Name = "RemoteLAN_SessionRecorder" };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
    }

    /// <summary>Queues a frame without ever blocking the network receive loop.</summary>
    public void AddFrame(byte[] jpegBytes)
    {
        try
        {
            _queue.TryAdd((jpegBytes, Stopwatch.GetTimestamp()));
        }
        catch (InvalidOperationException)
        {
            // Recording already stopped
        }
    }

    public void Stop() => _queue.CompleteAdding();

    internal static (int Width, int Height) VideoSize(int width, int height)
    {
        double scale = Math.Min(1.0, Math.Min((double)MaxVideoWidth / width, (double)MaxVideoHeight / height));
        return ((int)(width * scale + 0.5) & ~1, (int)(height * scale + 0.5) & ~1);
    }

    private void Run()
    {
        try
        {
            MediaFactory.MFStartup().CheckError();
            try
            {
                EncodeQueuedFrames();
            }
            finally
            {
                MediaFactory.MFShutdown();
            }
            _completion.SetResult();
        }
        catch (Exception ex)
        {
            _queue.CompleteAdding();
            try
            {
                // Without finalization an MP4 is unreadable and only wastes disk space
                File.Delete(OutputPath);
            }
            catch { }
            _completion.SetException(ex);
        }
    }

    private void EncodeQueuedFrames()
    {
        IMFSinkWriter? writer = null;
        Bitmap? canvas = null;
        Graphics? graphics = null;
        int stream = 0;
        long firstTimestamp = 0;

        try
        {
            foreach (var (jpeg, timestamp) in _queue.GetConsumingEnumerable())
            {
                using var jpegStream = new MemoryStream(jpeg);
                Bitmap frame;
                try
                {
                    frame = new Bitmap(jpegStream);
                }
                catch (ArgumentException)
                {
                    continue; // Corrupt frame
                }

                using (frame)
                {
                    if (writer == null)
                    {
                        var (width, height) = VideoSize(frame.Width, frame.Height);
                        canvas = new Bitmap(width, height, PixelFormat.Format32bppRgb);
                        graphics = Graphics.FromImage(canvas);
                        writer = CreateWriter(width, height, out stream);
                        firstTimestamp = timestamp;
                    }

                    graphics!.Clear(Color.Black);
                    graphics.DrawImage(frame, FitInside(frame.Size, canvas!.Size));
                }

                long sampleTime = Stopwatch.GetElapsedTime(firstTimestamp, timestamp).Ticks;
                using IMFSample sample = CreateSample(canvas!, sampleTime);
                writer.WriteSample(stream, sample);
            }

            if (writer == null)
            {
                throw new InvalidOperationException("No video frames were received.");
            }

            writer.Finalize();
        }
        finally
        {
            writer?.Dispose();
            graphics?.Dispose();
            canvas?.Dispose();
        }
    }

    private IMFSinkWriter CreateWriter(int width, int height, out int stream)
    {
        IMFSinkWriter writer = MediaFactory.MFCreateSinkWriterFromURL(OutputPath, null, null);
        try
        {
            using IMFMediaType output = CreateVideoType(VideoFormatGuids.H264, width, height);
            output.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)(width * height * FramesPerSecond * BitsPerPixel));
            stream = writer.AddStream(output);

            using IMFMediaType input = CreateVideoType(VideoFormatGuids.Rgb32, width, height);
            writer.SetInputMediaType(stream, input, null);

            writer.BeginWriting();
            return writer;
        }
        catch
        {
            writer.Dispose();
            throw;
        }
    }

    private static IMFMediaType CreateVideoType(Guid subtype, int width, int height)
    {
        IMFMediaType type = MediaFactory.MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        type.Set(MediaTypeAttributeKeys.Subtype, subtype);
        type.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
        type.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
        type.Set(MediaTypeAttributeKeys.FrameRate, Pack(FramesPerSecond, 1));
        type.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
        return type;
    }

    private static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;

    /// <summary>Same letterbox/pillarbox rule as CoordinateTranslator, for a remote resolution change mid-recording.</summary>
    private static Rectangle FitInside(Size frame, Size canvas)
    {
        double scale = Math.Min((double)canvas.Width / frame.Width, (double)canvas.Height / frame.Height);
        int width = (int)Math.Round(frame.Width * scale);
        int height = (int)Math.Round(frame.Height * scale);
        return new Rectangle((canvas.Width - width) / 2, (canvas.Height - height) / 2, width, height);
    }

    private static unsafe IMFSample CreateSample(Bitmap canvas, long sampleTime)
    {
        int rowBytes = canvas.Width * 4;
        int length = rowBytes * canvas.Height;

        using IMFMediaBuffer buffer = MediaFactory.MFCreateMemoryBuffer(length);
        buffer.Lock(out IntPtr destination, out _, out _);
        var pixels = canvas.LockBits(new Rectangle(Point.Empty, canvas.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            // Media Foundation RGB32 is bottom-up, GDI+ bitmaps are top-down
            byte* source = (byte*)pixels.Scan0;
            byte* target = (byte*)destination;
            for (int y = 0; y < canvas.Height; y++)
            {
                Buffer.MemoryCopy(source + y * pixels.Stride, target + (canvas.Height - 1 - y) * rowBytes, rowBytes, rowBytes);
            }
        }
        finally
        {
            canvas.UnlockBits(pixels);
            buffer.Unlock();
        }
        buffer.CurrentLength = length;

        IMFSample sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = sampleTime;
        sample.SampleDuration = SampleDuration;
        return sample;
    }
}
