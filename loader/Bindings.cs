// RyGT loader: the controller and keyboard bindings (like shadPS4's controller settings).
// They live in user\input_config\default.ini, the bindings file shadGT reads (unified input
// config); saving keeps every line this does not manage (hotkeys, mouse bindings, colours).
// SPDX-License-Identifier: GPL-2.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace RyGT
{
    class Bindings
    {
        /// One PS4 control: its name in the bindings file, its label, and whether a controller
        /// stick axis (rather than a button) is bound to it.
        public class Row
        {
            public string Output, Label;
            public bool Axis, KeyboardOnly, ControllerOnly;
        }

        public static readonly string[] PadButtons =
        {
            "cross", "circle", "square", "triangle", "l1", "r1", "l2", "r2", "l3", "r3", "options", "back",
            "pad_up", "pad_down", "pad_left", "pad_right", "lpaddle_high", "lpaddle_low", "rpaddle_high", "rpaddle_low", "qam"
        };
        public static readonly string[] PadAxes = { "axis_left_x", "axis_left_y", "axis_right_x", "axis_right_y" };
        public static readonly string[] Keys =
        {
            "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m", "n", "o", "p", "q", "r", "s", "t", "u", "v", "w", "x", "y", "z",
            "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "space", "enter", "escape", "backspace", "tab", "capslock",
            "lshift", "rshift", "lctrl", "rctrl", "lalt", "ralt", "up", "down", "left", "right",
            "insert", "delete", "home", "end", "pgup", "pgdown",
            "kp0", "kp1", "kp2", "kp3", "kp4", "kp5", "kp6", "kp7", "kp8", "kp9", "kpperiod", "kpslash",
            "f1", "f2", "f3", "f4", "f5", "f6", "f7",
            "grave", "minus", "equals", "lbracket", "rbracket", "backslash", "semicolon", "apostrophe", "comma", "period", "slash"
        };

        static readonly string[] MouseInputs = { "leftbutton", "middlebutton", "rightbutton", "mousewheelup", "mousewheeldown" };

        public static readonly Row[] Rows =
        {
            new Row { Output = "cross", Label = "Cross" },
            new Row { Output = "circle", Label = "Circle" },
            new Row { Output = "square", Label = "Square" },
            new Row { Output = "triangle", Label = "Triangle" },
            new Row { Output = "l1", Label = "L1" },
            new Row { Output = "r1", Label = "R1" },
            new Row { Output = "l2", Label = "L2 (brake)" },
            new Row { Output = "r2", Label = "R2 (accelerate)" },
            new Row { Output = "l3", Label = "L3" },
            new Row { Output = "r3", Label = "R3" },
            new Row { Output = "options", Label = "Options" },
            new Row { Output = "touchpad_center", Label = "Touchpad" },
            new Row { Output = "pad_up", Label = "D-pad up" },
            new Row { Output = "pad_down", Label = "D-pad down" },
            new Row { Output = "pad_left", Label = "D-pad left" },
            new Row { Output = "pad_right", Label = "D-pad right" },
            new Row { Output = "axis_left_x", Label = "Left stick, left/right (steering)", Axis = true, ControllerOnly = true },
            new Row { Output = "axis_left_y", Label = "Left stick, up/down", Axis = true, ControllerOnly = true },
            new Row { Output = "axis_right_x", Label = "Right stick, left/right", Axis = true, ControllerOnly = true },
            new Row { Output = "axis_right_y", Label = "Right stick, up/down", Axis = true, ControllerOnly = true },
            new Row { Output = "axis_left_x_minus", Label = "Steer left (left stick left)", KeyboardOnly = true },
            new Row { Output = "axis_left_x_plus", Label = "Steer right (left stick right)", KeyboardOnly = true },
            new Row { Output = "axis_left_y_minus", Label = "Left stick up", KeyboardOnly = true },
            new Row { Output = "axis_left_y_plus", Label = "Left stick down", KeyboardOnly = true },
            new Row { Output = "axis_right_x_minus", Label = "Right stick left", KeyboardOnly = true },
            new Row { Output = "axis_right_x_plus", Label = "Right stick right", KeyboardOnly = true },
            new Row { Output = "axis_right_y_minus", Label = "Right stick up", KeyboardOnly = true },
            new Row { Output = "axis_right_y_plus", Label = "Right stick down", KeyboardOnly = true },
        };

        // shadGT's default bindings (input_handler.cpp GetDefaultInputConfig).
        static readonly Dictionary<string, string[]> DefaultKeys = new Dictionary<string, string[]>
        {
            { "triangle", new[] { "kp8", "c" } }, { "circle", new[] { "kp6", "b" } }, { "cross", new[] { "kp2", "n" } },
            { "square", new[] { "kp4", "v" } }, { "l1", new[] { "q" } }, { "r1", new[] { "u" } }, { "l2", new[] { "e" } },
            { "r2", new[] { "o" } }, { "l3", new[] { "x" } }, { "r3", new[] { "m" } }, { "options", new[] { "enter" } },
            { "touchpad_center", new[] { "space" } }, { "pad_up", new[] { "up" } }, { "pad_down", new[] { "down" } },
            { "pad_left", new[] { "left" } }, { "pad_right", new[] { "right" } },
            { "axis_left_x_minus", new[] { "a" } }, { "axis_left_x_plus", new[] { "d" } },
            { "axis_left_y_minus", new[] { "w" } }, { "axis_left_y_plus", new[] { "s" } },
            { "axis_right_x_minus", new[] { "j" } }, { "axis_right_x_plus", new[] { "l" } },
            { "axis_right_y_minus", new[] { "i" } }, { "axis_right_y_plus", new[] { "k" } },
        };

        /// What each PS4 control is on the controller (one input) and on the keyboard (up to two keys).
        public readonly Dictionary<string, string> Pad = new Dictionary<string, string>();
        public readonly Dictionary<string, List<string>> Key = new Dictionary<string, List<string>>();
        /// Deadzones, 1 (almost none) to 127: the sticks' and the triggers'.
        public int LeftDeadzone = 5, RightDeadzone = 5, L2Deadzone = 1, R2Deadzone = 1;

        readonly string root;
        readonly string path;
        bool backedUp;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint loadFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("kernel32.dll")]
        static extern bool FreeLibrary(IntPtr module);

        delegate int DefaultConfigFn(IntPtr buffer, int size);

        /// The bindings in `root`'s bindings file (made from shadGT's defaults when there is none yet).
        public Bindings(string root) : this(root, false)
        {
        }

        Bindings(string root, bool defaults)
        {
            this.root = root;
            path = Path.Combine(root, "user", "input_config", "default.ini");
            if (defaults) { LoadDefaults(); return; }
            if (!File.Exists(path)) WriteDefaultConfig();
            Load(File.Exists(path) ? File.ReadAllLines(path) : null);
        }

        public static Row RowOf(string output)
        {
            return Rows.FirstOrDefault(r => r.Output == output);
        }

        /// The default config text from the deployed shadGT library, or null when the library
        /// or its ryty_get_default_input_config export is unavailable (older builds), in which
        /// case the hardcoded DefaultKeys apply.
        static string GetDefaultConfigFromLibrary(string root)
        {
            var prx = Path.Combine(root, "libs", "libSceVideoOut.prx");
            if (!File.Exists(prx)) return null;
            IntPtr module = IntPtr.Zero;
            try
            {
                module = LoadLibraryEx(prx, IntPtr.Zero, 0);
                if (module == IntPtr.Zero) return null;
                IntPtr proc = GetProcAddress(module, "ryty_get_default_input_config");
                if (proc == IntPtr.Zero) return null;
                var fn = (DefaultConfigFn)Marshal.GetDelegateForFunctionPointer(proc, typeof(DefaultConfigFn));
                int required = -fn(IntPtr.Zero, 0);
                if (required <= 1) return null;
                IntPtr buffer = Marshal.AllocHGlobal(required);
                try
                {
                    if (fn(buffer, required) != required) return null;
                    var bytes = new byte[required - 1];
                    Marshal.Copy(buffer, bytes, 0, required - 1);
                    return Encoding.ASCII.GetString(bytes);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                if (module != IntPtr.Zero) FreeLibrary(module);
            }
        }

        /// A binding line, "output[:player] = input[:controller]" with # comments, split apart.
        static bool Parse(string line, out string output, out string input)
        {
            output = input = null;
            var text = line;
            int hash = text.IndexOf('#');
            if (hash >= 0) text = text.Substring(0, hash);
            text = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
            int eq = text.IndexOf('=');
            if (eq <= 0) return false;
            output = text.Substring(0, eq);
            input = text.Substring(eq + 1);
            int colon = output.IndexOf(':');
            if (colon >= 0) output = output.Substring(0, colon);
            return true;
        }

        static bool IsManaged(string output)
        {
            return Rows.Any(r => r.Output == output) || output == "analog_deadzone";
        }

        static bool IsMouseInput(string input)
        {
            int colon = input.IndexOf(':');
            if (colon >= 0) input = input.Substring(0, colon);
            return MouseInputs.Contains(input);
        }

        /// Creates the bindings file from shadGT's defaults when it doesn't exist yet.
        void WriteDefaultConfig()
        {
            var text = GetDefaultConfigFromLibrary(root);
            if (text == null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        /// shadGT's defaults (from its library when it has them).
        void LoadDefaults()
        {
            var text = GetDefaultConfigFromLibrary(root);
            Load(text != null ? text.Split('\n') : null);
        }

        /// Every binding back to shadGT's defaults (not saved yet).
        public void RestoreDefaults()
        {
            LoadDefaults();
        }

        /// One control back to its default (not saved yet).
        public void Reset(string output)
        {
            var defaults = new Bindings(root, true);
            string pad;
            if (defaults.Pad.TryGetValue(output, out pad)) Pad[output] = pad; else Pad.Remove(output);
            List<string> keys;
            if (defaults.Key.TryGetValue(output, out keys)) Key[output] = keys.ToList(); else Key.Remove(output);
        }

        /// Whether a control is as it is by default.
        public bool IsDefault(string output)
        {
            var defaults = new Bindings(root, true);
            string a, b;
            Pad.TryGetValue(output, out a);
            defaults.Pad.TryGetValue(output, out b);
            List<string> x, y;
            Key.TryGetValue(output, out x);
            defaults.Key.TryGetValue(output, out y);
            return a == b && (x ?? new List<string>()).SequenceEqual(y ?? new List<string>());
        }

        public string PadOf(string output)
        {
            string pad;
            return Pad.TryGetValue(output, out pad) ? pad : null;
        }

        /// The control's key `index` (0 or 1), or null.
        public string KeyOf(string output, int index)
        {
            List<string> list;
            return Key.TryGetValue(output, out list) && index < list.Count ? list[index] : null;
        }

        public void SetPad(string output, string input)
        {
            if (input == null) Pad.Remove(output); else Pad[output] = input;
        }

        /// Sets key `index` (0 or 1); null clears it.
        public void SetKey(string output, int index, string key)
        {
            List<string> list;
            if (!Key.TryGetValue(output, out list)) Key[output] = list = new List<string>();
            while (list.Count <= index) list.Add(null);
            list[index] = key;
            // The first key goes first in the file, so the second moves up when the first is cleared.
            Key[output] = list.Where(k => k != null).Distinct().ToList();
        }

        void Load(string[] lines)
        {
            Pad.Clear();
            Key.Clear();
            LeftDeadzone = RightDeadzone = 5;
            L2Deadzone = R2Deadzone = 1;
            if (lines == null)
            {
                foreach (var row in Rows)
                {
                    if (!row.KeyboardOnly) Pad[row.Output] = row.Output == "touchpad_center" ? "back" : row.Output;
                    string[] defaultKeys;
                    if (DefaultKeys.TryGetValue(row.Output, out defaultKeys)) Key[row.Output] = defaultKeys.ToList();
                }
                return;
            }
            foreach (var line in lines)
            {
                string output, input;
                if (!Parse(line, out output, out input)) continue;
                if (output == "analog_deadzone")
                {
                    var parts = input.Split(',');
                    int value;
                    if (parts.Length >= 2 && int.TryParse(parts[1], out value))
                    {
                        value = Math.Max(1, Math.Min(127, value));
                        if (parts[0] == "leftjoystick") LeftDeadzone = value;
                        if (parts[0] == "rightjoystick") RightDeadzone = value;
                        if (parts[0] == "l2") L2Deadzone = value;
                        if (parts[0] == "r2") R2Deadzone = value;
                    }
                    continue;
                }
                if (!Rows.Any(r => r.Output == output) || input.Contains(',')) continue;
                int colon = input.IndexOf(':');
                var name = colon >= 0 ? input.Substring(0, colon) : input;
                if (Keys.Contains(name))
                {
                    List<string> list;
                    if (!Key.TryGetValue(output, out list)) Key[output] = list = new List<string>();
                    if (list.Count < 2) list.Add(name);
                }
                else if (PadButtons.Contains(name) || PadAxes.Contains(name))
                {
                    if (!Pad.ContainsKey(output)) Pad[output] = name;
                }
            }
        }

        /// Writes the bindings file; the keyboard plays as `keyboardPlayer` (1 or 2).
        public void Save(int keyboardPlayer)
        {
            string keyboardTarget = keyboardPlayer > 1 ? ":" + keyboardPlayer : "";
            var text = new StringBuilder();
            text.AppendLine("# Made by RyGT7.exe (Controls). Lines it does not manage are kept at the end.");
            text.AppendLine();
            text.AppendLine("# Keyboard bindings" + (keyboardPlayer > 1 ? " (player " + keyboardPlayer + ")" : ""));
            foreach (var row in Rows)
            {
                if (row.ControllerOnly) continue;
                List<string> list;
                if (!Key.TryGetValue(row.Output, out list)) continue;
                foreach (var key in list.Take(2)) text.AppendLine(row.Output + keyboardTarget + " = " + key);
            }
            text.AppendLine();
            text.AppendLine("# Controller bindings (every controller)");
            foreach (var row in Rows)
            {
                string pad;
                if (!row.KeyboardOnly && Pad.TryGetValue(row.Output, out pad)) text.AppendLine(row.Output + " = " + pad);
            }
            text.AppendLine();
            text.AppendLine("# Range of deadzones: 1 (almost none) to 127 (max), for players 1-2");
            for (int p = 1; p <= 4; ++p)
            {
                string target = p > 1 ? ":" + p : "";
                text.AppendLine("analog_deadzone" + target + " = leftjoystick, " + LeftDeadzone + ", 127");
                text.AppendLine("analog_deadzone" + target + " = rightjoystick, " + RightDeadzone + ", 127");
                // The triggers' only when changed (shadGT's default is 1).
                if (L2Deadzone > 1) text.AppendLine("analog_deadzone" + target + " = l2, " + L2Deadzone + ", 127");
                if (R2Deadzone > 1) text.AppendLine("analog_deadzone" + target + " = r2, " + R2Deadzone + ", 127");
            }
            if (File.Exists(path))
            {
                var kept = new List<string>();
                foreach (var line in File.ReadAllLines(path))
                {
                    string output, input;
                    if (Parse(line, out output, out input) && (!IsManaged(output) || IsMouseInput(input))) kept.Add(line.Trim());
                }
                if (kept.Count > 0)
                {
                    text.AppendLine();
                    text.AppendLine("# Other bindings");
                    foreach (var line in kept) text.AppendLine(line);
                }
            }
            else
            {
                text.AppendLine();
                text.AppendLine("override_controller_color = false, 0, 0, 255");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            // The file as it was before this launcher session's first change.
            if (File.Exists(path) && !backedUp) { File.Copy(path, path + ".bak", true); backedUp = true; }
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
        }
    }
}
