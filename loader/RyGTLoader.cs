// RyGT loader: sets up Gran Turismo 7 from the player's own dump, firmware modules and fonts,
// then starts it. Built with build.cmd (C# for .NET Framework 4.x, which Windows includes).
// SPDX-License-Identifier: GPL-2.0-only

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace RyGT
{
    static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateHardLink(string newFile, string existingFile, IntPtr security);

        // Returns a one-byte BOOLEAN, not a BOOL.
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool CreateSymbolicLink(string link, string target, int flags);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        public static extern int SetWindowTheme(IntPtr window, string appName, string idList);

        [DllImport("d3d11.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software,
            uint flags, IntPtr featureLevels, uint featureLevelsCount, uint sdkVersion,
            out IntPtr device, out int featureLevel, out IntPtr immediateContext);
    }

    /// Wine (Linux, Steam Proton): its kernel32 has wine_get_unix_file_name. Wine before 11 cannot
    /// make Windows links (CreateSymbolicLink does nothing there and mklink /J fails), so under Wine
    /// the game's files and folders are linked with Unix symbolic links, which Wine follows like the
    /// files themselves (and reports as reparse points when they are folders).
    static class Wine
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandle(string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, BestFitMapping = false)]
        static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("kernel32.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
        static extern IntPtr wine_get_unix_file_name(string dosPath);

        [DllImport("kernel32.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, ExactSpelling = true,
                   BestFitMapping = false)]
        static extern IntPtr wine_get_dos_file_name(string unixPath);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetProcessHeap();

        [DllImport("kernel32.dll")]
        static extern bool HeapFree(IntPtr heap, uint flags, IntPtr memory);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct StartupInfo
        {
            public int cb;
            public string reserved, desktop, title;
            public int x, y, width, height, columns, rows, fill, flags;
            public short show, reserved2;
            public IntPtr reserved3, input, output, error;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ProcessInformation
        {
            public IntPtr process, thread;
            public int processId, threadId;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string directory,
            ref StartupInfo startup, out ProcessInformation information);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);

        static readonly bool detected = DetectWine();

        public static bool Detected { get { return detected; } }

        static bool DetectWine()
        {
            try
            {
                IntPtr kernel = GetModuleHandle("kernel32.dll");
                return kernel != IntPtr.Zero && GetProcAddress(kernel, "wine_get_unix_file_name") != IntPtr.Zero;
            }
            catch { return false; }
        }

        /// The Unix path of a Windows path (whose last part need not exist yet).
        public static string UnixPath(string path)
        {
            IntPtr unix = wine_get_unix_file_name(Path.GetFullPath(path));
            if (unix == IntPtr.Zero) throw new Exception("Wine has no Unix path for " + path + ".");
            try
            {
                int length = 0;
                while (Marshal.ReadByte(unix, length) != 0) length++;
                var bytes = new byte[length];
                Marshal.Copy(unix, bytes, 0, length);
                return Encoding.UTF8.GetString(bytes);
            }
            finally { HeapFree(GetProcessHeap(), 0, unix); }
        }

        static string DosPath(string unixPath)
        {
            IntPtr dos = wine_get_dos_file_name(unixPath);
            if (dos == IntPtr.Zero) throw new Exception("Wine has no Windows path for " + unixPath + ".");
            try { return Marshal.PtrToStringUni(dos); }
            finally { HeapFree(GetProcessHeap(), 0, dos); }
        }

        static string Quote(string unixPath)
        {
            return "'" + unixPath.Replace("'", "'\\''") + "'";
        }

        /// Makes each link (key) a Unix symbolic link to its target (value), replacing an earlier
        /// symbolic link but never a real file or folder, with one /bin/sh script. Wine starts Unix
        /// programs detached, so the script's last line writes a file to wait for.
        public static void Link(IEnumerable<KeyValuePair<string, string>> links)
        {
            string name = "rygt-link-" + Guid.NewGuid().ToString("N");
            string script = Path.Combine(Path.GetTempPath(), name + ".sh");
            string done = Path.Combine(Path.GetTempPath(), name + ".done");
            var text = new StringBuilder();
            text.Append("#!/bin/sh\n# Written by RyGT7.exe: links the game's files under Wine.\n");
            text.Append("PATH=/usr/local/bin:/usr/bin:/bin\n");
            text.Append("link() { if [ -L \"$2\" ] || [ ! -e \"$2\" ]; then ln -sfn \"$1\" \"$2\"; fi; }\n");
            foreach (var pair in links)
            {
                // The link's folder exists; its last part may not (Wine still gives the Unix path).
                string folder = UnixPath(Path.GetDirectoryName(Path.GetFullPath(pair.Key)));
                text.Append("link " + Quote(UnixPath(pair.Value)) + " " +
                            Quote(folder.TrimEnd('/') + "/" + Path.GetFileName(pair.Key)) + "\n");
            }
            text.Append("echo done > " + Quote(UnixPath(done)) + "\n");
            File.WriteAllText(script, text.ToString(), new UTF8Encoding(false));
            try
            {
                var startup = new StartupInfo { cb = Marshal.SizeOf(typeof(StartupInfo)) };
                ProcessInformation information;
                var command = new StringBuilder("\"" + DosPath("/bin/sh") + "\" \"" + UnixPath(script) + "\"");
                const uint CreateNoWindow = 0x08000000;
                if (!CreateProcess(null, command, IntPtr.Zero, IntPtr.Zero, false, CreateNoWindow, IntPtr.Zero, null,
                                   ref startup, out information))
                    throw new Exception("Could not start /bin/sh to link the game's files (error " +
                                        Marshal.GetLastWin32Error() + ").");
                if (information.thread != IntPtr.Zero) CloseHandle(information.thread);
                if (information.process != IntPtr.Zero) CloseHandle(information.process);
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
                while (!File.Exists(done) && DateTime.UtcNow < deadline) Thread.Sleep(50);
                if (!File.Exists(done)) throw new Exception("Linking the game's files with /bin/sh did not finish.");
            }
            finally
            {
                try { File.Delete(script); } catch { }
                try { File.Delete(done); } catch { }
            }
        }
    }

    /// The launcher's look (after the Epic Games launcher): a charcoal window with rounded panels
    /// and cards, white text with grey secondary text, white main buttons and grey rounded ones.
    static class Dark
    {
        public static readonly Color Back = Color.FromArgb(17, 18, 22);       // the window
        public static readonly Color Panel = Color.FromArgb(22, 24, 29);      // the settings pages
        public static readonly Color Sidebar = Color.FromArgb(13, 14, 18);    // the settings' category list
        public static readonly Color Card = Color.FromArgb(28, 30, 36);       // cards on a page
        public static readonly Color Field = Color.FromArgb(32, 35, 42);
        public static readonly Color Button = Color.FromArgb(32, 35, 42);
        public static readonly Color ButtonHover = Color.FromArgb(44, 48, 56);
        public static readonly Color ButtonDown = Color.FromArgb(26, 28, 34);
        public static readonly Color Hover = Color.FromArgb(30, 33, 40);      // list rows under the mouse
        public static readonly Color Line = Color.FromArgb(38, 41, 48);       // between settings rows
        public static readonly Color Disabled = Color.FromArgb(96, 101, 112);
        public static readonly Color Accent = Color.FromArgb(178, 24, 36);
        public static readonly Color AccentHover = Color.FromArgb(206, 40, 52);
        public static readonly Color AccentText = Color.FromArgb(240, 92, 76);
        public static readonly Color Heading = Color.FromArgb(128, 134, 146);
        public static readonly Color Border = Color.FromArgb(44, 47, 55);
        public static readonly Color Text = Color.FromArgb(236, 238, 242);
        public static readonly Color DimText = Color.FromArgb(132, 138, 150);
        public const int Radius = 4;

        static readonly bool HasBahnschrift = FontFamily.Families.Any(f => f.Name == "Bahnschrift SemiBold");
        static readonly Dictionary<float, Font> caps = new Dictionary<float, Font>();

        /// The capitals font of headings and buttons: Bahnschrift SemiBold (Windows 10 and later).
        public static Font Caps(float size)
        {
            Font font;
            if (!caps.TryGetValue(size, out font))
                caps[size] = font = HasBahnschrift ? new Font("Bahnschrift SemiBold", size) : new Font("Segoe UI Semibold", size);
            return font;
        }

        /// The text with a hair space between its letters (spaced capitals).
        public static string Spaced(string text)
        {
            return string.Join(((char)0x200A).ToString(), text.Select(c => c.ToString()).ToArray());
        }

        public static void Apply(Control control)
        {
            // Other controls take their parent's color (a card's, the window's) unless they set one.
            if (control is Form) control.BackColor = Back;
            if (control.Tag as string != "heading") control.ForeColor = Text;
            var button = control as Button;
            if (button != null && !(button is GlowButton)) Round(button);
            var box = control as TextBox;
            if (box != null)
            {
                box.BackColor = Field;
                if (box.BorderStyle == BorderStyle.Fixed3D) box.BorderStyle = BorderStyle.FixedSingle;
                box.HandleCreated += delegate { Native.SetWindowTheme(box.Handle, "DarkMode_Explorer", null); };
            }
            var number = control as NumericUpDown;
            if (number != null)
            {
                number.BackColor = Field;
                number.BorderStyle = BorderStyle.None;
                // The up and down buttons, drawn over in the field's colors.
                var spin = number.Controls.Count > 0 ? number.Controls[0] : null;
                if (spin != null && !(spin is TextBoxBase)) spin.Paint += (sender, e) => PaintSpin(spin, e.Graphics);
            }
            var check = control as CheckBox;
            if (check != null && !(check is DarkCheckBox)) check.FlatStyle = FlatStyle.Flat;
            var combo = control as ComboBox;
            if (combo != null) DarkCombo(combo);
            var scroll = control as ScrollableControl;
            if (scroll != null && scroll.AutoScroll)
                scroll.HandleCreated += delegate { Native.SetWindowTheme(scroll.Handle, "DarkMode_Explorer", null); };
            foreach (Control child in control.Controls) Apply(child);
        }

        /// A dark title bar (Windows 10 2004 and later; ignored before).
        public static void TitleBar(Form form)
        {
            int on = 1;
            if (Native.DwmSetWindowAttribute(form.Handle, 20, ref on, sizeof(int)) != 0)
                Native.DwmSetWindowAttribute(form.Handle, 19, ref on, sizeof(int));
        }

        public static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { path.AddRectangle(r); return path; }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void FillRounded(Graphics g, Color color, RectangleF r, float radius)
        {
            using (var path = RoundedRect(r, radius))
            using (var brush = new SolidBrush(color))
                g.FillPath(brush, path);
        }

        /// Paints what lies under `control`: the banner picture, or the nearest opaque parent's color.
        public static void PaintUnder(Control control, Graphics g)
        {
            // See-through cards between the control and what is under them, innermost first.
            var glass = new List<Card>();
            for (var parent = control.Parent; parent != null; parent = parent.Parent)
            {
                var card = parent as Card;
                if (card != null && card.Fill.A < 255) { glass.Add(card); continue; }
                var backdrop = parent as IBackdrop;
                if (backdrop != null && backdrop.DrawBackdrop(control, g, false)) { FillGlass(glass, control, g); return; }
                if (parent.BackColor.A == 255)
                {
                    using (var back = new SolidBrush(parent.BackColor)) g.FillRectangle(back, control.ClientRectangle);
                    FillGlass(glass, control, g);
                    return;
                }
            }
            g.Clear(Back);
            FillGlass(glass, control, g);
        }

        static void FillGlass(List<Card> glass, Control control, Graphics g)
        {
            for (int i = glass.Count - 1; i >= 0; --i)
            {
                using (var fill = new SolidBrush(glass[i].Fill)) g.FillRectangle(fill, control.ClientRectangle);
            }
        }

        /// Gives the controls that take their parent's color a transparent one, so the window's
        /// picture shows through them (the cards, fields, buttons and lists paint their own).
        public static void SeeThrough(Control control)
        {
            foreach (Control child in control.Controls)
            {
                bool own = child is Card || child is TextBoxBase || child is ListControl || child is ButtonBase ||
                           child is TrackBar || child is UpDownBase || child is DarkBar || child is Slider || child is ToggleRow;
                if (!own && !TypeDescriptor.GetProperties(child)["BackColor"].ShouldSerializeValue(child))
                    child.BackColor = Color.Transparent;
                if (!(child is TextBoxBase) && !(child is ListControl) && !(child is UpDownBase)) SeeThrough(child);
            }
        }

        /// Draws a button as a rounded rectangle: white with dark text for the main action (Tag
        /// "accent"), grey otherwise; Tag "nav" is a top bar tab and "nav-on" the open one.
        static void Round(Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.UseVisualStyleBackColor = false;
            button.Cursor = Cursors.Hand;
            if (button.Padding == Padding.Empty) button.Padding = new Padding(18, 7, 18, 7);
            if (button.Font.Name == "Segoe UI" && !(button.Tag is string && (string)button.Tag == "icon")) button.Font = Caps(9f);
            bool hover = false, down = false;
            button.MouseEnter += delegate { hover = true; button.Invalidate(); };
            button.MouseLeave += delegate { hover = false; down = false; button.Invalidate(); };
            button.MouseDown += delegate { down = true; button.Invalidate(); };
            button.MouseUp += delegate { down = false; button.Invalidate(); };
            button.Paint += (sender, e) => PaintButton(button, e.Graphics, hover, down);
        }

        static void PaintButton(Button button, Graphics g, bool hover, bool down)
        {
            PaintUnder(button, g);
            string kind = button.Tag as string;
            bool accent = kind == "accent", play = kind == "play", icon = kind == "icon";
            Color fill = Color.Empty, edge, text = Text;
            if (!button.Enabled)
            {
                fill = play ? Color.FromArgb(90, 20, 22, 27) : Color.Empty;
                edge = Color.FromArgb(52, 55, 62);
                text = Disabled;
            }
            else if (accent)
            {
                fill = down ? Color.FromArgb(200, 203, 210) : hover ? Color.White : Color.FromArgb(232, 234, 238);
                edge = fill;
                text = Back;
            }
            else if (play && (hover || down || button.Focused))
            {
                fill = down ? Color.FromArgb(214, 217, 224) : Color.White;
                edge = fill;
                text = Back;
            }
            else if (play)
            {
                fill = Color.FromArgb(120, 10, 11, 14);
                edge = Color.FromArgb(214, 218, 226);
            }
            else
            {
                fill = down ? Color.FromArgb(10, 255, 255, 255) : hover ? Color.FromArgb(22, 255, 255, 255) : Color.Empty;
                edge = hover ? Color.FromArgb(150, 156, 168) : Color.FromArgb(72, 77, 88);
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, button.Width - 1f, button.Height - 1f);
            if (!fill.IsEmpty) FillRounded(g, fill, r, 3);
            using (var path = RoundedRect(r, 3))
            using (var pen = new Pen(edge))
                g.DrawPath(pen, path);
            string label = icon ? button.Text : play ? Spaced(button.Text.ToUpperInvariant()) : button.Text.ToUpperInvariant();
            TextRenderer.DrawText(g, label, button.Font, button.ClientRectangle, text,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }

        static void PaintSpin(Control spin, Graphics g)
        {
            using (var back = new SolidBrush(Button)) g.FillRectangle(back, spin.ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float x = spin.Width / 2f, half = spin.Height / 2f;
            using (var pen = new Pen(spin.Enabled ? Color.FromArgb(200, 200, 200) : Color.FromArgb(100, 100, 100), 1.5f))
            {
                g.DrawLines(pen, new[] { new PointF(x - 3.5f, half / 2 + 2), new PointF(x, half / 2 - 1.5f), new PointF(x + 3.5f, half / 2 + 2) });
                g.DrawLines(pen, new[] { new PointF(x - 3.5f, half + half / 2 - 2), new PointF(x, half + half / 2 + 1.5f), new PointF(x + 3.5f, half + half / 2 - 2) });
            }
        }

        /// A drop-down list drawn dark: the closed box and the open list both in the field color.
        static void DarkCombo(ComboBox combo)
        {
            combo.BackColor = Field;
            if (combo.DrawMode != DrawMode.Normal) return;
            combo.DrawMode = DrawMode.OwnerDrawFixed;
            combo.ItemHeight = 18;
            combo.DrawItem += (sender, e) =>
            {
                bool edit = (e.State & DrawItemState.ComboBoxEdit) != 0;
                bool selected = (e.State & DrawItemState.Selected) != 0 && !edit;
                using (var back = new SolidBrush(selected ? ButtonHover : Field)) e.Graphics.FillRectangle(back, e.Bounds);
                if (e.Index < 0) return;
                var bounds = new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 4, e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, combo.GetItemText(combo.Items[e.Index]), combo.Font, bounds,
                                      combo.Enabled ? Text : Color.FromArgb(112, 112, 112),
                                      TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                                      TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            };
            combo.HandleCreated += delegate { Native.SetWindowTheme(combo.Handle, "DarkMode_CFD", null); };
        }
    }

    /// A window that paints a picture behind its controls (the launcher: launcher\background.png).
    interface IBackdrop
    {
        /// The picture as it lies under `control`; `raw` without the darkening. False without one.
        bool DrawBackdrop(Control control, Graphics g, bool raw);
    }

    /// A check box drawn in the loader's colors: a dark rounded square, red with a white tick when checked.
    class DarkCheckBox : CheckBox
    {
        public DarkCheckBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            var text = TextRenderer.MeasureText(Text, Font);
            return new Size(text.Width + 26, Math.Max(text.Height, 18) + 4);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Dark.PaintUnder(this, e.Graphics);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            OnPaintBackground(e);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int size = 16;
            var box = new RectangleF(1.5f, (Height - size) / 2 + 0.5f, size - 1, size - 1);
            bool hover = ClientRectangle.Contains(PointToClient(Cursor.Position));
            Dark.FillRounded(g, !Enabled ? Color.FromArgb(36, 36, 36) : Checked ? (hover ? Dark.AccentHover : Dark.Accent) : Dark.Field, box, 4);
            if (!Checked)
            {
                using (var path = Dark.RoundedRect(box, 4))
                using (var border = new Pen(!Enabled ? Color.FromArgb(58, 58, 58) : hover ? Color.FromArgb(140, 140, 140) : Color.FromArgb(88, 88, 88)))
                    g.DrawPath(border, path);
            }
            else
            {
                using (var tick = new Pen(Enabled ? Color.White : Color.FromArgb(140, 140, 140), 2f))
                {
                    g.DrawLines(tick, new[]
                    {
                        new PointF(box.Left + 3.5f, box.Top + 8f),
                        new PointF(box.Left + 6.5f, box.Top + 11f),
                        new PointF(box.Left + 11.5f, box.Top + 4.5f)
                    });
                }
            }
            TextRenderer.DrawText(g, Text, Font, new Point(size + 8, (Height - TextRenderer.MeasureText(Text, Font).Height) / 2),
                                  Enabled ? ForeColor : Color.FromArgb(112, 112, 112), TextFormatFlags.NoPrefix);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Invalidate(); }
    }

    /// A drop-down list with a rounded dark frame and arrow, drawn over the flat combo box's
    /// white border once Windows has painted it (its items are drawn by Dark.Apply).
    class DarkComboBox : ComboBox
    {
        bool hover;

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
        // Windows redraws parts of the box for these without a full paint; paint the frame again.
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }
        protected override void OnDropDownClosed(EventArgs e) { base.OnDropDownClosed(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != 0xF || !IsHandleCreated) return; // WM_PAINT
            using (var g = Graphics.FromHwnd(Handle))
            {
                var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
                using (var outside = new Region(ClientRectangle))
                using (var path = Dark.RoundedRect(r, 6))
                {
                    outside.Exclude(path);
                    g.SetClip(outside, CombineMode.Replace);
                    Dark.PaintUnder(this, g);
                    g.ResetClip();
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    // The flat style's inner white line, then the arrow's area.
                    using (var inner = new Pen(Dark.Field)) g.DrawRectangle(inner, 1, 1, Width - 3, Height - 3);
                    var arrow = new RectangleF(Width - 22, 2, 20, Height - 4);
                    using (var field = new SolidBrush(Dark.Field)) g.FillRectangle(field, arrow);
                    using (var pen = new Pen(!Enabled ? Color.FromArgb(52, 52, 52) : Focused || DroppedDown ? Color.FromArgb(120, 120, 120) :
                                             hover ? Color.FromArgb(96, 96, 96) : Color.FromArgb(62, 62, 62)))
                        g.DrawPath(pen, path);
                    float x = arrow.X + arrow.Width / 2 - 1, y = Height / 2f;
                    using (var pen = new Pen(Enabled ? Color.FromArgb(200, 200, 200) : Color.FromArgb(100, 100, 100), 1.5f))
                        g.DrawLines(pen, new[] { new PointF(x - 4, y - 2), new PointF(x, y + 2), new PointF(x + 4, y - 2) });
                }
            }
        }
    }

    /// A setting that is on or off: its text on the left, a switch on the right, a line under it.
    class ToggleRow : CheckBox
    {
        bool hover;
        float knob = -1; // 0 off .. 1 on, while it slides
        int version;

        public ToggleRow()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            AutoSize = false;
            Dock = DockStyle.Fill;
            Height = 40;
            Margin = new Padding(0);
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            return new Size(TextRenderer.MeasureText(Text, Font).Width + 70, 40);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Dark.PaintUnder(this, e.Graphics);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            OnPaintBackground(e);
            if (hover && Enabled)
                using (var lit = new SolidBrush(Color.FromArgb(8, 255, 255, 255))) g.FillRectangle(lit, ClientRectangle);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width - 56, Height - 1), Enabled ? Dark.Text : Dark.Disabled,
                                  TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var track = new RectangleF(Width - 40, (Height - 1 - 18) / 2f, 36, 18);
            float on = knob < 0 ? (Checked ? 1 : 0) : knob;
            Color trackColor = !Enabled ? Color.FromArgb(36, 39, 46) : Anim.Mix(Color.FromArgb(52, 56, 64), Color.FromArgb(232, 234, 238), on);
            Dark.FillRounded(g, trackColor, track, 9);
            const float size = 12;
            float x = track.X + 3 + (track.Width - size - 6) * on;
            Color knobColor = !Enabled ? Color.FromArgb(70, 74, 84) : Anim.Mix(Color.FromArgb(150, 155, 166), Dark.Back, on);
            using (var fill = new SolidBrush(knobColor)) g.FillEllipse(fill, x, track.Y + 3, size, size);
            using (var line = new Pen(Dark.Line)) g.DrawLine(line, 0, Height - 1, Width, Height - 1);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

        protected override void OnCheckedChanged(EventArgs e)
        {
            base.OnCheckedChanged(e);
            if (!Visible || !IsHandleCreated) { knob = -1; return; }
            int mine = ++version;
            float from = knob < 0 ? (Checked ? 0 : 1) : knob, to = Checked ? 1 : 0;
            int start = Environment.TickCount;
            Anim.Run(() =>
            {
                if (mine != version || IsDisposed) return false;
                float t = Math.Min(1, (Environment.TickCount - start) / 140f);
                knob = from + (to - from) * Anim.Ease(t);
                Invalidate();
                if (t >= 1) knob = -1;
                return t < 1;
            });
        }
    }

    /// A setting with a control (a drop-down, a slider, a button): its name on the left, the control
    /// on the right, a line under it. The name greys out with the control.
    class SettingRow : TableLayoutPanel
    {
        readonly Label label;
        readonly Control control;

        /// The row's line goes elsewhere (under a hint that belongs to the setting).
        public bool NoLine;

        public SettingRow(string text, Control control)
        {
            this.control = control;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            ColumnCount = 2;
            RowCount = 1;
            Dock = DockStyle.Fill;
            Margin = new Padding(0);
            Padding = new Padding(0, 0, 0, 1);
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            label = new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0), BackColor = Color.Transparent, Tag = "heading" };
            Controls.Add(label, 0, 0);
            control.Anchor = AnchorStyles.Right;
            control.Margin = new Padding(12, 0, 0, 0);
            Controls.Add(control, 1, 0);
            control.EnabledChanged += delegate { ShowEnabled(); };
            ShowEnabled();
            // A table sizes its rows from this height (a new one is 100 high).
            Height = GetPreferredSize(Size.Empty).Height;
            control.SizeChanged += delegate { Height = GetPreferredSize(Size.Empty).Height; };
        }

        void ShowEnabled()
        {
            label.ForeColor = control.Enabled ? Dark.Text : Dark.Disabled;
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            // From the label and the control (a table offered more height would take all of it).
            var text = label.GetPreferredSize(Size.Empty);
            var right = control.AutoSize ? control.GetPreferredSize(Size.Empty) : control.Size;
            return new Size(text.Width + control.Margin.Horizontal + right.Width, Math.Max(40, Math.Max(text.Height, right.Height) + 12));
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Dark.PaintUnder(this, e.Graphics);
            if (NoLine) return;
            using (var line = new Pen(Dark.Line)) e.Graphics.DrawLine(line, 0, Height - 1, Width, Height - 1);
        }
    }

    /// A thin slider: a line filled up to the value, a round knob, and the value on its right.
    class Slider : Control
    {
        int minimum, maximum = 100, value;
        bool dragging;
        public int TickFrequency = 10, SmallChange = 1, LargeChange = 10;
        /// Shown after the value ("%").
        public string Suffix = "";
        public event EventHandler ValueChanged;

        public Slider()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Size = new Size(280, 28);
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public int Minimum { get { return minimum; } set { minimum = value; Value = this.value; Invalidate(); } }
        public int Maximum { get { return maximum; } set { maximum = value; Value = this.value; Invalidate(); } }

        public int Value
        {
            get { return value; }
            set
            {
                int v = Math.Max(minimum, Math.Min(maximum, value));
                if (v == this.value) return;
                this.value = v;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        Rectangle Track { get { return new Rectangle(8, Height / 2 - 1, Math.Max(10, Width - 8 - 64), 2); } }

        void SetFromX(int x)
        {
            var track = Track;
            float amount = Math.Max(0, Math.Min(1, (x - track.X) / (float)track.Width));
            Value = minimum + (int)Math.Round(amount * (maximum - minimum));
        }

        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); dragging = true; Capture = true; SetFromX(e.X); }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (dragging) SetFromX(e.X); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; Capture = false; }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Left || keyData == Keys.Right || keyData == Keys.Up || keyData == Keys.Down || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Down) Value -= SmallChange;
            else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Up) Value += SmallChange;
            else if (e.KeyCode == Keys.PageDown) Value -= LargeChange;
            else if (e.KeyCode == Keys.PageUp) Value += LargeChange;
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Dark.PaintUnder(this, g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var track = Track;
            float amount = maximum > minimum ? (value - minimum) / (float)(maximum - minimum) : 0;
            float x = track.X + track.Width * amount;
            using (var rest = new SolidBrush(Color.FromArgb(62, 66, 76))) g.FillRectangle(rest, track);
            using (var done = new SolidBrush(Enabled ? Color.FromArgb(220, 223, 230) : Dark.Disabled))
                g.FillRectangle(done, track.X, track.Y, x - track.X, track.Height);
            float knob = Focused ? 14 : 12;
            using (var fill = new SolidBrush(Enabled ? Color.White : Dark.Disabled)) g.FillEllipse(fill, x - knob / 2, track.Y + 1 - knob / 2, knob, knob);
            TextRenderer.DrawText(g, value + Suffix, Dark.Caps(9f), new Rectangle(track.Right + 8, 0, Width - track.Right - 8, Height),
                                  Enabled ? Dark.Text : Dark.Disabled, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    /// A category in the settings' sidebar: grey text, white with a red bar on its left when open.
    class SidebarItem : Control
    {
        bool active, hover;

        public SidebarItem(string text)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Text = text;
            Height = 38;
            Cursor = Cursors.Hand;
            Font = new Font("Segoe UI", 10.5f);
            TabStop = true;
        }

        public bool Active { get { return active; } set { active = value; Invalidate(); } }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) OnClick(EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var back = new SolidBrush(Dark.Sidebar)) g.FillRectangle(back, ClientRectangle);
            if (active)
                using (var bar = new SolidBrush(Dark.Accent)) g.FillRectangle(bar, 16, 6, 3, Height - 12);
            Color text = active ? Color.White : hover ? Color.FromArgb(206, 210, 218) : Dark.DimText;
            TextRenderer.DrawText(g, Text, Font, new Rectangle(30, 0, Width - 34, Height), text,
                                  TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    /// The launcher's animations: one timer (about 60 times a second) that calls each running
    /// animation's step until the step says it is done.
    static class Anim
    {
        static readonly List<Func<bool>> running = new List<Func<bool>>();
        static System.Windows.Forms.Timer timer;

        /// Runs `step` every tick while it returns true.
        public static void Run(Func<bool> step)
        {
            running.Add(step);
            if (timer == null)
            {
                timer = new System.Windows.Forms.Timer { Interval = 15 };
                timer.Tick += delegate
                {
                    for (int i = running.Count - 1; i >= 0; --i)
                    {
                        bool more;
                        try { more = running[i](); } catch { more = false; }
                        if (!more) running.RemoveAt(i);
                    }
                    if (running.Count == 0) timer.Stop();
                };
            }
            if (!timer.Enabled) timer.Start();
        }

        /// Ease-out (fast start, gentle end) of 0..1.
        public static float Ease(float t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return 1 - (1 - t) * (1 - t) * (1 - t);
        }

        public static Color Mix(Color a, Color b, float t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }
    }

    /// A home view button: outlined, with a light sweeping across it now and then; under the mouse
    /// (or with the keyboard focus) it fills white, and the home view draws a glow around it.
    class GlowButton : Button
    {
        float hot;
        int version;
        bool over, pressing;
        /// The text is a Segoe MDL2 Assets glyph (the settings button), not a word in capitals.
        public bool Glyph;
        static readonly int sweepStart = Environment.TickCount;

        public GlowButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
        }

        /// 0..1: how far it has filled white.
        public float Hot { get { return hot; } }

        void Animate()
        {
            int mine = ++version;
            float from = hot, to = (over || (Focused && ShowFocusCues)) && Enabled ? 1 : 0;
            int start = Environment.TickCount;
            Anim.Run(() =>
            {
                if (mine != version || IsDisposed) return false;
                float t = Math.Min(1, (Environment.TickCount - start) / 170f);
                hot = from + (to - from) * Anim.Ease(t);
                Invalidate();
                if (Parent != null) Parent.Invalidate(Rectangle.Inflate(Bounds, 16, 16), false);
                return t < 1;
            });
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); over = true; Animate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); over = false; pressing = false; Animate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); pressing = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressing = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Animate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Animate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Animate(); }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Dark.PaintUnder(this, g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            float h = Enabled ? hot : 0;
            Color fill = !Enabled ? Color.FromArgb(90, 40, 43, 50)
                       : Anim.Mix(Color.FromArgb(120, 10, 11, 14), pressing ? Color.FromArgb(214, 217, 224) : Color.White, h);
            Color edge = !Enabled ? Color.FromArgb(52, 55, 62) : Anim.Mix(Color.FromArgb(214, 218, 226), Color.White, h);
            Color text = !Enabled ? Dark.Disabled : Anim.Mix(Color.White, Dark.Back, h);
            using (var path = Dark.RoundedRect(r, 3))
            {
                using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
                // The sweeping light: 1.1 s across, every 3.5 s, while not filled.
                double phase = ((Environment.TickCount - sweepStart) % 3500) / 1100.0;
                if (Enabled && h < 0.99f && phase < 1)
                {
                    float band = 70, x = r.X - band + (float)phase * (r.Width + band);
                    var bandRect = new RectangleF(x, r.Y, band, r.Height);
                    using (var light = new LinearGradientBrush(bandRect, Color.Transparent, Color.Transparent, 0f))
                    {
                        var blend = new ColorBlend
                        {
                            Colors = new[] { Color.FromArgb(0, 255, 255, 255), Color.FromArgb((int)(70 * (1 - h)), 255, 255, 255), Color.FromArgb(0, 255, 255, 255) },
                            Positions = new[] { 0f, 0.5f, 1f }
                        };
                        light.InterpolationColors = blend;
                        var clip = g.Clip;
                        g.SetClip(path, CombineMode.Intersect);
                        g.FillRectangle(light, bandRect);
                        g.Clip = clip;
                    }
                }
                using (var pen = new Pen(edge)) g.DrawPath(pen, path);
            }
            string label = Glyph ? Text : Dark.Spaced(Text.ToUpperInvariant());
            TextRenderer.DrawText(g, label, Font, ClientRectangle, text,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    /// The launcher's home view, drawn in one piece: the pictures (drifting slowly sideways and
    /// fading from one to the next every 10 seconds), darkened toward the bottom left, the logo
    /// or the title, the caption, the status and the version, with glows around its buttons.
    class HomeView : Control, IBackdrop
    {
        readonly List<Image> pictures;
        readonly GlowButton[] buttons;
        public Image Logo;
        public string Title = "", Accent = "", Caption = "", Status = "", Version = "";
        /// The updater's notice (a newer release) at the top right, above its Update button.
        public string Notice = "";
        public GlowButton UpdateButton;
        /// The signed-in online ID and the ryGT7 logo beside it, at the top right; the notice goes under them.
        public string Player = "";
        public Image PlayerLogo;
        int NoticeTop { get { return Player.Length > 0 ? 92 : 30; } }
        readonly Dictionary<int, Bitmap> scaled = new Dictionary<int, Bitmap>();
        Size scaledFor;
        Bitmap overlay, frame;
        int current, next = -1, fadeFrom, shownSince;
        readonly int started = Environment.TickCount;
        readonly System.Windows.Forms.Timer ticker;
        const int ShowMs = 10000, FadeMs = 1400;
        const int Left_ = 56, Bottom_ = 46, Gap = 10;

        public HomeView(List<Image> pictures, params GlowButton[] buttons)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            this.pictures = pictures;
            this.buttons = buttons;
            BackColor = Dark.Back;
            foreach (var button in buttons) Controls.Add(button);
            shownSince = Environment.TickCount;
            ticker = new System.Windows.Forms.Timer { Interval = 33 };
            ticker.Tick += delegate { Tick(); };
            VisibleChanged += delegate { if (Visible) ticker.Start(); else ticker.Stop(); };
            ticker.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { ticker.Dispose(); foreach (var b in scaled.Values) b.Dispose(); if (overlay != null) overlay.Dispose(); if (frame != null) frame.Dispose(); }
            base.Dispose(disposing);
        }

        void Tick()
        {
            var form = FindForm();
            if (!Visible || form == null || form.WindowState == FormWindowState.Minimized) return;
            if (pictures.Count > 1 && next < 0 && Environment.TickCount - shownSince > ShowMs)
            {
                next = (current + 1) % pictures.Count;
                fadeFrom = Environment.TickCount;
            }
            Compose();
            Invalidate(true);
        }

        Bitmap Scaled(int index)
        {
            Bitmap cached;
            if (scaled.TryGetValue(index, out cached)) return cached;
            var image = pictures[index];
            // Covering the view with 6% to spare, for the drift.
            float scale = 1.06f * Math.Max(Width / (float)image.Width, Height / (float)image.Height);
            int width = (int)Math.Ceiling(image.Width * scale), height = (int)Math.Ceiling(image.Height * scale);
            cached = new Bitmap(width, height);
            using (var g = Graphics.FromImage(cached))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(image, 0, 0, width, height);
            }
            scaled[index] = cached;
            return cached;
        }

        Bitmap Overlay()
        {
            if (overlay != null) return overlay;
            overlay = new Bitmap(Width, Height);
            var r = new Rectangle(0, 0, Width, Height);
            using (var g = Graphics.FromImage(overlay))
            {
                using (var dim = new SolidBrush(Color.FromArgb(40, 6, 7, 9))) g.FillRectangle(dim, r);
                using (var left = new LinearGradientBrush(r, Color.FromArgb(215, 8, 9, 12), Color.FromArgb(0, 8, 9, 12), 0f))
                {
                    left.Blend = new Blend { Factors = new[] { 0f, 0.35f, 1f, 1f }, Positions = new[] { 0f, 0.3f, 0.65f, 1f } };
                    g.FillRectangle(left, r);
                }
                using (var bottom = new LinearGradientBrush(r, Color.FromArgb(0, 8, 9, 12), Color.FromArgb(225, 8, 9, 12), 90f))
                {
                    bottom.Blend = new Blend { Factors = new[] { 0f, 0f, 1f }, Positions = new[] { 0f, 0.45f, 1f } };
                    g.FillRectangle(bottom, r);
                }
            }
            return overlay;
        }

        void DrawPicture(Graphics g, int index, float alpha, float drift)
        {
            var image = Scaled(index);
            int x = -(int)Math.Round((image.Width - Width) * drift);
            int y = (Height - image.Height) / 12;
            if (alpha >= 1) { g.DrawImageUnscaled(image, x, y); return; }
            using (var attributes = new System.Drawing.Imaging.ImageAttributes())
            {
                attributes.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = alpha });
                g.DrawImage(image, new Rectangle(x, y, image.Width, image.Height), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
            }
        }

        /// This moment's picture (with the darkening) into `frame`.
        void Compose()
        {
            if (Width <= 0 || Height <= 0) return;
            if (scaledFor != Size)
            {
                foreach (var b in scaled.Values) b.Dispose();
                scaled.Clear();
                if (overlay != null) { overlay.Dispose(); overlay = null; }
                if (frame != null) { frame.Dispose(); frame = null; }
                scaledFor = Size;
            }
            if (frame == null) frame = new Bitmap(Width, Height);
            using (var g = Graphics.FromImage(frame))
            {
                if (pictures.Count == 0)
                {
                    using (var glow = new LinearGradientBrush(ClientRectangle, Color.FromArgb(14, 15, 19), Color.FromArgb(52, 14, 20), 0f))
                        g.FillRectangle(glow, ClientRectangle);
                    return;
                }
                // Side to side and back every 48 seconds, slowest at the ends.
                float drift = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * ((Environment.TickCount - started) / 48000.0)));
                DrawPicture(g, current, 1, drift);
                if (next >= 0)
                {
                    float t = (Environment.TickCount - fadeFrom) / (float)FadeMs;
                    DrawPicture(g, next, Math.Min(1, t), drift);
                    if (t >= 1) { current = next; next = -1; shownSince = Environment.TickCount; }
                }
                g.DrawImageUnscaled(Overlay(), 0, 0);
            }
        }

        public bool DrawBackdrop(Control control, Graphics g, bool raw)
        {
            if (frame == null || frame.Size != Size) Compose();
            if (frame == null) return false;
            var at = control == this ? Point.Empty : control.Parent == this ? control.Location : PointToClient(control.PointToScreen(Point.Empty));
            g.DrawImageUnscaled(frame, -at.X, -at.Y);
            return true;
        }

        int ButtonsTop { get { return Height - Bottom_ - 30 - buttons[0].Height; } }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int x = Left_;
            foreach (var button in buttons)
            {
                button.Location = new Point(x, ButtonsTop);
                x += button.Width + Gap;
            }
            if (UpdateButton != null)
                UpdateButton.Location = new Point(Width - UpdateButton.Width - 30, NoticeTop + NoticeHeight + 14);
        }

        const int NoticeWidth = 420;
        int NoticeHeight
        {
            get
            {
                return Notice.Length == 0 ? 0 : TextRenderer.MeasureText(Notice, Font, new Size(NoticeWidth, 400),
                                                                         TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            DrawBackdrop(this, g, false);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // The glow around a button under the mouse, outside the button itself.
            foreach (var button in buttons)
            {
                if (button.Hot <= 0.01f || !button.Enabled) continue;
                var r = new RectangleF(button.Left, button.Top, button.Width, button.Height);
                for (int i = 14; i >= 1; i -= 1)
                {
                    int alpha = (int)(button.Hot * 34 * Math.Pow(1 - i / 15.0, 2));
                    using (var path = Dark.RoundedRect(RectangleF.Inflate(r, i, i), 3 + i))
                    using (var pen = new Pen(Color.FromArgb(alpha, 255, 255, 255), 2f))
                        g.DrawPath(pen, path);
                }
            }
            int top = ButtonsTop;
            // The caption above the buttons, the logo (or title) above it.
            var captionFont = Dark.Caps(8.5f);
            var captionSize = TextRenderer.MeasureText(Caption, captionFont);
            int captionY = top - 22 - captionSize.Height;
            TextRenderer.DrawText(g, Caption, captionFont, new Point(Left_ + 3, captionY), Color.FromArgb(190, 194, 204), TextFormatFlags.NoPrefix);
            if (Logo != null)
            {
                int w = 330, h = w * Logo.Height / Logo.Width;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(Logo, new Rectangle(Left_, captionY - 12 - h, w, h));
            }
            else
            {
                var font = Dark.Caps(34f);
                var size = TextRenderer.MeasureText(Title + " ", font);
                int y = captionY - size.Height;
                TextRenderer.DrawText(g, Title + " ", font, new Point(Left_, y), Color.White, TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, Accent, font, new Point(Left_ + size.Width - 8, y), Dark.AccentText, TextFormatFlags.NoPrefix);
            }
            // The status under the buttons, the version at the bottom right.
            TextRenderer.DrawText(g, Status, Font, new Rectangle(Left_ + 2, top + buttons[0].Height + 12, Math.Max(100, Width - Left_ - 260), 40),
                                  Color.FromArgb(200, 204, 212), TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak);
            if (Player.Length > 0)
            {
                const int logoSize = 36, pad = 8;
                var nameFont = Dark.Caps(10f);
                var nameSize = TextRenderer.MeasureText(Player, nameFont, Size.Empty, TextFormatFlags.NoPrefix);
                int width = nameSize.Width + logoSize + pad * 3, x = Width - width - 30;
                using (var path = Dark.RoundedRect(new RectangleF(x, 24, width, logoSize + pad * 2), 4))
                using (var shade = new SolidBrush(Color.FromArgb(150, 10, 11, 14)))
                    g.FillPath(shade, path);
                TextRenderer.DrawText(g, Player, nameFont, new Point(x + pad, 24 + pad + (logoSize - nameSize.Height) / 2), Color.White, TextFormatFlags.NoPrefix);
                if (PlayerLogo != null)
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(PlayerLogo, new Rectangle(x + width - pad - logoSize, 24 + pad, logoSize, logoSize));
                }
            }
            if (Notice.Length > 0)
            {
                var box = new Rectangle(Width - NoticeWidth - 30, NoticeTop, NoticeWidth, NoticeHeight);
                using (var shade = new SolidBrush(Color.FromArgb(150, 10, 11, 14)))
                    g.FillRectangle(shade, Rectangle.Inflate(box, 12, 10));
                TextRenderer.DrawText(g, Notice, Font, box, Color.FromArgb(226, 230, 238),
                                      TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.Right);
            }
            var versionFont = Dark.Caps(9f);
            var versionSize = TextRenderer.MeasureText(Version, versionFont);
            TextRenderer.DrawText(g, Version, versionFont, new Point(Width - versionSize.Width - 30, Height - versionSize.Height - 24),
                                  Color.FromArgb(200, 204, 212), TextFormatFlags.NoPrefix);
        }
    }

    /// Covers the window while it changes views: the old view darkens, then the new one appears.
    class Curtain : Control
    {
        Bitmap picture;
        float dark;

        public Curtain()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Color.Black;
        }

        public void Show(Bitmap image, float darkness)
        {
            picture = image;
            dark = darkness;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            if (picture != null) g.DrawImageUnscaled(picture, 0, 0);
            else g.Clear(Dark.Back);
            using (var shade = new SolidBrush(Color.FromArgb((int)(255 * Math.Max(0, Math.Min(1, dark))), 6, 7, 9)))
                g.FillRectangle(shade, ClientRectangle);
        }
    }

    /// A rounded panel with a thin border; the controls on it take its color.
    class Card : TableLayoutPanel
    {
        public float Radius = Dark.Radius;
        public Color Edge = Dark.Border;
        /// The card's color; a translucent one lets the window's picture show through it.
        public readonly Color Fill;

        public Card(Color fill)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            Fill = fill;
            BackColor = fill.A == 255 ? fill : Color.Transparent;
            ColumnCount = 1;
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            Dark.PaintUnder(this, g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            using (var path = Dark.RoundedRect(r, Radius))
            {
                using (var fill = new SolidBrush(Fill)) g.FillPath(fill, path);
                if (!Edge.IsEmpty) using (var pen = new Pen(Edge)) g.DrawPath(pen, path);
            }
        }
    }

    /// A level meter in the launcher's colors (the live input of the wheel window): a dark rounded
    /// track, filled red up to Value.
    class DarkBar : Control
    {
        int minimum, maximum = 100, value;

        public DarkBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
        }

        public int Minimum { get { return minimum; } set { minimum = value; Invalidate(); } }
        public int Maximum { get { return maximum; } set { maximum = value; Invalidate(); } }

        public int Value
        {
            get { return value; }
            set { if (value != this.value) { this.value = value; Invalidate(); } }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Dark.PaintUnder(this, g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var track = new RectangleF(0, 0, Width - 0.5f, Height - 0.5f);
            Dark.FillRounded(g, Dark.Field, track, Height / 2f);
            int range = maximum - minimum;
            float amount = range > 0 ? Math.Max(0, Math.Min(1, (value - minimum) / (float)range)) : 0;
            if (amount > 0)
                Dark.FillRounded(g, Dark.Accent, new RectangleF(0, 0, Math.Max(Height, (Width - 0.5f) * amount), Height - 0.5f), Height / 2f);
        }
    }

    /// A game or system module file Set up can't use (missing, not a SELF/ELF, encrypted): the dump is
    /// incomplete or damaged, and Set up shows how to fix it (dump again).
    class DumpFileException : Exception
    {
        public readonly string File;
        public DumpFileException(string file, string message) : base(message) { File = file; }
    }

    /// Unwraps an unencrypted (fake-signed) PS4 SELF into a plain ELF.
    static class Self
    {
        public static bool IsElf(byte[] d)
        {
            return d.Length >= 4 && d[0] == 0x7f && d[1] == (byte)'E' && d[2] == (byte)'L' && d[3] == (byte)'F';
        }

        public static byte[] ToElf(byte[] d, string name)
        {
            if (IsElf(d)) return d;
            if (d.Length < 32 || d[0] != 0x4f || d[1] != 0x15 || d[2] != 0x3d || d[3] != 0x1d)
                throw new DumpFileException(name, name + " is neither a SELF nor an ELF file.");
            int count = BitConverter.ToUInt16(d, 24);
            var entries = new Dictionary<long, long[]>();
            for (int i = 0; i < count; i++)
            {
                long props = (long)BitConverter.ToUInt64(d, 32 + i * 32);
                long offset = (long)BitConverter.ToUInt64(d, 40 + i * 32);
                long size = (long)BitConverter.ToUInt64(d, 48 + i * 32);
                if ((props & 0xA) != 0)
                    throw new DumpFileException(name, name + " has encrypted or compressed segments. The dump must be decrypted.");
                if ((props & 0x800) != 0) entries[(props >> 20) & 0xFFF] = new[] { offset, size };
            }
            int elf = 32 + count * 32;
            long phoff = (long)BitConverter.ToUInt64(d, elf + 0x20);
            int phnum = BitConverter.ToUInt16(d, elf + 0x38);
            if (phoff != 0x40) throw new Exception(name + ": unexpected ELF header layout.");
            int headerLength = 0x40 + phnum * 0x38;
            long outSize = headerLength;
            var headers = new List<long[]>();
            for (int i = 0; i < phnum; i++)
            {
                int b = elf + 0x40 + i * 0x38;
                long type = BitConverter.ToUInt32(d, b);
                long offset = (long)BitConverter.ToUInt64(d, b + 8);
                long fileSize = (long)BitConverter.ToUInt64(d, b + 0x20);
                headers.Add(new[] { type, offset, fileSize });
                outSize = Math.Max(outSize, offset + fileSize);
            }
            var output = new byte[outSize];
            Array.Copy(d, elf, output, 0, headerLength);
            long selfSize = (long)BitConverter.ToUInt64(d, 16);
            for (int i = 0; i < headers.Count; i++)
            {
                long type = headers[i][0], offset = headers[i][1], fileSize = headers[i][2];
                if (fileSize == 0) continue;
                long[] entry;
                if (entries.TryGetValue(i, out entry))
                    Array.Copy(d, entry[0], output, offset, Math.Min(entry[1], fileSize));
                else if (type == 0x6fffff01 && selfSize + fileSize <= d.Length)
                    Array.Copy(d, selfSize, output, offset, fileSize); // PT_SCE_VERSION follows the body
            }
            return output;
        }

        /// Rewrites PT_SCE_RELRO (0x61000010) as a read-write PT_LOAD so the converter maps it and
        /// allows relocations into it.
        public static byte[] RelroToLoad(byte[] elf)
        {
            long phoff = (long)BitConverter.ToUInt64(elf, 0x20);
            int phnum = BitConverter.ToUInt16(elf, 0x38);
            for (int i = 0; i < phnum; i++)
            {
                int header = (int)phoff + i * 0x38;
                if (BitConverter.ToUInt32(elf, header) != 0x61000010) continue;
                BitConverter.GetBytes(1u).CopyTo(elf, header);     // PT_LOAD
                BitConverter.GetBytes(6u).CopyTo(elf, header + 4); // read, write
            }
            return elf;
        }
    }

    /// Finishes the converted GT7.exe using the player's own game icon,
    /// so Explorer and shortcuts show the game's art instead of a blank program icon, and the Windows
    /// (GUI) subsystem, so a double-click opens only the game window, without a console.
    static class GameExe
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr BeginUpdateResource(string file, bool deleteExisting);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool UpdateResource(IntPtr update, IntPtr type, IntPtr name, ushort language, byte[] data, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool EndUpdateResource(IntPtr update, bool discard);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);

        [DllImport("kernel32.dll")]
        static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);

        [DllImport("kernel32.dll")]
        static extern bool FreeLibrary(IntPtr module);

        const int RtIcon = 3, RtGroupIcon = 14;
        const ushort GuiSubsystem = 2;

        public static bool IsPrepared(string exe)
        {
            return (Wine.Detected || HasIcon(exe)) && ReadSubsystem(exe) == GuiSubsystem;
        }

        public static void Prepare(string exe, string iconPath = null)
        {
            // (Not the icon under Wine: it is only for Windows' Explorer, and this keeps Set up from
            // depending on Wine rewriting the resources of a 100 MB program.)
            if (iconPath == null) iconPath = Path.Combine(Path.GetDirectoryName(exe), "app0", "sce_sys", "icon0.png");
            if (!Wine.Detected && File.Exists(iconPath) && !HasIcon(exe)) ApplyIcon(exe, iconPath);
            if (ReadSubsystem(exe) != GuiSubsystem) WriteSubsystem(exe, GuiSubsystem);
        }

        // The subsystem field of the PE optional header: PE header offset at 0x3c, then the "PE\0\0"
        // signature (4 bytes), the file header (20 bytes) and 68 bytes into the optional header.
        static long SubsystemOffset(BinaryReader reader)
        {
            reader.BaseStream.Position = 0x3c;
            return reader.ReadInt32() + 4 + 20 + 68;
        }

        static ushort ReadSubsystem(string exe)
        {
            using (var reader = new BinaryReader(File.OpenRead(exe)))
            {
                reader.BaseStream.Position = SubsystemOffset(reader);
                return reader.ReadUInt16();
            }
        }

        static void WriteSubsystem(string exe, ushort subsystem)
        {
            using (var stream = new FileStream(exe, FileMode.Open, FileAccess.ReadWrite))
            {
                long offset = SubsystemOffset(new BinaryReader(stream));
                stream.Position = offset;
                stream.Write(BitConverter.GetBytes(subsystem), 0, 2);
            }
        }

        static bool HasIcon(string exe)
        {
            IntPtr module = LoadLibraryEx(exe, IntPtr.Zero, 0x2 | 0x20); // as a data file and image resource
            if (module == IntPtr.Zero) return false;
            try { return FindResource(module, (IntPtr)1, (IntPtr)RtGroupIcon) != IntPtr.Zero; }
            finally { FreeLibrary(module); }
        }

        /// Adds every image of the .ico as RT_ICON 1..n and the icon group as RT_GROUP_ICON 1.
        static void ApplyIcon(string exe, string iconPath)
        {
            byte[] ico;
            using (var original = Image.FromFile(iconPath))
            using (var output = new MemoryStream())
            using (var writer = new BinaryWriter(output))
            {
                int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
                var images = new List<byte[]>();
                foreach (int size in sizes)
                    using (var bitmap = new Bitmap(original, size, size))
                    using (var png = new MemoryStream())
                    { bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png); images.Add(png.ToArray()); }
                writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
                int offset = 6 + sizes.Length * 16;
                for (int i = 0; i < sizes.Length; i++)
                {
                    writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                    writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                    writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)32);
                    writer.Write(images[i].Length); writer.Write(offset); offset += images[i].Length;
                }
                foreach (byte[] image in images) writer.Write(image);
                ico = output.ToArray();
            }
            int count = BitConverter.ToUInt16(ico, 4);
            IntPtr update = BeginUpdateResource(exe, false);
            if (update == IntPtr.Zero) throw new Exception("Could not open " + exe + " (error " + Marshal.GetLastWin32Error() + ").");
            bool ok = false;
            try
            {
                var group = new MemoryStream();
                var writer = new BinaryWriter(group);
                writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)count);
                for (int i = 0; i < count; i++)
                {
                    int entry = 6 + 16 * i;
                    int size = BitConverter.ToInt32(ico, entry + 8), offset = BitConverter.ToInt32(ico, entry + 12);
                    var image = new byte[size];
                    Array.Copy(ico, offset, image, 0, size);
                    if (!UpdateResource(update, (IntPtr)RtIcon, (IntPtr)(i + 1), 0, image, (uint)size))
                        throw new Exception("Could not add the icon (error " + Marshal.GetLastWin32Error() + ").");
                    writer.Write(ico, entry, 12);   // width, height, colors, reserved, planes, bit count, size
                    writer.Write((ushort)(i + 1)); // the RT_ICON id instead of the file offset
                }
                var data = group.ToArray();
                if (!UpdateResource(update, (IntPtr)RtGroupIcon, (IntPtr)1, 0, data, (uint)data.Length))
                    throw new Exception("Could not add the icon group (error " + Marshal.GetLastWin32Error() + ").");
                ok = true;
            }
            finally
            {
                if (!EndUpdateResource(update, !ok) && ok)
                    throw new Exception("Could not write the icon into " + exe + " (error " + Marshal.GetLastWin32Error() + ").");
            }
        }
    }

    static class AtomicConfig
    {
        internal static void Write(string path, string text)
        {
            path = Path.GetFullPath(path);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var stream = new FileStream(temporary, FileMode.CreateNew,
                                                 FileAccess.Write, FileShare.None))
                {
                    using (var writer = new StreamWriter(stream,
                                                         new UTF8Encoding(false), 1024, true))
                        writer.Write(text);
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                try { File.Delete(temporary); } catch { }
            }
        }
    }

    class Settings
    {
        public string Game = "", Update = "", Modules = "", Fonts = "";
        public string StdoutLevel = "Warning";
        readonly string path;

        public Settings(string path)
        {
            this.path = path;
            if (!File.Exists(path)) return;
            foreach (var line in File.ReadAllLines(path))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                if (key == "game") Game = value;
                else if (key == "update") Update = value;
                else if (key == "modules") Modules = value;
                else if (key == "fonts") Fonts = value;
                else if (key == "stdout_level") StdoutLevel = LogFiles.Levels[LogFiles.LevelIndex(value)];
            }
        }

        public void Save()
        {
            File.WriteAllLines(path, new[] { "game=" + Game, "update=" + Update, "modules=" + Modules, "fonts=" + Fonts, "stdout_level=" + StdoutLevel });
            // Existing runtime libraries read their log threshold from this compatibility file.
            string runtimeIni = Path.Combine(Path.GetDirectoryName(path), "RyGT.ini");
            if (!string.Equals(runtimeIni, path, StringComparison.OrdinalIgnoreCase))
            {
                var lines = File.Exists(runtimeIni) ? File.ReadAllLines(runtimeIni).ToList() : new List<string>();
                lines.RemoveAll(line => line.TrimStart().StartsWith("stdout_level=", StringComparison.OrdinalIgnoreCase));
                lines.Add("stdout_level=" + StdoutLevel);
                File.WriteAllLines(runtimeIni, lines);
            }
        }
    }

    partial class LoaderForm : Form, IBackdrop
    {
        static readonly string[] FirmwareModules = { "libSceFont", "libSceFontFt", "libSceFreeTypeOt", "libSceJson2", "libSceNgs2" };
        // Used when present: the JPEG decoder (Scapes thumbnails and backgrounds) and PNG encoder (photos).
        // Both are plain software modules needing only libkernel and libSceLibcInternal.
        // (The other firmware modules GT7 imports stay RyGT's stubs in libs: converted from a full
        // firmware folder, some of them need modules nothing provides, such as libSceOttvCapture,
        // and GT7.exe then stopped at start with "Failed to load module".)
        static readonly string[] OptionalFirmwareModules = { "libSceJpegDec", "libScePngEnc" };

        readonly string root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        readonly Settings settings;
        readonly TextBox game, update, modules, fonts, log;
        readonly Button setup, play, addDlss, downloadDlss;
        readonly Label status;
        // Home: the picture over the whole window with the title, Play and the settings button.
        // Settings: a sidebar of categories and their pages.
        readonly HomeView home;
        readonly GlowButton updateButton;
        ReleaseInfo pendingUpdate;
        ControlsPage controls;
        readonly Panel settingsView, pagesHost;
        readonly Curtain curtain;
        readonly Dictionary<string, Panel> pages = new Dictionary<string, Panel>();
        readonly Dictionary<string, SidebarItem> pageItems = new Dictionary<string, SidebarItem>();
        string lastPage = "Game";
        volatile bool busy;

        public LoaderForm()
        {
            settings = new Settings(Path.Combine(root, "RyGT7.ini"));
            EnsureGT7Defaults();
            Text = "ryGT7 - Gran Turismo 7";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            try
            {
                string iconPath = Path.Combine(root, "app0", "sce_sys", "icon0.png");
                if (File.Exists(iconPath))
                    using (var bitmap = new Bitmap(iconPath)) Icon = Icon.FromHandle(bitmap.GetHicon());
            }
            catch { }
            Font = new Font("Segoe UI", 9.5f);
            ClientSize = new Size(1280, 760);
            MinimumSize = new Size(1040, 640);
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            BackColor = Dark.Back;
            // The launcher pictures (launcher\background*.png or .jpg, else GT7's own pic1.png) fill the
            // home view, one after another when there are several.
            pictures = LoadBackgrounds();
            ResizeRedraw = true;

            settingsView = new Panel { Dock = DockStyle.Fill, BackColor = Dark.Panel, Visible = false };

            // ---- Home: the pictures, the logo (launcher\logo.png) or the title, Play and the settings button
            play = new GlowButton { Text = "Play", Size = new Size(236, 46), Font = Dark.Caps(11f) };
            play.Click += delegate { PlayFromSwitch(); };
            var gear = new GlowButton { Text = ((char)0xE713).ToString(), Glyph = true, Size = new Size(46, 46), Font = new Font("Segoe MDL2 Assets", 13f) };
            gear.Click += delegate { ShowSettings(lastPage); };
            var logoImage = LoadLogo();
            home = new HomeView(pictures, (GlowButton)play, gear)
            {
                Dock = DockStyle.Fill, Logo = logoImage, Title = "GRAN TURISMO", Accent = "7",
                Caption = Dark.Spaced("CUSA24769   " + (char)183 + "   PS4   " + (char)183 + "   RYGT7 FOR WINDOWS"),
                Version = "ryGT7 " + (Updater.InstalledVersion(root) ?? "") + "  " + (char)183 + "  GT7 " + (char)183 + " CUSA24769"
            };
            // The updater: a newer release shows a notice and an Update button at the top right.
            Updater.CleanUp(root);
            updateButton = new GlowButton { Text = "Update", Size = new Size(230, 40), Font = Dark.Caps(10f), Visible = false };
            updateButton.Click += delegate { InstallUpdate(); };
            home.UpdateButton = updateButton;
            try { home.PlayerLogo = Icon.ToBitmap(); } catch { }
            home.Controls.Add(updateButton);
            curtain = new Curtain { Dock = DockStyle.Fill, Visible = false };
            Controls.Add(curtain);
            Controls.Add(settingsView);
            Controls.Add(home);
            // The status shows on the home view and on the Game page.
            status = new Label { AutoSize = true };
            status.TextChanged += delegate { home.Status = status.Text; home.Invalidate(false); };

            // ---- Settings: the sidebar
            var sidebar = new Panel { Dock = DockStyle.Left, Width = 224, BackColor = Dark.Sidebar };
            pagesHost = new Panel { Dock = DockStyle.Fill, BackColor = Dark.Panel };
            settingsView.Controls.Add(pagesHost);
            settingsView.Controls.Add(sidebar);
            var back = new Label
            {
                Text = (char)0x2039 + "   " + Dark.Spaced("BACK"), AutoSize = true, Font = Dark.Caps(9f), ForeColor = Dark.DimText,
                Tag = "heading", Cursor = Cursors.Hand, Location = new Point(28, 30)
            };
            back.Click += delegate { ShowHome(); };
            back.MouseEnter += delegate { back.ForeColor = Dark.Text; };
            back.MouseLeave += delegate { back.ForeColor = Dark.DimText; };
            sidebar.Controls.Add(back);
            if (logoImage != null)
            {
                sidebar.Controls.Add(new PictureBox
                {
                    Image = logoImage, SizeMode = PictureBoxSizeMode.Zoom, Location = new Point(26, 70),
                    Size = new Size(150, 150 * logoImage.Height / logoImage.Width)
                });
            }
            else
            {
                var logo = new PictureBox { Size = new Size(34, 34), SizeMode = PictureBoxSizeMode.Zoom, Location = new Point(28, 78) };
                try { logo.Image = new Icon(Icon, 48, 48).ToBitmap(); } catch { }
                sidebar.Controls.Add(logo);
                sidebar.Controls.Add(new Label { Text = "GRAN TURISMO", AutoSize = true, Font = Dark.Caps(12f), Location = new Point(70, 76) });
                sidebar.Controls.Add(new Label { Text = "7", AutoSize = true, Font = Dark.Caps(12f), ForeColor = Dark.AccentText, Tag = "heading", Location = new Point(70, 95) });
            }
            var sideVersion = new Label { Text = "ryGT7 for Windows", AutoSize = true, ForeColor = Color.FromArgb(96, 102, 114), Tag = "heading" };
            sidebar.Controls.Add(sideVersion);
            sidebar.Layout += delegate { sideVersion.Location = new Point(28, sidebar.ClientSize.Height - sideVersion.Height - 24); };
            var pageNames = new[] { "Game", "Display", "Graphics", "Shaders", "Controls", "Account", "Online", "About" };
            for (int i = 0; i < pageNames.Length; ++i)
            {
                string page = pageNames[i];
                var item = new SidebarItem(page) { Location = new Point(0, 150 + i * 40), Width = 224 };
                item.Click += delegate { ShowSettings(page); };
                sidebar.Controls.Add(item);
                pageItems[page] = item;
            }

            // ---- Game: the files, Set up and its log
            var gamePage = NewPage("Game", "Game",
                "Your own decrypted Gran Turismo 7 base game (CUSA24769), its update if it is separate, and your PS4 firmware's " +
                "system modules and fonts. Set up converts the game once; Play starts it.");
            var files = Section(gamePage, "Files");
            game = AddRow(files, "Base game (CUSA24769)", settings.Game);
            update = AddRow(files, "Update (optional, if separate)", settings.Update);
            modules = AddRow(files, "System modules", settings.Modules);
            fonts = AddRow(files, "Fonts", settings.Fonts);
            var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 16, 0, 0) };
            setup = new Button { Text = "Set up", AutoSize = true, Tag = "accent", Margin = new Padding(0, 0, 10, 0) };
            setup.Click += delegate { StartSetup(); };
            var open = new Button { Text = "Open folder", AutoSize = true, Margin = new Padding(0) };
            open.Click += delegate { OpenFolder(); };
            actions.Controls.Add(setup);
            actions.Controls.Add(open);
            gamePage.Controls.Add(actions);
            var gameStatus = new Label { AutoSize = true, MaximumSize = new Size(860, 0), ForeColor = Dark.DimText, Tag = "heading", Margin = new Padding(1, 12, 0, 0) };
            gamePage.Controls.Add(gameStatus);
            status.TextChanged += delegate { gameStatus.Text = status.Text; };
            var logSection = Section(gamePage, "Set up log");
            var logBox = new Card(Dark.Field) { Dock = DockStyle.Fill, Height = 190, Radius = 3, Edge = Color.Empty, Padding = new Padding(10, 8, 4, 8), Margin = new Padding(0, 6, 0, 0) };
            logBox.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            log = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 8.5f), Margin = new Padding(0)
            };
            logBox.Controls.Add(log);
            logSection.Controls.Add(logBox);

            // ---- Display
            var displayPage = NewPage("Display", "Display", "How the game's window looks. Changes apply the next time you play.");
            var display = Section(displayPage, "Screen");
            AddScreenSettings(display);
            AddOption(display, "Show an FPS counter", "General", "show_fps_counter", false);
            AddOption(display, "Show the splash screen while the game starts", "General", "show_splash", false);
            AddOption(display, "Show the game on Discord (Rich Presence)", "General", "discord_rpc_enabled", true);
            AddMemorySetting(display);
            AddVideoMemorySetting(display);
            AddUpdateSettings(display);

            // ---- Graphics
            var graphicsPage = NewPage("Graphics", "Graphics", "The renderer, upscaling and frame generation. Changes apply the next time you play.");
            var graphics = Section(graphicsPage, "Rendering");
            // Sparks, tire smoke and other effects are always on; an older launcher could have
            // turned them off in the config.
            if (!ReadSetting("GPU", "particle_effects", true))
            {
                try { WriteSetting("GPU", "particle_effects", true); } catch { }
            }
            // Upscaling: one method at a time, scaling the game's 1920x1080 picture. AMD FSR 1 is
            // shadGT's own pass; FSR 3.1 goes through amd_fidelityfx_vk.dll (part of the release);
            // NVIDIA DLSS goes through Streamline (sl.dlss.dll and nvngx_dlss.dll in the dlss folder,
            // which the Download button fetches from NVIDIA's GitHub; DlssDownload.cs), or a
            // self-built ryGT_dlss.dll when Streamline's DLSS plugin is absent. Settings:
            // GPU.fsr_enabled, fsr3_enabled, dlss_enabled, rcas_enabled and upscale_quality (the game
            // gets the last as RYGT_UPSCALE_QUALITY).
            var upscaling = Section(graphicsPage, "Upscaling");
            bool fsr3Installed = File.Exists(Path.Combine(root, "amd_fidelityfx_vk.dll"));
            string dlssDir = Path.Combine(root, "dlss");
            Func<string, DarkComboBox, Label, FlowLayoutPanel> comboRow = (text, combo, note) =>
            {
                var row = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 0), WrapContents = false };
                row.Controls.Add(new Label { Text = text, AutoSize = false, Width = 110, Height = 20, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) });
                row.Controls.Add(combo);
                return row;
            };
            Func<Label> noteLabel = () => new Label
            {
                AutoSize = true, MaximumSize = new Size(860, 0), BackColor = Color.Transparent, ForeColor = Dark.DimText,
                Tag = "heading", Margin = new Padding(2, 2, 0, 4)
            };
            var method = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210, FlatStyle = FlatStyle.Flat };
            method.Items.AddRange(new object[] { "Off", "AMD FSR 1", "AMD FSR 3.1", "NVIDIA DLSS" });
            var methodNote = noteLabel();
            upscaling.Controls.Add(comboRow("Method", method, methodNote));
            upscaling.Controls.Add(methodNote);

            // Quality: the size the upscaler scales to. The game always draws 1920x1080, so a mode
            // picks the output (the picture times 1.5, 1.72, 2 or 3), which is then fitted to the
            // window; Auto scales straight to the window (ryty_upscale.h in the renderer).
            var qualityKeys = new[] { "auto", "native", "quality", "balanced", "performance", "ultra_performance" };
            var qualityScales = new[] { 0.0, 1.0, 1.5, 1.0 / 0.58, 2.0, 3.0 };
            var quality = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210, FlatStyle = FlatStyle.Flat };
            quality.Items.AddRange(new object[] { "Auto (window size)", "Native (anti-aliasing only)", "Quality", "Balanced", "Performance", "Ultra Performance" });
            quality.SelectedIndex = Math.Max(0, Array.IndexOf(qualityKeys, ReadStringSetting("GPU", "upscale_quality", "auto")));
            var qualityNote = noteLabel();
            upscaling.Controls.Add(comboRow("Quality", quality, qualityNote));
            upscaling.Controls.Add(qualityNote);
            var rcas = AddOption(upscaling, "Sharpening (FSR)", "GPU", "rcas_enabled", true);

            // NVIDIA files: the Download button, the status of what is there, and Add from a folder.
            var dlssStatus = noteLabel();
            dlssStatus.Margin = new Padding(0, 2, 0, 2);
            var dlssButtons = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 8, 0, 0), WrapContents = false };
            downloadDlss = new Button { Text = "Download NVIDIA DLSS " + DlssDownload.DlssVersion, AutoSize = true, Margin = new Padding(0, 0, 6, 0) };
            addDlss = new Button { Text = "Add from a folder...", AutoSize = true, Margin = new Padding(0) };
            dlssButtons.Controls.Add(downloadDlss);
            dlssButtons.Controls.Add(addDlss);
            upscaling.Controls.Add(dlssButtons);
            upscaling.Controls.Add(dlssStatus);

            // Frame generation: one at a time, AMD's (any GPU, about 2x) or NVIDIA's (RTX 40 and
            // newer, Windows' hardware-accelerated GPU scheduling on; 2x-6x, the multiplier is
            // GPU.dlssg_frames, which the game gets as RYGT_DLSSG_FRAMES). Settings:
            // GPU.framegen_enabled, dlssg_enabled.
            var frameGen = Section(graphicsPage, "Frame generation");
            var fg = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210, FlatStyle = FlatStyle.Flat };
            fg.Items.AddRange(new object[] { "Off", "AMD FSR 3.1 (any GPU)", "NVIDIA DLSS (RTX 40+)" });
            var fgNote = noteLabel();
            frameGen.Controls.Add(comboRow("Method", fg, fgNote));
            var mult = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210, FlatStyle = FlatStyle.Flat };
            mult.Items.AddRange(new object[] { "2x", "3x (RTX 50)", "4x (RTX 50)", "5x (RTX 50)", "6x (RTX 50)" });
            mult.SelectedIndex = Math.Max(1, Math.Min(5, ReadIntSetting("GPU", "dlssg_frames", 1))) - 1;
            var multRow = comboRow("Multiplier", mult, null);
            frameGen.Controls.Add(multRow);
            frameGen.Controls.Add(fgNote);

            bool loading = true;
            Func<string, bool> has = name => File.Exists(Path.Combine(dlssDir, name));
            Func<bool> dlssReady = () => has("nvngx_dlss.dll") && ((has("sl.interposer.dll") && has("sl.common.dll") && has("sl.dlss.dll")) || has("ryGT_dlss.dll"));
            Func<bool> dlssgReady = () => has("sl.interposer.dll") && has("sl.common.dll") && has("sl.dlss_g.dll") && has("nvngx_dlssg.dll");
            Action save = delegate
            {
                if (loading) return;
                try
                {
                    int m = method.SelectedIndex;
                    WriteSetting("GPU", "fsr_enabled", m == 1 || m == 2);
                    if (m == 1 || m == 2) WriteSetting("GPU", "fsr3_enabled", m == 2);
                    WriteSetting("GPU", "dlss_enabled", m == 3);
                    WriteSetting("GPU", "upscale_quality", qualityKeys[Math.Max(0, quality.SelectedIndex)]);
                    WriteSetting("GPU", "framegen_enabled", fg.SelectedIndex == 1);
                    WriteSetting("GPU", "dlssg_enabled", fg.SelectedIndex == 2);
                    if (fg.SelectedIndex == 2) WriteSetting("GPU", "dlssg_frames", mult.SelectedIndex + 1);
                }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
            Action refresh = delegate
            {
                bool dlssOk = dlssReady(), dlssgOk = dlssgReady();
                int m = method.SelectedIndex;
                methodNote.Text = m == 0 ? "The picture is stretched to the window as it is."
                                : m == 1 ? "Any GPU. Light and simple."
                                : m == 2 ? (fsr3Installed ? "Any GPU. Sharper than FSR 1." : "Needs amd_fidelityfx_vk.dll next to GT7.exe.")
                                : dlssOk ? "RTX cards only. The best image."
                                         : "RTX cards only. Download the NVIDIA files below first.";
                bool scaler = m == 2 || m == 3;
                quality.Enabled = scaler;
                int q = Math.Max(0, quality.SelectedIndex);
                if (!scaler)
                    qualityNote.Text = m == 1 ? "FSR 1 always scales to the window size." : "Pick FSR 3.1 or DLSS to choose a quality.";
                else if (q == 0)
                    qualityNote.Text = "1920 x 1080, scaled to the window size (only when the window is bigger).";
                else
                {
                    int w = (int)Math.Round(1920 * qualityScales[q]), h = (int)Math.Round(1080 * qualityScales[q]);
                    qualityNote.Text = q == 1 ? "1920 x 1080 at its own size, anti-aliased, then fitted to the window."
                                              : "1920 x 1080 -> " + w + " x " + h + ", then fitted to the window. Higher is sharper and costs more GPU.";
                }
                rcas.Visible = m == 1 || m == 2;
                fgNote.Text = fg.SelectedIndex == 0 ? "Off: the game's own frame rate."
                            : fg.SelectedIndex == 1 ? (fsr3Installed ? "About 2x the frames on any GPU." : "Needs amd_fidelityfx_vk.dll next to GT7.exe.")
                            : dlssgOk ? "Generated frames between game frames. RTX 40 or newer."
                                      : "RTX 40 or newer. Download the NVIDIA files above first.";
                // The launcher turns the label + list rows into SettingRows (Rowify), so the row on
                // screen is the list's parent, not multRow.
                (mult.Parent ?? multRow).Visible = fg.SelectedIndex == 2;
                var missing = new List<string>();
                if (!has("nvngx_dlss.dll")) missing.Add("nvngx_dlss.dll");
                if (!has("sl.interposer.dll") || !has("sl.common.dll")) missing.Add("Streamline");
                else if (!has("sl.dlss.dll") && !has("ryGT_dlss.dll")) missing.Add("sl.dlss.dll");
                if (!has("sl.dlss_g.dll") && has("sl.interposer.dll")) missing.Add("sl.dlss_g.dll");
                if (!has("nvngx_dlssg.dll")) missing.Add("nvngx_dlssg.dll");
                bool none = !has("nvngx_dlss.dll") && !has("sl.interposer.dll") && !has("nvngx_dlssg.dll");
                dlssStatus.Text = none
                    ? "NVIDIA DLSS files: not installed. RyGT7 does not include them; the button downloads NVIDIA's own signed files from NVIDIA's GitHub."
                    : "NVIDIA DLSS files: " + (has("nvngx_dlss.dll") ? "DLSS " + FileVersion(Path.Combine(dlssDir, "nvngx_dlss.dll")) : "no DLSS") +
                      ", " + (has("sl.interposer.dll") ? "Streamline " + FileVersion(Path.Combine(dlssDir, "sl.interposer.dll")) : "no Streamline") +
                      (missing.Count == 0 ? ". Ready." : ". Missing: " + string.Join(", ", missing) + ".");
            };
            Action load = delegate
            {
                loading = true;
                bool dlssOn = ReadSetting("GPU", "dlss_enabled", false) && dlssReady();
                bool fsrOn = ReadSetting("GPU", "fsr_enabled", false);
                method.SelectedIndex = dlssOn ? 3 : !fsrOn ? 0 : ReadSetting("GPU", "fsr3_enabled", true) && fsr3Installed ? 2 : 1;
                bool dlssgOn = ReadSetting("GPU", "dlssg_enabled", false) && dlssgReady();
                fg.SelectedIndex = dlssgOn ? 2 : ReadSetting("GPU", "framegen_enabled", false) && fsr3Installed ? 1 : 0;
                loading = false;
                refresh();
            };
            Action refreshDlss = delegate { load(); };
            int lastMethod = -1, lastFg = -1;
            method.SelectedIndexChanged += delegate
            {
                if (!loading && ((method.SelectedIndex == 3 && !dlssReady()) || (method.SelectedIndex == 2 && !fsr3Installed)))
                {
                    MessageBox.Show(this, method.SelectedIndex == 3
                        ? "DLSS needs NVIDIA's files. Use \"Download NVIDIA DLSS\" first (RTX cards only)."
                        : "FSR 3.1 needs amd_fidelityfx_vk.dll next to GT7.exe.");
                    method.SelectedIndex = Math.Max(0, lastMethod);
                    return;
                }
                lastMethod = method.SelectedIndex;
                save();
                refresh();
            };
            quality.SelectedIndexChanged += delegate { save(); refresh(); };
            fg.SelectedIndexChanged += delegate
            {
                if (!loading && ((fg.SelectedIndex == 2 && !dlssgReady()) || (fg.SelectedIndex == 1 && !fsr3Installed)))
                {
                    MessageBox.Show(this, fg.SelectedIndex == 2
                        ? "DLSS frame generation needs NVIDIA's files. Use \"Download NVIDIA DLSS\" first (RTX 40 or newer)."
                        : "FSR frame generation needs amd_fidelityfx_vk.dll next to GT7.exe.");
                    fg.SelectedIndex = Math.Max(0, lastFg);
                    return;
                }
                lastFg = fg.SelectedIndex;
                save();
                refresh();
            };
            mult.SelectedIndexChanged += delegate { save(); };
            load();
            lastMethod = method.SelectedIndex;
            lastFg = fg.SelectedIndex;
            addDlss.Click += delegate
            {
                // The game keeps the DLSS files open while it runs (#61).
                var games = Process.GetProcessesByName("GT7");
                bool gameRunning = games.Length != 0;
                foreach (var process in games) process.Dispose();
                if (gameRunning)
                {
                    MessageBox.Show(this, "Close GT7 before adding or replacing the DLSS files.");
                    return;
                }
                using (var dialog = new FolderBrowserDialog
                {
                    Description = "Pick the folder with your NVIDIA DLSS files: nvngx_dlss.dll and Streamline's " +
                                  "sl.*.dll for upscaling; for frame generation also nvngx_dlssg.dll and " +
                                  "NvLowLatencyVk.dll (from NVIDIA's DLSS and Streamline SDKs)."
                })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    var names = new[] { "nvngx_dlss.dll", "nvngx_dlssg.dll", "sl.interposer.dll", "sl.common.dll",
                                        "sl.dlss.dll", "sl.dlss_g.dll", "sl.reflex.dll", "sl.pcl.dll", "NvLowLatencyVk.dll" };
                    int copied = 0;
                    try
                    {
                        Directory.CreateDirectory(dlssDir);
                        foreach (var name in names)
                        {
                            // Found anywhere under the picked folder (the SDKs keep them in bin\x64).
                            var found = Directory.GetFiles(dialog.SelectedPath, name, SearchOption.AllDirectories);
                            if (found.Length == 0) continue;
                            // The upscaler is checked to be NVIDIA's 64-bit DLSS library, and the one
                            // it replaces is kept as a .bak (#61).
                            if (name == "nvngx_dlss.dll") InstallDlssLibrary(found[0], dlssDir);
                            else File.Copy(found[0], Path.Combine(dlssDir, name), true);
                            ++copied;
                        }
                    }
                    catch (Exception e)
                    {
                        MessageBox.Show(this, "Could not copy the DLSS files: " + e.Message);
                    }
                    refreshDlss();
                    MessageBox.Show(this, copied == 0
                        ? "No DLSS files were found in that folder."
                        : "Copied " + copied + " DLSS file(s) into the dlss folder.");
                }
            };
            // Download NVIDIA DLSS: NVIDIA's DLSS and Streamline files from NVIDIA's GitHub, pinned
            // versions checked by SHA-256 and NVIDIA's signature (DlssDownload.cs); no admin rights.
            downloadDlss.Click += delegate
            {
                var games = Process.GetProcessesByName("GT7");
                bool gameRunning = games.Length != 0;
                foreach (var process in games) process.Dispose();
                if (gameRunning)
                {
                    MessageBox.Show(this, "Close GT7 before downloading the DLSS files.");
                    return;
                }
                if (MessageBox.Show(this,
                        "Download NVIDIA DLSS " + DlssDownload.DlssVersion + " and Streamline " + DlssDownload.StreamlineVersion +
                        " (about 70 MB) into the dlss folder?\n\n" +
                        "From: NVIDIA's own GitHub repositories (github.com/NVIDIA/DLSS and github.com/NVIDIA-RTX/Streamline). " +
                        "RyGT7 does not ship these files.\n\n" +
                        "Checks: each file must match a pinned SHA-256 hash and carry NVIDIA Corporation's digital " +
                        "signature, or it is not used.\n\n" +
                        "Needs an NVIDIA RTX card (frame generation: RTX 40 or newer). The files are NVIDIA's, under " +
                        "NVIDIA's licenses (see dlss\\" + DlssDownload.NoticeName + ").", "Download NVIDIA DLSS",
                        MessageBoxButtons.OKCancel) != DialogResult.OK) return;
                // Busy like Set up, so the state timer keeps Play, Set up and both DLSS buttons off
                // while the files in the dlss folder are replaced one by one.
                busy = true;
                RefreshState();
                status.Text = "Downloading the NVIDIA DLSS files...";
                var worker = new Thread(() =>
                {
                    string message;
                    try
                    {
                        int count = DlssDownload.Install(dlssDir, text => BeginInvoke((Action)(() => dlssStatus.Text = text)));
                        message = count == 0 ? "The NVIDIA DLSS files were already up to date."
                                             : "Downloaded and checked " + count + " NVIDIA DLSS file(s).";
                    }
                    catch (Exception e)
                    {
                        message = "Could not download the DLSS files: " + e.Message;
                    }
                    BeginInvoke((Action)(() =>
                    {
                        busy = false;
                        RefreshState();
                        refreshDlss();
                        MessageBox.Show(this, message);
                    }));
                }) { IsBackground = true };
                worker.Start();
            };
            // Race frame rate (GPU.race_fps_lock, the game gets it as SHADGT_FPS_LOCK): an even 30
            // by default; 60 when the PC keeps up. 60 is marked experimental: busy race sections
            // stay well under it, and frames of 16.7, 33 and 50 ms mixed judder more than an even 30.
            var fpsRow = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 2), WrapContents = false };
            var fpsLock = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210, FlatStyle = FlatStyle.Flat };
            fpsLock.Items.AddRange(new object[] { "30 fps (steady)", "60 fps (experimental)" });
            fpsRow.Controls.Add(new Label { Text = "Race frame rate", AutoSize = false, Width = 196, Height = 20, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) });
            fpsRow.Controls.Add(fpsLock);
            graphics.Controls.Add(fpsRow);
            fpsLock.SelectedIndex = ReadIntSetting("GPU", "race_fps_lock", 30) == 60 ? 1 : 0;
            // Texture sharpness (GPU.texture_lod_bias, the game gets it as RYGT_TEXTURE_LOD_BIAS):
            // textures are read at finer detail levels than GT7 asks for. Two levels were far too
            // strong in testing, so the list stops at one.
            var sharpnessValues = new[] { "0", "-0.5", "-1" };
            var sharpness = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230, FlatStyle = FlatStyle.Flat };
            sharpness.Items.AddRange(new object[] { "Game default", "Sharper (half a level)", "Sharpest (one level)" });
            graphics.Controls.Add(new SettingRow("Texture sharpness", sharpness) { NoLine = true });
            // The setting's line goes under its hint.
            var sharpnessHint = Hint("Sharper textures cost some GPU time and can shimmer in the distance.");
            sharpnessHint.AutoSize = false;
            sharpnessHint.MaximumSize = Size.Empty;
            sharpnessHint.Dock = DockStyle.Fill;
            sharpnessHint.Height = 28;
            sharpnessHint.Margin = new Padding(0);
            sharpnessHint.Paint += (sender, e) => { using (var line = new Pen(Dark.Line)) e.Graphics.DrawLine(line, 0, sharpnessHint.Height - 1, sharpnessHint.Width, sharpnessHint.Height - 1); };
            graphics.Controls.Add(sharpnessHint);
            sharpness.SelectedIndex = Math.Max(0, Array.IndexOf(sharpnessValues, ReadStringSetting("GPU", "texture_lod_bias", "0")));
            sharpness.SelectedIndexChanged += delegate
            {
                try { WriteSetting("GPU", "texture_lod_bias", sharpnessValues[sharpness.SelectedIndex]); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
            // Shader cache size (GPU.driver_cache_gb, GPU.driver_cache_unlimited; the game gets it as
            // RYGT_DRIVER_CACHE_MAX_MB): the renderer's saved GPU shader cache, like NVIDIA's own
            // setting. 0 turns it off; a bigger cache keeps more cars and tracks compiled, and is saved
            // less often as it grows (each save copies it whole).
            // The driver keeps it in RAM, a little more than the file; a file over the limit is not loaded.
            var cacheRow = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 2), WrapContents = false };
            cacheRow.Controls.Add(new Label { Text = "Shader cache size (GB, 0 = off)", AutoSize = false, Width = 196, Height = 20, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) });
            var cacheSize = new NumericUpDown { Minimum = 0, Maximum = 100, DecimalPlaces = 0, Increment = 1, Width = 70,
                                                Value = Math.Max(0, Math.Min(100, ReadIntSetting("GPU", "driver_cache_gb", 2))) };
            var cacheUnlimited = new DarkCheckBox { Text = "Unlimited", AutoSize = true, Checked = ReadSetting("GPU", "driver_cache_unlimited", false), Margin = new Padding(10, 4, 0, 0) };
            cacheSize.Enabled = !cacheUnlimited.Checked;
            cacheRow.Controls.Add(cacheSize);
            cacheRow.Controls.Add(cacheUnlimited);
            graphics.Controls.Add(cacheRow);
            cacheSize.ValueChanged += delegate
            {
                try { WriteSetting("GPU", "driver_cache_gb", (int)cacheSize.Value); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
            cacheUnlimited.CheckedChanged += delegate
            {
                cacheSize.Enabled = !cacheUnlimited.Checked;
                try { WriteSetting("GPU", "driver_cache_unlimited", cacheUnlimited.Checked); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
            // FIX-220: AMD GPUs only, off by default: shaders that read vertex values are interpolated
            // through KHR barycentrics (GPU.amd_graphics_fix, the game gets it as
            // RYGT_AMD_SINGLE_SAMPLE_BARYCENTRIC). It fixes some AMD rendering glitches but makes shader
            // loading slower; other GPUs ignore it.
            AddOption(graphics, "AMD graphics fix (AMD GPUs only; slower shader loading)", "GPU", "amd_graphics_fix", false);
            // A tester's RX 6000 series card crashed loading into races with it on (2026-10-30).
            graphics.Controls.Add(Hint("Can crash RX 6000 series GPUs when loading into a race. If the game crashes when a race loads, turn this off."));
            // PERF-GT7-111/117: shader pre-translation (RYGT_MANIFEST_RECORD / RYGT_MANIFEST_PRETRANSLATE).
            AddOption(graphics, "Rebuild translated shaders in the background after a renderer update (about 30 MB a track)", "GPU", "shader_pretranslate", false);
            // PERF-GT7-108: the compile pack (RYGT_COMPILE_PACK).
            AddOption(graphics, "Compile the shaders already seen on this PC at start, after a driver update (background, pauses in races)", "GPU", "compile_pack", false);
            fpsLock.SelectedIndexChanged += delegate
            {
                try { WriteSetting("GPU", "race_fps_lock", fpsLock.SelectedIndex == 1 ? 60 : 30); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };

            // ---- Shaders
            var shadersPage = NewPage("Shaders", "Shaders", "Shaders are built once per car and track; these settings decide when that happens.");
            // Shaders: the stored ones are built while the splash screen shows; a new one pauses
            // the game until it is compiled (instead of a stutter and missing objects).
            var shaders = Section(shadersPage, "Compiling");
            // What the game does while a new shader compiles during play (GPU.compile_mode).
            var modeRow = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 2), WrapContents = false };
            var modeLabel = new Label { Text = "When new shaders compile", AutoSize = false, Width = 196, Height = 20, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) };
            var mode = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, FlatStyle = FlatStyle.Flat };
            mode.Items.AddRange(new object[] { "Pause the game", "Slow motion", "Keep going (objects pop in)" });
            mode.SelectedIndex = Math.Max(0, Math.Min(2, ReadIntSetting("GPU", "compile_mode", 2)));
            modeRow.Controls.Add(modeLabel);
            modeRow.Controls.Add(mode);
            shaders.Controls.Add(modeRow);
            var batchRow = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 2), WrapContents = false };
            var batchLabel = new Label { Text = "Compile batching (when pausing)", AutoSize = false, Width = 196, Height = 20, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) };
            // How long one pause may go on compiling the shaders of what comes next: longer pauses,
            // fewer of them (GPU.compile_batch_ms).
            var batch = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, FlatStyle = FlatStyle.Flat };
            var batchValues = new[] { 0, 100, 300, 1000 };
            batch.Items.AddRange(new object[] { "Off (only what is needed)", "Short (up to 0.1 s)", "Medium (up to 0.3 s)", "Long (up to 1 s)" });
            int current = ReadIntSetting("GPU", "compile_batch_ms", 0);
            int index = Array.IndexOf(batchValues, current);
            batch.SelectedIndex = index >= 0 ? index : 0;
            batch.SelectedIndexChanged += delegate
            {
                try { WriteSetting("GPU", "compile_batch_ms", batchValues[batch.SelectedIndex]); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
            batchRow.Controls.Add(batchLabel);
            batchRow.Controls.Add(batch);
            shaders.Controls.Add(batchRow);
            batch.Enabled = mode.SelectedIndex == 0;
            mode.SelectedIndexChanged += delegate
            {
                batch.Enabled = mode.SelectedIndex == 0;
                try { WriteSetting("GPU", "compile_mode", mode.SelectedIndex); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };


            // ---- Controls: the controller, keyboard and wheel, drawn (ControlsPage); the controller
            // ports open from it. It saves as you change things.
            var controlsHost = new Panel { Dock = DockStyle.Fill, BackColor = Dark.Panel, Visible = false, Padding = new Padding(46, 34, 46, 24) };
            controls = new ControlsPage(root, ReadIntSetting, WriteSetting, OpenPorts) { Dock = DockStyle.Fill };
            controlsHost.Controls.Add(controls);
            pagesHost.Controls.Add(controlsHost);
            pages["Controls"] = controlsHost;

            // ---- About: what ryGT is, its folders and logging
            // ---- Online: hosting, joining a friend (Online.cs)
            BuildOnlinePage();
            // ---- Account: the online account (Online.cs); nothing asks for one at start
            BuildAccountPage();

            var aboutPage = NewPage("About", "About", "ryGT7 for Windows " + (char)183 + " Gran Turismo 7 (CUSA24769)");
            aboutPage.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(860, 0), ForeColor = Dark.DimText, Tag = "heading", Margin = new Padding(1, 0, 0, 6),
                Text = "ryGT7 converts your own copy of Gran Turismo 7 to run on Windows. " +
                       "It is a fan project, not affiliated with Sony Interactive Entertainment or Polyphony Digital; " +
                       "Gran Turismo is their trademark. You need your own copy of the game."
            });
            var folders = Section(aboutPage, "Folders");
            folders.Controls.Add(OpenRow("The ryGT7 folder", OpenFolder, "Open folder"));
            folders.Controls.Add(OpenRow("Logs (stdout.log, stderr.log, setup.log)", delegate { OpenSubfolder("logs"); }, "Open log folder"));
            folders.Controls.Add(OpenRow("Shaders", delegate { OpenSubfolder("shaders"); }, "Open folder"));
            var logging = Section(aboutPage, "Logging");
            var logLevel = new DarkComboBox { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
            logLevel.Items.AddRange(LogFiles.Levels);
            logLevel.SelectedIndex = LogFiles.LevelIndex(settings.StdoutLevel);
            logLevel.SelectedIndexChanged += delegate
            {
                if (logLevel.SelectedIndex < 0) return;
                settings.StdoutLevel = LogFiles.Levels[logLevel.SelectedIndex];
                try { settings.Save(); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the logging level: " + e.Message); }
            };
            logging.Controls.Add(new SettingRow("stdout.log minimum level", logLevel));
            logging.Controls.Add(Hint("The selected level and below, from the next launch. Previous logs are kept with a timestamp."));

            // The setting helpers build label + drop-down rows; they become full-width rows here.
            Rowify(pagesHost);
            // Rowify replaced the Graphics rows' panels: shows and hides them again (the multiplier).
            refresh();
            Dark.Apply(this);
            Dark.SeeThrough(home);
            log.BackColor = Dark.Field;
            log.ForeColor = Color.FromArgb(180, 186, 196);
            HandleCreated += delegate { Dark.TitleBar(this); };
            Shown += delegate
            {
                ActiveControl = play.Enabled ? (Control)play : gear;
                if (ReadSetting("Launcher", "check_updates", true)) CheckForUpdate(false);
            };
            RefreshState();
            var stateTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            stateTimer.Tick += delegate { if (!busy) RefreshState(); };
            stateTimer.Start();
            FormClosed += delegate { stateTimer.Dispose(); };
        }

        /// Shows the home view (the picture, Play).
        public void ShowHome(bool animate = true)
        {
            if (home.Visible) return;
            Fade(settingsView, delegate { settingsView.Visible = false; home.Visible = true; }, home, animate);
        }

        /// Shows the settings at one of their pages (Game, Display, Graphics, Shaders, Controls, About).
        public void ShowSettings(string page, bool animate = true)
        {
            if (!pages.ContainsKey(page)) page = "Game";
            lastPage = page;
            foreach (var pair in pages) pair.Value.Visible = pair.Key == page;
            foreach (var pair in pageItems) pair.Value.Active = pair.Key == page;
            if (settingsView.Visible) return;
            Fade(home, delegate { home.Visible = false; settingsView.Visible = true; }, settingsView, animate);
        }

        /// For --snapshot: the Controls page's tab (0 controller, 1 keyboard, 2 wheel).
        public void ShowControlsTab(int index)
        {
            controls.ShowTab(index);
        }

        /// For --snapshot: 0 home, 1 the settings (Display), 2 the controls.
        public void ShowPage(int index)
        {
            if (index == 0) ShowHome(false);
            else ShowSettings(index == 2 ? "Controls" : "Display", false);
        }

        bool fading;

        /// Changes views behind the curtain: the old view darkens (120 ms), then the new one
        /// comes up out of the dark (220 ms).
        void Fade(Control from, Action change, Control to, bool animate)
        {
            if (!animate || fading || !IsHandleCreated || !Visible || !from.Visible || ClientSize.Width <= 0 || ClientSize.Height <= 0)
            {
                change();
                return;
            }
            fading = true;
            var before = new Bitmap(ClientSize.Width, ClientSize.Height);
            from.DrawToBitmap(before, new Rectangle(Point.Empty, from.Size));
            curtain.Show(before, 0);
            curtain.Visible = true;
            curtain.BringToFront();
            change();
            Bitmap after = null;
            int start = Environment.TickCount;
            const int Out = 120, In = 220;
            Anim.Run(() =>
            {
                int t = Environment.TickCount - start;
                if (t < Out)
                {
                    curtain.Show(before, t / (float)Out);
                    return true;
                }
                if (after == null)
                {
                    after = new Bitmap(ClientSize.Width, ClientSize.Height);
                    to.DrawToBitmap(after, new Rectangle(Point.Empty, to.Size));
                }
                float k = Anim.Ease((t - Out) / (float)In);
                curtain.Show(after, 1 - k);
                if (k < 1) return true;
                curtain.Visible = false;
                curtain.Show(null, 0);
                before.Dispose();
                after.Dispose();
                fading = false;
                return false;
            });
        }

        /// A settings page: a scrolling column with its title and the line under it.
        TableLayoutPanel NewPage(string name, string title, string subtitle)
        {
            var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Dark.Panel, Visible = false, Padding = new Padding(46, 34, 46, 30) };
            var content = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = new Padding(0) };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            content.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font("Segoe UI Light", 22f), Margin = new Padding(0, 0, 0, 2) });
            content.Controls.Add(new Label { Text = subtitle, AutoSize = true, MaximumSize = new Size(900, 0), ForeColor = Dark.DimText, Tag = "heading", Margin = new Padding(2, 0, 0, 10) });
            page.Controls.Add(content);
            pagesHost.Controls.Add(page);
            pages[name] = page;
            return content;
        }

        /// A group of settings under a small spaced capital heading with a line under it.
        TableLayoutPanel Section(TableLayoutPanel parent, string heading)
        {
            var panel = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 22, 0, 0) };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var label = new Label
            {
                Text = Dark.Spaced(heading.ToUpperInvariant()), AutoSize = false, Dock = DockStyle.Fill, Height = 26, Font = Dark.Caps(8.5f),
                ForeColor = Dark.Heading, Tag = "heading", Margin = new Padding(0), TextAlign = ContentAlignment.TopLeft
            };
            label.Paint += (sender, e) => { using (var line = new Pen(Dark.Line)) e.Graphics.DrawLine(line, 0, label.Height - 1, label.Width, label.Height - 1); };
            panel.Controls.Add(label);
            parent.Controls.Add(panel);
            return panel;
        }

        static Label Hint(string text)
        {
            return new Label { Text = text, AutoSize = true, MaximumSize = new Size(860, 0), ForeColor = Dark.DimText, Tag = "heading", Margin = new Padding(0, 8, 0, 0) };
        }

        /// A row with a button that opens a window or a folder.
        static SettingRow OpenRow(string text, Action action, string button = "Open")
        {
            var open = new Button { Text = button, AutoSize = true };
            open.Click += delegate { action(); };
            return new SettingRow(text, open);
        }

        /// The label + drop-down rows the setting helpers build (in FlowLayoutPanels) become
        /// SettingRows, in the same place.
        static void Rowify(Control root)
        {
            foreach (var child in root.Controls.Cast<Control>().ToList())
            {
                var flow = child as FlowLayoutPanel;
                if (flow != null && flow.Controls.Count == 2 && flow.Controls[0] is Label && flow.Controls[1] is ListControl)
                {
                    var text = flow.Controls[0].Text;
                    var control = flow.Controls[1];
                    control.Width = Math.Max(control.Width, 230);
                    int index = root.Controls.GetChildIndex(flow);
                    flow.Controls.Clear();
                    root.Controls.Remove(flow);
                    var row = new SettingRow(text, control);
                    root.Controls.Add(row);
                    root.Controls.SetChildIndex(row, index);
                    continue;
                }
                Rowify(child);
            }
        }

        /// The ryGT7 logo, launcher\logo.png next to RyGT7.exe (null without it: the title is text).
        Image LoadLogo()
        {
            string path = Path.Combine(root, "launcher", "logo.png");
            try { return File.Exists(path) ? Image.FromStream(new MemoryStream(File.ReadAllBytes(path))) : null; }
            catch { return null; }
        }

        void OpenSubfolder(string name)
        {
            string path = Path.Combine(root, name);
            try { Directory.CreateDirectory(path); } catch { }
            Process.Start("explorer.exe", "\"" + path + "\"");
        }

        void OpenPorts()
        {
            using (var form = new PortsForm(ReadStringSetting("Input", "controller_ports", ""),
                                            ReadStringSetting("General", "player_names", "").Split(','),
                                            (ports, names) =>
                                            {
                                                WriteSetting("Input", "controller_ports", ports);
                                                // Read by the game's user service (libSceUserService).
                                                WriteSetting("General", "player_names",
                                                             string.Join(",", names.Select(n => n.Replace(",", ""))));
                                            }))
            {
                form.ShowDialog(this);
            }
        }

        void OpenFolder()
        {
            Process.Start("explorer.exe", "\"" + root + "\"");
        }

        // ---- The pictures behind the home view

        readonly List<Image> pictures;
        List<Image> LoadBackgrounds()
        {
            var found = new List<Image>();
            string folder = Path.Combine(root, "launcher");
            try
            {
                if (Directory.Exists(folder))
                {
                    var paths = Directory.GetFiles(folder, "background*.*")
                        .Where(p => p.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                        .Where(p => !Path.GetFileNameWithoutExtension(p).EndsWith(".bak", StringComparison.OrdinalIgnoreCase) &&
                                    Path.GetFileName(p).IndexOf("bak", StringComparison.OrdinalIgnoreCase) < 0)
                        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
                    foreach (var path in paths)
                    {
                        try { found.Add(Image.FromStream(new MemoryStream(File.ReadAllBytes(path)))); } catch { }
                    }
                }
            }
            catch { }
            if (found.Count == 0)
            {
                foreach (var path in new[] { Path.Combine(root, "app0", "sce_sys", "pic1.png"), Path.Combine(settings.Game, "sce_sys", "pic1.png") })
                {
                    try { if (File.Exists(path)) { found.Add(Image.FromStream(new MemoryStream(File.ReadAllBytes(path)))); break; } }
                    catch { }
                }
            }
            return found;
        }

        /// The home view's picture (for --snapshot and the controls over it).
        public bool DrawBackdrop(Control control, Graphics g, bool raw)
        {
            return home.Visible && home.DrawBackdrop(control, g, raw);
        }

        /// A file setting: its name, the folder in a field, and a Browse button.
        TextBox AddRow(TableLayoutPanel section, string label, string value)
        {
            var inner = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
            inner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            inner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var field = new Card(Dark.Field) { AutoSize = false, Size = new Size(520, 30), Radius = 3, Edge = Color.Empty, Padding = new Padding(10, 7, 10, 4), Margin = new Padding(0, 0, 8, 0) };
            var box = new TextBox { Text = value, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Margin = new Padding(0) };
            field.Controls.Add(box);
            inner.Controls.Add(field, 0, 0);
            var browse = new Button { Text = "Browse", AutoSize = true, Margin = new Padding(0) };
            browse.Click += delegate
            {
                using (var dialog = new FolderBrowserDialog { Description = label, SelectedPath = box.Text })
                {
                    if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.SelectedPath;
                }
            };
            inner.Controls.Add(browse, 1, 0);
            section.Controls.Add(new SettingRow(label, inner));
            return box;
        }

        string Exe { get { return Path.Combine(root, "GT7.exe"); } }

        string ConfigPath { get { return Path.Combine(root, "user", "custom_configs", "CUSA03220.json"); } }

        /// A shadGT setting in a section of the GT7 config (General: show_splash,
        /// show_fps_counter, discord_rpc_enabled; GPU: full_screen, particle_effects, fsr_enabled, fsr3_enabled, rcas_enabled, dlss_enabled,
        /// framegen_enabled, precompile_shaders, compile_freeze).
        bool ReadSetting(string sectionName, string key, bool fallback)
        {
            try
            {
                var config = LoadConfig();
                object found, value;
                var section = config.TryGetValue(sectionName, out found) ? found as Dictionary<string, object> : null;
                if (section != null && section.TryGetValue(key, out value) && value is bool) return (bool)value;
            }
            catch { }
            return fallback;
        }

        int ReadIntSetting(string sectionName, string key, int fallback)
        {
            try
            {
                var config = LoadConfig();
                object found, value;
                var section = config.TryGetValue(sectionName, out found) ? found as Dictionary<string, object> : null;
                if (section != null && section.TryGetValue(key, out value) && value is int) return (int)value;
            }
            catch { }
            return fallback;
        }

        void WriteSetting(string sectionName, string key, object on)
        {
            var config = LoadConfig();
            object found;
            var section = config.TryGetValue(sectionName, out found) ? found as Dictionary<string, object> : null;
            if (section == null) { section = new Dictionary<string, object>(); config[sectionName] = section; }
            section[key] = on;
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
            AtomicConfig.Write(ConfigPath,
                new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(config));
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

        static int InstalledRamGb()
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx)) };
            if (!GlobalMemoryStatusEx(ref status)) return 32;
            return (int)Math.Round(status.TotalPhys / (1024.0 * 1024 * 1024));
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DevMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public ushort SpecVersion, DriverVersion, Size, DriverExtra;
            public uint Fields;
            public int PositionX, PositionY;
            public uint DisplayOrientation, DisplayFixedOutput;
            public short Color, Duplex, YResolution, TTOption, Collate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
            public ushort LogPixels;
            public uint BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
            public uint IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplaySettings(string device, int mode, ref DevMode devMode);

        class Descending<T> : IComparer<T> where T : IComparable<T>
        {
            public int Compare(T a, T b) { return b.CompareTo(a); }
        }

        /// The primary display's modes (resolution, largest first -> refresh rates in Hz, highest
        /// first) and its current mode.
        static SortedDictionary<long, SortedSet<int>> DisplayModes(out int currentWidth, out int currentHeight, out int currentHz)
        {
            var modes = new SortedDictionary<long, SortedSet<int>>(new Descending<long>());
            var mode = new DevMode { Size = (ushort)Marshal.SizeOf(typeof(DevMode)) };
            for (int i = 0; EnumDisplaySettings(null, i, ref mode); ++i)
            {
                if (mode.BitsPerPel < 24 || mode.PelsWidth < 1280 || mode.PelsHeight < 720) continue;
                long key = ((long)mode.PelsWidth << 32) | mode.PelsHeight;
                SortedSet<int> rates;
                if (!modes.TryGetValue(key, out rates)) modes[key] = rates = new SortedSet<int>(new Descending<int>());
                if (mode.DisplayFrequency > 1) rates.Add((int)mode.DisplayFrequency);
            }
            currentWidth = currentHeight = currentHz = 0;
            if (EnumDisplaySettings(null, -1, ref mode))
            {
                currentWidth = (int)mode.PelsWidth;
                currentHeight = (int)mode.PelsHeight;
                currentHz = (int)mode.DisplayFrequency;
            }
            return modes;
        }

        string ReadStringSetting(string sectionName, string key, string fallback)
        {
            try
            {
                var config = LoadConfig();
                object found, value;
                var section = config.TryGetValue(sectionName, out found) ? found as Dictionary<string, object> : null;
                if (section != null && section.TryGetValue(key, out value) && value is string) return (string)value;
            }
            catch { }
            return fallback;
        }

        static string ModeText(long key)
        {
            return string.Format("{0} x {1}", key >> 32, key & 0xffffffff);
        }

        // Screen: windowed, borderless fullscreen (the desktop's resolution and refresh rate), or
        // exclusive fullscreen at a chosen resolution and refresh rate (GPU.full_screen,
        // full_screen_mode, window_width/height, fullscreen_refresh_rate). The game draws at
        // 1920x1080 and runs at up to 60 fps; FSR or DLSS scale it to the screen, and a higher
        // refresh rate shows frame generation's extra frames.
        void AddScreenSettings(TableLayoutPanel panel)
        {
            int desktopWidth, desktopHeight, desktopHz;
            var modes = DisplayModes(out desktopWidth, out desktopHeight, out desktopHz);

            var screenRow = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 2), WrapContents = false };
            var screen = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, FlatStyle = FlatStyle.Flat };
            screen.Items.AddRange(new object[] { "Windowed", "Borderless fullscreen", "Exclusive fullscreen" });
            bool full = ReadSetting("GPU", "full_screen", false);
            bool exclusive = ReadStringSetting("GPU", "full_screen_mode", "Windowed") == "Fullscreen";
            screen.SelectedIndex = !full ? 0 : exclusive ? 2 : 1;
            screenRow.Controls.Add(new Label { Text = "Screen", AutoSize = false, Width = 196, Height = 20, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) });
            screenRow.Controls.Add(screen);
            panel.Controls.Add(screenRow);

            var resolutionRow = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 2), WrapContents = false };
            var resolution = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, FlatStyle = FlatStyle.Flat };
            var keys = modes.Keys.ToList();
            int width = ReadIntSetting("GPU", "window_width", 0), height = ReadIntSetting("GPU", "window_height", 0);
            if (width < 640 || height < 360)
            {
                width = desktopWidth > 0 ? Math.Min(1920, desktopWidth) : 1920;
                height = desktopHeight > 0 ? Math.Min(1080, desktopHeight) : 1080;
            }
            long wanted = ((long)width << 32) | (uint)height;
            if (!keys.Contains(wanted)) keys.Add(wanted);
            foreach (var key in keys) resolution.Items.Add(ModeText(key));
            resolution.SelectedIndex = keys.IndexOf(wanted);
            resolutionRow.Controls.Add(new Label { Text = "Resolution", AutoSize = false, Width = 196, Height = 20, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) });
            resolutionRow.Controls.Add(resolution);
            panel.Controls.Add(resolutionRow);

            var refreshRow = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 2), WrapContents = false };
            var refresh = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, FlatStyle = FlatStyle.Flat };
            refreshRow.Controls.Add(new Label { Text = "Refresh rate", AutoSize = false, Width = 196, Height = 20, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) });
            refreshRow.Controls.Add(refresh);
            panel.Controls.Add(refreshRow);

            bool loading = true;
            Action<int> fillRates = delegate (int wantedHz)
            {
                refresh.Items.Clear();
                refresh.Items.Add(desktopHz > 0 ? string.Format("Desktop ({0} Hz)", desktopHz) : "Desktop");
                SortedSet<int> rates;
                if (modes.TryGetValue(keys[resolution.SelectedIndex], out rates))
                {
                    foreach (int hz in rates) refresh.Items.Add(hz + " Hz");
                }
                int index = refresh.Items.IndexOf(wantedHz + " Hz");
                refresh.SelectedIndex = wantedHz > 0 && index > 0 ? index : 0;
            };
            Action save = delegate
            {
                if (loading) return;
                try
                {
                    WriteSetting("GPU", "full_screen", screen.SelectedIndex != 0);
                    WriteSetting("GPU", "full_screen_mode", screen.SelectedIndex == 2 ? "Fullscreen" : "Borderless");
                    long key = keys[resolution.SelectedIndex];
                    WriteSetting("GPU", "window_width", (int)(key >> 32));
                    WriteSetting("GPU", "window_height", (int)(key & 0xffffffff));
                    WriteSetting("GPU", "fullscreen_refresh_rate",
                                 refresh.SelectedIndex > 0 ? int.Parse(((string)refresh.SelectedItem).Replace(" Hz", "")) : 0);
                }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
            Action enable = delegate
            {
                // Borderless fullscreen always uses the desktop's mode.
                resolution.Enabled = screen.SelectedIndex != 1;
                refresh.Enabled = screen.SelectedIndex == 2;
            };
            // Ultrawide (GPU.aspect_ratio): the camera widens to this shape and the HUD stays at
            // 16:9 in the middle. A preset also picks its resolution (added to the list when the
            // monitor does not report it, e.g. for a borderless window on another PC's screen).
            var wideRow = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 2), WrapContents = false };
            var wide = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, FlatStyle = FlatStyle.Flat };
            var wideAspects = new List<string> { "16:9", "auto", "2560:1080", "3440:1440", "3840:1600", "5120:1440" };
            var wideSizes = new List<long> { 0, 0, (2560L << 32) | 1080, (3440L << 32) | 1440, (3840L << 32) | 1600, (5120L << 32) | 1440 };
            wide.Items.AddRange(new object[] { "Off (16:9)", "Auto (screen shape)", "21:9 - 2560 x 1080", "21:9 - 3440 x 1440", "24:10 - 3840 x 1600", "32:9 - 5120 x 1440" });
            string storedAspect = ReadStringSetting("GPU", "aspect_ratio", "16:9");
            int wideIndex = wideAspects.IndexOf(storedAspect);
            if (wideIndex < 0)
            {
                wideAspects.Add(storedAspect);
                wideSizes.Add(0);
                wide.Items.Add("Custom (" + storedAspect + ")");
                wideIndex = wideAspects.Count - 1;
            }
            wide.SelectedIndex = wideIndex;
            wideRow.Controls.Add(new Label { Text = "Ultrawide", AutoSize = false, Width = 196, Height = 20, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) });
            wideRow.Controls.Add(wide);
            panel.Controls.Add(wideRow);
            wide.SelectedIndexChanged += delegate
            {
                if (loading) return;
                try
                {
                    WriteSetting("GPU", "aspect_ratio", wideAspects[wide.SelectedIndex]);
                    long size = wideSizes[wide.SelectedIndex];
                    if (size != 0)
                    {
                        if (!keys.Contains(size))
                        {
                            keys.Add(size);
                            resolution.Items.Add(ModeText(size));
                        }
                        resolution.SelectedIndex = keys.IndexOf(size); // saves the resolution too
                    }
                }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };

            fillRates(ReadIntSetting("GPU", "fullscreen_refresh_rate", 0));
            enable();
            screen.SelectedIndexChanged += delegate { enable(); save(); };
            resolution.SelectedIndexChanged += delegate
            {
                if (loading) return;
                loading = true;
                int hz = refresh.SelectedIndex > 0 ? int.Parse(((string)refresh.SelectedItem).Replace(" Hz", "")) : 0;
                fillRates(hz);
                loading = false;
                save();
            };
            refresh.SelectedIndexChanged += delegate { save(); };
            loading = false;
        }

        // System memory (GPU.system_ram_gb): how much the game may use for things it can do
        // without. With 16 GB the driver's pipeline cache file (it grows past 1 GB and is loaded
        // whole, then copied again when saved) is off and fewer freed GPU images are kept for
        // reuse. Tokyo rain race, committed memory: 32 GB setting ~11 GB average / 13.5 GB peak,
        // 16 GB setting ~8.6 / 9.0 GB. Picked from the installed RAM the first time.
        void AddMemorySetting(TableLayoutPanel panel)
        {
            var row = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 2), WrapContents = false };
            var label = new Label { Text = "System memory (RAM)", AutoSize = false, Width = 196, Height = 20, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) };
            var ram = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, FlatStyle = FlatStyle.Flat };
            ram.Items.AddRange(new object[] { "16 GB (minimum)", "32 GB+ (recommended)" });
            int stored = ReadIntSetting("GPU", "system_ram_gb", 0);
            int gb = stored > 0 ? stored : InstalledRamGb();
            ram.SelectedIndex = gb >= 24 ? 1 : 0;
            Action apply = delegate
            {
                bool large = ram.SelectedIndex == 1;
                WriteSetting("GPU", "system_ram_gb", large ? 32 : 16);
                WriteSetting("Vulkan", "pipeline_cache_enabled", large);
                WriteSetting("GPU", "image_recycle_mb", large ? 512 : 128);
            };
            if (stored == 0)
            {
                try { apply(); } catch { }
            }
            ram.SelectedIndexChanged += delegate
            {
                try { apply(); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
            row.Controls.Add(label);
            row.Controls.Add(ram);
            panel.Controls.Add(row);
        }

        /// The largest dedicated video memory of the display adapters, in GB (0 when unknown):
        /// the drivers' HardwareInformation.qwMemorySize (64-bit, unlike WMI's AdapterRAM).
        static int InstalledVramGb()
        {
            long best = 0;
            try
            {
                using (var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                {
                    if (cls == null) return 0;
                    foreach (var name in cls.GetSubKeyNames())
                    {
                        try
                        {
                            using (var adapter = cls.OpenSubKey(name))
                            {
                                object size = adapter == null ? null : adapter.GetValue("HardwareInformation.qwMemorySize");
                                if (size is long) best = Math.Max(best, (long)size);
                            }
                        }
                        catch { } // Properties and other keys can't be opened
                    }
                }
            }
            catch { }
            return (int)Math.Round(best / (1024.0 * 1024 * 1024));
        }

        // Video memory (GPU.vram_budget_mb): how much of the graphics card's memory the renderer
        // plans for. Auto (0) uses the driver's budget, as before. A smaller value makes the
        // renderer free cached textures and render targets it hasn't used lately sooner, leaving
        // the rest of the card to other programs (or a card that shares memory).
        void AddVideoMemorySetting(TableLayoutPanel panel)
        {
            var row = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 2), WrapContents = false };
            var label = new Label { Text = "Video memory (VRAM)", AutoSize = false, Width = 196, Height = 20, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) };
            var vram = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, FlatStyle = FlatStyle.Flat };
            int card = InstalledVramGb();
            var sizes = new List<int> { 0 };
            foreach (int gb in new[] { 2, 3, 4, 6, 8, 10, 12, 16, 20, 24, 32, 48 })
            {
                if (card == 0 ? gb <= 24 : gb <= card) sizes.Add(gb);
            }
            foreach (int gb in sizes)
            {
                vram.Items.Add(gb == 0 ? (card > 0 ? "Auto (" + card + " GB card)" : "Auto")
                                       : gb + " GB" + (gb == card ? " (all)" : ""));
            }
            int stored = ReadIntSetting("GPU", "vram_budget_mb", 0);
            int index = 0;
            for (int i = 1; i < sizes.Count; ++i)
            {
                if (sizes[i] * 1024 <= stored) index = i; // the largest choice not above the stored value
            }
            vram.SelectedIndex = index;
            vram.SelectedIndexChanged += delegate
            {
                try { WriteSetting("GPU", "vram_budget_mb", sizes[vram.SelectedIndex] * 1024); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
            row.Controls.Add(label);
            row.Controls.Add(vram);
            panel.Controls.Add(row);
        }

        /// Asks GitLab for the newest release in the background; never holds up Play.
        void CheckForUpdate(bool manual)
        {
            string installed = Updater.InstalledVersion(root);
            if (installed == null)
            {
                if (manual) MessageBox.Show(this, "This launcher didn't come from a release package (it has no manifest.json), so it can't update itself.");
                return;
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                ReleaseInfo latest = null;
                Exception error = null;
                try { latest = Updater.Latest(); }
                catch (Exception e) { error = e; }
                try { BeginInvoke((Action)delegate { ShowUpdate(installed, latest, error, manual); }); }
                catch (InvalidOperationException) { } // closed meanwhile
            });
        }

        void ShowUpdate(string installed, ReleaseInfo latest, Exception error, bool manual)
        {
            if (latest != null && Updater.IsNewer(latest.Version, installed))
            {
                pendingUpdate = latest;
                home.Notice = "ryGT7 " + latest.Version + " is available (you have " + installed + ")." + Environment.NewLine + latest.Summary(240);
                updateButton.Text = "Update to " + latest.Version;
                updateButton.Visible = true;
                home.PerformLayout();
                home.Invalidate(true);
            }
            else if (manual)
            {
                MessageBox.Show(this, error != null ? "Could not check for updates: " + error.Message
                                                    : "ryGT7 " + installed + " is the newest release.");
            }
        }

        /// Downloads and installs the pending release, then restarts the launcher.
        void InstallUpdate()
        {
            var release = pendingUpdate;
            if (release == null || busy) return;
            if (MessageBox.Show(this, "Update ryGT7 to " + release.Version + "?" + Environment.NewLine + Environment.NewLine +
                                "The launcher downloads " + release.ZipName + ", checks it, replaces its own program files and restarts. " +
                                "Your game, saves, settings and shader caches stay as they are. The old files are kept, so you can " +
                                "roll back from Settings > Display.", "Update ryGT7", MessageBoxButtons.OKCancel) != DialogResult.OK)
                return;
            busy = true;
            updateButton.Enabled = false;
            play.Enabled = false;
            string last = null;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Exception error = null;
                try
                {
                    Updater.Install(root, release, (text, fraction) =>
                    {
                        if (text == last) return;
                        last = text;
                        BeginInvoke((Action)delegate { status.Text = text; });
                    });
                }
                catch (Exception e) { error = e; }
                BeginInvoke((Action)delegate
                {
                    busy = false;
                    if (error == null) { Updater.Restart(root); return; }
                    updateButton.Enabled = true;
                    RefreshState();
                    status.Text = "The update failed: " + error.Message;
                    MessageBox.Show(this, "The update failed, and the launcher's files are as they were." + Environment.NewLine + error.Message);
                });
            });
        }

        // Launcher updates: the check at start, a check now, and a roll back of the last update.
        void AddUpdateSettings(TableLayoutPanel panel)
        {
            // The question asked in the settings: whether the launcher looks for a newer release at start.
            AddOption(panel, "Check for updates", "Launcher", "check_updates", true);
            var row = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 2), WrapContents = false };
            var label = new Label
            {
                Text = "Launcher " + (Updater.InstalledVersion(root) ?? "(not from a release)"), AutoSize = false, Width = 196, Height = 20,
                BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0)
            };
            var check = new Button { Text = "Check now", AutoSize = true, Margin = new Padding(0, 0, 6, 0) };
            check.Click += delegate { CheckForUpdate(true); };
            var last = Updater.LastUpdate(root);
            var rollBack = new Button { Text = last == null ? "Roll back" : "Roll back to " + last.Item2, AutoSize = true, Enabled = last != null, Margin = new Padding(0) };
            rollBack.Click += delegate
            {
                if (busy) return;
                if (MessageBox.Show(this, "Put back ryGT7 " + last.Item2 + " (as before the update to " + last.Item3 + ") and restart the launcher?",
                                    "Roll back", MessageBoxButtons.OKCancel) != DialogResult.OK)
                    return;
                try { Updater.RollBack(root); Updater.Restart(root); }
                catch (Exception e) { MessageBox.Show(this, "Could not roll back: " + e.Message); }
            };
            row.Controls.Add(label);
            row.Controls.Add(check);
            row.Controls.Add(rollBack);
            panel.Controls.Add(row);
        }

        CheckBox AddOption(TableLayoutPanel panel, string text, string sectionName, string key, bool fallback)
        {
            var box = new ToggleRow { Text = text, Checked = ReadSetting(sectionName, key, fallback) };
            box.CheckedChanged += delegate
            {
                try { WriteSetting(sectionName, key, box.Checked); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
            panel.Controls.Add(box);
            return box;
        }

        Dictionary<string, object> LoadConfig()
        {
            if (!File.Exists(ConfigPath)) return new Dictionary<string, object>();
            var parsed = new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(File.ReadAllText(ConfigPath));
            return parsed as Dictionary<string, object> ?? new Dictionary<string, object>();
        }

        /// A GPU section setting from the game's settings file, or null.
        static object GpuSetting(string root, string key)
        {
            try
            {
                string path = Path.Combine(root, "user", "custom_configs", "CUSA03220.json");
                var config = new System.Web.Script.Serialization.JavaScriptSerializer()
                    .DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
                object found, value;
                var gpu = config != null && config.TryGetValue("GPU", out found) ? found as Dictionary<string, object> : null;
                if (gpu != null && gpu.TryGetValue(key, out value)) return value;
            }
            catch { }
            return null;
        }

        /// The shader cache limit in MB for RYGT_DRIVER_CACHE_MAX_MB: -1 unlimited, 0 off, 2 GB unless set.
        static long DriverCacheMaxMb(string root)
        {
            if (GpuSetting(root, "driver_cache_unlimited") is bool && (bool)GpuSetting(root, "driver_cache_unlimited")) return -1;
            object gb = GpuSetting(root, "driver_cache_gb");
            int size = gb is int ? Math.Max(0, Math.Min(100, (int)gb)) : 2;
            return size * 1024L;
        }

        /// GPU.race_fps_lock from the game's settings: the race frame rate, 30 unless 60 was chosen.
        static int RaceFpsLock(string root)
        {
            try
            {
                string path = Path.Combine(root, "user", "custom_configs", "CUSA03220.json");
                var config = new System.Web.Script.Serialization.JavaScriptSerializer()
                    .DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
                object found, value;
                var gpu = config != null && config.TryGetValue("GPU", out found) ? found as Dictionary<string, object> : null;
                if (gpu != null && gpu.TryGetValue("race_fps_lock", out value) && value is int && (int)value == 60)
                    return 60;
            }
            catch { }
            return 30;
        }

        /// GPU.dlssg_frames from the game's settings: DLSS generated frames a game frame (1-5).
        static int DlssgFrames(string root)
        {
            try
            {
                string path = Path.Combine(root, "user", "custom_configs", "CUSA03220.json");
                var config = new System.Web.Script.Serialization.JavaScriptSerializer()
                    .DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
                object found, value;
                var gpu = config != null && config.TryGetValue("GPU", out found) ? found as Dictionary<string, object> : null;
                if (gpu != null && gpu.TryGetValue("dlssg_frames", out value) && value is int)
                    return Math.Max(1, Math.Min(5, (int)value));
            }
            catch { }
            return 1;
        }

        /// A DLL's file version ("310.9.16"), or "?" when it has none.
        static string FileVersion(string path)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                return info.FileMajorPart + "." + info.FileMinorPart + "." + info.FileBuildPart;
            }
            catch (Exception) { return "?"; }
        }

        internal static void InstallDlssLibrary(string source, string directory)
        {
            if (!string.Equals(Path.GetFileName(source), "nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Select the NVIDIA Super Resolution file named nvngx_dlss.dll.");
            using (var reader = new BinaryReader(File.OpenRead(source)))
            {
                if (reader.BaseStream.Length < 64 || reader.ReadUInt16() != 0x5a4d)
                    throw new InvalidDataException("The selected file is not a Windows DLL.");
                reader.BaseStream.Position = 0x3c;
                int pe = reader.ReadInt32();
                if (pe < 64 || pe > reader.BaseStream.Length - 26)
                    throw new InvalidDataException("The selected DLL has an invalid PE header.");
                reader.BaseStream.Position = pe;
                if (reader.ReadUInt32() != 0x4550 || reader.ReadUInt16() != 0x8664)
                    throw new InvalidDataException("DLSS requires a 64-bit Windows DLL.");
                reader.BaseStream.Position = pe + 22;
                if ((reader.ReadUInt16() & 0x2000) == 0 || reader.ReadUInt16() != 0x20b)
                    throw new InvalidDataException("DLSS requires a 64-bit Windows DLL.");
            }
            var version = FileVersionInfo.GetVersionInfo(source);
            if ((version.CompanyName ?? "").IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) < 0 ||
                (version.FileDescription ?? "").IndexOf("DLSS", StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidDataException("The selected file is not an NVIDIA DLSS library.");
            string destination = Path.Combine(directory, "nvngx_dlss.dll");
            if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)) return;
            Directory.CreateDirectory(directory);
            string temporary = destination + ".install-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Copy(source, temporary);
                if (File.Exists(destination))
                    File.Replace(temporary, destination, destination + "." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + ".bak");
                else
                    File.Move(temporary, destination);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        void RefreshState()
        {
            bool ready = File.Exists(Exe) && File.Exists(Path.Combine(root, "app0", "contents", "gt.idx"));
            bool running = IsGameRunning();
            play.Enabled = ready && !busy && !running;
            setup.Enabled = !busy && !running;
            addDlss.Enabled = !busy && !running;
            downloadDlss.Enabled = !busy && !running;
            if (!busy) status.Text = ready ? "Ready. Press Play." : "Not set up yet: open the settings (the gear), pick your game's folders and press Set up.";
            if (running) status.Text = "GT7 is running. Close the game before running Set up.";
        }

        static bool IsGameRunning()
        {
            var games = Process.GetProcessesByName("GT7");
            bool running = games.Length > 0;
            foreach (var game in games) game.Dispose();
            return running;
        }

        readonly object logLock = new object();
        string setupLogPath;

        void Log(string line)
        {
            lock (logLock)
            {
                try
                {
                    if (setupLogPath == null) setupLogPath = LogFiles.Prepare(root, "setup.log");
                    File.AppendAllText(setupLogPath, line + Environment.NewLine);
                }
                catch { }
            }
            if (!IsHandleCreated || IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action<string>(AppendLogText), line); return; }
            AppendLogText(line);
        }

        void AppendLogText(string line)
        {
            if (!IsDisposed) log.AppendText(line + Environment.NewLine);
        }
        void SetStatus(string text)
        {
            Log("== " + text);
            if (!IsHandleCreated) return;
            if (InvokeRequired) { BeginInvoke(new Action<string>(SetStatusText), text); return; }
            SetStatusText(text);
        }

        void SetStatusText(string text)
        {
            status.Text = text;
        }

        /// RyGT7.exe --setup: the same set up without the window, from the folders in RyGT7.ini.
        public int SetupWithoutWindow()
        {
            try { setupLogPath = LogFiles.Prepare(root, "setup.log"); RunSetup(); return 0; }
            catch (Exception e) { Log("ERROR: " + e.Message); return 1; }
        }

        void StartSetup()
        {
            settings.Game = game.Text.Trim();
            settings.Update = update.Text.Trim();
            settings.Modules = modules.Text.Trim();
            settings.Fonts = fonts.Text.Trim();
            try { settings.Save(); } catch { }
            try { setupLogPath = LogFiles.Prepare(root, "setup.log"); }
            catch (Exception e) { MessageBox.Show(this, "Could not prepare setup log: " + e.Message); return; }
            busy = true;
            RefreshState();
            log.Clear();
            var thread = new Thread(delegate ()
            {
                bool ok = false;
                DumpFileException dumpProblem = null;
                try { RunSetup(); ok = true; }
                catch (DumpFileException e) { Log("ERROR: " + e.Message); dumpProblem = e; }
                catch (Exception e) { Log("ERROR: " + e.Message); }
                BeginInvoke(new Action(delegate
                {
                    busy = false;
                    RefreshState();
                    if (ok) status.Text = "Set up finished. Press Play.";
                    else status.Text = "Set up failed; see the log.";
                    if (dumpProblem != null) ShowRedumpHint(dumpProblem);
                }));
            });
            thread.IsBackground = true;
            thread.Start();
        }

        /// Set up couldn't use a game or system module file: the dump needs doing again.
        void ShowRedumpHint(DumpFileException problem)
        {
            MessageBox.Show(this,
                "Set up couldn't use this file:\n" + problem.File + "\n\n" + problem.Message + "\n\n" +
                "Your game dump or system modules (sys_modules) look incomplete or damaged. " +
                "Dump the game and/or the system modules from your PS4 again, then run Set up again.",
                "Set up: redump needed", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void RunSetup()
        {
            if (IsGameRunning())
                throw new Exception("Close GT7 before running Set up.");
            string baseDir = settings.Game, updateDir = settings.Update;
            var layout = new GameLayout(baseDir, updateDir, root);
            settings.Save();
            Log("Gran Turismo 7 (CUSA24769), version " + layout.Version);
            if (layout.Version != "01.00")
                Log("This update is not yet verified. The converter will use its executable without version-specific offsets.");
            foreach (string name in new[] { "libSceVideoOut.prx", "libSceMbus.prx", "libSceTextToSpeech.prx" })
                if (!File.Exists(Path.Combine(root, "libs", name)))
                    throw new Exception("The ryGT7 runtime is incomplete: libs\\" + name + " is missing. Extract the full release archive first.");

            string fontDir, font2Dir;
            FindFonts(settings.Fonts, out fontDir, out font2Dir);
            var firmware = new Dictionary<string, string>();
            foreach (var name in FirmwareModules)
            {
                var found = FindModule(settings.Modules, name);
                if (found == null) throw new DumpFileException(name + ".sprx", "System module " + name + " was not found under " + settings.Modules + ".");
                firmware[name] = found;
            }
            foreach (var name in OptionalFirmwareModules)
            {
                var found = FindModule(settings.Modules, name);
                if (found != null) firmware[name] = found;
                else Log("Note: " + name + " is not in the system modules folder; some related game features may be unavailable.");
            }

            // The bundled community Offline mode patch (only that group of the file is used).
            string offlinePath = OfflinePatchPath;
            OfflinePatch offline = null;
            if (File.Exists(offlinePath))
            {
                offline = OfflinePatch.Load(offlinePath);
                Log("Found the " + OfflinePatch.GroupName + " patch " + offline.Version + " (" + offline.Writes.Count + " lines) in patches\\" + GameLayout.TitleId + ".xml.");
            }

            // 1. The executable and modules to convert, unwrapped to plain ELF.
            SetStatus("Preparing the game files...");
            string work = Path.Combine(root, "convert");
            string workModules = Path.Combine(work, "sce_module");
            if (Directory.Exists(workModules)) Directory.Delete(workModules, true);
            Directory.CreateDirectory(workModules);
            Unwrap(layout.Get("eboot.bin"), Path.Combine(work, "eboot.elf"), offline);
            foreach (var name in new[] { "libc.prx", "libSceFios2.prx" })
            {
                string source = layout.Get("sce_module\\" + name);
                if (source == null) throw new DumpFileException("sce_module\\" + name, "The game's sce_module\\" + name + " is missing.");
                Unwrap(source, Path.Combine(workModules, name));
            }
            foreach (var name in new[] { "libSceSecure.prx", "logiWheel.prx" })
            {
                string source = layout.Get(name);
                if (source != null) Unwrap(source, Path.Combine(workModules, name));
            }
            foreach (var pair in firmware) Unwrap(pair.Value, Path.Combine(workModules, pair.Key + ".prx"));

            // 2. Convert.
            SetStatus("Converting the game for Windows (this takes a while)...");
            string converter = Path.Combine(root, "converter", "ryty.exe");
            if (!File.Exists(converter)) throw new Exception("converter\\ryty.exe is missing from the RyGT folder.");
            string convertedRoot = Path.Combine(work, "runtime-" + Guid.NewGuid().ToString("N"));
            string convertedExe = Path.Combine(convertedRoot, "GT7.exe");
            string convertedModules = Path.Combine(convertedRoot, "app0", "sce_module");
            Directory.CreateDirectory(convertedModules);
            int code = Run(converter, "--windows --ps4 --skip-syscall-check --registry --report \"" +
                Path.Combine(work, "port-report.json") + "\" \"" + Path.Combine(work, "eboot.elf") + "\" \"" + convertedExe + "\"", work);
            if (code != 0 || !File.Exists(convertedExe)) throw new Exception("The converter failed (exit code " + code + "); the previous game executable was preserved. See convert\\convert.log.");
            if (offline != null)
            {
                int removed = offline.Apply(convertedExe, File.ReadAllBytes(Path.Combine(work, "eboot.elf")));
                // The patch writes its dealership catalog to /data/gt7offline.
                Directory.CreateDirectory(Path.Combine(root, "data", "gt7offline"));
                Log("Applied the " + OfflinePatch.GroupName + " patch to GT7.exe" + (removed > 0 ? " (" + removed + " overlapping relocations disabled)." : "."));
            }
            GameExe.Prepare(convertedExe, layout.Get("sce_sys\\icon0.png"));
            Log("Prepared GT7.exe with the game's icon.");

            // 3. app0: the game's files, the update's over the base game's, linked rather than copied.
            SetStatus("Linking the game files...");
            LinkGameFiles(layout, convertedModules);
            File.Copy(convertedExe, Exe, true);
            string registry = Path.ChangeExtension(convertedExe, ".registry.json");
            if (File.Exists(registry)) File.Copy(registry, Path.ChangeExtension(Exe, ".registry.json"), true);
            // The converter's own GT7.exe (and those of earlier Set ups) stay as GT7.exe.converted: started
            // from convert\runtime-*, GT7 found none of the launcher's settings next to it.
            foreach (string runtimeDir in Directory.GetDirectories(work, "runtime-*"))
            {
                string leftover = Path.Combine(runtimeDir, "GT7.exe");
                if (!File.Exists(leftover)) continue;
                string kept = leftover + ".converted";
                if (File.Exists(kept)) File.Delete(kept);
                File.Move(leftover, kept);
            }

            // 4. Fonts.
            SetStatus("Copying the fonts...");
            CopyDirectory(fontDir, Path.Combine(root, "sys", "common", "font"));
            CopyDirectory(font2Dir, Path.Combine(root, "sys", "common", "font2"));

            // GT7's runtime settings and directories; no GT Sport scripts or shader packs.
            ConfigureGT7();
            UsePortableShaderCache();
            // Which Offline mode patch file GT7.exe was built with, so that Play notices a file added,
            // changed or removed later (adding it alone changes nothing until GT7.exe is rebuilt).
            File.WriteAllText(OfflineStampPath, OfflinePatchState());

            Log("Set up finished.");
        }

        string OfflinePatchPath { get { return Path.Combine(root, "patches", GameLayout.TitleId + ".xml"); } }
        string OfflineStampPath { get { return Path.Combine(root, "convert", "offline-patch.txt"); } }

        // The SHA-256 of patches\CUSA24769.xml, or "none" without one.
        string OfflinePatchState()
        {
            return File.Exists(OfflinePatchPath) ? GameLayout.Sha256(File.ReadAllBytes(OfflinePatchPath)) : "none";
        }

        // Set up must run again when the Offline mode patch file differs from the one GT7.exe was built
        // with. A folder set up before this check has no record: it was built without the file unless
        // the dealership catalog folder exists.
        bool OfflinePatchChanged()
        {
            string state = OfflinePatchState();
            if (File.Exists(OfflineStampPath)) return File.ReadAllText(OfflineStampPath).Trim() != state;
            bool applied = Directory.Exists(Path.Combine(root, "data", "gt7offline"));
            return (state != "none") != applied;
        }

        static void FindFonts(string selected, out string font, out string font2)
        {
            if (!Directory.Exists(selected)) throw new Exception("Pick the fonts folder (the one holding font and font2).");
            if (Directory.Exists(Path.Combine(selected, "font")) && Directory.Exists(Path.Combine(selected, "font2")))
            {
                font = Path.Combine(selected, "font");
                font2 = Path.Combine(selected, "font2");
                return;
            }
            var parent = Directory.GetParent(selected);
            if (parent != null && Path.GetFileName(selected).Equals("font", StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(Path.Combine(parent.FullName, "font2")))
            {
                font = selected;
                font2 = Path.Combine(parent.FullName, "font2");
                return;
            }
            throw new Exception("The fonts folder needs font and font2 folders inside it.");
        }

        static string FindModule(string folder, string name)
        {
            if (!Directory.Exists(folder)) throw new Exception("Pick the system modules folder.");
            foreach (var extension in new[] { ".sprx", ".prx", ".elf" })
            {
                var hit = Directory.EnumerateFiles(folder, name + extension, SearchOption.AllDirectories).FirstOrDefault();
                if (hit != null) return hit;
            }
            return null;
        }

        static string FirstExisting(params string[] paths)
        {
            return paths.FirstOrDefault(File.Exists);
        }

        void Unwrap(string source, string destination, OfflinePatch offline = null)
        {
            var bytes = File.ReadAllBytes(source);
            byte[] elf;
            try { elf = Self.RelroToLoad(Self.ToElf(bytes, Path.GetFileName(source))); }
            catch (DumpFileException e) { throw new DumpFileException(source, e.Message); }
            if (Path.GetFileName(source).Equals("eboot.bin", StringComparison.OrdinalIgnoreCase) && GameLayout.PatchKnownExecutable(elf))
                Log("Applied the verified CUSA24769 1.00 conversion fix.");
            if (offline != null)
            {
                elf = offline.ExtendLayout(elf);
                Log("Extended the executable's segments for the " + OfflinePatch.GroupName + " patch.");
            }
            File.WriteAllBytes(destination, elf);
            Log("Prepared " + Path.GetFileName(destination) + " from " + source);
        }

        int Run(string exe, string arguments, string directory)
        {
            Log("> " + Path.GetFileName(exe) + " " + arguments);
            var info = new ProcessStartInfo(exe, arguments)
            {
                WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var process = new Process { StartInfo = info })
            {
                var lines = new List<string>();
                DataReceivedEventHandler collect = delegate (object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (lines) lines.Add(e.Data);
                    if (e.Data.StartsWith("FAIL") || e.Data.StartsWith("error") || e.Data.Contains("Exception")) Log(e.Data);
                };
                process.OutputDataReceived += collect;
                process.ErrorDataReceived += collect;
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                lock (lines) File.WriteAllLines(Path.Combine(directory, "convert.log"), lines);
                Log("Converter finished with exit code " + process.ExitCode + " (full output in convert\\convert.log).");
                return process.ExitCode;
            }
        }

        void LinkGameFiles(GameLayout layout, string convertedModules)
        {
            string app0 = Path.Combine(root, "app0");
            string stage = Path.Combine(root, "app0.setup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            var unixLinks = new Dictionary<string, string>();
            int linked = 0;
            foreach (var pair in layout.Files)
            {
                if (pair.Key.Equals("eboot.bin", StringComparison.OrdinalIgnoreCase) ||
                    pair.Key.StartsWith("sce_module\\", StringComparison.OrdinalIgnoreCase)) continue;
                string target = Path.Combine(stage, pair.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                if (Wine.Detected) unixLinks[target] = pair.Value;
                else if (!Native.CreateHardLink(target, pair.Value, IntPtr.Zero) &&
                         !Native.CreateSymbolicLink(target, pair.Value, 2))
                    throw new Exception("Could not link " + pair.Value + ". Keep ryGT7 on the game's drive or enable Windows Developer Mode.");
                linked++;
            }
            if (Wine.Detected) Wine.Link(unixLinks);
            foreach (var pair in layout.Files)
            {
                if (pair.Key.Equals("eboot.bin", StringComparison.OrdinalIgnoreCase) ||
                    pair.Key.StartsWith("sce_module\\", StringComparison.OrdinalIgnoreCase)) continue;
                string problem = ReadProblem(Path.Combine(stage, pair.Key), new FileInfo(pair.Value).Length);
                if (problem != null) throw new Exception("Linked " + pair.Key + " " + problem + ".");
            }
            // The converter writes its modules under app0. Copy those small converted files,
            // never the source PRX files, into the new data tree before swapping directories.
            CopyDirectory(convertedModules, Path.Combine(stage, "sce_module"));
            string previous = app0 + ".previous-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff");
            if (Directory.Exists(app0)) Directory.Move(app0, previous);
            try { Directory.Move(stage, app0); }
            catch { if (Directory.Exists(previous)) Directory.Move(previous, app0); throw; }
            Log("Linked " + linked + " game files. Update files override matching base files; other base files are preserved.");
        }

        void ConfigureGT7()
        {
            WriteSetting("GPU", "native_renderer", true);
            WriteSetting("GPU", "readbacks_mode", 2);
            if (!LoadConfig().ContainsKey("General")) WriteSetting("General", "show_splash", true);
            foreach (string folder in new[] { "_sd/local-data", "_sd/sce_sdmemory", "download0/APP_DATA", "temp", "temp0", "logs", "patches", "shaders" })
                Directory.CreateDirectory(Path.Combine(root, folder));
            for (int i = 1000; i < 1004; i++)
                foreach (string folder in new[] { "savedata", "trophy", "inputs" })
                    Directory.CreateDirectory(Path.Combine(root, "user", "home", i.ToString(), folder));
        }

        void EnsureGT7Defaults()
        {
            DefaultSetting("GPU", "native_renderer", true);
            DefaultSetting("GPU", "readbacks_mode", 2);
            DefaultSetting("GPU", "compile_mode", 2);
            DefaultSetting("GPU", "precompile_shaders", false);
            DefaultSetting("General", "show_splash", true);
            DefaultSetting("General", "player_names", "Player 1,Player 2");
            DefaultSetting("Input", "background_controller_input", true);
        }

        void DefaultSetting(string section, string key, object value)
        {
            var config = LoadConfig();
            object stored;
            var values = config.TryGetValue(section, out stored) ? stored as Dictionary<string, object> : null;
            if (values == null || !values.ContainsKey(key)) WriteSetting(section, key, value);
        }

        /// Why a linked game file can't be used (missing, unreadable or a different size), or null.
        /// Opening it follows a symbolic link, unlike FileInfo.
        static string ReadProblem(string path, long expectedLength)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (expectedLength >= 0 && stream.Length != expectedLength)
                        return "has " + stream.Length + " bytes instead of " + expectedLength;
                }
                return null;
            }
            catch (FileNotFoundException) { return "is missing"; }
            catch (Exception e) { return "can't be read (" + e.Message.TrimEnd('.') + ")"; }
        }

        static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
            foreach (var dir in Directory.GetDirectories(source))
                CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }

        // Everything RyGT makes stays in the RyGT folder (portable), and the shaders are in plain sight
        // in <RyGT>\shaders (the renderer's cache folder), never in AppData. Older folders kept them in
        // user\cache: a real folder there is moved to shaders once. Builds from 2026-10-18 to 10-20
        // made user\cache a link to one AppData folder shared by every RyGT folder; its shaders are
        // copied instead (the AppData folder stays for other RyGT folders still linked to it).
        static string OldSharedShaderCache
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    "RyGT", "shader-cache");
            }
        }

        string ShaderFolder { get { return Path.Combine(root, "shaders"); } }

        bool OldShaderCacheExists()
        {
            return Directory.Exists(Path.Combine(root, "user", "cache"));
        }

        void UsePortableShaderCache()
        {
            string old = Path.Combine(root, "user", "cache");
            try
            {
                var info = new DirectoryInfo(old);
                if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    // File by file, skipping what an earlier run (perhaps closed halfway) already
                    // copied, so a second start picks up where the first stopped. A link to a
                    // shared folder that is gone is just removed.
                    if (Directory.Exists(old))
                    {
                        Log("Copying the shaders from the shared AppData folder into the shaders folder (once)...");
                        int copied = 0;
                        CopyShaders(old, ShaderFolder, ref copied);
                        Log("Copied " + copied + " shader files.");
                    }
                    Directory.Delete(old, false); // the link only, not the shared folder
                    Log("Shaders are kept in the shaders folder now. " + OldSharedShaderCache +
                        " can be deleted once no other RyGT folder uses it.");
                }
                else if (info.Exists)
                {
                    Log("Moving the shaders from user\\cache to the shaders folder (once)...");
                    if (!Directory.Exists(ShaderFolder)) Directory.Move(old, ShaderFolder);
                    else { MergeShaderCache(old, ShaderFolder, true); Directory.Delete(old, true); }
                }
                Directory.CreateDirectory(ShaderFolder);
            }
            catch (Exception e)
            {
                Log("Note: shader folder: " + e.Message);
                try { Directory.CreateDirectory(ShaderFolder); } catch { }
            }
        }

        // Copies a shader folder's files that the target doesn't have yet, with progress in the status
        // line. The driver's pipeline cache file (several GB, unused since FIX-165) stays behind.
        void CopyShaders(string source, string target, ref int copied)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source))
            {
                string name = Path.GetFileName(file);
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                    name.IndexOf(".vkpipelinecache", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                string destination = Path.Combine(target, name);
                if (File.Exists(destination)) continue;
                File.Copy(file, destination + ".part", true);
                File.Move(destination + ".part", destination);
                if (++copied % 2000 == 0) SetStatus("Copying your shaders into the shaders folder (once): " + copied + " files...");
            }
            foreach (var dir in Directory.GetDirectories(source))
                CopyShaders(dir, Path.Combine(target, Path.GetFileName(dir)), ref copied);
        }

        static void MergeShaderCache(string source, string target, bool move = true)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source))
            {
                string name = Path.GetFileName(file);
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                string destination = Path.Combine(target, name);
                if (!File.Exists(destination))
                {
                    if (move) File.Move(file, destination); else File.Copy(file, destination);
                }
                else if (name.EndsWith(".vkpipelinecache", StringComparison.OrdinalIgnoreCase) &&
                         new FileInfo(file).Length > new FileInfo(destination).Length)
                    File.Copy(file, destination, true);
            }
            foreach (var dir in Directory.GetDirectories(source))
                MergeShaderCache(dir, Path.Combine(target, Path.GetFileName(dir)), move);
        }

        void Play()
        {
            if (IsGameRunning()) { RefreshState(); return; }
            if (!File.Exists(Exe)) { MessageBox.Show(this, "Press Set up first."); return; }
            // Without its data index GT7 closes right after the splash screen.
            string dataProblem = ReadProblem(Path.Combine(root, "app0", "contents", "gt.idx"), -1);
            if (dataProblem != null)
            {
                MessageBox.Show(this, "GT7's data index app0\\contents\\gt.idx " + dataProblem + ", so the game can't start.\n\n" +
                                "Press Set up again. If the RyGT folder is on a different drive than the game, move it " +
                                "to the game's drive first.");
                return;
            }
            // The one-time steps below can take minutes (copying shaders, preparing them), so they run
            // beside the window, which keeps showing their progress.
            busy = true;
            RefreshState();
            var thread = new Thread(delegate ()
            {
                bool prepared = false;
                DumpFileException playDumpProblem = null;
                try
                {
                    if (OfflinePatchChanged())
                    {
                        // patches\CUSA24769.xml was added, changed or removed after Set up: GT7.exe is
                        // rebuilt with it first (the dealerships said "Failed to get sales list" otherwise).
                        SetStatus("The Offline mode patch file changed since Set up; running Set up again...");
                        Log("patches\\" + GameLayout.TitleId + ".xml changed since Set up; running Set up again.");
                        setupLogPath = LogFiles.Prepare(root, "setup.log");
                        RunSetup();
                    }
                    if (OldShaderCacheExists()) SetStatus("Moving your shaders to the shaders folder (once)...");
                    UsePortableShaderCache();
                    ConfigureGT7();
                    settings.Save();
                    prepared = true;
                }
                catch (DumpFileException e) { Log("ERROR: " + e.Message); playDumpProblem = e; }
                catch (Exception e) { Log("ERROR: " + e.Message); }
                BeginInvoke(new Action(delegate
                {
                    busy = false;
                    RefreshState();
                    if (playDumpProblem != null) ShowRedumpHint(playDumpProblem);
                    if (prepared)
                    {
                        StartGame(root);
                        status.Text = "GT7 is starting. Logs: logs\\stdout.log and logs\\stderr.log.";
                    }
                    else status.Text = "Could not prepare GT7. See the log.";
                }));
            });
            thread.IsBackground = true;
            thread.Start();
        }

        /// Starts GT7.exe with session log rotation and waits for GPU recovery if Vulkan loses
        /// its device.
        static void StartGame(string root)
        {
            string exe = Path.Combine(root, "GT7.exe");
            // Folders set up by an earlier RyGT7.exe get the icon and the Windows subsystem now.
            try { if (!GameExe.IsPrepared(exe)) GameExe.Prepare(exe); } catch { }
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool recoveredOnce = false;
                while (true)
                {
                    string logDirectory;
                    try
                    {
                        logDirectory = LogFiles.PrepareRuntimeLogs(root);
                    }
                    catch (Exception e)
                    {
                        AppendSetupLog(root, "Could not prepare game logs: " + e.Message);
                        return;
                    }

                    Process started;
                    try
                    {
                        var start = new ProcessStartInfo(exe)
                        { WorkingDirectory = root, UseShellExecute = false };
                        start.EnvironmentVariables["RYGT_LOG_DIRECTORY"] = logDirectory;
                        start.EnvironmentVariables["RYTY_INTEROP_BASE"] = "8000000000";
                        // PERF-161: GT7's per-frame memory (0x200000000-0x208000000), which the CPU rewrites
                        // every frame and a few compute passes write too, stays in guest memory: kept in a
                        // renderer region, each CPU write faulted (about 60,000 a second) and was copied in.
                        start.EnvironmentVariables["RYGT_NATIVE_INPLACE_RANGES"] = "1020000000+1de000,1020235000+1dd000,1020407000+b000,200000000+8000000";
                        // FIX-213: GT7 reads back what the GPU writes into its CPU pools from 0xe000000000
                        // up (GT Sport's are above 0xf000000000, the renderer's default); kept in a
                        // renderer region, the CPU saw stale data and culled the whole rear-view mirror.
                        start.EnvironmentVariables["RYGT_NATIVE_INPLACE_FROM"] = "e000000000";
                        start.EnvironmentVariables["SHADGT_FPS_LOCK"] = RaceFpsLock(root).ToString();
                        // Shader cache size and the AMD graphics fix from the Graphics page.
                        start.EnvironmentVariables["RYGT_DRIVER_CACHE_MAX_MB"] = DriverCacheMaxMb(root).ToString();
                        start.EnvironmentVariables["RYGT_AMD_SINGLE_SAMPLE_BARYCENTRIC"] =
                            GpuSetting(root, "amd_graphics_fix") is bool && (bool)GpuSetting(root, "amd_graphics_fix") ? "1" : "0";
                        // PERF-GT7-111/117 from the Graphics page: each shader translation leaves a record
                        // (shaders\<serial>.manifest) and its code (shaders\<serial>.capture, this PC's only);
                        // at start, the ones the shader store lacks (after a renderer update) are translated
                        // again in the background, so the races after an update start as warm ones.
                        // Only when checked (like the compile pack below): a launcher script that
                        // sets them itself keeps its setting.
                        if (GpuSetting(root, "shader_pretranslate") is bool && (bool)GpuSetting(root, "shader_pretranslate"))
                        {
                            start.EnvironmentVariables["RYGT_MANIFEST_RECORD"] = "1";
                            start.EnvironmentVariables["RYGT_MANIFEST_PRETRANSLATE"] = "1";
                        }
                        // PERF-GT7-108 from the Graphics page: the pipelines this PC's driver cache lacks (after
                        // a driver update) are compiled at start from the recipes kept in the compile pack.
                        // Only when checked: Jouer-Compile-Pack.cmd sets it itself before starting this launcher.
                        if (GpuSetting(root, "compile_pack") is bool && (bool)GpuSetting(root, "compile_pack"))
                            start.EnvironmentVariables["RYGT_COMPILE_PACK"] = "1";
                        // DLSS frame multiplier from the Graphics page (generated frames, 1-5).
                        start.EnvironmentVariables["RYGT_DLSSG_FRAMES"] = DlssgFrames(root).ToString();
                        // Upscaling quality from the Graphics page (the size DLSS and FSR 3.1 scale to).
                        start.EnvironmentVariables["RYGT_UPSCALE_QUALITY"] = GpuSetting(root, "upscale_quality") as string ?? "auto";
                        // Texture sharpness from the Graphics page (detail levels added to every sampler's bias).
                        start.EnvironmentVariables["RYGT_TEXTURE_LOD_BIAS"] = GpuSetting(root, "texture_lod_bias") as string ?? "0";
                        started = Process.Start(start);
                    }
                    catch (Exception e)
                    {
                        AppendSetupLog(root, "Could not start GT7: " + e.Message);
                        return;
                    }
                    if (started == null) return;
                    using (Process game = started) game.WaitForExit();

                    if (recoveredOnce || !RendererReportedDeviceLoss(logDirectory)) return;
                    recoveredOnce = true;
                    AppendSetupLog(root, "Vulkan lost the GPU device. Waiting for Windows to restore the graphics device before restarting GT7.");
                    if (!WaitForGraphicsDevice(TimeSpan.FromMinutes(2)))
                    {
                        AppendSetupLog(root, "The graphics device did not recover within 2 minutes; GT7 was not restarted.");
                        return;
                    }
                    AppendSetupLog(root, "The graphics device is responding again. Restarting GT7 once; the interrupted session cannot be resumed.");
                }
            });
        }

        static bool RendererReportedDeviceLoss(string logDirectory)
        {
            string path = Path.Combine(logDirectory, "stdout.log");
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    int count = (int)Math.Min(stream.Length, 1024 * 1024);
                    stream.Seek(-count, SeekOrigin.End);
                    byte[] bytes = new byte[count];
                    int read = 0;
                    while (read < bytes.Length)
                    {
                        int n = stream.Read(bytes, read, bytes.Length - read);
                        if (n == 0) break;
                        read += n;
                    }
                    string tail = Encoding.UTF8.GetString(bytes, 0, read);
                    return tail.IndexOf("ErrorDeviceLost", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           tail.IndexOf("Device lost", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        static bool WaitForGraphicsDevice(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                IntPtr device = IntPtr.Zero, context = IntPtr.Zero;
                try
                {
                    // A fresh hardware D3D11 device is a small probe that the Windows display
                    // driver has recovered. Driver type 1 is HARDWARE; SDK version is 7.
                    int featureLevel;
                    int result = Native.D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0,
                        IntPtr.Zero, 0, 7, out device, out featureLevel, out context);
                    if (result >= 0) return true;
                }
                catch { }
                finally
                {
                    if (context != IntPtr.Zero) Marshal.Release(context);
                    if (device != IntPtr.Zero) Marshal.Release(device);
                }
                Thread.Sleep(2000);
            }
            return false;
        }

        static void AppendSetupLog(string root, string line)
        {
            try { File.AppendAllText(Path.Combine(root, "setup.log"), line + Environment.NewLine); }
            catch { }
        }

        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // Portable: the tools' work folders (and anything else asking for a temporary folder) go
            // into the RyGT folder instead of AppData\Local\Temp; the tools delete theirs when done.
            try
            {
                string temp = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp");
                Directory.CreateDirectory(temp);
                Environment.SetEnvironmentVariable("TMP", temp);
                Environment.SetEnvironmentVariable("TEMP", temp);
            }
            catch { }
            if (args.Contains("--setup")) return new LoaderForm().SetupWithoutWindow();
            if (args.Length == 2 && args[0] == "--prepare")
            {
                // Gives an already converted GT7.exe the ryGT icon and the Windows subsystem.
                try { GameExe.Prepare(Path.GetFullPath(args[1])); return 0; }
                catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
            }
            if ((args.Length == 2 || args.Length == 3) && args[0] == "--snapshot")
            {
                // Renders the window's contents to a PNG (for checking the layout); a third argument
                // picks the view: home, settings or controls, or a settings page (game, display,
                // graphics, shaders, about), or the Controls page's keyboard or wheel tab.
                using (var form = new LoaderForm())
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(-4000, -4000);
                    form.ShowInTaskbar = false;
                    form.Show();
                    if (args.Length == 3)
                    {
                        int known = Array.IndexOf(new[] { "home", "settings", "controls" }, args[2]);
                        int tab = Array.IndexOf(new[] { "keyboard", "wheel" }, args[2]);
                        if (known >= 0) form.ShowPage(known);
                        else if (tab >= 0) { form.ShowPage(2); form.ShowControlsTab(tab + 1); }
                        else form.ShowSettings(char.ToUpperInvariant(args[2][0]) + args[2].Substring(1), false); // game, graphics, shaders, online, about
                    }
                    Application.DoEvents();
                    // The client area only: each part of the window drawn where it sits.
                    using (var image = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
                    {
                        using (var g = Graphics.FromImage(image)) { g.Clear(Dark.Back); form.DrawBackdrop(form, g, false); }
                        for (int i = form.Controls.Count - 1; i >= 0; --i)
                        {
                            var part = form.Controls[i];
                            if (part.Visible) part.DrawToBitmap(image, part.Bounds);
                        }
                        image.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                    }
                    form.Close();
                }
                return 0;
            }
            if (args.Length == 2 && args[0] == "--snapshot-previews")
            {
                // The controller preview's layouts side by side as a PNG, some controls pressed.
                var kinds = new[] { PadKind.DualShock4, PadKind.DualSense, PadKind.Xbox, PadKind.Generic };
                using (var image = new Bitmap(420 * kinds.Length, 290))
                {
                    for (int i = 0; i < kinds.Length; ++i)
                    {
                        using (var preview = new ControllerPreview
                        {
                            Kind = kinds[i], Connected = i != 3, Caption = kinds[i].ToString(), Size = new Size(420, 290),
                            Cross = true, R1 = true, PadLeft = true, LX = -0.8f, RY = 0.6f, L2 = 0.5f, R2 = 1f
                        })
                        using (var tile = new Bitmap(420, 290))
                        {
                            preview.DrawToBitmap(tile, new Rectangle(0, 0, 420, 290));
                            using (var g = Graphics.FromImage(image)) g.DrawImage(tile, 420 * i, 0);
                        }
                    }
                    image.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                }
                return 0;
            }
            if (args.Length == 2 && args[0] == "--snapshot-ports")
            {
                // The Controller ports window as a PNG (nothing is saved).
                using (Form form = new PortsForm("", null, (ports, names) => { }))
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(-4000, -4000);
                    form.ShowInTaskbar = false;
                    form.Show();
                    Application.DoEvents();
                    var settle = DateTime.Now.AddMilliseconds(200);
                    while (DateTime.Now < settle) { Application.DoEvents(); Thread.Sleep(20); }
                    using (var image = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
                    {
                        using (var g = Graphics.FromImage(image)) g.Clear(Dark.Back);
                        for (int i = form.Controls.Count - 1; i >= 0; --i)
                        {
                            var part = form.Controls[i];
                            part.DrawToBitmap(image, part.Bounds);
                        }
                        image.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                    }
                    form.Close();
                }
                return 0;
            }
            Application.Run(new LoaderForm());
            return 0;
        }
    }
}
