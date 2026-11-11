using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace CELMapViewer;

/// <summary>What to include in an exported screenshot; <see cref="Scale"/> is output pixels per world pixel.</summary>
public sealed record ScreenshotOptions(float Scale, bool Entities, bool Triggers, bool Borders, bool Labels, bool Transparent);

public sealed partial class MapCanvas
{
    /// <summary>Largest exported image (pixels) and side length GDI+ handles reliably.</summary>
    public const long MaxShotPixels = 200_000_000;
    public const int MaxShotSide = 32_000;

    public Rectangle? MapWorldBounds => Rooms.Count == 0 ? null : MapBounds();
    public RectangleF ViewWorldBounds => new(_origin.X, _origin.Y, Width / Zoom, Height / Zoom);

    public static Size ShotSize(RectangleF area, float scale) =>
        new(Math.Max(1, (int)Math.Ceiling(area.Width * scale)), Math.Max(1, (int)Math.Ceiling(area.Height * scale)));

    public static bool ShotFits(RectangleF area, float scale)
    {
        var s = ShotSize(area, scale);
        return s.Width <= MaxShotSide && s.Height <= MaxShotSide && (long)s.Width * s.Height <= MaxShotPixels;
    }

    /// <summary>
    /// Renders the world rectangle <paramref name="area"/> at full detail (mip 0, regardless of the current zoom):
    /// tiles and decals of <paramref name="rooms"/> on worker threads, then entities/triggers/labels on the UI thread.
    /// </summary>
    public async Task<Bitmap> RenderScreenshotAsync(RectangleF area, IReadOnlyList<Room> rooms, ScreenshotOptions o, CancellationToken ct = default)
    {
        float s = o.Scale;
        if (!ShotFits(area, s)) throw new InvalidOperationException("Screenshot is too large; use a smaller scale.");
        var size = ShotSize(area, s);
        var jobs = rooms.Select(r => (r.Bounds, Job: RoomRenderJob.From(r, _fgTiler, _bgTiler, _showFgDecals, _showBgDecals))).ToList();
        bool showBg = ShowBg;
        Color back = o.Transparent ? Color.Transparent : BackColor, fill = Themes.Current.RoomFill;

        // A throwaway pass requests every sprite / tile block the overlay needs, so they load while tiles render.
        using (var probe = new Bitmap(1, 1))
        using (var pg = Graphics.FromImage(probe))
            DrawShotOverlay(pg, rooms, o, s);

        var bmp = await Task.Run(() =>
        {
            var b = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            try
            {
                using var g = Graphics.FromImage(b);
                g.Clear(back);
                SetupShotGraphics(g, area, s);
                using (var br = new SolidBrush(fill))
                    foreach (var (rb, _) in jobs) g.FillRectangle(br, rb);
                var gate = new object();
                Parallel.ForEach(jobs, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount - 1, 1, 6) }, j =>
                {
                    using var img = j.Job.Render();
                    lock (gate)
                    {
                        if (showBg) DrawLayer(g, RoomImage.Level(img.Bg, 0), j.Bounds);
                        DrawLayer(g, RoomImage.Level(img.Fg, 0), j.Bounds);
                    }
                });
                return b;
            }
            catch { b.Dispose(); throw; }
        }, ct);

        try
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while (AssetWorker.Pending > 0 && DateTime.UtcNow < until) await Task.Delay(50, ct);

            using var g = Graphics.FromImage(bmp);
            SetupShotGraphics(g, area, s);
            DrawShotOverlay(g, rooms, o, s);
            g.ResetTransform();
            if (o.Labels) DrawShotLabels(g, area, rooms, s);
            return bmp;
        }
        catch { bmp.Dispose(); throw; }
    }

    private static void SetupShotGraphics(Graphics g, RectangleF area, float s)
    {
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.SmoothingMode = SmoothingMode.None;
        g.CompositingQuality = CompositingQuality.HighSpeed;
        g.TranslateTransform(-area.X * s, -area.Y * s);
        g.ScaleTransform(s, s);
    }

    private void DrawShotOverlay(Graphics g, IReadOnlyList<Room> rooms, ScreenshotOptions o, float s)
    {
        // Pen widths in the Draw* helpers are 1 / Zoom; borrow Zoom so lines come out one output pixel wide.
        float saved = Zoom;
        Zoom = s;
        try
        {
            using var nodePen = new Pen(Color.FromArgb(160, 255, 255, 255), 1 / s) { DashStyle = DashStyle.Dot };
            if (o.Borders)
            {
                using var roomPen = new Pen(Themes.Current.RoomBorder, 1 / s);
                foreach (var room in rooms) g.DrawRectangle(roomPen, room.Bounds);
            }
            foreach (var room in rooms)
            {
                if (o.Triggers)
                    foreach (var t in room.TriggersEl.Children) DrawTrigger(g, room, t, nodePen);
                if (o.Entities)
                    foreach (var e in room.EntitiesEl.Children) DrawEntity(g, room, e, nodePen);
            }
        }
        finally { Zoom = saved; }
    }

    private static void DrawShotLabels(Graphics g, RectangleF area, IReadOnlyList<Room> rooms, float s)
    {
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var font = new Font("Segoe UI", Math.Clamp(9f * s, 8f, 48f));
        using var shadow = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
        foreach (var room in rooms)
        {
            float x = (room.X - area.X) * s, y = (room.Y - area.Y) * s;
            var sz = g.MeasureString(room.Name, font);
            g.FillRectangle(shadow, x + 1, y + 1, sz.Width, sz.Height);
            g.DrawString(room.Name, font, Brushes.Gainsboro, x + 1, y);
        }
    }
}
