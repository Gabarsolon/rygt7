// FEAT-GTS-254 (ryGT7; from RyGTS FEAT-GTS-437..467): the Advanced page. The experimental renderer and runtime switches (RYGT_* and SHADGT_* environment
// variables read by shadGT and RyGTS) as one table with a description on hover, and a table for any
// other variable. All of them are saved in RyGT7.ini as env= lines and set for GT7.exe when the
// launcher starts it (CustomEnvironment.Apply).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace RyGT
{
    /// One RYGT_* or SHADGT_* switch found in the renderer (libSceVideoOut.prx).
    class RendererSwitch
    {
        public string Name = "", Kind = "", Default = "", Values = "", Tooltip = "", Group = "";

        public bool IsFlag { get { return Kind == "flag"; } }
        public bool Diagnostic { get { return Group == SwitchWalker.DiagnosticsGroup; } }
        public bool Described { get { return Tooltip.Length > 0; } }
    }

    /// FEAT-GTS-437: finds the switches in the renderer file instead of a list in the launcher, so a
    /// new switch appears as soon as the renderer reads it. Reads the file (never loads it) and
    /// collects:
    /// - every string that is exactly RYGT_ or SHADGT_ and capitals, digits and _ (the names the renderer passes
    ///   to getenv), and
    /// - every "RYGTSW1|NAME|kind|group|default|values|description" string: the renderer's own
    ///   description of a switch (src/video_core/rygt_switches.cpp in shadgt7).
    /// A switch without a description is still listed; a description without the name is not.
    static class SwitchWalker
    {
        // FIX-GTS-439: the game loads the renderer from libs\, so read that copy, not one in the game folder.
        public const string RendererFile = @"libs\libSceVideoOut.prx";
        public const string DiagnosticsGroup = "Diagnostics & tracing";
        public const string UndescribedGroup = "Not described in this renderer build";
        const string MetadataPrefix = "RYGTSW1|";

        /// Set by the launcher itself from its own settings; changing them here would be overwritten.
        static readonly HashSet<string> LauncherManaged = new HashSet<string>
        {
            // Set by ryGT7's StartGame every time (from its own settings); the Advanced page would only
            // override them. The ones it sets only when an option is checked (RYGT_MANIFEST_*,
            // RYGT_COMPILE_PACK) stay listed.
            "RYGT_LOG_DIRECTORY", "RYGT_NATIVE_INPLACE_RANGES", "RYGT_NATIVE_INPLACE_FROM", "SHADGT_FPS_LOCK",
            "RYGT_DRIVER_CACHE_MAX_MB", "RYGT_AMD_SINGLE_SAMPLE_BARYCENTRIC", "RYGT_DLSSG_FRAMES",
            "RYGT_UPSCALE_QUALITY", "RYGT_TEXTURE_LOD_BIAS",
            // PS4 Pro mode on the Graphics page (NEO-GT7, #33) sets or removes these itself; set here they
            // would silently override it.
            "RYGT_NEO", "RYGT_NEO_TV", "RYGT_NEO_EXTRAS"
        };

        static readonly string[] GroupOrder =
        {
            "Shader objects & caches", "Shader compiling", "Native recorder & sync", "Memory & VRAM", "Tessellation",
            "Frame pacing & present", "Rendering workarounds", "AMD", "NVIDIA", "Runtime", DiagnosticsGroup, UndescribedGroup
        };

        public static List<RendererSwitch> Walk(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            var names = new HashSet<string>(StringComparer.Ordinal);
            var described = new Dictionary<string, RendererSwitch>(StringComparer.Ordinal);
            for (int i = 0; i + 5 < data.Length; ++i)
            {
                // Strings may follow other data directly (the compiler packs them), so only a name
                // character right before rules a match out.
                if ((data[i] != (byte)'R' && data[i] != (byte)'S') || (i > 0 && IsNameByte(data[i - 1]))) continue;
                if (!StartsWith(data, i, "RYGT") && !StartsWith(data, i, "SHADGT_")) continue;
                int end = i;
                while (end < data.Length && data[end] != 0 && end - i < 4096) ++end;
                if (end >= data.Length || data[end] != 0) continue;
                string text = Encoding.UTF8.GetString(data, i, end - i);
                i = end;
                if (text.StartsWith(MetadataPrefix, StringComparison.Ordinal))
                {
                    var entry = Parse(text.Substring(MetadataPrefix.Length));
                    if (entry != null) described[entry.Name] = entry;
                }
                else if (IsSwitchName(text))
                    names.Add(text);
            }

            var list = new List<RendererSwitch>();
            // Only names the renderer really reads: a description alone (e.g. for a switch that only a
            // diagnostics build reads) adds nothing.
            foreach (var name in names)
            {
                if (LauncherManaged.Contains(name)) continue;
                RendererSwitch entry;
                if (!described.TryGetValue(name, out entry)) entry = new RendererSwitch { Name = name, Group = UndescribedGroup };
                list.Add(entry);
            }
            Func<string, int> order = group => { int i = Array.IndexOf(GroupOrder, group); return i < 0 ? GroupOrder.Length - 2 : i; };
            return list.OrderBy(s => order(s.Group)).ThenBy(s => s.Group, StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.Ordinal).ToList();
        }

        /// The prefixes of the renderer's switches: RYGT_ (ryGT's) and SHADGT_ (shadGT's, FEAT-GTS-443).
        static readonly string[] NamePrefixes = { "RYGT_", "SHADGT_" };

        static bool IsSwitchName(string text)
        {
            if (!CustomEnvironment.IsValidName(text) || text.ToUpperInvariant() != text) return false;
            foreach (var prefix in NamePrefixes)
                if (text.Length > prefix.Length && text.StartsWith(prefix, StringComparison.Ordinal)) return true;
            return false;
        }

        static bool StartsWith(byte[] data, int at, string prefix)
        {
            if (at + prefix.Length > data.Length) return false;
            for (int k = 0; k < prefix.Length; ++k)
                if (data[at + k] != (byte)prefix[k]) return false;
            return true;
        }

        static bool IsNameByte(byte b)
        {
            return b == '_' || (b >= '0' && b <= '9') || (b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z');
        }

        /// NAME|kind|group|default|values|description (the description may contain |).
        static RendererSwitch Parse(string text)
        {
            var parts = text.Split(new[] { '|' }, 6);
            if (parts.Length < 6 || !CustomEnvironment.IsValidName(parts[0])) return null;
            return new RendererSwitch
            {
                Name = parts[0], Kind = parts[1].Trim().ToLowerInvariant(), Group = parts[2].Trim().Length > 0 ? parts[2].Trim() : "Other",
                Default = parts[3].Trim(), Values = parts[4].Trim(), Tooltip = parts[5].Trim()
            };
        }
    }

    /// FIX-GTS-440: Tab and Shift+Tab go to the next or previous switch's setting (skipping the
    /// "When not set" column, headings and hidden rows), also while typing in a value; past the
    /// last switch Tab leaves the table.
    sealed class SwitchGrid : DataGridView
    {
        public Func<DataGridViewRow, bool> IsSwitch;

        protected override bool ProcessDialogKey(Keys keyData)
        {
            if ((keyData & Keys.KeyCode) == Keys.Tab && (keyData & (Keys.Control | Keys.Alt)) == 0)
                return MoveToSwitch((keyData & Keys.Shift) != 0);
            return base.ProcessDialogKey(keyData);
        }

        protected override bool ProcessDataGridViewKey(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Tab && !e.Control && !e.Alt) return MoveToSwitch(e.Shift);
            return base.ProcessDataGridViewKey(e);
        }

        bool MoveToSwitch(bool back)
        {
            int step = back ? -1 : 1;
            int start = CurrentCell != null ? CurrentCell.RowIndex : (back ? Rows.Count : -1);
            for (int i = start + step; i >= 0 && i < Rows.Count; i += step)
            {
                var row = Rows[i];
                if (!row.Visible || IsSwitch == null || !IsSwitch(row)) continue;
                CurrentCell = row.Cells[1]; // ends an edit in progress first
                return true;
            }
            // No switch further on: leave the table like any other control.
            if (IsCurrentCellInEditMode) EndEdit();
            var form = FindForm();
            if (form != null) form.SelectNextControl(this, !back, true, true, true);
            return true;
        }
    }

    sealed class RowComparer : System.Collections.IComparer
    {
        readonly Comparison<DataGridViewRow> compare;
        public RowComparer(Comparison<DataGridViewRow> compare) { this.compare = compare; }
        public int Compare(object a, object b) { return compare((DataGridViewRow)a, (DataGridViewRow)b); }
    }

    static class AdvancedPage
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, string lParam);

        static readonly Color BoxFill = Color.FromArgb(54, 54, 54), BoxEdge = Color.FromArgb(78, 78, 78);

        /// Builds the page into `page`. `env` is the saved list (RyGT.ini env= lines); `save` gets the
        /// new full list after every change.
        /// FEAT-GTS-467: what Refresh keeps of the page while it reads the renderer again.
        sealed class ViewState
        {
            public string Search = "";
            public bool Diagnostics, OnlyChanged;
        }

        public static void Build(TableLayoutPanel page, Control title, Color cardFill, string root, List<EnvVar> env, Action<List<EnvVar>> save)
        {
            Build(page, title, cardFill, root, env, save, null);
        }

        static void Build(TableLayoutPanel page, Control title, Color cardFill, string root, List<EnvVar> env, Action<List<EnvVar>> save,
                          ViewState view)
        {
            string rendererPath = Path.Combine(root, SwitchWalker.RendererFile);
            // The list as last saved, for Refresh (the env passed in is the one at build time).
            var savedEnv = env;
            Action<List<EnvVar>> keep = list => { savedEnv = list; save(list); };
            List<RendererSwitch> catalog;
            string walkError = null;
            try { catalog = SwitchWalker.Walk(rendererPath); }
            catch (Exception e) { catalog = new List<RendererSwitch>(); walkError = e.Message; }
            var known = new HashSet<string>(catalog.Select(s => s.Name));
            string rendererDate = "";
            try
            {
                if (File.Exists(rendererPath))
                    rendererDate = " \u00B7 renderer from " + File.GetLastWriteTime(rendererPath).ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
            }
            catch { }
            // name -> value for the switches in the table; everything else stays in the other table.
            var values = new Dictionary<string, string>();
            var others = new List<EnvVar>();
            foreach (var entry in env)
            {
                // An unticked row stays in the other table (unticked) rather than being lost.
                if (known.Contains(entry.Name) && entry.Enabled) values[entry.Name] = entry.Value;
                else others.Add(entry);
            }
            Action store = delegate
            {
                var list = new List<EnvVar>();
                foreach (var s in catalog)
                {
                    string value;
                    if (values.TryGetValue(s.Name, out value)) list.Add(new EnvVar { Name = s.Name, Value = value });
                }
                list.AddRange(others);
                keep(list);
            };

            page.ColumnCount = 1;
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            page.Controls.Add(title);

            // The search box, the filters and Reset all.
            var bar = new FlowLayoutPanel { AutoSize = true, WrapContents = true, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 8) };
            var searchField = new Card(Dark.Field) { AutoSize = false, Width = 260, Height = 30, Radius = 8, Edge = Color.Empty, Padding = new Padding(10, 7, 6, 6), Margin = new Padding(0, 0, 14, 0) };
            var search = new TextBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Margin = new Padding(0), AccessibleName = "Search switches" };
            // FEAT-GTS-441: a clear button at the box's right edge, shown while there is text; Esc
            // in the box clears it too.
            var clearSearch = new Label
            {
                Text = "\u2715", AutoSize = false, Size = new Size(18, 17), TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI Symbol", 8f), ForeColor = Dark.DimText, BackColor = Dark.Field, Tag = "heading",
                Cursor = Cursors.Hand, Margin = new Padding(4, 0, 0, 0), Visible = false,
                AccessibleName = "Clear search", AccessibleRole = AccessibleRole.PushButton
            };
            clearSearch.MouseEnter += delegate { clearSearch.ForeColor = Dark.Text; };
            clearSearch.MouseLeave += delegate { clearSearch.ForeColor = Dark.DimText; };
            clearSearch.Click += delegate { search.Clear(); search.Focus(); };
            search.KeyDown += (sender, e) =>
            {
                if (e.KeyCode != Keys.Escape || search.TextLength == 0) return;
                search.Clear();
                e.Handled = e.SuppressKeyPress = true;
            };
            search.TextChanged += delegate { clearSearch.Visible = search.TextLength > 0; };
            searchField.ColumnStyles.Clear();
            searchField.ColumnCount = 2;
            searchField.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            searchField.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            searchField.Controls.Add(search, 0, 0);
            searchField.Controls.Add(clearSearch, 1, 0);
            // The grey hint in the empty box (EM_SETCUEBANNER).
            search.HandleCreated += delegate { SendMessage(search.Handle, 0x1501, new IntPtr(1), "Search names and descriptions"); };
            var showDiagnostics = new DarkCheckBox { Text = "Show diagnostics and tracing", AutoSize = true, Margin = new Padding(0, 6, 14, 0) };
            var onlyChanged = new DarkCheckBox { Text = "Only changed", AutoSize = true, Margin = new Padding(0, 6, 14, 0) };
            var reset = new Button { Text = "Reset all", AutoSize = true, Margin = new Padding(0, 1, 0, 0) };
            // FEAT-GTS-467: read the renderer again (after a rebuild or a new libs\libSceVideoOut.prx)
            // without restarting the launcher.
            var refresh = new Button { Text = "Refresh", AutoSize = true, Margin = new Padding(8, 1, 0, 0), AccessibleName = "Refresh switches" };
            var barTips = new ToolTip();
            barTips.SetToolTip(refresh, "Read the switches and their descriptions again from " + SwitchWalker.RendererFile +
                                        ", e.g. after the renderer was rebuilt. Your settings are kept.");
            refresh.Disposed += delegate { barTips.Dispose(); };
            var count = new Label { AutoSize = true, ForeColor = Dark.DimText, Tag = "heading", BackColor = Color.Transparent, Margin = new Padding(14, 7, 0, 0) };
            bar.Controls.Add(searchField);
            bar.Controls.Add(showDiagnostics);
            bar.Controls.Add(onlyChanged);
            bar.Controls.Add(reset);
            bar.Controls.Add(refresh);
            bar.Controls.Add(count);
            page.Controls.Add(bar);

            // The switches: a group heading row, then one row per switch.
            var card = new Card(cardFill) { Dock = DockStyle.Fill, Padding = new Padding(12), Margin = new Padding(0, 0, 0, 12) };
            card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var grid = CustomEnvironment.DarkGrid<SwitchGrid>("Experimental switches");
            grid.Dock = DockStyle.Fill;
            grid.Margin = new Padding(0);
            grid.AllowUserToDeleteRows = false;
            grid.MultiSelect = false;
            grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
            grid.EditMode = DataGridViewEditMode.EditOnEnter;
            grid.ShowCellToolTips = false; // FIX-GTS-444: the page's own tool tip, below
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "Switch", ReadOnly = true, FillWeight = 40, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.Programmatic });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "setting", HeaderText = "Setting", FillWeight = 22, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.Programmatic });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "default", HeaderText = "When not set", ReadOnly = true, FillWeight = 38, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.Programmatic });
            grid.Columns[2].DefaultCellStyle.ForeColor = Dark.DimText;
            grid.Columns[0].DefaultCellStyle.Font = new Font("Consolas", 9f);
            grid.Columns[1].DefaultCellStyle.Padding = new Padding(9, 5, 9, 5); // the editor inside the drawn box

            var headingStyle = new DataGridViewCellStyle
            {
                BackColor = Dark.Button, SelectionBackColor = Dark.Button, ForeColor = Dark.Text, SelectionForeColor = Dark.Text,
                Font = new Font("Segoe UI Semibold", 9f)
            };
            var rowSwitch = new Dictionary<DataGridViewRow, RendererSwitch>();
            var headings = new List<KeyValuePair<DataGridViewRow, string>>();
            bool loading = true;
            string group = null;
            foreach (var s in catalog)
            {
                if (s.Group != group)
                {
                    group = s.Group;
                    int h = grid.Rows.Add(group, "", "");
                    grid.Rows[h].Tag = h;
                    var heading = grid.Rows[h];
                    heading.ReadOnly = true;
                    heading.DefaultCellStyle = headingStyle;
                    headings.Add(new KeyValuePair<DataGridViewRow, string>(heading, group));
                }
                int index = grid.Rows.Add(s.Name, "", s.Default);
                grid.Rows[index].Tag = index;
                var row = grid.Rows[index];
                rowSwitch[row] = s;
                string current;
                values.TryGetValue(s.Name, out current);
                // An on/off switch is a toggle (clicked, not typed into); the cell holds "1", "0" or "".
                row.Cells[1].Value = current ?? "";
                row.Cells[1].ReadOnly = s.IsFlag;
                string tip = Tip(s);
                foreach (DataGridViewCell cell in row.Cells) cell.ToolTipText = tip;
                Mark(row, current != null);
            }
            loading = false;
            card.Controls.Add(grid);
            page.Controls.Add(card);
            page.RowStyles.Clear();
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            int sortColumn = -1; // -1: grouped (the order the renderer's groups give)
            bool sortDescending = false;
            int lastShown = 0;
            Action filter = delegate
            {
                string text = search.Text.Trim();
                int shown = 0;
                grid.SuspendLayout();
                var visibleGroups = new HashSet<string>();
                if (grid.CurrentCell != null && grid.CurrentCell.RowIndex >= 0) grid.CurrentCell = null;
                foreach (var pair in rowSwitch)
                {
                    var s = pair.Value;
                    bool changed = values.ContainsKey(s.Name);
                    bool visible = (showDiagnostics.Checked || !s.Diagnostic || changed) && (!onlyChanged.Checked || changed) &&
                                   (text.Length == 0 || s.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    s.Tooltip.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);
                    pair.Key.Visible = visible;
                    if (visible) { ++shown; visibleGroups.Add(s.Group); }
                }
                foreach (var heading in headings) heading.Key.Visible = sortColumn < 0 && visibleGroups.Contains(heading.Value);
                grid.ResumeLayout();
                count.Text = walkError != null ? "Could not read " + SwitchWalker.RendererFile + ": " + walkError
                           : catalog.Count == 0 ? "No RYGT_ or SHADGT_ switches found in " + SwitchWalker.RendererFile
                           : shown + " of " + catalog.Count + " switches, " + values.Count + " changed" + rendererDate;
                lastShown = shown;
            };

            Action<DataGridViewRow> apply = row =>
            {
                RendererSwitch s;
                if (loading || !rowSwitch.TryGetValue(row, out s)) return;
                string text = ((row.Cells[1].Value as string) ?? "").Trim();
                string value = text;
                string error = value.Length == 0 || Valid(s, value) ? "" : "Not saved: " + KindHint(s) + ".";
                row.ErrorText = error;
                if (value.Length == 0 || error.Length > 0) values.Remove(s.Name);
                else values[s.Name] = value;
                Mark(row, values.ContainsKey(s.Name));
                store();
                if (walkError == null && catalog.Count > 0)
                    count.Text = lastShown + " of " + catalog.Count + " switches, " + values.Count + " changed" + rendererDate;
            };
            grid.CellValueChanged += (sender, e) => { if (e.RowIndex >= 0 && e.ColumnIndex == 1) apply(grid.Rows[e.RowIndex]); };
            grid.DataError += (sender, e) => { e.ThrowException = false; };
            grid.IsSwitch = row => rowSwitch.ContainsKey(row);

            // FIX-GTS-444: a switch's description stays up while the pointer rests on its row (the
            // grid's own cell tool tips vanish after about 5 seconds). Shown after the usual pause,
            // without a time limit; hidden when the pointer leaves the row or the table, or on a
            // click, a key, a scroll or a page change.
            var switchTip = new ToolTip { UseAnimation = false, UseFading = false };
            var tipDelay = new Timer { Interval = SystemInformation.MouseHoverTime };
            int tipRow = -1, pendingRow = -1;
            Action hideTip = delegate
            {
                tipDelay.Stop();
                pendingRow = -1;
                if (tipRow < 0) return;
                tipRow = -1;
                switchTip.Hide(grid);
            };
            grid.CellMouseEnter += (sender, e) =>
            {
                if (e.RowIndex == tipRow && tipRow >= 0) return; // another cell of the same row
                hideTip();
                if (e.RowIndex < 0 || !rowSwitch.ContainsKey(grid.Rows[e.RowIndex])) return;
                pendingRow = e.RowIndex;
                tipDelay.Start();
            };
            tipDelay.Tick += delegate
            {
                tipDelay.Stop();
                int row = pendingRow;
                pendingRow = -1;
                if (row < 0 || row >= grid.Rows.Count) return;
                var at = grid.PointToClient(Cursor.Position);
                var hit = grid.HitTest(at.X, at.Y);
                if (hit.RowIndex != row) return;
                string text = grid.Rows[row].Cells[0].ToolTipText;
                if (string.IsNullOrEmpty(text)) return;
                tipRow = row;
                switchTip.Show(text, grid, at.X + 14, at.Y + 22); // no duration: stays until hidden
            };
            grid.MouseLeave += delegate { hideTip(); };
            grid.MouseDown += delegate { hideTip(); };
            grid.KeyDown += delegate { hideTip(); };
            grid.Scroll += delegate { hideTip(); };
            grid.VisibleChanged += delegate { if (!grid.Visible) hideTip(); };
            grid.Disposed += delegate { tipDelay.Dispose(); switchTip.Dispose(); };

            // Every setting looks like what it is: an on/off switch as a toggle, any other switch (a number,
            // a mask, a name) as a text box, with the default in grey when empty.
            grid.RowTemplate.Height = 26;
            foreach (DataGridViewRow row in grid.Rows) row.Height = 26;
            grid.CellPainting += (sender, e) =>
            {
                if (e.RowIndex < 0 || e.ColumnIndex != 1) return;
                RendererSwitch s;
                if (!rowSwitch.TryGetValue(grid.Rows[e.RowIndex], out s)) return;
                e.PaintBackground(e.ClipBounds, (e.State & DataGridViewElementStates.Selected) != 0);
                if (s.IsFlag)
                {
                    PaintToggle(e.Graphics, e.CellBounds, s, Convert.ToString(e.Value) ?? "", grid.Font);
                    e.Handled = true;
                    return;
                }
                var box = new Rectangle(e.CellBounds.X + 4, e.CellBounds.Y + 3, e.CellBounds.Width - 10, e.CellBounds.Height - 7);
                var g = e.Graphics;
                var smoothing = g.SmoothingMode;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                bool hot = grid.CurrentCell != null && grid.CurrentCell.RowIndex == e.RowIndex && grid.CurrentCell.ColumnIndex == 1;
                Dark.FillRounded(g, BoxFill, box, 4);
                using (var path = Dark.RoundedRect(new RectangleF(box.X + 0.5f, box.Y + 0.5f, box.Width - 1, box.Height - 1), 4))
                using (var pen = new Pen(hot ? Dark.AccentText : BoxEdge))
                    g.DrawPath(pen, path);
                g.SmoothingMode = smoothing;
                string text = Convert.ToString(e.FormattedValue) ?? "";
                bool placeholder = false;
                var textArea = new Rectangle(box.X + 6, box.Y, box.Width - 12, box.Height);
                if (text.Length == 0)
                {
                    text = s.Described && s.Default.Length > 0 ? s.Default : "value";
                    placeholder = true;
                }
                var color = placeholder ? Color.FromArgb(120, 120, 120) : e.CellStyle.ForeColor;
                TextRenderer.DrawText(g, text, placeholder ? grid.Font : e.CellStyle.Font, textArea, color,
                                      TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                e.Handled = true;
            };
            // The editor sits inside the same box.
            grid.EditingControlShowing += (sender, e) =>
            {
                e.Control.BackColor = BoxFill;
                e.Control.ForeColor = Dark.Text;
                var text = e.Control as TextBox;
                if (text != null) text.BorderStyle = BorderStyle.None;
            };
            // Clicking a toggle (or Space on it) flips it. Flipping back to the default state clears the
            // setting, so only real changes are saved.
            Action<int> toggle = rowIndex =>
            {
                RendererSwitch s;
                if (rowIndex < 0 || !rowSwitch.TryGetValue(grid.Rows[rowIndex], out s) || !s.IsFlag) return;
                var cell = grid.Rows[rowIndex].Cells[1];
                bool defaultKnown;
                bool defaultOn = DefaultOn(s, out defaultKnown);
                string now = Convert.ToString(cell.Value) ?? "";
                if (!defaultKnown)
                {
                    // Default (unset) -> On -> Off -> Default: the default depends on the GPU or a setting.
                    cell.Value = now.Length == 0 ? "1" : now == "0" ? "" : "0";
                    return;
                }
                bool on = !IsOn(now, defaultOn);
                cell.Value = on == defaultOn ? "" : on ? "1" : "0";
            };
            grid.CellMouseClick += (sender, e) => { if (e.Button == MouseButtons.Left && e.ColumnIndex == 1) toggle(e.RowIndex); };
            grid.CellMouseDoubleClick += (sender, e) => { if (e.Button == MouseButtons.Left && e.ColumnIndex == 1) toggle(e.RowIndex); };
            grid.CellMouseMove += (sender, e) =>
            {
                RendererSwitch s;
                bool hand = e.RowIndex >= 0 && e.ColumnIndex == 1 && rowSwitch.TryGetValue(grid.Rows[e.RowIndex], out s) && s.IsFlag;
                grid.Cursor = hand ? Cursors.Hand : Cursors.Default;
            };
            grid.CellEnter += (sender, e) => { if (e.ColumnIndex == 1) grid.InvalidateCell(e.ColumnIndex, e.RowIndex); };
            grid.CellLeave += (sender, e) => { if (e.ColumnIndex == 1) grid.InvalidateCell(e.ColumnIndex, e.RowIndex); };
            // Delete or Backspace on a setting returns it to the default.
            grid.KeyDown += (sender, e) =>
            {
                var cell = grid.CurrentCell;
                if (cell == null || cell.ColumnIndex != 1 || grid.IsCurrentCellInEditMode || !rowSwitch.ContainsKey(grid.Rows[cell.RowIndex])) return;
                if (e.KeyCode == Keys.Space && cell.ReadOnly) { toggle(cell.RowIndex); e.Handled = true; return; }
                if (e.KeyCode != Keys.Delete && e.KeyCode != Keys.Back) return;
                cell.Value = "";
                e.Handled = true;
            };

            search.TextChanged += delegate { filter(); };
            showDiagnostics.CheckedChanged += delegate { filter(); };
            onlyChanged.CheckedChanged += delegate { filter(); };
            reset.Click += delegate
            {
                if (values.Count == 0) return;
                if (MessageBox.Show(page.FindForm(), "Return all " + values.Count + " changed switches to their defaults?", "Reset all",
                                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                loading = true;
                foreach (var pair in rowSwitch)
                {
                    pair.Key.Cells[1].Value = "";
                    pair.Key.ErrorText = "";
                    Mark(pair.Key, false);
                }
                loading = false;
                values.Clear();
                store();
                filter();
            };
            // FEAT-GTS-437: a header click sorts by that column: ascending, descending, then back to the
            // groups. Setting sorts On toggles and filled-in values first (ascending), then Off and empty.
            Func<DataGridViewRow, int> settingRank = row =>
            {
                RendererSwitch s;
                if (!rowSwitch.TryGetValue(row, out s)) return 2;
                string value = Convert.ToString(row.Cells[1].Value) ?? "";
                if (!s.IsFlag) return value.Length > 0 ? 0 : 1;
                bool defaultKnown;
                return IsOn(value, DefaultOn(s, out defaultKnown)) ? 0 : 1;
            };
            Comparison<DataGridViewRow> compare = (a, b) =>
            {
                if (sortColumn < 0) return ((int)a.Tag).CompareTo((int)b.Tag);
                // Headings (hidden while sorted) go last, in their own order.
                bool ha = !rowSwitch.ContainsKey(a), hb = !rowSwitch.ContainsKey(b);
                if (ha || hb) return ha && hb ? ((int)a.Tag).CompareTo((int)b.Tag) : ha ? 1 : -1;
                int result;
                if (sortColumn == 1) result = settingRank(a).CompareTo(settingRank(b));
                else result = string.Compare(Convert.ToString(a.Cells[sortColumn].Value), Convert.ToString(b.Cells[sortColumn].Value), StringComparison.OrdinalIgnoreCase);
                if (sortDescending) result = -result;
                // Ties by name, always A to Z.
                return result != 0 ? result : string.CompareOrdinal(Convert.ToString(a.Cells[0].Value), Convert.ToString(b.Cells[0].Value));
            };
            grid.ColumnHeaderMouseClick += (sender, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                if (grid.IsCurrentCellInEditMode) grid.EndEdit();
                if (sortColumn != e.ColumnIndex) { sortColumn = e.ColumnIndex; sortDescending = false; }
                else if (!sortDescending) sortDescending = true;
                else sortColumn = -1;
                foreach (DataGridViewColumn column in grid.Columns)
                    column.HeaderCell.SortGlyphDirection = column.Index != sortColumn ? SortOrder.None
                                                         : sortDescending ? SortOrder.Descending : SortOrder.Ascending;
                grid.CurrentCell = null;
                grid.Sort(new RowComparer(compare));
                filter();
                // FIX-GTS-442: back to the top, the first row still shown (row 0 is a heading, hidden
                // while sorted, and the table throws for a hidden row).
                int top = grid.Rows.GetFirstRow(DataGridViewElementStates.Visible);
                if (top >= 0 && grid.FirstDisplayedScrollingRowIndex != top) grid.FirstDisplayedScrollingRowIndex = top;
            };
            filter();

            // Any other variable: names the table does not list.
            var otherCard = new Card(cardFill) { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(16, 12, 16, 14), Margin = new Padding(0) };
            otherCard.Controls.Add(new Label { Text = "Other environment variables", AutoSize = true, Font = new Font("Segoe UI Semibold", 11f), Margin = new Padding(0, 0, 0, 8) });
            Control otherEditor;
            otherCard.Controls.Add(otherEditor = CustomEnvironment.Editor(others, list =>
            {
                // A switch typed here belongs in the table above: kept here until the next start.
                others = list;
                store();
            }));
            page.Controls.Add(otherCard);
            Action cap = delegate
            {
                otherCard.MaximumSize = new Size(0, Math.Max(120, page.ClientSize.Height / 3));
                var refit = otherEditor.Tag as Action;
                if (refit != null) refit();
            };
            EventHandler onResize = delegate { cap(); };
            page.Resize += onResize;
            cap();

            // FEAT-GTS-467: Refresh builds the page again from a fresh read of the renderer, keeping the
            // saved settings (as last saved), the search text and the two filters.
            refresh.Click += delegate
            {
                if (grid.IsCurrentCellInEditMode) grid.EndEdit();
                var keepView = new ViewState { Search = search.Text, Diagnostics = showDiagnostics.Checked, OnlyChanged = onlyChanged.Checked };
                var form = page.FindForm();
                page.SuspendLayout();
                page.Resize -= onResize;
                var old = new List<Control>();
                foreach (Control control in page.Controls) if (control != title) old.Add(control);
                page.Controls.Clear();
                page.RowStyles.Clear();
                page.ColumnStyles.Clear();
                foreach (var control in old) control.Dispose();
                Build(page, title, cardFill, root, savedEnv, save, keepView);
                Dark.Apply(page);
                if (cardFill.A < 255) Dark.SeeThrough(page);
                page.ResumeLayout(true);
                page.Invalidate(true);
                if (form != null) form.ActiveControl = null;
            };
            if (view != null)
            {
                search.Text = view.Search;
                showDiagnostics.Checked = view.Diagnostics;
                onlyChanged.Checked = view.OnlyChanged;
            }
        }

        static readonly string[] OnWords = { "on", "1", "enabled", "true", "yes" };
        static readonly string[] OffWords = { "off", "0", "disabled", "false", "no", "unset", "not set" };

        static bool StartsWithWord(string text, string word)
        {
            if (!text.StartsWith(word, StringComparison.Ordinal)) return false;
            return text.Length == word.Length || !char.IsLetterOrDigit(text[word.Length]);
        }

        /// Whether a switch is on when not set, from its description's default; `known` is false when
        /// the default says neither (then it is drawn off).
        /// Words that make a default depend on something (the GPU, another setting, the build): such a
        /// switch gets Default / On / Off instead of a plain toggle (FEAT-GTS-254 review).
        static readonly string[] ConditionalWords =
        {
            "with", "when", "if", "unless", "only", "except", "elsewhere", "otherwise", "depend", "nvidia", "amd",
            "intel", "radeon", "gcn", "rdna", "driver", "vk_", "extension", "native", "fsr", "dlss", "frame gen",
            "launcher", "setting", "mode", "build", "older", "newer"
        };

        static bool DefaultOn(RendererSwitch s, out bool known)
        {
            string d = s.Default.Trim().ToLowerInvariant();
            known = false;
            bool on = false, matched = false;
            foreach (var word in OnWords) if (!matched && StartsWithWord(d, word)) { on = true; matched = true; d = d.Substring(word.Length); }
            foreach (var word in OffWords) if (!matched && StartsWithWord(d, word)) { on = false; matched = true; d = d.Substring(word.Length); }
            if (!matched) return false;
            foreach (var word in ConditionalWords) if (d.Contains(word)) return on; // known stays false
            known = true;
            return on;
        }

        static bool IsOn(string value, bool defaultOn)
        {
            return value.Length == 0 ? defaultOn : value != "0";
        }

        /// A switch: a rounded track with a knob, red when set on here, then On or Off, and "(default)"
        /// while it is not changed.
        static void PaintToggle(Graphics g, Rectangle cell, RendererSwitch s, string value, Font font)
        {
            bool known;
            bool defaultOn = DefaultOn(s, out known);
            bool changed = value.Length > 0, on = IsOn(value, defaultOn);
            bool middle = !changed && !known; // Default, which depends on the GPU or a setting
            var track = new RectangleF(cell.X + 8, cell.Y + (cell.Height - 16) / 2f, 32, 16);
            var smoothing = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Color fill = middle ? BoxFill : on ? (changed ? Dark.Accent : Color.FromArgb(104, 56, 60)) : BoxFill;
            Dark.FillRounded(g, fill, track, 8);
            if (!on || middle)
                using (var path = Dark.RoundedRect(new RectangleF(track.X + 0.5f, track.Y + 0.5f, track.Width - 1, track.Height - 1), 8))
                using (var pen = new Pen(changed ? Dark.AccentText : BoxEdge))
                    g.DrawPath(pen, path);
            float knob = 12, kx = middle ? track.X + (track.Width - knob) / 2 : on ? track.Right - knob - 2 : track.X + 2;
            using (var brush = new SolidBrush((on && !middle) || changed ? Color.White : Color.FromArgb(170, 170, 170)))
                g.FillEllipse(brush, kx, track.Y + 2, knob, knob);
            g.SmoothingMode = smoothing;
            string text = middle ? "Default" : (on ? "On" : "Off") + (changed ? "" : "  (default)");
            var area = new Rectangle((int)track.Right + 8, cell.Y, cell.Right - (int)track.Right - 10, cell.Height);
            TextRenderer.DrawText(g, text, font, area, changed ? Dark.AccentText : Dark.DimText,
                                  TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }

        static void Mark(DataGridViewRow row, bool changed)
        {
            row.Cells[1].Style.ForeColor = changed ? Dark.AccentText : Dark.Text;
            row.Cells[1].Style.SelectionForeColor = changed ? Dark.AccentText : Dark.Text;
            row.Cells[0].Style.ForeColor = changed ? Dark.AccentText : Dark.Text;
        }

        static string KindHint(RendererSwitch s)
        {
            switch (s.Kind)
            {
                case "int": return "a whole number";
                case "hex": return "a hexadecimal number such as 0xFFFF0000";
                case "float": return "a number such as 1.5";
                default: return "letters and digits";
            }
        }

        static bool Valid(RendererSwitch s, string value)
        {
            if (value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0) return false;
            long l; double d; ulong u;
            switch (s.Kind)
            {
                case "int": return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out l);
                case "hex":
                    string hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value.Substring(2) : value;
                    return hex.Length > 0 && ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out u) ||
                           long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out l);
                case "float": return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
                default: return true;
            }
        }

        /// The hover text: the description, then the values and the default, wrapped for the tooltip.
        static string Tip(RendererSwitch s)
        {
            if (!s.Described)
                return Wrap(s.Name + " is read by this renderer build (" + SwitchWalker.RendererFile + ") but has no description in it. " +
                            "Most switches take 1 to turn on and 0 to turn off.", 72);
            var text = new StringBuilder(Wrap(s.Tooltip, 72));
            if (s.Values.Length > 0) text.Append("\n\nValues: ").Append(Wrap(s.Values, 64));
            if (s.Default.Length > 0) text.Append("\nWhen not set: ").Append(Wrap(s.Default, 60));
            if (s.Diagnostic) text.Append("\n\nDiagnosis only: adds logging or dumps.");
            return text.ToString();
        }

        static string Wrap(string text, int width)
        {
            var lines = new StringBuilder();
            int column = 0;
            foreach (var word in (text ?? "").Split(' '))
            {
                if (column > 0 && column + 1 + word.Length > width) { lines.Append('\n'); column = 0; }
                else if (column > 0) { lines.Append(' '); ++column; }
                lines.Append(word);
                column += word.Length;
            }
            return lines.ToString();
        }
    }
}
