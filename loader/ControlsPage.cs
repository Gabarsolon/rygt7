// RyGT loader: the Controls page (like GT4Recomp's). CONTROLLER, KEYBOARD and WHEEL tabs, each
// a drawing of the controller or the wheel and pedals: click a part to see what it is on your
// controller, keyboard or wheel, click it again (or Rebind) to change it. Changes save at once:
// the bindings to user\input_config\default.ini (Bindings), the wheel to ryty-input.ini and
// ryty-wheel.ini (Wheel).
// SPDX-License-Identifier: GPL-2.0-only

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RyGT
{
    /// Names for controller inputs and keys, as the page shows them.
    static class InputNames
    {
        /// A controller input ("cross", "axis_left_x") the way an Xbox or a PlayStation controller calls it.
        public static string Pad(string input, bool xbox)
        {
            switch (input)
            {
                case null: return "None";
                case "cross": return xbox ? "A" : "Cross";
                case "circle": return xbox ? "B" : "Circle";
                case "square": return xbox ? "X" : "Square";
                case "triangle": return xbox ? "Y" : "Triangle";
                case "l1": return xbox ? "LB" : "L1";
                case "r1": return xbox ? "RB" : "R1";
                case "l2": return xbox ? "LT" : "L2";
                case "r2": return xbox ? "RT" : "R2";
                case "l3": return xbox ? "LS click" : "L3";
                case "r3": return xbox ? "RS click" : "R3";
                case "options": return xbox ? "Menu" : "Options";
                case "back": return xbox ? "View" : "Share";
                case "pad_up": return "D-pad up";
                case "pad_down": return "D-pad down";
                case "pad_left": return "D-pad left";
                case "pad_right": return "D-pad right";
                case "lpaddle_high": return "Paddle 1";
                case "rpaddle_high": return "Paddle 2";
                case "lpaddle_low": return "Paddle 3";
                case "rpaddle_low": return "Paddle 4";
                case "qam": return xbox ? "Guide" : "PS button";
                case "axis_left_x": return "Left stick " + (char)0x2194;
                case "axis_left_y": return "Left stick " + (char)0x2195;
                case "axis_right_x": return "Right stick " + (char)0x2194;
                case "axis_right_y": return "Right stick " + (char)0x2195;
            }
            return input;
        }

        /// The short form for the drawing's labels ("A", "LT", "↑", "LS ↔").
        public static string PadShort(string input, bool xbox)
        {
            switch (input)
            {
                case null: return "-";
                case "l3": return xbox ? "LS" : "L3";
                case "r3": return xbox ? "RS" : "R3";
                case "pad_up": return ((char)0x2191).ToString();
                case "pad_down": return ((char)0x2193).ToString();
                case "pad_left": return ((char)0x2190).ToString();
                case "pad_right": return ((char)0x2192).ToString();
                case "lpaddle_high": return "P1";
                case "rpaddle_high": return "P2";
                case "lpaddle_low": return "P3";
                case "rpaddle_low": return "P4";
                case "qam": return xbox ? "Guide" : "PS";
                case "axis_left_x": return "LS " + (char)0x2194;
                case "axis_left_y": return "LS " + (char)0x2195;
                case "axis_right_x": return "RS " + (char)0x2194;
                case "axis_right_y": return "RS " + (char)0x2195;
            }
            return Pad(input, xbox);
        }

        /// A key ("kp2", "lshift") as it is printed on the keyboard ("Num 2", "Left Shift").
        public static string Key(string key)
        {
            if (key == null) return "None";
            if (key.Length == 1) return key.ToUpperInvariant();
            if (key.StartsWith("kp") && key.Length == 3) return "Num " + key[2];
            if (key.Length >= 2 && key[0] == 'f' && char.IsDigit(key[1])) return key.ToUpperInvariant();
            switch (key)
            {
                case "kpperiod": return "Num .";
                case "kpslash": return "Num /";
                case "space": return "Space";
                case "enter": return "Enter";
                case "escape": return "Esc";
                case "backspace": return "Backspace";
                case "tab": return "Tab";
                case "capslock": return "Caps Lock";
                case "lshift": return "Left Shift";
                case "rshift": return "Right Shift";
                case "lctrl": return "Left Ctrl";
                case "rctrl": return "Right Ctrl";
                case "lalt": return "Left Alt";
                case "ralt": return "Right Alt";
                case "up": return ((char)0x2191).ToString();
                case "down": return ((char)0x2193).ToString();
                case "left": return ((char)0x2190).ToString();
                case "right": return ((char)0x2192).ToString();
                case "pgup": return "Page Up";
                case "pgdown": return "Page Down";
                case "grave": return "`";
                case "minus": return "-";
                case "equals": return "=";
                case "lbracket": return "[";
                case "rbracket": return "]";
                case "backslash": return "\\";
                case "semicolon": return ";";
                case "apostrophe": return "'";
                case "comma": return ",";
                case "period": return ".";
                case "slash": return "/";
            }
            return char.ToUpperInvariant(key[0]) + key.Substring(1);
        }

        /// The short form for the drawing's labels.
        public static string KeyShort(string key)
        {
            if (key == null) return "-";
            switch (key)
            {
                case "lshift": return "L Shift";
                case "rshift": return "R Shift";
                case "lctrl": return "L Ctrl";
                case "rctrl": return "R Ctrl";
                case "lalt": return "L Alt";
                case "ralt": return "R Alt";
                case "capslock": return "Caps";
                case "backspace": return "Bksp";
                case "pgup": return "PgUp";
                case "pgdown": return "PgDn";
                case "insert": return "Ins";
                case "delete": return "Del";
            }
            return Key(key);
        }

        [DllImport("user32.dll")]
        static extern short GetKeyState(int key);

        static bool Down(int key) { return (GetKeyState(key) & 0x8000) != 0; }

        /// The bindings file's name for a key pressed in the window, or null for one it cannot bind.
        public static string FromKeys(Keys keys)
        {
            var code = keys & System.Windows.Forms.Keys.KeyCode;
            if (code >= System.Windows.Forms.Keys.A && code <= System.Windows.Forms.Keys.Z) return ((char)('a' + (code - System.Windows.Forms.Keys.A))).ToString();
            if (code >= System.Windows.Forms.Keys.D0 && code <= System.Windows.Forms.Keys.D9) return ((char)('0' + (code - System.Windows.Forms.Keys.D0))).ToString();
            if (code >= System.Windows.Forms.Keys.NumPad0 && code <= System.Windows.Forms.Keys.NumPad9) return "kp" + (char)('0' + (code - System.Windows.Forms.Keys.NumPad0));
            if (code >= System.Windows.Forms.Keys.F1 && code <= System.Windows.Forms.Keys.F7) return "f" + (1 + (code - System.Windows.Forms.Keys.F1));
            switch (code)
            {
                case System.Windows.Forms.Keys.Decimal: return "kpperiod";
                case System.Windows.Forms.Keys.Divide: return "kpslash";
                case System.Windows.Forms.Keys.Space: return "space";
                case System.Windows.Forms.Keys.Return: return "enter";
                case System.Windows.Forms.Keys.Back: return "backspace";
                case System.Windows.Forms.Keys.Tab: return "tab";
                case System.Windows.Forms.Keys.CapsLock: return "capslock";
                case System.Windows.Forms.Keys.ShiftKey: return Down(0xA1) ? "rshift" : "lshift";
                case System.Windows.Forms.Keys.ControlKey: return Down(0xA3) ? "rctrl" : "lctrl";
                case System.Windows.Forms.Keys.Menu: return Down(0xA5) ? "ralt" : "lalt";
                case System.Windows.Forms.Keys.Up: return "up";
                case System.Windows.Forms.Keys.Down: return "down";
                case System.Windows.Forms.Keys.Left: return "left";
                case System.Windows.Forms.Keys.Right: return "right";
                case System.Windows.Forms.Keys.Insert: return "insert";
                case System.Windows.Forms.Keys.Delete: return "delete";
                case System.Windows.Forms.Keys.Home: return "home";
                case System.Windows.Forms.Keys.End: return "end";
                case System.Windows.Forms.Keys.PageUp: return "pgup";
                case System.Windows.Forms.Keys.PageDown: return "pgdown";
                case System.Windows.Forms.Keys.Oemtilde: return "grave";
                case System.Windows.Forms.Keys.OemMinus: return "minus";
                case System.Windows.Forms.Keys.Oemplus: return "equals";
                case System.Windows.Forms.Keys.OemOpenBrackets: return "lbracket";
                case System.Windows.Forms.Keys.OemCloseBrackets: return "rbracket";
                case System.Windows.Forms.Keys.OemPipe: return "backslash";
                case System.Windows.Forms.Keys.OemSemicolon: return "semicolon";
                case System.Windows.Forms.Keys.OemQuotes: return "apostrophe";
                case System.Windows.Forms.Keys.Oemcomma: return "comma";
                case System.Windows.Forms.Keys.OemPeriod: return "period";
                case System.Windows.Forms.Keys.OemQuestion: return "slash";
            }
            return null;
        }
    }

    /// A plain surface its owner paints (through Paint), without flicker.
    class Canvas : Control
    {
        public Canvas()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
    }

    /// A drop-down entry: the value it stands for and how it reads.
    class Choice
    {
        public string Value, Text;
        public override string ToString() { return Text; }
    }

    /// The CONTROLLER / KEYBOARD / WHEEL switch: a white pill slides to the open tab.
    class Segmented : Control
    {
        readonly string[] items;
        int selected, hover = -1, version;
        float at; // where the pill is, in tabs (slides between them)
        public event EventHandler SelectedIndexChanged;

        public Segmented(params string[] items)
        {
            this.items = items;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            Size = new Size(110 * items.Length + 8, 46);
        }

        public int SelectedIndex
        {
            get { return selected; }
            set
            {
                if (value == selected) return;
                float from = at;
                selected = value;
                int mine = ++version, start = Environment.TickCount;
                if (!IsHandleCreated || !Visible) at = value;
                else
                    Anim.Run(() =>
                    {
                        if (mine != version || IsDisposed) return false;
                        float t = Math.Min(1, (Environment.TickCount - start) / 180f);
                        at = from + (selected - from) * Anim.Ease(t);
                        Invalidate();
                        return t < 1;
                    });
                Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        float Slot { get { return (Width - 8) / (float)items.Length; } }

        int IndexAt(int x) { return Math.Max(0, Math.Min(items.Length - 1, (int)((x - 4) / Slot))); }

        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int h = IndexAt(e.X); if (h != hover) { hover = h; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) SelectedIndex = IndexAt(e.X); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Dark.PaintUnder(this, g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            Dark.FillRounded(g, Color.FromArgb(14, 15, 19), r, r.Height / 2);
            using (var path = Dark.RoundedRect(r, r.Height / 2))
            using (var pen = new Pen(Dark.Border))
                g.DrawPath(pen, path);
            var pill = new RectangleF(4 + at * Slot, 4, Slot, Height - 8);
            Dark.FillRounded(g, Color.White, pill, pill.Height / 2);
            for (int i = 0; i < items.Length; ++i)
            {
                var cell = new Rectangle((int)(4 + i * Slot), 0, (int)Slot, Height);
                // The text turns dark as the pill slides under it.
                float under = Math.Max(0, 1 - Math.Abs(at - i));
                Color text = Anim.Mix(i == hover ? Color.FromArgb(214, 218, 226) : Dark.DimText, Dark.Back, under);
                TextRenderer.DrawText(g, Dark.Spaced(items[i]), Dark.Caps(9f), cell, text,
                                      TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }
    }

    /// The device the open tab uses: a green dot when connected, its name and a line under it.
    class DevicePill : Control
    {
        public bool On;
        public string Title = "", Detail = "";

        public DevicePill()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Size = new Size(220, 46);
        }

        public void Show(bool on, string title, string detail)
        {
            if (on == On && title == Title && detail == Detail) return;
            On = on; Title = title; Detail = detail;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Dark.PaintUnder(this, g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            Dark.FillRounded(g, Color.FromArgb(14, 15, 19), r, r.Height / 2);
            using (var path = Dark.RoundedRect(r, r.Height / 2))
            using (var pen = new Pen(Dark.Border))
                g.DrawPath(pen, path);
            var dot = On ? Color.FromArgb(52, 199, 89) : Color.FromArgb(96, 101, 112);
            if (On) using (var glow = new SolidBrush(Color.FromArgb(50, dot))) g.FillEllipse(glow, 13, Height / 2f - 8, 16, 16);
            using (var fill = new SolidBrush(dot)) g.FillEllipse(fill, 17, Height / 2f - 4, 8, 8);
            using (var bold = new Font("Segoe UI Semibold", 9.5f))
                TextRenderer.DrawText(g, Title, bold, new Rectangle(36, 5, Width - 46, 20), Dark.Text,
                                      TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, Detail, Font, new Rectangle(36, 23, Width - 46, 18), Dark.DimText,
                                  TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }

    /// A small spaced-capitals heading with a line under it, and optionally a note on its right.
    class Heading : Control
    {
        readonly string right;

        public Heading(string text, string right = null)
        {
            this.right = right;
            Text = text;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Dock = DockStyle.Fill;
            Height = 44;
            Margin = new Padding(0);
            Tag = "heading";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Dark.PaintUnder(this, g);
            var r = new Rectangle(0, Height - 24, Width, 18);
            TextRenderer.DrawText(g, Dark.Spaced(Text.ToUpperInvariant()), Dark.Caps(8.5f), r, Dark.Heading, TextFormatFlags.Left | TextFormatFlags.NoPrefix);
            if (right != null)
                TextRenderer.DrawText(g, Dark.Spaced(right.ToUpperInvariant()), Dark.Caps(7.5f), r, Color.FromArgb(96, 101, 112), TextFormatFlags.Right | TextFormatFlags.NoPrefix);
            using (var line = new Pen(Dark.Line)) g.DrawLine(line, 0, Height - 1, Width, Height - 1);
        }
    }

    /// A trigger as the game reads it (after its deadzone): its name, a bar and the percentage.
    class TriggerMeter : Control
    {
        float value;

        public TriggerMeter(string text)
        {
            Text = text;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Dock = DockStyle.Fill;
            Height = 34;
            Margin = new Padding(0);
        }

        public float Value { get { return value; } set { if (Math.Abs(value - this.value) > 0.001f) { this.value = value; Invalidate(); } } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Dark.PaintUnder(this, g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            TextRenderer.DrawText(g, Text, Dark.Caps(10f), new Rectangle(0, 0, 40, Height), Dark.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            var track = new RectangleF(48, Height / 2f - 3, Math.Max(10, Width - 48 - 52), 6);
            Dark.FillRounded(g, Dark.Field, track, 3);
            if (value <= 0)
            {
                // Hatched while the game reads nothing.
                using (var hatch = new HatchBrush(HatchStyle.WideUpwardDiagonal, Color.FromArgb(70, 74, 84), Dark.Field))
                    g.FillRectangle(hatch, track.X, track.Y, 24, track.Height);
            }
            else Dark.FillRounded(g, Color.White, new RectangleF(track.X, track.Y, Math.Max(6, track.Width * value), track.Height), 3);
            TextRenderer.DrawText(g, (int)Math.Round(value * 100) + "%", Dark.Caps(9f), new Rectangle(Width - 50, 0, 50, Height), Dark.Text,
                                  TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }
    }

    /// A row of the binding card: its name, and one or two drop-downs, or a value and a level bar.
    class SlotRow : Control
    {
        public readonly Label Name_;
        public readonly DarkComboBox First, Second;
        public readonly Label Value;
        public readonly DarkBar Bar;
        public bool Two, Meter;

        public SlotRow()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Dark.Card;
            Dock = DockStyle.Fill;
            Height = 44;
            Margin = new Padding(0);
            Name_ = new Label { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Tag = "heading", ForeColor = Dark.DimText, BackColor = Color.Transparent };
            First = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, MaxDropDownItems = 14 };
            Second = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, MaxDropDownItems = 14 };
            Value = new Label { AutoSize = false, TextAlign = ContentAlignment.MiddleRight, BackColor = Color.Transparent, AutoEllipsis = true };
            Bar = new DarkBar { Minimum = 0, Maximum = 1000, Height = 8 };
            Controls.AddRange(new Control[] { Name_, First, Second, Value, Bar });
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (Bar == null) return; // (while the constructor makes the controls)
            const int label = 86;
            Name_.SetBounds(0, 0, label, Height - 1);
            int x = label, w = Width - label;
            First.Visible = !Meter;
            Second.Visible = !Meter && Two;
            Value.Visible = Meter;
            Bar.Visible = Meter;
            if (Meter)
            {
                Value.SetBounds(x, 4, w, 20);
                Bar.SetBounds(x + 30, 28, w - 30, 8);
                return;
            }
            int top = (Height - 1 - First.Height) / 2;
            if (Two)
            {
                int a = (w - 6) * 55 / 100;
                First.SetBounds(x, top, a, First.Height);
                Second.SetBounds(x + a + 6, top, w - a - 6, Second.Height);
            }
            else First.SetBounds(x, top, w, First.Height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (var back = new SolidBrush(BackColor)) e.Graphics.FillRectangle(back, ClientRectangle);
            using (var line = new Pen(Dark.Line)) e.Graphics.DrawLine(line, 0, Height - 1, Width, Height - 1);
        }
    }

    /// A drawing of a controller or a wheel whose parts can be clicked. It is drawn in a fixed
    /// design size (700 x 470) and scaled to fit. Parts in the rotating group (the wheel) turn
    /// with Angle around Pivot.
    abstract class Diagram : Control
    {
        protected const float DW = 700, DH = 470;

        protected class Part
        {
            public string Target;
            public GraphicsPath Shape;
            public bool Group;            // turns with the wheel
            public PointF Chip, Anchor;   // the label's centre and where its line points; no label: Chip empty
            public RectangleF ChipRect;   // as last drawn
            public string Label;          // as last drawn
        }

        protected readonly List<Part> parts = new List<Part>();
        protected string hover;
        public string Selected;
        /// The rotating group's angle (degrees) and centre.
        protected float Angle;
        protected PointF Pivot;
        public event Action<string> Picked;

        protected static readonly Color SelectColor = Color.FromArgb(58, 120, 255);
        protected static readonly Font ChipFont = new Font(Dark.Caps(9f).FontFamily, 11f, FontStyle.Regular, GraphicsUnit.Pixel);
        protected static readonly Font SmallFont = new Font(Dark.Caps(9f).FontFamily, 9f, FontStyle.Regular, GraphicsUnit.Pixel);

        protected Diagram()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
        }

        protected Part Add(string target, GraphicsPath shape, bool group = false)
        {
            var part = new Part { Target = target, Shape = shape, Group = group };
            parts.Add(part);
            return part;
        }

        protected static GraphicsPath Rect(float x, float y, float w, float h, float radius)
        {
            return Dark.RoundedRect(new RectangleF(x, y, w, h), radius);
        }

        protected static GraphicsPath Circle(float cx, float cy, float r)
        {
            var path = new GraphicsPath();
            path.AddEllipse(cx - r, cy - r, r * 2, r * 2);
            return path;
        }

        float scale, ox, oy;

        Matrix View()
        {
            scale = Math.Min(Width / DW, Height / DH);
            ox = (Width - DW * scale) / 2;
            oy = (Height - DH * scale) / 2;
            var m = new Matrix();
            m.Translate(ox, oy);
            m.Scale(scale, scale);
            return m;
        }

        PointF ToDesign(Point p)
        {
            View().Dispose();
            return new PointF((p.X - ox) / scale, (p.Y - oy) / scale);
        }

        static PointF Turn(PointF p, PointF centre, float degrees)
        {
            double a = degrees * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
            float dx = p.X - centre.X, dy = p.Y - centre.Y;
            return new PointF(centre.X + (float)(dx * c - dy * s), centre.Y + (float)(dx * s + dy * c));
        }

        string TargetAt(Point point)
        {
            var p = ToDesign(point);
            foreach (var part in parts)
                if (!part.ChipRect.IsEmpty && part.ChipRect.Contains(p)) return part.Target;
            for (int i = parts.Count - 1; i >= 0; --i)
            {
                var part = parts[i];
                var q = part.Group ? Turn(p, Pivot, -Angle) : p;
                if (part.Shape.IsVisible(q)) return part.Target;
            }
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var at = TargetAt(e.Location);
            Cursor = at != null ? Cursors.Hand : Cursors.Default;
            if (at != hover) { hover = at; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = null; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            var at = TargetAt(e.Location);
            if (at != null && e.Button == MouseButtons.Left && Picked != null) Picked(at);
        }

        /// The label next to a part, or null for none.
        protected abstract string LabelOf(string target);
        /// How far a part is pressed right now, 0 to 1.
        protected abstract float LiveOf(string target);
        protected abstract void PaintBody(Graphics g);
        protected virtual void PaintOver(Graphics g) { }
        protected virtual void PaintGroupUnder(Graphics g) { }
        protected virtual void PaintGroupOver(Graphics g) { }
        /// Dim a part (a wheel control nothing is mapped to).
        protected virtual bool Dim(string target) { return false; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Dark.PaintUnder(this, g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            using (var view = View()) g.Transform = view;
            // A soft blue light behind the drawing.
            using (var glow = new GraphicsPath())
            {
                glow.AddEllipse(DW / 2 - 330, DH / 2 - 210, 660, 400);
                using (var brush = new PathGradientBrush(glow) { CenterColor = Color.FromArgb(46, 40, 70, 160), SurroundColors = new[] { Color.FromArgb(0, 40, 70, 160) } })
                    g.FillPath(brush, glow);
            }
            PaintBody(g);
            var plain = g.Transform;
            var turned = plain.Clone();
            turned.RotateAt(Angle, Pivot);
            g.Transform = turned;
            PaintGroupUnder(g);
            foreach (var part in parts.Where(p => p.Group)) PaintPart(g, part);
            PaintGroupOver(g);
            g.Transform = plain;
            foreach (var part in parts.Where(p => !p.Group)) PaintPart(g, part);
            PaintOver(g);
            foreach (var part in parts) PaintChip(g, part);
            turned.Dispose();
            plain.Dispose();
        }

        protected virtual void PaintPart(Graphics g, Part part)
        {
            var bounds = part.Shape.GetBounds();
            float live = LiveOf(part.Target);
            bool dim = Dim(part.Target);
            Color top = Color.FromArgb(62, 65, 74), bottom = Color.FromArgb(40, 42, 49);
            if (part.Target == hover) { top = Color.FromArgb(76, 80, 90); bottom = Color.FromArgb(52, 55, 63); }
            if (live > 0) { top = Anim.Mix(top, Color.FromArgb(170, 176, 190), live); bottom = Anim.Mix(bottom, Color.FromArgb(130, 136, 150), live); }
            if (dim) { top = Color.FromArgb(40, 42, 48); bottom = Color.FromArgb(32, 34, 39); }
            using (var fill = new LinearGradientBrush(new RectangleF(bounds.X, bounds.Y - 1, bounds.Width, bounds.Height + 2), top, bottom, 90f))
                g.FillPath(fill, part.Shape);
            using (var edge = new Pen(dim ? Color.FromArgb(54, 57, 64) : Color.FromArgb(84, 88, 100), 1f))
                g.DrawPath(edge, part.Shape);
            PaintSymbol(g, part, dim);
            if (part.Target == Selected)
            {
                using (var glow = new Pen(Color.FromArgb(70, SelectColor), 7f) { LineJoin = LineJoin.Round }) g.DrawPath(glow, part.Shape);
                using (var line = new Pen(SelectColor, 2.2f) { LineJoin = LineJoin.Round }) g.DrawPath(line, part.Shape);
            }
        }

        /// What is printed on a part (the face buttons' symbols, the arrows, the shoulder names).
        protected virtual void PaintSymbol(Graphics g, Part part, bool dim) { }

        void PaintChip(Graphics g, Part part)
        {
            part.ChipRect = RectangleF.Empty;
            if (part.Chip.IsEmpty) return;
            var text = LabelOf(part.Target);
            if (text == null) return;
            part.Label = text;
            var size = g.MeasureString(text, ChipFont);
            float w = Math.Max(26, size.Width + 14), h = 22;
            var r = new RectangleF(part.Chip.X - w / 2, part.Chip.Y - h / 2, w, h);
            bool on = part.Target == Selected, over = part.Target == hover;
            // The line from the label to the part, starting at the label's nearest edge.
            var from = new PointF(Math.Max(r.Left, Math.Min(r.Right, part.Anchor.X)), Math.Max(r.Top, Math.Min(r.Bottom, part.Anchor.Y)));
            using (var line = new Pen(on ? SelectColor : Color.FromArgb(78, 84, 96), 1f)) g.DrawLine(line, from, part.Anchor);
            Color fill = on ? Color.FromArgb(34, 84, 214) : over ? Color.FromArgb(34, 37, 44) : Color.FromArgb(20, 22, 27);
            Color edge = on ? Color.FromArgb(120, 168, 255) : Color.FromArgb(104, 110, 124);
            Dark.FillRounded(g, fill, r, 6);
            using (var path = Dark.RoundedRect(r, 6))
            using (var pen = new Pen(edge, 1.2f))
                g.DrawPath(pen, path);
            using (var brush = new SolidBrush(text == "-" ? Dark.DimText : Color.White))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(text, ChipFont, brush, new RectangleF(r.X, r.Y + 1, r.Width, r.Height), format);
            part.ChipRect = r;
        }

        /// The selected part drawn alone in `r` (the binding card's icon).
        public void PaintIcon(Graphics g, RectangleF r, string target)
        {
            var part = parts.FirstOrDefault(p => p.Target == target);
            if (part == null) return;
            var bounds = part.Shape.GetBounds();
            float s = Math.Min(r.Width * 0.72f / Math.Max(1, bounds.Width), r.Height * 0.72f / Math.Max(1, bounds.Height));
            var old = g.Transform;
            using (var m = new Matrix())
            {
                m.Translate(r.X + r.Width / 2, r.Y + r.Height / 2);
                m.Scale(s, s);
                m.Translate(-(bounds.X + bounds.Width / 2), -(bounds.Y + bounds.Height / 2));
                g.Transform = m;
                using (var fill = new SolidBrush(SelectColor)) g.FillPath(fill, part.Shape);
                using (var line = new Pen(Color.FromArgb(150, 190, 255), 1.5f / s)) g.DrawPath(line, part.Shape);
            }
            g.Transform = old;
        }
    }

    /// The controller: PS4 controls, each labelled with what it is on your controller or keyboard.
    class PadDiagram : Diagram
    {
        public Func<string, string> Label = t => null;
        public Func<string, float> Live = t => 0;
        /// The sticks' positions (-1..1), drawn moved.
        public PointF LeftStick, RightStick;
        readonly GraphicsPath body;

        public PadDiagram()
        {
            Add("l2", Rect(150, 30, 112, 28, 9)).Chip = new PointF(80, 44);
            Add("l1", Rect(144, 62, 124, 24, 8)).Chip = new PointF(80, 74);
            Add("r2", Rect(438, 30, 112, 28, 9)).Chip = new PointF(620, 44);
            Add("r1", Rect(432, 62, 124, 24, 8)).Chip = new PointF(620, 74);
            foreach (var p in parts) p.Anchor = new PointF(p.Chip.X < 350 ? p.Shape.GetBounds().Left : p.Shape.GetBounds().Right, p.Chip.Y);

            body = new GraphicsPath();
            body.AddPath(Rect(112, 92, 476, 276, 86), false);
            foreach (bool right in new[] { false, true })
            {
                Func<float, float> x = v => right ? DW - v : v;
                var grip = new GraphicsPath();
                grip.AddBezier(x(130), 200, x(104), 300, x(112), 420, x(170), 446);
                grip.AddBezier(x(170), 446, x(220), 462, x(258), 400, x(282), 340);
                grip.AddLine(x(282), 340, x(250), 200);
                grip.CloseFigure();
                body.AddPath(grip, false);
            }

            Part part;
            part = Add("pad_up", Rect(216, 161, 28, 36, 5)); part.Chip = new PointF(230, 116); part.Anchor = new PointF(230, 161);
            part = Add("pad_down", Rect(216, 213, 28, 36, 5)); part.Chip = new PointF(230, 294); part.Anchor = new PointF(230, 249);
            part = Add("pad_left", Rect(186, 191, 36, 28, 5)); part.Chip = new PointF(128, 205); part.Anchor = new PointF(186, 205);
            part = Add("pad_right", Rect(238, 191, 36, 28, 5)); part.Chip = new PointF(322, 205); part.Anchor = new PointF(274, 205);
            part = Add("triangle", Circle(470, 160, 19)); part.Chip = new PointF(526, 146); part.Anchor = new PointF(487, 152);
            part = Add("circle", Circle(515, 205, 19)); part.Chip = new PointF(574, 205); part.Anchor = new PointF(534, 205);
            part = Add("cross", Circle(470, 250, 19)); part.Chip = new PointF(470, 294); part.Anchor = new PointF(470, 269);
            part = Add("square", Circle(425, 205, 19)); part.Chip = new PointF(378, 205); part.Anchor = new PointF(406, 205);
            part = Add("touchpad_center", Rect(300, 110, 100, 64, 12)); part.Chip = new PointF(350, 142); part.Anchor = new PointF(350, 142);
            part = Add("options", Rect(414, 116, 12, 28, 6)); part.Chip = new PointF(452, 104); part.Anchor = new PointF(426, 116);
            part = Add("stick_left", Circle(300, 315, 30)); part.Chip = new PointF(300, 398); part.Anchor = new PointF(300, 357);
            part = Add("stick_right", Circle(400, 315, 30)); part.Chip = new PointF(400, 398); part.Anchor = new PointF(400, 357);
        }

        protected override string LabelOf(string target) { return Label(target); }
        protected override float LiveOf(string target) { return Live(target); }

        protected override void PaintBody(Graphics g)
        {
            using (var fill = new LinearGradientBrush(new RectangleF(100, 90, 500, 380), Color.FromArgb(58, 61, 69), Color.FromArgb(30, 32, 38), 90f))
                g.FillPath(fill, body);
            using (var top = new Pen(Color.FromArgb(40, 255, 255, 255), 1.2f)) g.DrawPath(top, body);
            // The wells under the d-pad, the face buttons and the sticks.
            foreach (var well in new[] { new PointF(230, 205), new PointF(470, 205) }) Well(g, well, 70);
            foreach (var well in new[] { new PointF(300, 315), new PointF(400, 315) }) Well(g, well, 42);
            // The middle of the d-pad, Share and the PS button (not bindable).
            Dark.FillRounded(g, Color.FromArgb(44, 46, 54), new RectangleF(216, 191, 28, 28), 3);
            Dark.FillRounded(g, Color.FromArgb(40, 42, 49), new RectangleF(274, 116, 12, 28), 6);
            using (var ps = new SolidBrush(Color.FromArgb(36, 38, 44))) g.FillEllipse(ps, 341, 252, 18, 18);
        }

        static void Well(Graphics g, PointF c, float r)
        {
            using (var path = Circle(c.X, c.Y, r))
            using (var fill = new PathGradientBrush(path) { CenterColor = Color.FromArgb(34, 36, 42), SurroundColors = new[] { Color.FromArgb(22, 23, 28) } })
                g.FillPath(fill, path);
            using (var edge = new Pen(Color.FromArgb(18, 19, 23), 2f)) g.DrawEllipse(edge, c.X - r, c.Y - r, r * 2, r * 2);
        }

        protected override void PaintPart(Graphics g, Part part)
        {
            if (part.Target == "stick_left" || part.Target == "stick_right")
            {
                // The stick's cap, moved by the stick.
                var stick = part.Target == "stick_left" ? LeftStick : RightStick;
                var state = g.Save();
                g.TranslateTransform(stick.X * 10, stick.Y * 10);
                base.PaintPart(g, part);
                var b = part.Shape.GetBounds();
                using (var ring = new Pen(Color.FromArgb(30, 32, 37), 1.5f))
                    for (float r = 10; r <= 22; r += 6) g.DrawEllipse(ring, b.X + b.Width / 2 - r, b.Y + b.Height / 2 - r, r * 2, r * 2);
                g.Restore(state);
                return;
            }
            base.PaintPart(g, part);
        }

        protected override void PaintSymbol(Graphics g, Part part, bool dim)
        {
            var b = part.Shape.GetBounds();
            float cx = b.X + b.Width / 2, cy = b.Y + b.Height / 2;
            Color symbol;
            switch (part.Target)
            {
                case "triangle": symbol = Color.FromArgb(64, 200, 170); break;
                case "circle": symbol = Color.FromArgb(236, 96, 116); break;
                case "cross": symbol = Color.FromArgb(120, 160, 245); break;
                case "square": symbol = Color.FromArgb(222, 128, 206); break;
                default: symbol = Color.FromArgb(200, 204, 212); break;
            }
            using (var pen = new Pen(symbol, 2.2f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var brush = new SolidBrush(Color.FromArgb(220, 224, 230)))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                switch (part.Target)
                {
                    case "triangle": g.DrawPolygon(pen, new[] { new PointF(cx, cy - 8), new PointF(cx + 8, cy + 6), new PointF(cx - 8, cy + 6) }); break;
                    case "circle": g.DrawEllipse(pen, cx - 8, cy - 8, 16, 16); break;
                    case "cross": g.DrawLine(pen, cx - 7, cy - 7, cx + 7, cy + 7); g.DrawLine(pen, cx + 7, cy - 7, cx - 7, cy + 7); break;
                    case "square": g.DrawRectangle(pen, cx - 7, cy - 7, 14, 14); break;
                    case "pad_up": Arrow(g, cx, cy, 0); break;
                    case "pad_right": Arrow(g, cx, cy, 90); break;
                    case "pad_down": Arrow(g, cx, cy, 180); break;
                    case "pad_left": Arrow(g, cx, cy, 270); break;
                    case "l1": case "l2": case "r1": case "r2":
                        g.DrawString(part.Target.ToUpperInvariant(), ChipFont, brush, new RectangleF(b.X, b.Y + 1, b.Width, b.Height), format);
                        break;
                    case "stick_left": case "stick_right":
                        g.DrawString(part.Target == "stick_left" ? "L3" : "R3", SmallFont, brush, new RectangleF(b.X, b.Y + 1, b.Width, b.Height), format);
                        break;
                }
            }
        }

        static void Arrow(Graphics g, float cx, float cy, float degrees)
        {
            var state = g.Save();
            g.TranslateTransform(cx, cy);
            g.RotateTransform(degrees);
            using (var fill = new SolidBrush(Color.FromArgb(150, 156, 168)))
                g.FillPolygon(fill, new[] { new PointF(0, -6), new PointF(5, 2), new PointF(-5, 2) });
            g.Restore(state);
        }
    }

    /// The racing wheel and pedals: the wheel turns with your wheel, the pedals fill as you press them.
    class WheelDiagram : Diagram
    {
        public Func<string, string> Label = t => null;
        public Func<string, float> Live = t => 0;
        public Func<string, bool> Mapped = t => true;
        static readonly PointF C = new PointF(270, 228);
        static readonly float[] PedalX = { 545, 600, 655 };
        static readonly string[] Pedals = { "Clutch", "Brake", "Throttle" };

        public WheelDiagram()
        {
            Pivot = C;
            // Behind the rim: the paddles.
            Part part;
            part = Add("L1", Rect(C.X - 188, C.Y - 62, 34, 124, 14), true); part.Chip = new PointF(56, 112); part.Anchor = new PointF(C.X - 176, C.Y - 62);
            part = Add("R1", Rect(C.X + 154, C.Y - 62, 34, 124, 14), true); part.Chip = new PointF(484, 112); part.Anchor = new PointF(C.X + 176, C.Y - 62);
            var rim = new GraphicsPath();
            rim.AddEllipse(C.X - 160, C.Y - 160, 320, 320);
            rim.AddEllipse(C.X - 134, C.Y - 134, 268, 268);
            part = Add("Steering", rim, true); part.Chip = new PointF(C.X, 30); part.Anchor = new PointF(C.X, C.Y - 160);
            // On the hub and the spokes.
            Add("Triangle", Circle(C.X + 100, C.Y - 22, 10), true);
            Add("Circle", Circle(C.X + 122, C.Y, 10), true);
            Add("Cross", Circle(C.X + 100, C.Y + 22, 10), true);
            Add("Square", Circle(C.X + 78, C.Y, 10), true);
            Add("Up", Rect(C.X - 107, C.Y - 30, 14, 20, 3), true);
            Add("Down", Rect(C.X - 107, C.Y + 10, 14, 20, 3), true);
            Add("Left", Rect(C.X - 130, C.Y - 7, 20, 14, 3), true);
            Add("Right", Rect(C.X - 90, C.Y - 7, 20, 14, 3), true);
            Add("L2", Rect(C.X - 52, C.Y - 36, 30, 16, 6), true);
            Add("Options", Rect(C.X - 15, C.Y - 36, 30, 16, 6), true);
            Add("R2", Rect(C.X + 22, C.Y - 36, 30, 16, 6), true);
            Add("L3", Circle(C.X - 15, C.Y + 66, 8), true);
            Add("R3", Circle(C.X + 15, C.Y + 66, 8), true);
            Add("TouchLeft", Rect(C.X - 17, C.Y + 86, 34, 16, 6), true);
            for (int i = 0; i < 3; ++i) Add(Pedals[i], Rect(PedalX[i] - 20, 232, 40, 120, 9));
        }

        public float WheelAngle { get { return Angle; } set { if (Math.Abs(value - Angle) > 0.05f) { Angle = value; Invalidate(); } } }

        protected override string LabelOf(string target) { return Label(target); }
        protected override float LiveOf(string target) { return Pedals.Contains(target) || target == "Steering" ? 0 : Live(target); }
        protected override bool Dim(string target) { return !Mapped(target); }

        protected override void PaintBody(Graphics g)
        {
            // The pedals' base plate.
            using (var fill = new LinearGradientBrush(new RectangleF(510, 210, 180, 190), Color.FromArgb(44, 46, 53), Color.FromArgb(26, 28, 33), 90f))
            using (var path = Rect(512, 214, 176, 160, 14))
                g.FillPath(fill, path);
        }

        protected override void PaintGroupUnder(Graphics g)
        {
            using (var spoke = new LinearGradientBrush(new RectangleF(C.X - 140, C.Y - 60, 280, 200), Color.FromArgb(52, 55, 63), Color.FromArgb(32, 34, 40), 90f))
            {
                using (var path = Rect(C.X - 142, C.Y - 22, 284, 44, 16)) g.FillPath(spoke, path);
                using (var path = Rect(C.X - 30, C.Y + 30, 60, 112, 18)) g.FillPath(spoke, path);
                using (var path = Rect(C.X - 66, C.Y - 48, 132, 100, 26)) g.FillPath(spoke, path);
            }
            using (var hub = new Pen(Color.FromArgb(70, 74, 85), 1f))
            using (var path = Rect(C.X - 66, C.Y - 48, 132, 100, 26))
                g.DrawPath(hub, path);
            using (var logo = new SolidBrush(Color.FromArgb(30, 32, 38))) g.FillEllipse(logo, C.X - 18, C.Y - 6, 36, 36);
        }

        protected override void PaintPart(Graphics g, Part part)
        {
            if (part.Target == "Steering")
            {
                // The rim: a thick ring with a light grip line and the centre marker on top.
                using (var rim = new Pen(Color.FromArgb(40, 42, 48), 26f)) g.DrawEllipse(rim, C.X - 147, C.Y - 147, 294, 294);
                using (var shine = new Pen(Color.FromArgb(62, 66, 76), 2f)) g.DrawEllipse(shine, C.X - 158, C.Y - 158, 316, 316);
                using (var inner = new Pen(Color.FromArgb(24, 25, 30), 2f)) g.DrawEllipse(inner, C.X - 135, C.Y - 135, 270, 270);
                if (part.Target == hover) using (var lit = new Pen(Color.FromArgb(24, 255, 255, 255), 26f)) g.DrawEllipse(lit, C.X - 147, C.Y - 147, 294, 294);
                Dark.FillRounded(g, Color.FromArgb(236, 238, 242), new RectangleF(C.X - 5, C.Y - 160, 10, 26), 3);
                if (part.Target == Selected)
                {
                    using (var glow = new Pen(Color.FromArgb(70, SelectColor), 7f)) g.DrawPath(glow, part.Shape);
                    using (var line = new Pen(SelectColor, 2.2f)) g.DrawPath(line, part.Shape);
                }
                return;
            }
            int pedal = Array.IndexOf(Pedals, part.Target);
            if (pedal >= 0)
            {
                base.PaintPart(g, part);
                var b = part.Shape.GetBounds();
                float amount = Live(part.Target);
                if (amount > 0)
                {
                    var state = g.Save();
                    g.SetClip(part.Shape, CombineMode.Intersect);
                    using (var fill = new SolidBrush(Color.FromArgb(200, SelectColor))) g.FillRectangle(fill, b.X, b.Bottom - b.Height * amount, b.Width, b.Height * amount);
                    g.Restore(state);
                }
                // The pedal's ridges, its name and what it is mapped to.
                using (var ridge = new Pen(Color.FromArgb(30, 32, 37), 2f))
                    for (float y = b.Y + 16; y < b.Bottom - 10; y += 12) g.DrawLine(ridge, b.X + 9, y, b.Right - 9, y);
                using (var name = new SolidBrush(Dark.Heading))
                using (var value = new SolidBrush(Mapped(part.Target) ? Color.White : Dark.DimText))
                using (var format = new StringFormat { Alignment = StringAlignment.Center })
                {
                    g.DrawString(part.Target.ToUpperInvariant(), SmallFont, name, b.X + b.Width / 2, b.Bottom + 8, format);
                    g.DrawString(Label(part.Target) ?? "-", ChipFont, value, b.X + b.Width / 2, b.Bottom + 22, format);
                }
                return;
            }
            base.PaintPart(g, part);
        }

        protected override void PaintSymbol(Graphics g, Part part, bool dim)
        {
            var b = part.Shape.GetBounds();
            float cx = b.X + b.Width / 2, cy = b.Y + b.Height / 2;
            Color symbol;
            switch (part.Target)
            {
                case "Triangle": symbol = Color.FromArgb(64, 200, 170); break;
                case "Circle": symbol = Color.FromArgb(236, 96, 116); break;
                case "Cross": symbol = Color.FromArgb(120, 160, 245); break;
                case "Square": symbol = Color.FromArgb(222, 128, 206); break;
                default: symbol = Color.FromArgb(200, 204, 212); break;
            }
            if (dim) symbol = Color.FromArgb(90, symbol);
            using (var pen = new Pen(symbol, 1.6f) { LineJoin = LineJoin.Round })
            using (var brush = new SolidBrush(dim ? Color.FromArgb(110, 114, 124) : Color.FromArgb(220, 224, 230)))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                switch (part.Target)
                {
                    case "Triangle": g.DrawPolygon(pen, new[] { new PointF(cx, cy - 4.5f), new PointF(cx + 4.5f, cy + 3.5f), new PointF(cx - 4.5f, cy + 3.5f) }); break;
                    case "Circle": g.DrawEllipse(pen, cx - 4.5f, cy - 4.5f, 9, 9); break;
                    case "Cross": g.DrawLine(pen, cx - 4, cy - 4, cx + 4, cy + 4); g.DrawLine(pen, cx + 4, cy - 4, cx - 4, cy + 4); break;
                    case "Square": g.DrawRectangle(pen, cx - 4, cy - 4, 8, 8); break;
                    case "L1": case "R1": g.DrawString(part.Target, SmallFont, brush, new RectangleF(b.X, b.Y, b.Width, b.Height), format); break;
                    case "L2": case "R2": case "L3": case "R3": g.DrawString(part.Target, SmallFont, brush, new RectangleF(b.X - 4, b.Y, b.Width + 8, b.Height), format); break;
                    case "Options": g.DrawString("OPT", SmallFont, brush, new RectangleF(b.X - 4, b.Y, b.Width + 8, b.Height), format); break;
                    case "TouchLeft": g.DrawString("TOUCH", SmallFont, brush, new RectangleF(b.X - 6, b.Y, b.Width + 12, b.Height), format); break;
                }
            }
        }
    }

    /// The Controls page.
    class ControlsPage : Control
    {
        readonly Bindings bindings;
        readonly Wheel wheel;
        readonly Func<string, string, int, int> readInt;
        readonly Action<string, string, object> write;
        readonly Action openPorts;

        readonly Label title, subtitle, hint;
        readonly Segmented tabs;
        readonly DevicePill pill;
        readonly PadDiagram pad;
        readonly WheelDiagram wheelView;
        readonly Panel side;
        readonly Control header;
        readonly SlotRow[] slots = new SlotRow[5];
        readonly Button rebind, reset;
        readonly Label cardHint;
        readonly ToggleRow invert;
        readonly TableLayoutPanel padExtras, keyExtras, wheelExtras;
        readonly TriggerMeter l2Meter, r2Meter;
        readonly Label rawInput;
        readonly ComboBox keyboardPlayer;
        readonly Timer timer;

        string padTarget = "cross", wheelTarget = "Steering";
        bool filling;
        string error;

        // The controller: the connected ones, the one shown, its state and what it was when listening started.
        List<Joysticks.Device> controllers = new List<Joysticks.Device>();
        Joysticks.Device active;
        readonly ControllerPreview state = new ControllerPreview();
        Dictionary<string, float> inputs = new Dictionary<string, float>();
        Dictionary<int, Dictionary<string, float>> listenBase;
        int rescan;

        // Rebinding: on the controller (until listenUntil) or the keyboard (the keys still to press).
        enum Listen { None, Pad, Keys }
        Listen listening;
        DateTime listenUntil;
        readonly List<string> keyQueue = new List<string>();

        static readonly string[] StickKeys = { "_x_minus", "_x_plus", "_y_minus", "_y_plus" };
        static readonly string[] StickKeyNames = { "Left", "Right", "Up", "Down" };

        public ControlsPage(string root, Func<string, string, int, int> readInt, Action<string, string, object> write, Action openPorts)
        {
            this.readInt = readInt;
            this.write = write;
            this.openPorts = openPorts;
            bindings = new Bindings(root);
            wheel = new Wheel(root);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Dark.Panel;

            title = new Label { Text = "Controls", AutoSize = true, Font = new Font("Segoe UI Light", 22f), BackColor = Color.Transparent };
            subtitle = new Label { Text = "Changes save at once.", AutoSize = true, ForeColor = Dark.DimText, Tag = "heading", BackColor = Color.Transparent };
            tabs = new Segmented("CONTROLLER", "KEYBOARD", "WHEEL");
            pill = new DevicePill();
            pad = new PadDiagram { BackColor = Dark.Panel };
            wheelView = new WheelDiagram { BackColor = Dark.Panel, Visible = false };
            hint = new Label { AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Dark.DimText, Tag = "heading", BackColor = Color.Transparent };
            Controls.AddRange(new Control[] { title, subtitle, tabs, pill, pad, wheelView, hint });

            // ---- the side: the card of the picked control, then the tab's settings
            side = new Panel { AutoScroll = true, BackColor = Dark.Panel };
            var column = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = new Padding(0), Padding = new Padding(0, 0, 10, 0) };
            column.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            side.Controls.Add(column);
            Controls.Add(side);

            var card = new Card(Dark.Card) { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(16, 12, 16, 14), Margin = new Padding(0), Radius = 8 };
            header = new Canvas { Dock = DockStyle.Fill, Height = 74, Margin = new Padding(0), BackColor = Dark.Card };
            header.Paint += (sender, e) => PaintHeader(e.Graphics);
            card.Controls.Add(header);
            for (int i = 0; i < slots.Length; ++i)
            {
                var slot = slots[i] = new SlotRow();
                int index = i;
                slot.First.SelectedIndexChanged += delegate { OnSlotChanged(index, 0); };
                slot.Second.SelectedIndexChanged += delegate { OnSlotChanged(index, 1); };
                card.Controls.Add(slot);
            }
            invert = new ToggleRow { Text = "Invert", BackColor = Color.Transparent };
            invert.CheckedChanged += delegate
            {
                if (filling) return;
                var row = wheel.RowOf(wheelTarget);
                if (row == null) return;
                row.Invert = invert.Checked;
                SaveWheel();
            };
            card.Controls.Add(invert);
            // Rebind and Reset side by side, half the card each.
            var buttons = new Panel { Dock = DockStyle.Fill, Height = 58, Margin = new Padding(0), BackColor = Dark.Card };
            rebind = new Button { Text = "Rebind", Tag = "accent" };
            reset = new Button { Text = "Reset" };
            rebind.Click += delegate { StartRebind(); };
            reset.Click += delegate { ResetTarget(); };
            buttons.Controls.Add(rebind);
            buttons.Controls.Add(reset);
            buttons.Layout += delegate
            {
                int half = (buttons.Width - 12) / 2;
                rebind.SetBounds(0, 14, half, 44);
                reset.SetBounds(half + 12, 14, buttons.Width - half - 12, 44);
            };
            card.Controls.Add(buttons);
            cardHint = new Label { AutoSize = true, MaximumSize = new Size(250, 0), ForeColor = Dark.DimText, Tag = "heading", Margin = new Padding(0, 10, 0, 0), BackColor = Color.Transparent };
            card.Controls.Add(cardHint);
            column.Controls.Add(card);

            // Controller: the sticks' and triggers' deadzones, what the game reads, trigger feedback, ports.
            padExtras = Column(column);
            padExtras.Controls.Add(new Heading("Sticks"));
            padExtras.Controls.Add(new SettingRow("Left deadzone", DeadzoneSlider(() => bindings.LeftDeadzone, v => bindings.LeftDeadzone = v)));
            padExtras.Controls.Add(new SettingRow("Right deadzone", DeadzoneSlider(() => bindings.RightDeadzone, v => bindings.RightDeadzone = v)));
            padExtras.Controls.Add(new Heading("Triggers", "Game reads"));
            padExtras.Controls.Add(l2Meter = new TriggerMeter("L2"));
            padExtras.Controls.Add(new SettingRow("L2 deadzone", DeadzoneSlider(() => bindings.L2Deadzone, v => bindings.L2Deadzone = v)));
            padExtras.Controls.Add(r2Meter = new TriggerMeter("R2"));
            padExtras.Controls.Add(new SettingRow("R2 deadzone", DeadzoneSlider(() => bindings.R2Deadzone, v => bindings.R2Deadzone = v)));
            var strength = new Slider { Minimum = 0, Maximum = 200, SmallChange = 5, LargeChange = 25, Size = new Size(150, 28), Suffix = "%", AccessibleName = "Trigger feedback strength" };
            strength.Value = Math.Max(0, Math.Min(200, readInt("Input", "trigger_strength", 100)));
            strength.ValueChanged += delegate { Try(() => write("Input", "trigger_strength", strength.Value)); };
            padExtras.Controls.Add(new SettingRow("Feedback", strength));
            padExtras.Controls.Add(Note("Adaptive trigger feedback: 0% off, 100% standard, 200% boosted. Updates while playing."));
            padExtras.Controls.Add(new Heading("Players"));
            padExtras.Controls.Add(ActionRow("Controller ports", "Open", openPorts));
            padExtras.Controls.Add(ActionRow("Every button", "Reset all", ResetAll));

            // Keyboard: which player it is.
            keyExtras = Column(column);
            keyExtras.Controls.Add(new Heading("Keyboard"));
            keyboardPlayer = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Width = 120 };
            keyboardPlayer.Items.AddRange(new object[] { "Player 1", "Player 2" });
            keyboardPlayer.SelectedIndex = Math.Max(0, Math.Min(PortsForm.Players - 1, readInt("Input", "keyboard_player", 1) - 1));
            keyboardPlayer.SelectedIndexChanged += delegate { Try(() => write("Input", "keyboard_player", keyboardPlayer.SelectedIndex + 1)); SaveBindings(); };
            keyExtras.Controls.Add(new SettingRow("Plays as", keyboardPlayer));
            keyExtras.Controls.Add(Note("Player 2 on the keyboard: no second controller needed. Every key can have a second one: pick it next to the first."));
            keyExtras.Controls.Add(new Heading("Reset"));
            keyExtras.Controls.Add(ActionRow("Every key", "Reset all", ResetAll));

            // Wheel: force feedback and how the game sees the wheel, and what the probe reads.
            wheelExtras = Column(column);
            wheelExtras.Controls.Add(new Heading("Wheel"));
            wheelExtras.Controls.Add(new SettingRow("Rotation", WheelSlider(90, 2880, 10, (char)0xB0 + "", () => wheel.Rotation, v => wheel.Rotation = v)));
            wheelExtras.Controls.Add(Note("Degrees, as set in your wheel's software."));
            wheelExtras.Controls.Add(new Heading("Force feedback"));
            wheelExtras.Controls.Add(new SettingRow("Strength", WheelSlider(0, 200, 5, "%", () => wheel.Gain, v => wheel.Gain = v)));
            wheelExtras.Controls.Add(new SettingRow("Damping", WheelSlider(0, 100, 5, "%", () => wheel.Damping, v => wheel.Damping = v)));
            wheelExtras.Controls.Add(new SettingRow("Road detail", WheelSlider(0, 200, 5, "%", () => wheel.Detail, v => wheel.Detail = v)));
            var reverse = new ToggleRow { Text = "Reverse the force direction", Checked = wheel.InvertForces };
            reverse.CheckedChanged += delegate { wheel.InvertForces = reverse.Checked; SaveWheel(); };
            wheelExtras.Controls.Add(reverse);
            var present = new DarkComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Width = 150 };
            present.Items.AddRange(Wheel.PresentNames);
            present.SelectedIndex = Math.Max(0, Math.Min(Wheel.PresentNames.Length - 1, wheel.Present));
            present.SelectedIndexChanged += delegate { wheel.Present = present.SelectedIndex; SaveWheel(); };
            wheelExtras.Controls.Add(new SettingRow("Game sees", present));
            wheelExtras.Controls.Add(Note("The wheel model the game is told it has; it uses that model's force feedback."));
            var padIsWheel = new ToggleRow { Text = "Pad reports as the wheel (experimental)", Checked = wheel.PadIsWheel };
            padIsWheel.CheckedChanged += delegate { wheel.PadIsWheel = padIsWheel.Checked; SaveWheel(); };
            wheelExtras.Controls.Add(padIsWheel);
            wheelExtras.Controls.Add(Note("Leave off: the game then ignores the wheel's forces and input."));
            wheelExtras.Controls.Add(new Heading("Live input"));
            rawInput = new Label { AutoSize = true, MaximumSize = new Size(260, 0), ForeColor = Dark.DimText, Tag = "heading", Font = new Font("Consolas", 8f), Margin = new Padding(0, 8, 0, 0) };
            wheelExtras.Controls.Add(rawInput);

            tabs.SelectedIndexChanged += delegate { OnTab(); };
            pad.Label = PadLabel;
            pad.Live = PadLive;
            pad.Picked += target => Pick(target);
            wheelView.Label = target => { var row = wheel.RowOf(target); return row == null ? null : Wheel.Short(row); };
            wheelView.Mapped = target => { var row = wheel.RowOf(target); return row != null && row.Source != null; };
            wheelView.Live = WheelLive;
            wheelView.Picked += target => Pick(target);

            timer = new Timer { Interval = 33 };
            timer.Tick += delegate { Tick(); };
            VisibleChanged += delegate { OnShown(); };
            OnTab();
        }

        static TableLayoutPanel Column(TableLayoutPanel parent)
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = new Padding(0, 8, 0, 0) };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            parent.Controls.Add(panel);
            return panel;
        }

        static Label Note(string text)
        {
            return new Label { Text = text, AutoSize = true, MaximumSize = new Size(260, 0), ForeColor = Dark.DimText, Tag = "heading", Margin = new Padding(0, 6, 0, 4) };
        }

        static SettingRow ActionRow(string text, string button, Action action)
        {
            var open = new Button { Text = button, AutoSize = true };
            open.Click += delegate { action(); };
            return new SettingRow(text, open);
        }

        /// A deadzone (1-127) as a 0-100% slider.
        Slider DeadzoneSlider(Func<int> get, Action<int> set)
        {
            var slider = new Slider { Minimum = 0, Maximum = 100, SmallChange = 1, LargeChange = 10, Size = new Size(150, 28), Suffix = "%" };
            slider.Value = (int)Math.Round(get() * 100 / 127.0);
            slider.ValueChanged += delegate { set(Math.Max(1, (int)Math.Round(slider.Value * 127 / 100.0))); SaveBindings(); };
            return slider;
        }

        Slider WheelSlider(int min, int max, int step, string suffix, Func<int> get, Action<int> set)
        {
            var slider = new Slider { Minimum = min, Maximum = max, SmallChange = step, LargeChange = step * 5, Size = new Size(150, 28), Suffix = suffix };
            slider.Value = get();
            slider.ValueChanged += delegate
            {
                int v = (int)Math.Round(slider.Value / (double)step) * step;
                if (v != slider.Value) { slider.Value = v; return; }
                set(v);
                SaveWheel();
            };
            return slider;
        }

        void Try(Action action)
        {
            try { action(); error = null; }
            catch (Exception e) { error = "Could not save: " + e.Message; }
            ShowHint();
        }

        void SaveBindings() { Try(() => bindings.Save(keyboardPlayer.SelectedIndex + 1)); pad.Invalidate(); header.Invalidate(); }
        void SaveWheel() { Try(() => wheel.Save()); wheelView.Invalidate(); header.Invalidate(); }

        // ---------------------------------------------------------------- tabs and layout

        int Tab { get { return tabs.SelectedIndex; } }
        bool Xbox { get { return active != null && active.Xbox; } }

        /// For --snapshot: 0 controller, 1 keyboard, 2 wheel.
        public void ShowTab(int index) { tabs.SelectedIndex = index; }

        void OnTab()
        {
            StopListening();
            pad.Visible = Tab != 2;
            wheelView.Visible = Tab == 2;
            padExtras.Visible = Tab == 0;
            keyExtras.Visible = Tab == 1;
            wheelExtras.Visible = Tab == 2;
            hint.Text = Tab == 2
                ? "Click a part of the wheel or a pedal, then Detect and use it on your wheel."
                : "Click a control to see it on your " + (Tab == 0 ? "controller" : "keyboard") + ". Click it again to rebind it.";
            OnShown();
            Fill();
            PerformLayout();
        }

        void OnShown()
        {
            bool shown = Visible && IsHandleCreated;
            if (shown) timer.Start(); else timer.Stop();
            if (shown && Tab == 2) wheel.StartProbe(); else if (wheel.Probing) wheel.StopProbe();
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); OnShown(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Dispose(); wheel.StopProbe(); }
            base.Dispose(disposing);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (side == null) return; // (while the constructor adds the controls)
            const int sideWidth = 312, top = 92;
            title.Location = new Point(-4, -6);
            subtitle.Location = new Point(0, title.Bottom);
            pill.SetBounds(Width - pill.Width, 4, pill.Width, pill.Height);
            tabs.SetBounds(pill.Left - 14 - tabs.Width, 4, tabs.Width, tabs.Height);
            side.SetBounds(Width - sideWidth, top, sideWidth, Math.Max(100, Height - top));
            var area = new Rectangle(0, top, Math.Max(100, Width - sideWidth - 20), Math.Max(100, Height - top - 30));
            pad.Bounds = area;
            wheelView.Bounds = area;
            hint.SetBounds(0, area.Bottom, area.Width, 30);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (var back = new SolidBrush(BackColor)) e.Graphics.FillRectangle(back, ClientRectangle);
        }

        // ---------------------------------------------------------------- the picked control and its card

        void Pick(string target)
        {
            if (Tab == 2)
            {
                if (target == wheelTarget && wheel.Detecting == null) { StartRebind(); return; }
                wheelTarget = target;
            }
            else
            {
                if (target == padTarget && listening == Listen.None) { StartRebind(); return; }
                padTarget = target;
            }
            StopListening();
            Fill();
        }

        string Target { get { return Tab == 2 ? wheelTarget : padTarget; } }

        static string StickSide(string target) { return target == "stick_left" ? "left" : target == "stick_right" ? "right" : null; }

        /// The bindings file's controls a picked part stands for.
        IEnumerable<string> Outputs(string target)
        {
            var stick = StickSide(target);
            if (stick == null) return new[] { target };
            var click = stick == "left" ? "l3" : "r3";
            if (Tab == 0) return new[] { "axis_" + stick + "_x", "axis_" + stick + "_y", click };
            return StickKeys.Select(k => "axis_" + stick + k).Concat(new[] { click });
        }

        string TargetName(string target)
        {
            if (Tab == 2) { var row = wheel.RowOf(target); return row != null ? row.Label : target; }
            switch (target)
            {
                case "stick_left": return "Left stick";
                case "stick_right": return "Right stick";
                case "touchpad_center": return "Touchpad";
                case "l2": return "L2 (brake)";
                case "r2": return "R2 (accelerate)";
            }
            var r = Bindings.RowOf(target);
            return r != null ? r.Label : target;
        }

        void PaintHeader(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var back = new SolidBrush(Dark.Card)) g.FillRectangle(back, header.ClientRectangle);
            var tile = new RectangleF(0, 6, 56, 56);
            Dark.FillRounded(g, Color.FromArgb(20, 22, 27), tile, 8);
            using (var edge = new Pen(Dark.Border)) using (var path = Dark.RoundedRect(tile, 8)) g.DrawPath(edge, path);
            if (Tab == 2) wheelView.PaintIcon(g, tile, wheelTarget); else pad.PaintIcon(g, tile, padTarget);
            using (var big = new Font("Segoe UI", 15f))
                TextRenderer.DrawText(g, TargetName(Target), big, new Rectangle(70, 8, header.Width - 70, 30), Dark.Text,
                                      TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            string tag;
            if (Tab == 2) { var row = wheel.RowOf(wheelTarget); tag = row != null && row.Source != null ? "Mapped" : "Not mapped"; }
            else tag = Outputs(padTarget).All(o => bindings.IsDefault(o)) ? "Default" : "Custom";
            TextRenderer.DrawText(g, Dark.Spaced(tag.ToUpperInvariant()), Dark.Caps(8f), new Rectangle(71, 40, header.Width - 70, 18), Dark.Heading, TextFormatFlags.Left | TextFormatFlags.NoPrefix);
            using (var line = new Pen(Dark.Line)) g.DrawLine(line, 0, header.Height - 1, header.Width, header.Height - 1);
        }

        static List<Choice> PadChoices(bool axes, bool xbox)
        {
            var list = new List<Choice> { new Choice { Value = null, Text = "None" } };
            list.AddRange((axes ? Bindings.PadAxes : Bindings.PadButtons).Select(v => new Choice { Value = v, Text = InputNames.Pad(v, xbox) }));
            return list;
        }

        static List<Choice> KeyChoices()
        {
            var list = new List<Choice> { new Choice { Value = null, Text = "None" } };
            list.AddRange(Bindings.Keys.Select(v => new Choice { Value = v, Text = InputNames.Key(v) }));
            return list;
        }

        static void Set(ComboBox box, List<Choice> choices, string value)
        {
            box.BeginUpdate();
            box.Items.Clear();
            box.Items.AddRange(choices.Cast<object>().ToArray());
            int index = choices.FindIndex(c => c.Value == value);
            box.SelectedIndex = index >= 0 ? index : 0;
            box.EndUpdate();
        }

        // What each slot edits: the bindings file control, and pad or keys.
        readonly string[] slotOutput = new string[5];
        readonly bool[] slotPad = new bool[5];

        /// Fills the card from the picked control.
        void Fill()
        {
            filling = true;
            foreach (var slot in slots) slot.Visible = false;
            invert.Visible = false;
            if (Tab == 2)
            {
                var row = wheel.RowOf(wheelTarget);
                var slot = slots[0];
                slot.Name_.Text = "Mapped to";
                slot.Meter = true;
                slot.Value.Text = row != null ? Wheel.Describe(row) : "";
                slot.Visible = true;
                slot.PerformLayout();
                invert.Visible = row != null && row.Analog;
                invert.Checked = row != null && row.Invert;
                rebind.Text = "Detect";
                reset.Text = "Clear";
            }
            else
            {
                rebind.Text = "Rebind";
                reset.Text = "Reset";
                int n = 0;
                var stick = StickSide(padTarget);
                if (stick == null)
                {
                    var row = Bindings.RowOf(padTarget);
                    if (row != null && !row.KeyboardOnly) PadSlot(n++, "Controller", padTarget, row.Axis);
                    if (row != null && !row.ControllerOnly) KeySlot(n++, "Keyboard", padTarget);
                }
                else if (Tab == 0)
                {
                    PadSlot(n++, "Left/right", "axis_" + stick + "_x", true);
                    PadSlot(n++, "Up/down", "axis_" + stick + "_y", true);
                    PadSlot(n++, "Press", stick == "left" ? "l3" : "r3", false);
                }
                else
                {
                    for (int i = 0; i < 4; ++i) KeySlot(n++, StickKeyNames[i], "axis_" + stick + StickKeys[i]);
                    KeySlot(n++, "Press", stick == "left" ? "l3" : "r3");
                }
            }
            filling = false;
            header.Invalidate();
            pad.Selected = padTarget;
            wheelView.Selected = wheelTarget;
            pad.Invalidate();
            wheelView.Invalidate();
            ShowHint();
        }

        void PadSlot(int n, string name, string output, bool axis)
        {
            var slot = slots[n];
            slot.Name_.Text = name;
            slot.Meter = false;
            slot.Two = false;
            Set(slot.First, PadChoices(axis, Xbox), bindings.PadOf(output));
            slotOutput[n] = output;
            slotPad[n] = true;
            slot.Visible = true;
            slot.PerformLayout();
        }

        void KeySlot(int n, string name, string output)
        {
            var slot = slots[n];
            slot.Name_.Text = name;
            slot.Meter = false;
            slot.Two = true;
            var keys = KeyChoices();
            Set(slot.First, keys, bindings.KeyOf(output, 0));
            Set(slot.Second, keys, bindings.KeyOf(output, 1));
            slotOutput[n] = output;
            slotPad[n] = false;
            slot.Visible = true;
            slot.PerformLayout();
        }

        void OnSlotChanged(int n, int which)
        {
            if (filling || Tab == 2) return;
            var box = which == 0 ? slots[n].First : slots[n].Second;
            var choice = box.SelectedItem as Choice;
            var value = choice != null ? choice.Value : null;
            if (slotPad[n]) bindings.SetPad(slotOutput[n], value);
            else bindings.SetKey(slotOutput[n], which, value);
            StopListening();
            SaveBindings();
            if (!slotPad[n]) Fill(); // the second key moves up when the first is cleared
        }

        void ShowHint()
        {
            string text;
            if (error != null) text = error;
            else if (Tab == 2)
            {
                text = wheel.Detecting != null ? wheel.Status + " (" + wheel.SecondsLeft + ")" : wheel.Status.Length > 0 && wheel.Status != "Looking for wheels..." ? wheel.Status : "Click or tap the part again to detect it.";
            }
            else if (listening == Listen.Pad)
            {
                text = (StickSide(padTarget) != null ? "Move the stick or press a button on your controller" : "Press a button on your controller") +
                       " (" + Math.Max(0, (int)Math.Ceiling((listenUntil - DateTime.Now).TotalSeconds)) + "). Esc cancels.";
            }
            else if (listening == Listen.Keys)
            {
                text = "Press the key for " + (StickSide(padTarget) != null ? StickKeyNames[4 - keyQueue.Count].ToLowerInvariant() : TargetName(padTarget)) + ". Esc cancels.";
            }
            else text = "Click or tap the button again to rebind it.";
            if (cardHint.Text != text) cardHint.Text = text;
            rebind.Text = Tab == 2 ? (wheel.Detecting != null ? "Waiting" : "Detect") : listening != Listen.None ? "Waiting" : "Rebind";
        }

        // ---------------------------------------------------------------- rebinding

        void StartRebind()
        {
            error = null;
            if (Tab == 2)
            {
                var row = wheel.RowOf(wheelTarget);
                if (row != null) wheel.StartDetect(row);
            }
            else if (Tab == 0)
            {
                listening = Listen.Pad;
                listenUntil = DateTime.Now.AddSeconds(6);
                listenBase = new Dictionary<int, Dictionary<string, float>>();
                foreach (var device in controllers) listenBase[device.Id] = Read(device);
            }
            else
            {
                listening = Listen.Keys;
                keyQueue.Clear();
                var stick = StickSide(padTarget);
                if (stick != null) keyQueue.AddRange(StickKeys.Select(k => "axis_" + stick + k));
                else keyQueue.Add(padTarget);
                Focus();
            }
            ShowHint();
        }

        void StopListening()
        {
            listening = Listen.None;
            keyQueue.Clear();
            wheel.CancelDetect();
        }

        void ResetTarget()
        {
            StopListening();
            if (Tab == 2)
            {
                var row = wheel.RowOf(wheelTarget);
                if (row != null) { row.Source = null; row.DeviceName = null; row.Invert = false; }
                SaveWheel();
            }
            else
            {
                foreach (var output in Outputs(padTarget)) bindings.Reset(output);
                SaveBindings();
            }
            Fill();
        }

        void ResetAll()
        {
            if (MessageBox.Show(FindForm(), "Put every button and key back to the defaults?", "ryGT7", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            StopListening();
            bindings.RestoreDefaults();
            SaveBindings();
            Fill();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (listening == Listen.None && (Tab != 2 || wheel.Detecting == null)) return base.ProcessCmdKey(ref msg, keyData);
            if ((keyData & Keys.KeyCode) == Keys.Escape) { StopListening(); ShowHint(); return true; }
            if (listening != Listen.Keys) return base.ProcessCmdKey(ref msg, keyData);
            var key = InputNames.FromKeys(keyData);
            if (key == null) return true;
            var output = keyQueue[0];
            keyQueue.RemoveAt(0);
            // The new key replaces the first one; the second stays (unless it is the same key).
            var second = bindings.KeyOf(output, 1);
            bindings.SetKey(output, 0, key);
            if (second == key) bindings.SetKey(output, 1, null);
            if (keyQueue.Count == 0) listening = Listen.None;
            SaveBindings();
            Fill();
            return true;
        }

        // ---------------------------------------------------------------- live input

        Dictionary<string, float> Read(Joysticks.Device device)
        {
            Joysticks.JoyInfoEx info;
            var result = new Dictionary<string, float>();
            if (!Joysticks.Read(device.Id, out info) || !PortsForm.Apply(state, device, info)) return result;
            var p = state;
            Action<string, bool> b = (name, on) => result[name] = on ? 1 : 0;
            b("cross", p.Cross); b("circle", p.Circle); b("square", p.Square); b("triangle", p.Triangle);
            b("l1", p.L1); b("r1", p.R1); b("l3", p.L3); b("r3", p.R3); b("back", p.Share || p.Touchpad); b("options", p.Options);
            b("pad_up", p.PadUp); b("pad_down", p.PadDown); b("pad_left", p.PadLeft); b("pad_right", p.PadRight);
            result["l2"] = p.L2; result["r2"] = p.R2;
            // Other controllers' triggers: their buttons (their axes there are often something else, resting halfway).
            if (!device.PlayStation && !device.Xbox)
            {
                result["l2"] = (info.Buttons & (1u << 6)) != 0 ? 1 : 0;
                result["r2"] = (info.Buttons & (1u << 7)) != 0 ? 1 : 0;
            }
            result["axis_left_x"] = p.LX; result["axis_left_y"] = p.LY; result["axis_right_x"] = p.RX; result["axis_right_y"] = p.RY;
            return result;
        }

        float Input(string name)
        {
            float v;
            return name != null && inputs.TryGetValue(name, out v) ? v : 0;
        }

        string PadLabel(string target)
        {
            var stick = StickSide(target);
            if (Tab == 0) return InputNames.PadShort(bindings.PadOf(stick != null ? (stick == "left" ? "l3" : "r3") : target), Xbox);
            if (stick == null) return InputNames.KeyShort(bindings.KeyOf(target, 0));
            // The four keys of a stick, as "W A S D" when they are letters.
            var keys = new[] { "_y_minus", "_x_minus", "_y_plus", "_x_plus" }.Select(k => bindings.KeyOf("axis_" + stick + k, 0)).ToArray();
            if (keys.All(k => k != null && k.Length == 1)) return string.Join(" ", keys.Select(k => k.ToUpperInvariant()).ToArray());
            return string.Join(" ", keys.Select(k => InputNames.KeyShort(k)).ToArray());
        }

        float PadLive(string target)
        {
            if (Tab != 0) return 0;
            var stick = StickSide(target);
            var input = bindings.PadOf(stick != null ? (stick == "left" ? "l3" : "r3") : target);
            return Math.Min(1, Input(input));
        }

        float WheelLive(string target)
        {
            var row = wheel.RowOf(target);
            if (row == null || row.Source == null) return 0;
            lock (wheel.Gate) return wheel.Live(row) / 1000f;
        }

        /// What the game reads from a trigger: its input past the deadzone, 0 to 1.
        static float AfterDeadzone(float raw, int deadzone)
        {
            float v = raw * 127;
            return v <= deadzone ? 0 : Math.Min(1, (v - deadzone) / (127 - deadzone));
        }

        void Tick()
        {
            if (Tab == 2) { TickWheel(); return; }
            if (--rescan <= 0)
            {
                rescan = 60;
                controllers = Joysticks.Connected();
                if (active == null || !controllers.Any(d => d.Key == active.Key))
                {
                    bool wasXbox = Xbox;
                    active = controllers.FirstOrDefault();
                    if (Xbox != wasXbox) Fill();
                }
            }
            // The controller last used is the one shown.
            var now = new Dictionary<int, Dictionary<string, float>>();
            foreach (var device in controllers)
            {
                now[device.Id] = Read(device);
                if (device != active && now[device.Id].Any(p => !p.Key.StartsWith("axis") && p.Value > 0.5f))
                {
                    bool wasXbox = Xbox;
                    active = device;
                    if (Xbox != wasXbox) Fill();
                }
            }
            inputs = active != null && now.ContainsKey(active.Id) ? now[active.Id] : new Dictionary<string, float>();

            if (Tab == 0)
                pill.Show(active != null, active != null ? active.Name : "No controller", active != null ? "Connected" : "Plug one in");
            else
                pill.Show(true, "Keyboard", "Plays as player " + (keyboardPlayer.SelectedIndex + 1));

            pad.LeftStick = new PointF(Input(bindings.PadOf("axis_left_x")), Input(bindings.PadOf("axis_left_y")));
            pad.RightStick = new PointF(Input(bindings.PadOf("axis_right_x")), Input(bindings.PadOf("axis_right_y")));
            if (Tab == 1) pad.LeftStick = pad.RightStick = PointF.Empty;
            pad.Invalidate();
            l2Meter.Value = AfterDeadzone(Input(bindings.PadOf("l2")), bindings.L2Deadzone);
            r2Meter.Value = AfterDeadzone(Input(bindings.PadOf("r2")), bindings.R2Deadzone);

            if (listening == Listen.Pad) StepListen(now);
        }

        void StepListen(Dictionary<int, Dictionary<string, float>> now)
        {
            if (DateTime.Now > listenUntil) { listening = Listen.None; error = null; cardHint.Text = "Nothing pressed."; rebind.Text = "Rebind"; return; }
            var stick = StickSide(padTarget);
            foreach (var pair in now)
            {
                Dictionary<string, float> before;
                if (!listenBase.TryGetValue(pair.Key, out before)) continue;
                foreach (var input in pair.Value)
                {
                    float was;
                    before.TryGetValue(input.Key, out was);
                    bool axis = input.Key.StartsWith("axis_");
                    bool pressed = axis ? Math.Abs(input.Value) > 0.7f && Math.Abs(was) < 0.4f : input.Value > 0.6f && was < 0.3f;
                    if (!pressed || (axis && stick == null)) continue;
                    if (stick == null) bindings.SetPad(padTarget, input.Key);
                    else if (axis) bindings.SetPad("axis_" + stick + (input.Key.EndsWith("_x") ? "_x" : "_y"), input.Key);
                    else bindings.SetPad(stick == "left" ? "l3" : "r3", input.Key);
                    listening = Listen.None;
                    active = controllers.FirstOrDefault(d => d.Id == pair.Key) ?? active;
                    SaveBindings();
                    Fill();
                    return;
                }
            }
            ShowHint();
        }

        void TickWheel()
        {
            List<Wheel.Device> found;
            Wheel.Device primary;
            bool detected;
            string raw;
            float steering = 0;
            lock (wheel.Gate)
            {
                found = wheel.Devices;
                primary = wheel.Primary();
                detected = wheel.StepDetect();
                raw = wheel.RawInput();
                var row = wheel.RowOf("Steering");
                if (row.Source != null) steering = wheel.Live(row) / 1000f - 0.5f;
            }
            pill.Show(primary != null, primary != null ? primary.Name : "No wheel found",
                      primary != null ? (primary.IsWheel ? "Wheel connected" : "Joystick connected") : wheel.Probing ? "Plug it in and start its software" : "Probe not running");
            if (rawInput.Text != raw) rawInput.Text = raw.Length > 0 ? raw : "Nothing yet.";
            // The drawn wheel turns as far as yours (half the rotation each way).
            wheelView.WheelAngle = steering * wheel.Rotation;
            wheelView.Invalidate();
            if (slots[0].Visible && slots[0].Meter)
            {
                slots[0].Bar.Value = (int)(WheelLive(wheelTarget) * 1000);
            }
            if (detected) { SaveWheel(); Fill(); }
            else ShowHint();
        }
    }
}
