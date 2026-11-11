using System.Drawing.Imaging;

namespace CELMapViewer;

public sealed partial class MainForm
{
    private enum ShotKind { Map, Room, View }

    private bool _shooting;

    private async Task ScreenshotAsync(ShotKind kind, Room? room = null)
    {
        if (_shooting) return;
        if (_map == null || _canvas.Rooms.Count == 0) { Log("Open a map first."); return; }

        RectangleF area;
        List<Room> rooms;
        string what, file = Path.GetFileNameWithoutExtension(_path) ?? "map";
        switch (kind)
        {
            case ShotKind.Room:
                room ??= SelectedRoom();
                if (room == null) { Log("Select a room to screenshot."); return; }
                area = room.Bounds;
                rooms = new() { room };
                what = $"room {room.Name}";
                file += "_" + room.Name;
                break;
            case ShotKind.View:
                area = _canvas.ViewWorldBounds;
                rooms = _canvas.Rooms.Where(r => area.IntersectsWith(r.Bounds)).ToList();
                what = "current view";
                file += "_view";
                break;
            default:
                var b = _canvas.MapWorldBounds!.Value;
                b.Inflate(16, 16);
                area = b;
                rooms = _canvas.Rooms.ToList();
                what = "entire map";
                break;
        }

        var defaults = new ScreenshotOptions(kind == ShotKind.Room ? 2f : 1f, _canvas.ShowEntities, _canvas.ShowTriggers,
                                             kind != ShotKind.Room, kind != ShotKind.Room && _canvas.ShowLabels, false);
        using var dlg = new ScreenshotForm(what, area, defaults);
        var result = dlg.ShowDialog(this);
        if (result is not (DialogResult.OK or DialogResult.Yes)) return;
        var opts = dlg.Options;
        bool clipboard = dlg.ToClipboard;

        string? target = null;
        if (!clipboard)
        {
            using var save = new SaveFileDialog
            {
                Filter = "PNG image (*.png)|*.png",
                FileName = string.Concat((file + ".png").Split(Path.GetInvalidFileNameChars())),
                InitialDirectory = Directory.Exists(_settings.ScreenshotDir) ? _settings.ScreenshotDir : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
            };
            if (save.ShowDialog(this) != DialogResult.OK) return;
            target = save.FileName;
            _settings.ScreenshotDir = Path.GetDirectoryName(target);
            _settings.Save();
        }

        _shooting = true;
        UseWaitCursor = true;
        Log($"Rendering screenshot of {what}…");
        try
        {
            using var bmp = await _canvas.RenderScreenshotAsync(area, rooms, opts);
            if (target != null)
            {
                await Task.Run(() => bmp.Save(target, ImageFormat.Png));
                Log($"Saved {bmp.Width}x{bmp.Height} screenshot of {what} to {target}");
            }
            else
            {
                Clipboard.SetImage(bmp);
                Log($"Copied {bmp.Width}x{bmp.Height} screenshot of {what} to the clipboard.");
            }
        }
        catch (Exception ex)
        {
            Log($"Screenshot failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Screenshot failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
            _shooting = false;
        }
    }
}
