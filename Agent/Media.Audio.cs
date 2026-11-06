using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace CELMapViewer.Agent;

// Audio: decoding, statistics, waveform + spectrogram figure and WAV clips for models that accept audio input.
public static partial class Media
{
    /// <summary>Sample rate used when ffmpeg decodes (spectrum up to 16 kHz is plenty for analysis).</summary>
    private const int DecodeRate = 32000;

    /// <summary>Decodes <paramref name="maxSeconds"/> of audio from <paramref name="start"/>. Works for audio files and video soundtracks.</summary>
    public static Pcm LoadAudio(string path, double start, double maxSeconds)
    {
        start = Math.Max(0, start);
        maxSeconds = Math.Clamp(maxSeconds, 0.05, 900);
        if (Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase) && ReadWav(path, start, maxSeconds) is { } wav)
            return wav;
        if (Path.GetExtension(path).Equals(".bank", StringComparison.OrdinalIgnoreCase))
            throw new ToolException("FMOD .bank files can't be decoded here. Export the event from FMOD Studio (or the loose .ogg/.wav source) and analyze that.");

        string exe = Ffmpeg ?? throw new ToolException(NoFfmpeg);
        var probe = Probe(path);
        var stream = FirstStream(probe, "audio") ?? throw new ToolException("The file has no audio stream.");
        int srcCh = Math.Max(1, (int)Num(stream["channels"]));
        int ch = Math.Min(srcCh, 2);
        var (code, outp, err) = Run(exe, new[]
        {
            "-v", "error", "-nostdin", "-ss", F(start), "-t", F(maxSeconds), "-i", path,
            "-vn", "-ac", ch.ToString(CultureInfo.InvariantCulture), "-ar", DecodeRate.ToString(CultureInfo.InvariantCulture), "-f", "f32le", "-"
        }, 180_000);
        if (code != 0) throw new ToolException("ffmpeg could not decode the audio: " + err.Trim());
        int frames = outp.Length / 4 / ch;
        var pcm = new Pcm
        {
            Rate = DecodeRate, Start = start, SourceDuration = DurationOf(probe), SourceRate = (int)Num(stream["sample_rate"]),
            SourceChannels = srcCh, Codec = stream["codec_name"]?.ToString() ?? "", Ch = new float[ch][]
        };
        for (int c = 0; c < ch; c++) pcm.Ch[c] = new float[frames];
        for (int i = 0; i < frames; i++)
            for (int c = 0; c < ch; c++)
                pcm.Ch[c][i] = BitConverter.ToSingle(outp, (i * ch + c) * 4);
        return pcm;
    }

    private static string Tag(BinaryReader br) => Encoding.ASCII.GetString(br.ReadBytes(4));

    /// <summary>PCM / IEEE-float WAV (incl. WAVE_FORMAT_EXTENSIBLE). Null for compressed WAVs, which ffmpeg handles.</summary>
    internal static Pcm? ReadWav(string path, double start, double maxSeconds)
    {
        using var br = new BinaryReader(File.OpenRead(path));
        long total = br.BaseStream.Length;
        if (total < 12 || Tag(br) != "RIFF") return null;
        br.ReadUInt32();
        if (Tag(br) != "WAVE") return null;
        int fmt = 0, ch = 0, rate = 0, bits = 0;
        long dataPos = -1, dataLen = 0;
        while (br.BaseStream.Position + 8 <= total)
        {
            string id = Tag(br);
            long len = br.ReadUInt32();
            long pos = br.BaseStream.Position;
            if (id == "fmt ")
            {
                fmt = br.ReadUInt16();
                ch = br.ReadUInt16();
                rate = br.ReadInt32();
                br.ReadInt32();
                br.ReadUInt16();
                bits = br.ReadUInt16();
                if (fmt == 0xFFFE && len >= 40)
                {
                    br.ReadUInt16();
                    br.ReadUInt16();
                    br.ReadUInt32();
                    fmt = br.ReadUInt16();
                }
            }
            else if (id == "data")
            {
                dataPos = pos;
                dataLen = Math.Min(len, total - pos);
                break;
            }
            br.BaseStream.Position = pos + len + (len & 1);
        }
        if (dataPos < 0 || ch <= 0 || rate <= 0) return null;
        if (!((fmt == 1 && bits is 8 or 16 or 24 or 32) || (fmt == 3 && bits is 32 or 64))) return null;
        int bps = bits / 8, frameBytes = bps * ch;
        long frames = dataLen / frameBytes;
        long first = Math.Min(frames, (long)(start * rate));
        long count = Math.Min(frames - first, (long)(maxSeconds * rate));
        br.BaseStream.Position = dataPos + first * frameBytes;
        byte[] raw = br.ReadBytes((int)(count * frameBytes));
        count = raw.Length / frameBytes;
        int outCh = Math.Min(ch, 2);
        var pcm = new Pcm
        {
            Rate = rate, Start = (double)first / rate, SourceDuration = (double)frames / rate, SourceRate = rate, SourceChannels = ch,
            Codec = fmt == 3 ? $"pcm_f{bits}le" : $"pcm_s{bits}le", Ch = new float[outCh][]
        };
        for (int c = 0; c < outCh; c++) pcm.Ch[c] = new float[count];
        for (long i = 0; i < count; i++)
            for (int c = 0; c < outCh; c++)
            {
                int o = (int)(i * frameBytes + c * bps);
                pcm.Ch[c][i] = (fmt, bits) switch
                {
                    (3, 32) => BitConverter.ToSingle(raw, o),
                    (3, _) => (float)BitConverter.ToDouble(raw, o),
                    (_, 8) => (raw[o] - 128) / 128f,
                    (_, 16) => BitConverter.ToInt16(raw, o) / 32768f,
                    (_, 24) => ((raw[o] | (raw[o + 1] << 8) | ((sbyte)raw[o + 2] << 16))) / 8388608f,
                    _ => BitConverter.ToInt32(raw, o) / 2147483648f
                };
            }
        return pcm;
    }

    private static float[] Mono(Pcm pcm)
    {
        if (pcm.Ch.Length == 1) return pcm.Ch[0];
        var m = new float[pcm.Length];
        for (int i = 0; i < m.Length; i++) m[i] = (pcm.Ch[0][i] + pcm.Ch[1][i]) * 0.5f;
        return m;
    }

    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    double xr = re[b] * cr - im[b] * ci, xi = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - xr; im[b] = im[a] - xi;
                    re[a] += xr; im[a] += xi;
                    double t = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = t;
                }
            }
        }
    }

    /// <summary>Power spectra of <paramref name="columns"/> Hann-windowed frames spread evenly over the signal.</summary>
    private static double[][] Spectra(float[] x, int n, int columns)
    {
        var hann = new double[n];
        for (int i = 0; i < n; i++) hann[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1));
        var result = new double[columns][];
        Parallel.For(0, columns, c =>
        {
            var re = new double[n];
            var im = new double[n];
            long center = (long)((c + 0.5) / columns * x.Length);
            long s0 = center - n / 2;
            for (int i = 0; i < n; i++)
            {
                long k = s0 + i;
                re[i] = k >= 0 && k < x.Length ? x[k] * hann[i] : 0;
            }
            Fft(re, im);
            var p = new double[n / 2];
            for (int i = 0; i < n / 2; i++) p[i] = re[i] * re[i] + im[i] * im[i];
            result[c] = p;
        });
        return result;
    }

    private static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    private static string Note(double hz)
    {
        if (hz <= 0) return "";
        double m = 69 + 12 * Math.Log2(hz / 440);
        int mi = (int)Math.Round(m);
        int cents = (int)Math.Round((m - mi) * 100);
        return $"{NoteNames[((mi % 12) + 12) % 12]}{mi / 12 - 1}{(cents == 0 ? "" : cents > 0 ? $" +{cents}c" : $" {cents}c")}";
    }

    /// <summary>Loudness, silence, clipping, spectrum, stereo, tempo and loop-seam statistics.</summary>
    public static JsonObject AudioStats(Pcm pcm)
    {
        int len = pcm.Length;
        var o = new JsonObject
        {
            ["codec"] = pcm.Codec, ["source_sample_rate"] = pcm.SourceRate, ["source_channels"] = pcm.SourceChannels,
            ["source_duration_s"] = R(pcm.SourceDuration, 3),
            ["analyzed"] = new JsonObject { ["start_s"] = R(pcm.Start, 3), ["seconds"] = R(pcm.Duration, 3), ["sample_rate"] = pcm.Rate, ["channels"] = pcm.Ch.Length }
        };
        if (len == 0) { o["note"] = "no samples in the analyzed range"; return o; }

        double peak = 0, sumSq = 0, dc = 0;
        long clipped = 0;
        foreach (var c in pcm.Ch)
            foreach (float v in c)
            {
                double a = Math.Abs(v);
                if (a > peak) peak = a;
                if (a >= 0.999) clipped++;
                sumSq += v * (double)v;
                dc += v;
            }
        long n = (long)len * pcm.Ch.Length;
        double rms = Math.Sqrt(sumSq / n);
        o["peak_dbfs"] = R(Db(peak), 1);
        o["rms_dbfs"] = R(Db(rms), 1);
        o["crest_factor_db"] = R(Db(peak) - Db(rms), 1);
        o["clipped_samples"] = clipped;
        o["dc_offset"] = R(dc / n, 4);

        // 10 ms envelope on the mono mix
        var mono = Mono(pcm);
        int win = Math.Max(1, pcm.Rate / 100);
        int nw = Math.Max(1, len / win);
        var env = new double[nw];
        for (int w = 0; w < nw; w++)
        {
            double s = 0;
            int a = w * win, b = Math.Min(len, a + win);
            for (int i = a; i < b; i++) s += mono[i] * (double)mono[i];
            env[w] = Math.Sqrt(s / Math.Max(1, b - a));
        }
        const double silentDb = -50;
        int firstLoud = Array.FindIndex(env, e => Db(e) > silentDb), lastLoud = Array.FindLastIndex(env, e => Db(e) > silentDb);
        if (firstLoud < 0) o["note"] = "the analyzed range is silent (below -50 dBFS)";
        else
        {
            o["leading_silence_s"] = R(firstLoud * 0.01, 2);
            o["trailing_silence_s"] = R((nw - 1 - lastLoud) * 0.01, 2);
            var gaps = new JsonArray();
            for (int w = firstLoud; w <= lastLoud && gaps.Count < 12; w++)
            {
                if (Db(env[w]) > silentDb) continue;
                int e = w;
                while (e <= lastLoud && Db(env[e]) <= silentDb) e++;
                if (e - w >= 50) gaps.Add(new JsonObject { ["start_s"] = R(pcm.Start + w * 0.01, 2), ["end_s"] = R(pcm.Start + e * 0.01, 2) });
                w = e;
            }
            if (gaps.Count > 0) o["silent_gaps"] = gaps;
        }

        int segs = Math.Clamp((int)Math.Ceiling(pcm.Duration), 1, 32);
        var timeline = new JsonArray();
        for (int sgi = 0; sgi < segs; sgi++)
        {
            int a = (int)((long)nw * sgi / segs), b = Math.Max(a + 1, (int)((long)nw * (sgi + 1) / segs));
            double s = 0;
            for (int w = a; w < Math.Min(b, nw); w++) s += env[w] * env[w];
            timeline.Add(R(Db(Math.Sqrt(s / Math.Max(1, b - a))), 0));
        }
        o["loudness_timeline"] = new JsonObject { ["segment_s"] = R(pcm.Duration / segs, 2), ["rms_db"] = timeline };

        // average spectrum
        int fftN = pcm.Rate >= 22050 ? 4096 : 2048;
        var spectra = Spectra(mono, fftN, Math.Clamp(len / (fftN / 2), 1, 400));
        var avg = new double[fftN / 2];
        foreach (var sp in spectra) for (int i = 0; i < avg.Length; i++) avg[i] += sp[i];
        double binHz = (double)pcm.Rate / fftN, tot = 0, cen = 0, low = 0, mid = 0, high = 0;
        for (int i = 1; i < avg.Length; i++)
        {
            double f = i * binHz;
            tot += avg[i];
            cen += f * avg[i];
            if (f < 250) low += avg[i]; else if (f < 4000) mid += avg[i]; else high += avg[i];
        }
        if (tot > 0)
        {
            int lo = (int)Math.Ceiling(40 / binHz), hi = Math.Min(avg.Length - 2, (int)(5000 / binHz));
            int pk = lo;
            for (int i = lo; i <= hi; i++) if (avg[i] > avg[pk]) pk = i;
            double y0 = Math.Log(avg[pk - 1] + 1e-20), y1 = Math.Log(avg[pk] + 1e-20), y2 = Math.Log(avg[pk + 1] + 1e-20);
            double off = (y0 - 2 * y1 + y2) == 0 ? 0 : 0.5 * (y0 - y2) / (y0 - 2 * y1 + y2);
            double dom = (pk + off) * binHz;
            o["spectrum"] = new JsonObject
            {
                ["centroid_hz"] = R(cen / tot, 0),
                ["energy_low_lt250hz_pct"] = R(100 * low / tot, 1), ["energy_mid_250_4khz_pct"] = R(100 * mid / tot, 1),
                ["energy_high_gt4khz_pct"] = R(100 * high / tot, 1),
                ["dominant_hz"] = R(dom, 1), ["dominant_note"] = Note(dom)
            };
        }

        if (pcm.Ch.Length == 2)
        {
            double sl = 0, sr = 0, slr = 0;
            for (int i = 0; i < len; i++)
            {
                double l = pcm.Ch[0][i], r = pcm.Ch[1][i];
                sl += l * l; sr += r * r; slr += l * r;
            }
            o["stereo"] = new JsonObject
            {
                ["correlation"] = sl > 0 && sr > 0 ? R(slr / Math.Sqrt(sl * sr), 3) : 1,
                ["balance_db_right_minus_left"] = R(Db(Math.Sqrt(sr / len)) - Db(Math.Sqrt(sl / len)), 1),
                ["note"] = "correlation 1 = mono, ~0 = wide/uncorrelated, <0 = phase problems"
            };
        }

        if (pcm.Duration >= 4 && firstLoud >= 0)
        {
            var onset = new double[nw];
            for (int w = 1; w < nw; w++) onset[w] = Math.Max(0, Math.Log(env[w] + 1e-5) - Math.Log(env[w - 1] + 1e-5));
            double mean = onset.Average();
            for (int w = 0; w < nw; w++) onset[w] -= mean;
            double ac0 = onset.Sum(v => v * v);
            var ac = new double[102];
            for (int lag = 29; lag <= 101; lag++)
            {
                double s = 0;
                for (int w = lag; w < nw; w++) s += onset[w] * onset[w - lag];
                ac[lag] = s;
            }
            int best = 30;
            for (int lag = 30; lag <= 100; lag++) if (ac[lag] > ac[best]) best = lag;
            double d0 = ac[best - 1], d1 = ac[best], d2 = ac[best + 1];
            double shift = (d0 - 2 * d1 + d2) == 0 ? 0 : 0.5 * (d0 - d2) / (d0 - 2 * d1 + d2);
            double conf = ac0 > 0 ? ac[best] / ac0 : 0;
            o["tempo"] = conf < 0.08
                ? new JsonObject { ["note"] = "no clear steady beat" }
                : new JsonObject { ["bpm_estimate"] = R(6000 / (best + shift), 1), ["confidence"] = R(conf, 2), ["note"] = "may be half/double the felt tempo" };
        }

        int edge = Math.Min(len / 2, pcm.Rate / 20);
        if (edge > 0)
        {
            double jump = pcm.Ch.Max(c => Math.Abs(c[len - 1] - c[0]));
            double head = 0, tail = 0;
            for (int i = 0; i < edge; i++) { head += mono[i] * (double)mono[i]; tail += mono[len - 1 - i] * (double)mono[len - 1 - i]; }
            o["loop_seam"] = new JsonObject
            {
                ["sample_jump"] = R(jump, 4), ["start_50ms_rms_db"] = R(Db(Math.Sqrt(head / edge)), 1), ["end_50ms_rms_db"] = R(Db(Math.Sqrt(tail / edge)), 1),
                ["note"] = "if this clip loops end→start: a jump above ~0.05 or very different levels are likely to click"
            };
        }
        return o;
    }

    private static readonly Color[] Inferno =
    {
        Color.FromArgb(0, 0, 4), Color.FromArgb(40, 11, 84), Color.FromArgb(101, 21, 110), Color.FromArgb(159, 42, 99),
        Color.FromArgb(212, 72, 66), Color.FromArgb(245, 125, 21), Color.FromArgb(250, 193, 39), Color.FromArgb(252, 255, 164)
    };

    private static int Colormap(double t)
    {
        t = Math.Clamp(t, 0, 1) * (Inferno.Length - 1);
        int i = Math.Min((int)t, Inferno.Length - 2);
        double f = t - i;
        Color a = Inferno[i], b = Inferno[i + 1];
        return unchecked((int)0xFF000000) | ((int)(a.R + (b.R - a.R) * f) << 16) | ((int)(a.G + (b.G - a.G) * f) << 8) | (int)(a.B + (b.B - a.B) * f);
    }

    private static double NiceStep(double span, int maxTicks)
    {
        foreach (double s in new[] { 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 })
            if (span / s <= maxTicks) return s;
        return 1200;
    }

    private static string TimeLabel(double t, double step) =>
        step < 1 ? t.ToString(step < 0.1 ? "0.00" : "0.0", CultureInfo.InvariantCulture) + "s"
                 : t >= 60 ? $"{(int)t / 60}:{(int)t % 60:00}" : $"{t:0}s";

    /// <summary>Waveform lanes (one per channel, clipping in red) over a log-frequency spectrogram, with time/frequency axes.</summary>
    public static Bitmap AudioFigure(Pcm pcm, string title, int width = 1200)
    {
        const int left = 54, right = 10, top = 22, laneH = 80, specH = 240, axisH = 18;
        int plotW = Math.Max(100, width - left - right);
        int lanes = Math.Max(1, pcm.Ch.Length);
        int specTop = top + lanes * laneH + 8;
        int height = specTop + specH + axisH;
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(22, 22, 28));
        using var font = new Font("Segoe UI", 7.5f);
        using var titleFont = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        using var grid = new Pen(Color.FromArgb(60, 60, 72));
        using var wave = new Pen(Color.FromArgb(110, 200, 255));
        using var clipPen = new Pen(Color.FromArgb(255, 70, 70));
        g.DrawString(title, titleFont, Brushes.White, 4, 3);
        int len = pcm.Length;

        for (int c = 0; c < lanes; c++)
        {
            int y0 = top + c * laneH, mid = y0 + laneH / 2;
            g.FillRectangle(new SolidBrush(Color.FromArgb(30, 30, 38)), left, y0, plotW, laneH - 4);
            g.DrawLine(grid, left, mid, left + plotW, mid);
            g.DrawString(lanes == 1 ? "mono" : c == 0 ? "L" : "R", font, Brushes.Silver, 4, mid - 7);
            if (len == 0 || c >= pcm.Ch.Length) continue;
            var x = pcm.Ch[c];
            float half = (laneH - 6) / 2f;
            for (int px = 0; px < plotW; px++)
            {
                int a = (int)((long)len * px / plotW), b = Math.Max(a + 1, (int)((long)len * (px + 1) / plotW));
                float mn = 1, mx = -1;
                for (int i = a; i < Math.Min(b, len); i++) { if (x[i] < mn) mn = x[i]; if (x[i] > mx) mx = x[i]; }
                if (mn > mx) continue;
                bool clip = mx >= 0.999f || mn <= -0.999f;
                g.DrawLine(clip ? clipPen : wave, left + px, mid - mx * half, left + px, mid - mn * half + 0.5f);
            }
        }

        if (len > 0)
        {
            var mono = Mono(pcm);
            int n = pcm.Rate >= 22050 ? 2048 : 1024;
            var spec = Spectra(mono, n, plotW);
            double fmin = 30, fmax = pcm.Rate / 2.0, binHz = (double)pcm.Rate / n;
            double maxDb = -200;
            var db = new double[plotW][];
            for (int c = 0; c < plotW; c++)
            {
                db[c] = new double[spec[c].Length];
                for (int i = 0; i < spec[c].Length; i++)
                {
                    db[c][i] = 10 * Math.Log10(spec[c][i] + 1e-14);
                    if (db[c][i] > maxDb) maxDb = db[c][i];
                }
            }
            var px = new int[plotW * specH];
            for (int y = 0; y < specH; y++)
            {
                double f = fmin * Math.Pow(fmax / fmin, 1 - (double)y / (specH - 1));
                double bin = f / binHz;
                int b0 = Math.Clamp((int)bin, 0, n / 2 - 2);
                double fr = Math.Clamp(bin - b0, 0, 1);
                for (int c = 0; c < plotW; c++)
                {
                    double v = db[c][b0] * (1 - fr) + db[c][b0 + 1] * fr;
                    px[y * plotW + c] = Colormap((v - (maxDb - 90)) / 90);
                }
            }
            using var specBmp = new Bitmap(plotW, specH, PixelFormat.Format32bppArgb);
            var data = specBmp.LockBits(new Rectangle(0, 0, plotW, specH), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < specH; y++) System.Runtime.InteropServices.Marshal.Copy(px, y * plotW, data.Scan0 + y * data.Stride, plotW);
            specBmp.UnlockBits(data);
            g.DrawImageUnscaled(specBmp, left, specTop);
            foreach (double f in new double[] { 50, 100, 200, 500, 1000, 2000, 5000, 10000, 16000 })
            {
                if (f >= fmax) continue;
                int y = specTop + (int)Math.Round((1 - Math.Log(f / fmin) / Math.Log(fmax / fmin)) * (specH - 1));
                g.DrawLine(grid, left - 4, y, left, y);
                g.DrawString(f >= 1000 ? $"{f / 1000:0.#}k" : $"{f:0}", font, Brushes.Silver, 4, y - 7);
            }
            g.DrawString("Hz", font, Brushes.Gray, 4, specTop + specH - 12);
        }

        double dur = Math.Max(pcm.Duration, 1e-3);
        double step = NiceStep(dur, 14);
        double first = Math.Ceiling(pcm.Start / step) * step;
        for (double t = first; t <= pcm.Start + dur + 1e-9; t += step)
        {
            int xpx = left + (int)Math.Round((t - pcm.Start) / dur * plotW);
            g.DrawLine(grid, xpx, top, xpx, specTop + specH + 3);
            g.DrawString(TimeLabel(t, step), font, Brushes.Silver, xpx - 10, specTop + specH + 3);
        }
        return bmp;
    }

    /// <summary>A 16-bit mono WAV of the first <paramref name="seconds"/> of <paramref name="pcm"/> at ≤ 24 kHz, sized for model input.</summary>
    public static byte[] WavClip(Pcm pcm, double seconds)
    {
        var mono = Mono(pcm);
        int outRate = Math.Min(pcm.Rate, 24000);
        int count = (int)Math.Min((long)(Math.Min(seconds, pcm.Duration) * outRate), (long)mono.Length * outRate / Math.Max(1, pcm.Rate));
        double peak = 0;
        foreach (float v in mono) peak = Math.Max(peak, Math.Abs(v));
        double gain = peak > 1 ? 1 / peak : 1;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + count * 2);
        bw.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)1);
        bw.Write(outRate);
        bw.Write(outRate * 2);
        bw.Write((short)2);
        bw.Write((short)16);
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(count * 2);
        double ratio = (double)pcm.Rate / outRate;
        for (int i = 0; i < count; i++)
        {
            double pos = i * ratio;
            int k = (int)pos;
            double f = pos - k;
            double v = k + 1 < mono.Length ? mono[k] * (1 - f) + mono[k + 1] * f : k < mono.Length ? mono[k] : 0;
            bw.Write((short)Math.Clamp(Math.Round(v * gain * 32767), -32768, 32767));
        }
        bw.Flush();
        return ms.ToArray();
    }
}
