// FEAT-GTS-433: custom environment variables for GT7.exe, set in the launcher instead of a shell
// (RYGT_SO_CACHE, RYGT_SO_HASH_MASK and the other renderer and runtime switches).
// Saved in RyGT7.ini as "env=<1|0>|NAME=VALUE" lines; enabled rows are applied last when the game starts.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace RyGT
{
    class EnvVar
    {
        public bool Enabled = true;
        public string Name = "", Value = "";
    }

    static class CustomEnvironment
    {
        /// Letters, digits and underscores only (so never "=" or a space).
        public static bool IsValidName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 255) return false;
            foreach (char c in name)
                if (!(c == '_' || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))) return false;
            return true;
        }

        /// The part after "env=" in RyGT.ini, or null if it is not a valid entry.
        public static EnvVar Parse(string text)
        {
            if (text == null || text.Length < 3 || text[1] != '|') return null;
            string pair = text.Substring(2);
            int eq = pair.IndexOf('=');
            if (eq <= 0) return null;
            var entry = new EnvVar { Enabled = text[0] == '1', Name = pair.Substring(0, eq), Value = pair.Substring(eq + 1) };
            return IsValidName(entry.Name) ? entry : null;
        }

        public static string Format(EnvVar entry)
        {
            return (entry.Enabled ? "1" : "0") + "|" + entry.Name + "=" + Clean(entry.Value);
        }

        static string Clean(string value)
        {
            return (value ?? "").Replace("\r", "").Replace("\n", "");
        }

        /// Applies the enabled entries over the environment the game inherits (and over the
        /// launcher's own RYGT_* values); an empty value removes the variable. Returns how many applied.
        public static int Apply(ProcessStartInfo start, IEnumerable<EnvVar> entries)
        {
            int applied = 0;
            foreach (var entry in entries)
            {
                if (!entry.Enabled || !IsValidName(entry.Name)) continue;
                string value = Clean(entry.Value);
                if (value.Length == 0) start.EnvironmentVariables.Remove(entry.Name);
                else start.EnvironmentVariables[entry.Name] = value;
                ++applied;
            }
            return applied;
        }

        public static List<EnvVar> Copy(IEnumerable<EnvVar> entries)
        {
            var copy = new List<EnvVar>();
            foreach (var entry in entries) copy.Add(new EnvVar { Enabled = entry.Enabled, Name = entry.Name, Value = entry.Value });
            return copy;
        }

        const string InvalidName = "Not saved: names use letters, digits and _ only (no = or spaces).";

        /// A table in the launcher's dark colors.
        public static DataGridView DarkGrid(string accessibleName)
        {
            return DarkGrid<DataGridView>(accessibleName);
        }

        public static T DarkGrid<T>(string accessibleName) where T : DataGridView, new()
        {
            var grid = new T
            {
                Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 6),
                AllowUserToAddRows = false, AllowUserToDeleteRows = true, AllowUserToResizeRows = false,
                RowHeadersVisible = true, RowHeadersWidth = 24, RowHeadersWidthSizeMode = DataGridViewRowHeadersWidthSizeMode.DisableResizing, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = true,
                BorderStyle = BorderStyle.None, CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing, ColumnHeadersHeight = 24,
                EnableHeadersVisualStyles = false, BackgroundColor = Dark.Field, GridColor = Dark.Border,
                EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2, AccessibleName = accessibleName
            };
            grid.DefaultCellStyle.BackColor = Dark.Field;
            grid.DefaultCellStyle.ForeColor = Dark.Text;
            grid.DefaultCellStyle.SelectionBackColor = Dark.ButtonHover;
            grid.DefaultCellStyle.SelectionForeColor = Dark.Text;
            grid.DefaultCellStyle.Font = new Font("Consolas", 9f);
            grid.ColumnHeadersDefaultCellStyle.BackColor = Dark.Button;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Dark.DimText;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Dark.Button;
            grid.RowHeadersDefaultCellStyle.BackColor = Dark.Field;
            grid.RowHeadersDefaultCellStyle.SelectionBackColor = Dark.ButtonHover;
            grid.RowHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            return grid;
        }

        /// The editor: a table of on/off, name and value rows with Add and Remove buttons.
        /// save is called with the valid rows after every change.
        public static Control Editor(List<EnvVar> entries, Action<List<EnvVar>> save)
        {
            var panel = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill, BackColor = Color.Transparent, Margin = new Padding(0) };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.Controls.Add(new Label
            {
                Text = "Set for GT7.exe when it starts, over the launcher's own. Names: letters, digits and _. " +
                       "An empty value removes the variable. Restart the game to apply.",
                AutoSize = true, MaximumSize = new Size(380, 0), ForeColor = Color.Silver, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 6)
            });

            var grid = DarkGrid("Custom environment variables");
            grid.Dock = DockStyle.Top;
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "on", HeaderText = "On", Width = 36, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "Name", FillWeight = 45, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "value", HeaderText = "Value", FillWeight = 55, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            foreach (var entry in entries) grid.Rows.Add(entry.Enabled, entry.Name, entry.Value);

            bool loading = false;
            Action store = delegate
            {
                if (loading) return;
                var list = new List<EnvVar>();
                foreach (DataGridViewRow row in grid.Rows)
                {
                    string name = ((row.Cells[1].Value as string) ?? "").Trim();
                    if (!IsValidName(name)) continue; // empty or still being fixed: not saved
                    list.Add(new EnvVar { Enabled = row.Cells[0].Value is bool && (bool)row.Cells[0].Value, Name = name, Value = (row.Cells[2].Value as string) ?? "" });
                }
                save(list);
            };

            grid.CellValidating += (sender, e) =>
            {
                if (e.ColumnIndex != 1) return;
                string name = ((e.FormattedValue as string) ?? "").Trim();
                grid.Rows[e.RowIndex].ErrorText = name.Length == 0 || IsValidName(name) ? "" : InvalidName;
            };
            grid.CellEndEdit += (sender, e) =>
            {
                if (e.ColumnIndex == 1)
                {
                    var cell = grid.Rows[e.RowIndex].Cells[1];
                    loading = true;
                    cell.Value = ((cell.Value as string) ?? "").Trim();
                    loading = false;
                }
                string edited = ((grid.Rows[e.RowIndex].Cells[1].Value as string) ?? "");
                grid.Rows[e.RowIndex].ErrorText = edited.Length == 0 || IsValidName(edited) ? "" : InvalidName;
            };
            // The check box saves when clicked, not when the cell is left.
            grid.CurrentCellDirtyStateChanged += delegate
            {
                if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewCheckBoxCell) grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            grid.CellValueChanged += delegate { store(); };
            grid.RowsRemoved += delegate { store(); };
            // As tall as its rows (one empty row's worth at least); a parent's MaximumSize caps it.
            Action fit = delegate
            {
                int rows = Math.Max(1, grid.Rows.Count);
                int height = grid.ColumnHeadersHeight + rows * grid.RowTemplate.Height + 2;
                int max = int.MaxValue;
                for (Control c = panel; c != null; c = c.Parent)
                    if (c.MaximumSize.Height > 0) { max = c.MaximumSize.Height - (panel.Height - grid.Height) - 40; break; }
                grid.Height = Math.Max(grid.ColumnHeadersHeight + grid.RowTemplate.Height + 2, Math.Min(height, max));
            };
            grid.RowsAdded += delegate { fit(); };
            grid.RowsRemoved += delegate { fit(); };
            panel.Resize += delegate { fit(); };
            fit();
            panel.Tag = fit; // the page calls it when its room changes
            panel.Controls.Add(grid);

            var buttons = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, WrapContents = false, Margin = new Padding(0) };
            var add = new Button { Text = "Add", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
            var remove = new Button { Text = "Remove", AutoSize = true, Margin = new Padding(0) };
            add.Click += delegate
            {
                loading = true;
                int index = grid.Rows.Add(true, "", "");
                loading = false;
                grid.CurrentCell = grid.Rows[index].Cells[1];
                grid.BeginEdit(true);
            };
            remove.Click += delegate
            {
                if (grid.IsCurrentCellInEditMode) grid.CancelEdit();
                var rows = new List<DataGridViewRow>();
                foreach (DataGridViewRow row in grid.SelectedRows) rows.Add(row);
                if (rows.Count == 0 && grid.CurrentRow != null) rows.Add(grid.CurrentRow);
                foreach (var row in rows) grid.Rows.Remove(row);
            };
            buttons.Controls.Add(add);
            buttons.Controls.Add(remove);
            panel.Controls.Add(buttons);
            return panel;
        }
    }
}
