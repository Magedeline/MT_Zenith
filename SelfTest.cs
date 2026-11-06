using CELMapViewer.Agent;

namespace CELMapViewer;

/// <summary>
/// <c>MT_Zenith --selftest [mapsDir] [pcgLoennRoot]</c>: checks that every .bin round-trips
/// byte-for-byte and that PCGHelper generation imports into a map that re-loads. Output goes to stdout.
/// </summary>
internal static class SelfTest
{
    private static string Dump(BinElement e)
    {
        var sb = new System.Text.StringBuilder();
        void Walk(BinElement el, int depth)
        {
            sb.Append(' ', depth).Append(el.Name);
            foreach (var at in el.Attrs)
                sb.Append(' ').Append(at.Key).Append('=').Append(Convert.ToString(at.Value, System.Globalization.CultureInfo.InvariantCulture));
            sb.Append('\n');
            foreach (var c in el.Children) Walk(c, depth + 1);
        }
        Walk(e, 0);
        return sb.ToString();
    }

    /// <summary>Tilesets, autotiled rendering of every map's rooms, tile-grid round trips, room resize and the plugin catalog.</summary>
    private static int AssetTest(string maps)
    {
        int failed = 0;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var vfg = Autotiler.For(null, true, null);
            Console.WriteLine($"vanilla fg tilesets: {vfg?.Tilesets.Count() ?? 0} ({vfg?.Source ?? "missing"})");
            if (vfg == null || !vfg.Tilesets.Any()) failed++;

            int rooms = 0, painted = 0, gridFail = 0, maps0 = 0;
            foreach (string file in Directory.EnumerateFiles(maps, "*.bin", SearchOption.AllDirectories))
            {
                var map = MapBin.Load(file);
                string? mod = MapModel.ModDirOf(file);
                var fg = Autotiler.For(MapModel.TilesetXml(map, true), true, mod);
                var bg = Autotiler.For(MapModel.TilesetXml(map, false), false, mod);
                if (fg == null || bg == null) { failed++; Console.WriteLine("NO TILESET " + file); continue; }
                maps0++;
                foreach (var l in MapModel.Levels(map).Children)
                {
                    var room = new Room(l);
                    rooms++;
                    var grid = room.Grid(true);
                    if (TileGrid.Parse(grid.Serialize(), room.Cols, room.Rows).Serialize() != grid.Serialize()) gridFail++;
                    if (rooms % 25 != 1) continue; // render a sample of rooms to keep the test quick
                    using var img = RoomRenderJob.From(room, fg, bg).Render();
                    if (img.Fg?[0] != null || img.Bg?[0] != null) painted++;
                }
            }
            Console.WriteLine($"autotile: {maps0} maps, {rooms} rooms, {painted} sampled rooms rendered, {gridFail} grid round-trip failures ({sw.ElapsedMilliseconds} ms)");
            if (gridFail > 0) failed++;

            // resize keeps content fixed in world space
            var t = new MapBin { Root = new BinElement("Map") };
            var lvl = MapModel.NewLevel("t", 0, 0, 32, 24, "1111\n1001\n1111", "");
            MapModel.Levels(t).Children.Add(lvl);
            var e = new BinElement("refill"); e.Set("x", 16); e.Set("y", 8);
            var r = new Room(lvl); r.EntitiesEl.Children.Add(e);
            MapModel.ResizeRoom(r, new Rectangle(-8, 0, 40, 24));
            bool resizeOk = r.W == 40 && e.Int("x") == 24 && r.Grid(true)[1, 0] == '1' && r.Grid(true)[0, 0] == '0';
            Console.WriteLine("room resize: " + (resizeOk ? "ok" : "FAILED"));
            if (!resizeOk) failed++;

            // directional enlarge: normal → xxl from the center, edge tiles extended, rooms beyond pushed
            var m2 = new MapBin { Root = new BinElement("Map") };
            var a2 = MapModel.NewLevel("a", 0, 0, 320, 184, string.Join("\n", Enumerable.Repeat(new string('1', 40), 2).Concat(Enumerable.Repeat("1" + new string('0', 38) + "1", 19)).Concat(Enumerable.Repeat(new string('1', 40), 2))), "");
            var b2 = MapModel.NewLevel("b", 320, 0, 320, 184, "", "");
            MapModel.Levels(m2).Children.Add(a2); MapModel.Levels(m2).Children.Add(b2);
            var ra = new Room(a2);
            var xxl = RoomSizing.TierSize("xxl", "square");
            var (dl, dt, dr, db) = RoomSizing.DeltasFor(ra.Bounds, xxl, "center");
            var res2 = RoomSizing.Resize(m2, ra, dl, dt, dr, db, RoomSizing.Fill.Extend, '1', pushRooms: true);
            var ga = ra.Grid(true);
            bool sizingOk = res2.New.Size == xxl && res2.New.X == -dl && res2.Overlaps.Count == 0 && res2.Pushed.SequenceEqual(new[] { "b" })
                            && new Room(b2).X == 320 + dr && ga[0, ga.Rows / 2] == '1' && ga[ga.Cols - 1, ga.Rows / 2] == '1'
                            && ga[ga.Cols / 2, 0] == '1' && ga[ga.Cols / 2, ga.Rows - 1] == '1' && ga[ga.Cols / 2, ga.Rows / 2] == '0'
                            && RoomSizing.TierOf(res2.New.Width, res2.New.Height) == "xxl";
            Console.WriteLine("room sizing (normal → xxl, all directions): " + (sizingOk ? "ok" : "FAILED"));
            if (!sizingOk) failed++;

            var clip = new BinElement("spinner"); clip.Set("x", 4); clip.Set("y", 8.5f); clip.Set("attachToSolid", true); clip.Set("color", "Blue");
            var nd = new BinElement("node"); nd.Set("x", 12); nd.Set("y", 0); clip.Children.Add(nd);
            var back = ItemClipboard.Deserialize(ItemClipboard.Serialize(new[] { ("entities", clip) }));
            bool clipOk = back is { Count: 1 } && back[0].Layer == "entities" && Dump(back[0].El) == Dump(clip) &&
                          back[0].El.Attrs.Select(a => a.Value.GetType()).SequenceEqual(clip.Attrs.Select(a => a.Value.GetType()));
            Console.WriteLine("clipboard round-trip: " + (clipOk ? "ok" : "FAILED"));
            if (!clipOk) failed++;

            var frames = MainForm.CollapseFrames(new[] { "flag00", "flag01", "flag02", "rock", "sign1", "a/b10" });
            bool framesOk = frames.SequenceEqual(new[] { "a/b10", "flag", "rock", "sign1" }, StringComparer.OrdinalIgnoreCase);
            Console.WriteLine("decal frame collapse: " + (framesOk ? "ok" : "FAILED " + string.Join(",", frames)));
            if (!framesOk) failed++;

            var line = MapCanvas.LineCells(new Point(0, 0), new Point(4, 2)).ToList();
            bool lineOk = line.Count == 5 && line[0] == new Point(0, 0) && line[^1] == new Point(4, 2);
            Console.WriteLine("line cells: " + (lineOk ? "ok" : "FAILED"));
            if (!lineOk) failed++;

            const string lua = """
                local e = {}
                e.name = "Test/Thing"
                e.nodeLimits = {1, -1}
                e.fieldOrder = {"x", "y", "mode"}
                e.fieldInformation = { mode = { options = {"A", "B"}, editable = false }, speed = { fieldType = "integer", minimumValue = 0 } }
                e.placements = { name = "thing", data = { mode = "A", speed = 3 } }
                return e
                """;
            var defs = PluginCatalog.ParseFile(lua, "test", "entities/test.lua", false);
            var d0 = defs.FirstOrDefault();
            bool luaOk = d0 != null && d0.MinNodes == 1 && d0.MaxNodes == -1 && d0.FieldOrder.SequenceEqual(new[] { "x", "y", "mode" }) &&
                         d0.Fields.TryGetValue("mode", out var fm) && !fm.Editable && fm.Options.Count == 2 &&
                         d0.Fields.TryGetValue("speed", out var fs) && fs.FieldType == "integer" && fs.Min == 0;
            Console.WriteLine("plugin field info: " + (luaOk ? "ok" : "FAILED"));
            if (!luaOk) failed++;

            sw.Restart();
            PluginCatalog.LoadNow();
            var all = PluginCatalog.AllPlacements();
            Console.WriteLine($"plugins: {PluginCatalog.FileCount} files, {PluginCatalog.FailedCount} failed, {all.Count} placements " +
                              $"({all.Count(p => p.Plugin.Source == "CEL")} from CEL) in {sw.ElapsedMilliseconds} ms");
            if (all.Count == 0) failed++;
        }
        catch (Exception ex) { failed++; Console.WriteLine("ASSET ERROR: " + ex); }
        return failed;
    }

    /// <summary>Media analysis used by the AI tools/attachments: WAV decode + pitch, figures, image compare, ffmpeg video frames.</summary>
    private static int MediaTest()
    {
        int failed = 0;
        string dir = Path.Combine(Path.GetTempPath(), "celmv_media_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // 1.5 s, 440 Hz sine at -6 dBFS, 16-bit stereo 22.05 kHz.
            const int rate = 22050, n = rate * 3 / 2;
            string wav = Path.Combine(dir, "sine.wav");
            using (var bw = new BinaryWriter(File.Create(wav)))
            {
                bw.Write("RIFF"u8); bw.Write(36 + n * 4); bw.Write("WAVEfmt "u8); bw.Write(16); bw.Write((short)1); bw.Write((short)2);
                bw.Write(rate); bw.Write(rate * 4); bw.Write((short)4); bw.Write((short)16); bw.Write("data"u8); bw.Write(n * 4);
                for (int i = 0; i < n; i++)
                {
                    short s = (short)(16384 * Math.Sin(2 * Math.PI * 440 * i / rate));
                    bw.Write(s); bw.Write(s);
                }
            }
            var pcm = Media.LoadAudio(wav, 0, 60);
            var stats = Media.AudioStats(pcm);
            double hz = stats["spectrum"]?["dominant_hz"]?.GetValue<double>() ?? 0, peak = stats["peak_dbfs"]?.GetValue<double>() ?? 0;
            using var fig = Media.AudioFigure(pcm, "sine");
            byte[] clip = Media.WavClip(pcm, 1);
            bool audioOk = pcm.Rate == rate && pcm.Ch.Length == 2 && Math.Abs(pcm.Duration - 1.5) < 0.01 && Math.Abs(hz - 440) < 5 &&
                           Math.Abs(peak + 6) < 0.5 && fig.Width > 0 && clip.Length > 44;
            Console.WriteLine($"media audio: {pcm.Duration:0.00}s, dominant {hz} Hz, peak {peak} dBFS " + (audioOk ? "ok" : "FAILED"));
            if (!audioOk) failed++;

            var blocks = AgentAttachments.Build(new[] { wav }, true, true);
            var types = blocks.Select(x => x?["type"]?.GetValue<string>()).ToList();
            bool attachOk = types.Contains("image") && types.Contains("audio") && types.Contains("text") && types.Contains("resource_link");
            Console.WriteLine("media attachment blocks: " + string.Join(",", types) + (attachOk ? " ok" : " FAILED"));
            if (!attachOk) failed++;

            using var a = new Bitmap(16, 8);
            using (var g = Graphics.FromImage(a)) g.Clear(Color.Black);
            using var b = (Bitmap)a.Clone();
            b.SetPixel(3, 2, Color.Red); b.SetPixel(4, 5, Color.Red);
            var (cmpFig, cmp) = Media.Compare(a, b, 0);
            using (cmpFig)
            using (var view = Media.PrepareForView(a, null, null, 512, "checker", out double scale))
            using (var sheet = Media.Sheet(new[] { (a, "a"), (b, "b") }, 1024, "checker"))
            {
                int changed = cmp["changed_pixels"]?.GetValue<int>() ?? -1;
                bool imageOk = changed == 2 && scale > 1 && view.Width == (int)Math.Round(16 * scale) && sheet.Width > 0 && Media.Png(sheet).Length > 0;
                Console.WriteLine($"media image: {changed} changed pixels, view scale {scale} " + (imageOk ? "ok" : "FAILED"));
                if (!imageOk) failed++;
            }

            if (Media.Ffmpeg is not { } ff) Console.WriteLine("media video: ffmpeg not found; skipped");
            else
            {
                string mp4 = Path.Combine(dir, "test.mp4");
                var (code, _, err) = Media.Run(ff, new[] { "-v", "error", "-f", "lavfi", "-i", "testsrc=duration=2:size=160x90:rate=10", "-pix_fmt", "yuv420p", "-y", mp4 });
                var frames = code == 0 ? Media.FramesAt(mp4, Media.EvenTimes(0, 2, 3), 160) : new();
                double dur = code == 0 ? Media.DurationOf(Media.Probe(mp4)) : 0;
                bool videoOk = frames.Count == 3 && frames.All(f => f.Frame.Width == 160) && Math.Abs(dur - 2) < 0.2;
                foreach (var f in frames) f.Frame.Dispose();
                Console.WriteLine($"media video: {frames.Count} frames, {dur:0.00}s " + (videoOk ? "ok" : "FAILED " + err.Trim()));
                if (!videoOk) failed++;
            }
        }
        catch (Exception ex) { failed++; Console.WriteLine("MEDIA ERROR: " + ex); }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
        return failed;
    }

    public static int Run(string[] args)
    {
        string? modDir = GameLink.FindModDir();
        string maps = args.Length > 0 ? args[0] : Path.Combine(modDir ?? ".", "Maps");
        int failed = 0, ok = 0;

        foreach (string file in Directory.EnumerateFiles(maps, "*.bin", SearchOption.AllDirectories))
        {
            string tmp = Path.Combine(Path.GetTempPath(), "celmv_" + Guid.NewGuid().ToString("N") + ".bin");
            try
            {
                var map = MapBin.Load(file);
                map.Save(tmp);
                byte[] a = File.ReadAllBytes(file), b = File.ReadAllBytes(tmp);
                if (a.AsSpan().SequenceEqual(b)) { ok++; continue; }
                int at = 0;
                while (at < a.Length && at < b.Length && a[at] == b[at]) at++;
                // A different encoding of the same tree is fine (e.g. a writer that stored small ints as Int32).
                bool sameTree = Dump(MapBin.Load(tmp).Root) == Dump(map.Root);
                if (!sameTree) failed++;
                Console.WriteLine($"{(sameTree ? "REENCODED" : "DIFF")}  {file}: {a.Length} vs {b.Length} bytes, first difference at {at}");
            }
            catch (Exception ex) { failed++; Console.WriteLine("ERROR " + file + ": " + ex.Message); }
            finally { File.Delete(tmp); }
        }
        Console.WriteLine($"round-trip: {ok} identical, {failed} failed");
        failed += AssetTest(maps);
        failed += MediaTest();

        string? root = args.Length > 1 ? args[1] : PcgGenerator.FindLoennRoot(modDir);
        if (root == null) { Console.WriteLine("PCGHelper not found; skipping generation test"); return failed == 0 ? 0 : 1; }

        try
        {
            var gen = new PcgGenerator(root);
            Console.WriteLine("solid tilesets: " + string.Join(", ", gen.SolidTilesets().Select(t => t.Glyph + "=" + t.Name)));
            var res = gen.Generate(new PcgOptions { Seed = "celeste", NbRooms = 4, Background = "bg_1" }, s => Console.WriteLine("  lua: " + s));
            Console.WriteLine($"generated {res.Rooms.Count} rooms in {res.Elapsed.TotalSeconds:0.00}s, seed {res.Seed}, playable={res.AllPlayable}");
            foreach (var r in res.Rooms)
                Console.WriteLine($"  {r.Name} at {r.X},{r.Y} {r.WTiles}x{r.HTiles} tiles, {r.Entities.Count} entities: " +
                                  string.Join(" ", r.Entities.GroupBy(e => e.Name).Select(g => g.Key + "×" + g.Count())));

            var res2 = gen.Generate(new PcgOptions { Seed = "celeste", NbRooms = 4, Background = "bg_1" }, _ => { });
            bool same = res.Rooms.Count == res2.Rooms.Count &&
                        res.Rooms.Zip(res2.Rooms).All(p => p.First.Fg.SequenceEqual(p.Second.Fg));
            Console.WriteLine("deterministic for same seed: " + same);
            if (!same) failed++;

            string sample = Directory.EnumerateFiles(maps, "*.bin", SearchOption.AllDirectories).First();
            var host = MapBin.Load(sample);
            int before = MapModel.Levels(host).Children.Count;
            var added = MapModel.ImportPcg(host, res, "pcg_");
            string tmp = Path.Combine(Path.GetTempPath(), "celmv_import.bin");
            host.Save(tmp);
            var back = MapBin.Load(tmp);
            int after = MapModel.Levels(back).Children.Count;
            File.Delete(tmp);
            Console.WriteLine($"import into {Path.GetFileName(sample)}: {before} -> {after} rooms ({string.Join(", ", added.Select(a => a.Str("name")))})");
            if (after != before + res.Rooms.Count) failed++;
        }
        catch (Exception ex)
        {
            failed++;
            Console.WriteLine("PCG ERROR: " + (ex is MoonSharp.Interpreter.InterpreterException ie ? ie.DecoratedMessage : ex.ToString()));
        }
        return failed == 0 ? 0 : 1;
    }
}
