namespace VibePlayer.Core.Ai;

/// <summary>Tiny grayscale thumbnails of frames: comparing two of them tells a scene cut from ordinary motion.</summary>
internal static class FrameMath
{
    private const int ThumbWidth = 32;
    private const int ThumbHeight = 18;

    /// <summary>A 32×18 grayscale version of the frame (each cell is the average of its area).</summary>
    public static float[] Thumbnail(VideoFrame frame)
    {
        var thumb = new float[ThumbWidth * ThumbHeight];
        var px = frame.Pixels;
        int w = frame.Width, h = frame.Height;
        for (var ty = 0; ty < ThumbHeight; ty++)
        {
            int y0 = ty * h / ThumbHeight, y1 = Math.Max(y0 + 1, (ty + 1) * h / ThumbHeight);
            for (var tx = 0; tx < ThumbWidth; tx++)
            {
                int x0 = tx * w / ThumbWidth, x1 = Math.Max(x0 + 1, (tx + 1) * w / ThumbWidth);
                long sum = 0;
                var count = 0;
                // Every other pixel is plenty for an average.
                for (var y = y0; y < y1; y += 2)
                {
                    var row = y * w * 4;
                    for (var x = x0; x < x1; x += 2)
                    {
                        var i = row + x * 4;
                        sum += (px[i] * 29 + px[i + 1] * 150 + px[i + 2] * 77) >> 8; // B, G, R
                        count++;
                    }
                }
                thumb[ty * ThumbWidth + tx] = count > 0 ? (float)sum / count : 0;
            }
        }
        return thumb;
    }

    /// <summary>Mean absolute difference of two thumbnails, 0 (same) .. 1 (black vs white).</summary>
    public static float Difference(float[]? a, float[] b)
    {
        if (a == null || a.Length != b.Length) return 0;
        double sum = 0;
        for (var i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return (float)(sum / a.Length / 255.0);
    }
}
