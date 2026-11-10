using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CELMapViewer;

/// <summary>
/// Paints WinForms controls with <see cref="Themes.Current"/>. <see cref="Install"/> themes every form of the UI thread
/// as it is first activated (dialogs included), follows controls added later, and re-themes open forms on a theme change.
/// </summary>
public static class ThemeApplier
{
    private sealed class Info { public bool Muted; public bool Hooked; public bool CheckHooked; }
    private static readonly ConditionalWeakTable<Control, Info> Seen = new();
    private static readonly ConditionalWeakTable<TabControl, TabPainter> TabPainters = new();
    private static readonly ConditionalWeakTable<Form, object> ThemedForms = new();

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    private static HookProc? _hookProc;   // kept alive for the hook's lifetime
    private static IntPtr _hook;

    public static void Install()
    {
        ToolStripManager.Renderer = new ThemedRenderer();
        _hookProc = CbtProc;
        _hook = SetWindowsHookEx(WH_CBT, _hookProc, IntPtr.Zero, GetCurrentThreadId());
        Themes.Changed += (_, _) =>
        {
            foreach (Form f in Application.OpenForms) Apply(f);
        };
    }

    private static IntPtr CbtProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code == HCBT_ACTIVATE && Control.FromHandle(wParam) is Form f && !ThemedForms.TryGetValue(f, out _))
        {
            ThemedForms.Add(f, new object());
            try { Apply(f); } catch { /* never break activation */ }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    /// <summary>Themes <paramref name="root"/> and everything inside it.</summary>
    public static void Apply(Control root)
    {
        if (root is Form f) ThemedForms.AddOrUpdate(f, new object());
        root.SuspendLayout();
        try { ApplyTree(root, Themes.Current); }
        finally { root.ResumeLayout(); }
        root.Invalidate(true);
    }

    private static void ApplyTree(Control c, Theme t)
    {
        ApplyOne(c, t);
        foreach (Control child in c.Controls) ApplyTree(child, t);
    }

    private static void ApplyOne(Control c, Theme t)
    {
        if (!Seen.TryGetValue(c, out var info))
        {
            var fc = c.ForeColor;
            info = new Info { Muted = fc == Color.DimGray || fc == Color.Gray || fc == SystemColors.GrayText };
            Seen.Add(c, info);
        }
        if (!info.Hooked)
        {
            info.Hooked = true;
            c.ControlAdded += (_, e) => { if (e.Control != null) ApplyTree(e.Control, Themes.Current); };
        }

        Color fore = info.Muted ? t.Muted : t.Text;
        switch (c)
        {
            case Form form:
                form.BackColor = t.Back; form.ForeColor = t.Text;
                WhenHandle(form, () => TitleBar(form, t));
                break;
            case MapCanvas canvas:
                canvas.BackColor = t.Canvas;
                break;
            case PictureBox pb:
                pb.BackColor = t.Canvas;
                break;
            case DataGridView g:
                StyleGrid(g, t);
                break;
            case TextBoxBase or ListBox or TreeView or ListView:
                c.BackColor = t.Surface; c.ForeColor = fore;
                if (c is TreeView tv) tv.LineColor = t.Border;
                if (c is TextBox tb && tb.BorderStyle == BorderStyle.Fixed3D) tb.BorderStyle = BorderStyle.FixedSingle;
                if (c is ListBox lb && lb.BorderStyle == BorderStyle.Fixed3D) lb.BorderStyle = BorderStyle.FixedSingle;
                if (c is TreeView tv2 && tv2.BorderStyle == BorderStyle.Fixed3D) tv2.BorderStyle = BorderStyle.FixedSingle;
                WhenHandle(c, () => ScrollTheme(c, t));
                break;
            case ComboBox cb:
                cb.FlatStyle = FlatStyle.Flat; cb.BackColor = t.Surface; cb.ForeColor = t.Text;
                WhenHandle(c, () => SetWindowTheme(c.Handle, t.Dark ? "DarkMode_CFD" : "CFD", null));
                break;
            case NumericUpDown or DomainUpDown:
                c.BackColor = t.Surface; c.ForeColor = t.Text;
                break;
            case ButtonBase b when b is Button || (b is CheckBox { Appearance: Appearance.Button }) || (b is RadioButton { Appearance: Appearance.Button }):
                b.FlatStyle = FlatStyle.Flat;
                b.BackColor = t.Raised; b.ForeColor = t.Text;
                b.FlatAppearance.BorderColor = t.Border;
                b.FlatAppearance.MouseOverBackColor = Themes.Blend(t.Raised, t.Accent, 0.22f);
                b.FlatAppearance.MouseDownBackColor = Themes.Blend(t.Raised, t.Accent, 0.4f);
                b.FlatAppearance.CheckedBackColor = t.Selection;
                if (b is CheckBox { Checked: true } or RadioButton { Checked: true }) b.ForeColor = t.SelectionText;
                if (b is CheckBox chk && !info.CheckHooked) { info.CheckHooked = true; chk.CheckedChanged += (_, _) => ApplyOne(chk, Themes.Current); }
                break;
            case CheckBox or RadioButton:
                ((ButtonBase)c).FlatStyle = FlatStyle.Flat;
                c.ForeColor = fore;
                c.BackColor = c.Parent?.BackColor ?? t.Back;
                break;
            case LinkLabel ll:
                ll.ForeColor = t.Muted; ll.LinkColor = t.Accent2; ll.ActiveLinkColor = t.Accent; ll.VisitedLinkColor = t.Accent2;
                ll.BackColor = ll.Parent?.BackColor ?? t.Back;
                break;
            case Label:
                c.ForeColor = fore;
                c.BackColor = c.Parent?.BackColor ?? t.Back;
                break;
            case ToolStrip ts:
                ts.RenderMode = ToolStripRenderMode.ManagerRenderMode;
                ts.BackColor = t.Raised; ts.ForeColor = t.Text;
                foreach (ToolStripItem item in ts.Items) StyleItem(item, t);
                ts.Invalidate();
                break;
            case TabControl tc:
                tc.BackColor = t.Back; tc.ForeColor = t.Text;
                if (!TabPainters.TryGetValue(tc, out _)) TabPainters.Add(tc, new TabPainter(tc));
                break;
            case SplitContainer sc:
                sc.BackColor = t.Border; sc.ForeColor = t.Text;
                sc.Panel1.BackColor = t.Back; sc.Panel2.BackColor = t.Back;
                break;
            case TabPage tp:
                tp.UseVisualStyleBackColor = false; tp.BackColor = t.Back; tp.ForeColor = t.Text;
                break;
            case GroupBox:
                c.BackColor = c.Parent?.BackColor ?? t.Back; c.ForeColor = t.Text;
                break;
            case ScrollBar:
                WhenHandle(c, () => ScrollTheme(c, t));
                break;
            default:
                c.BackColor = t.Back; c.ForeColor = fore;
                break;
        }
    }

    private static void StyleItem(ToolStripItem item, Theme t)
    {
        item.ForeColor = t.Text;
        if (item is ToolStripControlHost host) ApplyTree(host.Control, t);
        if (item is ToolStripDropDownItem dd)
        {
            dd.DropDown.BackColor = t.Raised; dd.DropDown.ForeColor = t.Text;
            foreach (ToolStripItem sub in dd.DropDownItems) StyleItem(sub, t);
        }
    }

    private static void StyleGrid(DataGridView g, Theme t)
    {
        g.EnableHeadersVisualStyles = false;
        g.BackgroundColor = t.Surface;
        g.GridColor = t.Border;
        g.BorderStyle = BorderStyle.FixedSingle;
        g.DefaultCellStyle.BackColor = t.Surface;
        g.DefaultCellStyle.ForeColor = t.Text;
        g.DefaultCellStyle.SelectionBackColor = t.Selection;
        g.DefaultCellStyle.SelectionForeColor = t.SelectionText;
        g.AlternatingRowsDefaultCellStyle.BackColor = Themes.Blend(t.Surface, t.Raised, 0.5f);
        g.ColumnHeadersDefaultCellStyle.BackColor = t.Raised;
        g.ColumnHeadersDefaultCellStyle.ForeColor = t.Text;
        g.ColumnHeadersDefaultCellStyle.SelectionBackColor = t.Raised;
        g.RowHeadersDefaultCellStyle.BackColor = t.Raised;
        g.RowHeadersDefaultCellStyle.ForeColor = t.Text;
        g.RowHeadersDefaultCellStyle.SelectionBackColor = t.Selection;
        WhenHandle(g, () => ScrollTheme(g, t));
    }

    private static void WhenHandle(Control c, Action a)
    {
        if (c.IsHandleCreated) { a(); return; }
        void Once(object? s, EventArgs e) { c.HandleCreated -= Once; a(); }
        c.HandleCreated += Once;
    }

    private static void ScrollTheme(Control c, Theme t)
    {
        SetWindowTheme(c.Handle, t.Dark ? "DarkMode_Explorer" : "Explorer", null);
        foreach (Control child in c.Controls)
            if (child is ScrollBar && child.IsHandleCreated) SetWindowTheme(child.Handle, t.Dark ? "DarkMode_Explorer" : "Explorer", null);
    }

    private static void TitleBar(Form f, Theme t)
    {
        int dark = t.Dark ? 1 : 0;
        DwmSetWindowAttribute(f.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        int caption = ColorTranslator.ToWin32(t.Raised), text = ColorTranslator.ToWin32(t.Text), border = ColorTranslator.ToWin32(t.Border);
        DwmSetWindowAttribute(f.Handle, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));  // Windows 11 only; ignored elsewhere
        DwmSetWindowAttribute(f.Handle, DWMWA_TEXT_COLOR, ref text, sizeof(int));
        DwmSetWindowAttribute(f.Handle, DWMWA_BORDER_COLOR, ref border, sizeof(int));
    }

    /// <summary>Swaps colors in a rich text box's existing text (e.g. chat history after a theme change).</summary>
    public static void RecolorRtf(RichTextBox box, IEnumerable<(Color From, Color To)> map)
    {
        string rtf = box.Rtf ?? "";
        int start = rtf.IndexOf(@"{\colortbl", StringComparison.Ordinal);
        if (start < 0) return;
        int end = rtf.IndexOf('}', start);
        if (end < 0) return;
        string table = rtf[start..end];
        string Entry(Color c) => $@"\red{c.R}\green{c.G}\blue{c.B};";
        // Two passes through placeholders so A→B, B→C does not chain.
        var pairs = map.Where(p => p.From.ToArgb() != p.To.ToArgb()).DistinctBy(p => p.From.ToArgb()).ToList();
        for (int i = 0; i < pairs.Count; i++) table = table.Replace(Entry(pairs[i].From), $"\u0001{i}\u0001");
        for (int i = 0; i < pairs.Count; i++) table = table.Replace($"\u0001{i}\u0001", Entry(pairs[i].To));
        bool ro = box.ReadOnly;
        box.Rtf = rtf[..start] + table + rtf[end..];
        box.ReadOnly = ro;
        box.SelectionStart = box.TextLength;
        box.ScrollToCaret();
    }

    // ---- tab headers: native tabs ignore BackColor, so they are repainted after the control paints ----

    private sealed class TabPainter : NativeWindow
    {
        private readonly TabControl _tc;
        public TabPainter(TabControl tc)
        {
            _tc = tc;
            if (tc.IsHandleCreated) AssignHandle(tc.Handle);
            tc.HandleCreated += (_, _) => AssignHandle(tc.Handle);
            tc.HandleDestroyed += (_, _) => ReleaseHandle();
            tc.SelectedIndexChanged += (_, _) => tc.Invalidate();
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x000F /* WM_PAINT */ && _tc.TabCount > 0) Paint();
        }

        private void Paint()
        {
            var t = Themes.Current;
            using var g = Graphics.FromHwnd(_tc.Handle);
            var client = _tc.ClientRectangle;
            var display = _tc.DisplayRectangle;
            // Everything that is not the page itself: header strip and page frame.
            using (var region = new Region(client))
            {
                region.Exclude(display);
                using var back = new SolidBrush(t.Back);
                g.FillRegion(back, region);
            }
            using (var border = new Pen(t.Border))
                g.DrawRectangle(border, display.X - 1, display.Y - 1, display.Width + 1, display.Height + 1);

            var mouse = _tc.PointToClient(Cursor.Position);
            for (int i = 0; i < _tc.TabCount; i++)
            {
                var r = _tc.GetTabRect(i);
                bool sel = i == _tc.SelectedIndex;
                bool hot = !sel && r.Contains(mouse);
                if (!sel) r = new Rectangle(r.X, r.Y + 2, r.Width, r.Height - 2);
                var fill = sel ? t.Surface : hot ? Themes.Blend(t.Raised, t.Accent, 0.18f) : t.Raised;
                using (var b = new SolidBrush(fill)) g.FillRectangle(b, r);
                using (var p = new Pen(t.Border)) g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
                if (sel)
                    using (var a = new SolidBrush(t.Accent)) g.FillRectangle(a, r.X, r.Y, r.Width, 2);
                TextRenderer.DrawText(g, _tc.TabPages[i].Text, _tc.Font, r, sel ? t.Text : t.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            }
        }
    }

    // ---- menus, toolbars, status bar ----

    private sealed class ThemedRenderer : ToolStripProfessionalRenderer
    {
        public ThemedRenderer() : base(new ThemedColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var t = Themes.Current;
            bool onDropDown = e.Item.Owner is ToolStripDropDown;
            bool hi = e.Item.Selected && e.Item.Enabled && (onDropDown || e.Item is ToolStripMenuItem);
            e.TextColor = !e.Item.Enabled ? Themes.Blend(t.Muted, t.Raised, 0.35f) : hi ? t.SelectionText : t.Text;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item is { Selected: true } ? Themes.Current.SelectionText : Themes.Current.Text;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var t = Themes.Current;
            var r = e.ImageRectangle;
            if (r.Width <= 0) return;
            r.Inflate(1, 1);
            var g = e.Graphics;
            using (var b = new SolidBrush(t.Selection)) g.FillRectangle(b, r);
            using (var p = new Pen(t.Accent)) g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
            var old = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var p = new Pen(t.SelectionText, 2f))
            {
                float x = r.X + r.Width * 0.25f, y = r.Y + r.Height * 0.52f;
                g.DrawLines(p, new[] { new PointF(x, y), new PointF(r.X + r.Width * 0.43f, r.Y + r.Height * 0.7f), new PointF(r.X + r.Width * 0.76f, r.Y + r.Height * 0.3f) });
            }
            g.SmoothingMode = old;
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (e.ToolStrip is ToolStripDropDown)
            {
                using var p = new Pen(Themes.Current.Border);
                e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            }
            // Menu bar, toolbar and status bar: no border line.
        }
    }

    private sealed class ThemedColors : ProfessionalColorTable
    {
        public ThemedColors() { UseSystemColors = false; }
        private static Theme T => Themes.Current;
        private static Color Hover => Themes.Blend(T.Raised, T.Accent, 0.25f);
        private static Color Press => Themes.Blend(T.Raised, T.Accent, 0.4f);

        public override Color MenuStripGradientBegin => T.Raised;
        public override Color MenuStripGradientEnd => T.Raised;
        public override Color ToolStripGradientBegin => T.Raised;
        public override Color ToolStripGradientMiddle => T.Raised;
        public override Color ToolStripGradientEnd => T.Raised;
        public override Color ToolStripBorder => T.Border;
        public override Color ToolStripDropDownBackground => T.Raised;
        public override Color ToolStripContentPanelGradientBegin => T.Back;
        public override Color ToolStripContentPanelGradientEnd => T.Back;
        public override Color ToolStripPanelGradientBegin => T.Raised;
        public override Color ToolStripPanelGradientEnd => T.Raised;
        public override Color StatusStripGradientBegin => T.Raised;
        public override Color StatusStripGradientEnd => T.Raised;
        public override Color ImageMarginGradientBegin => T.Raised;
        public override Color ImageMarginGradientMiddle => T.Raised;
        public override Color ImageMarginGradientEnd => T.Raised;
        public override Color MenuBorder => T.Border;
        public override Color MenuItemBorder => T.Selection;
        public override Color MenuItemSelected => T.Selection;
        public override Color MenuItemSelectedGradientBegin => T.Selection;
        public override Color MenuItemSelectedGradientEnd => T.Selection;
        public override Color MenuItemPressedGradientBegin => T.Selection;
        public override Color MenuItemPressedGradientMiddle => T.Selection;
        public override Color MenuItemPressedGradientEnd => T.Selection;
        public override Color SeparatorDark => T.Border;
        public override Color SeparatorLight => T.Raised;
        public override Color GripDark => T.Border;
        public override Color GripLight => T.Raised;
        public override Color ButtonSelectedHighlight => Hover;
        public override Color ButtonSelectedHighlightBorder => T.Accent;
        public override Color ButtonSelectedBorder => T.Accent;
        public override Color ButtonSelectedGradientBegin => Hover;
        public override Color ButtonSelectedGradientMiddle => Hover;
        public override Color ButtonSelectedGradientEnd => Hover;
        public override Color ButtonPressedHighlight => Press;
        public override Color ButtonPressedBorder => T.Accent;
        public override Color ButtonPressedGradientBegin => Press;
        public override Color ButtonPressedGradientMiddle => Press;
        public override Color ButtonPressedGradientEnd => Press;
        public override Color ButtonCheckedHighlight => T.Selection;
        public override Color ButtonCheckedHighlightBorder => T.Accent;
        public override Color ButtonCheckedGradientBegin => T.Selection;
        public override Color ButtonCheckedGradientMiddle => T.Selection;
        public override Color ButtonCheckedGradientEnd => T.Selection;
        public override Color CheckBackground => T.Selection;
        public override Color CheckSelectedBackground => Press;
        public override Color CheckPressedBackground => Press;
        public override Color OverflowButtonGradientBegin => T.Raised;
        public override Color OverflowButtonGradientMiddle => T.Raised;
        public override Color OverflowButtonGradientEnd => T.Raised;
    }

    // ---- interop ----

    private const int WH_CBT = 5, HCBT_ACTIVATE = 5;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_BORDER_COLOR = 34, DWMWA_CAPTION_COLOR = 35, DWMWA_TEXT_COLOR = 36;

    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);
}
