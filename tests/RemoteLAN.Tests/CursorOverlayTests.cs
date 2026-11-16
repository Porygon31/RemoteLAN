using System.Drawing;
using System.Drawing.Imaging;
using RemoteLAN.Capture;

namespace RemoteLAN.Tests;

public class CursorOverlayTests
{
    private static Bitmap MakeSolid(int width, int height, Color color)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(color);
        return bitmap;
    }

    [Fact]
    public void Compose_CopiesSourcePixels_WithoutTouchingTheSource()
    {
        using var overlay = new CursorOverlay();
        using var source = MakeSolid(64, 64, Color.Green);
        var sourceBefore = Enumerable.Range(0, 64).Select(i => source.GetPixel(i, i).ToArgb()).ToArray();

        Bitmap composed = overlay.Compose(source);

        Assert.NotSame(source, composed);
        Assert.Equal(sourceBefore, Enumerable.Range(0, 64).Select(i => source.GetPixel(i, i).ToArgb()));
        // A corner the cursor can only cover if it happens to sit there; check all four so one always stays clean.
        var corners = new[] { composed.GetPixel(0, 0), composed.GetPixel(63, 0), composed.GetPixel(0, 63), composed.GetPixel(63, 63) };
        Assert.Contains(corners, c => c.ToArgb() == Color.Green.ToArgb());
    }

    [Fact]
    public void Compose_FollowsSourceDimensions()
    {
        using var overlay = new CursorOverlay();
        using var small = MakeSolid(64, 48, Color.Black);
        using var large = MakeSolid(128, 96, Color.Black);

        Assert.Equal(new Size(64, 48), overlay.Compose(small).Size);
        Assert.Equal(new Size(128, 96), overlay.Compose(large).Size);
    }

    [Theory]
    [InlineData(640, 360, 1280, 720, 1920, 1080, 960, 540)] // process sees a virtualized screen, capture is physical
    [InlineData(100, 200, 1920, 1080, 1920, 1080, 100, 200)] // same coordinate space
    [InlineData(2500, 100, 1920, 1080, 1920, 1080, 2500, 100)] // secondary screen stays outside the frame
    public void MapToFrame_ScalesScreenPositionToFrame(int x, int y, int screenW, int screenH, int frameW, int frameH, int expectedX, int expectedY)
    {
        Assert.Equal(new Point(expectedX, expectedY), CursorOverlay.MapToFrame(new Point(x, y), new Size(screenW, screenH), new Size(frameW, frameH)));
    }
}
