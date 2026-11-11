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
