// GT7 setup: validates the player's dump and overlays separate updates file by file.
// SPDX-License-Identifier: GPL-2.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace RyGT
{
    internal sealed class GameLayout
    {
        public const string TitleId = "CUSA24769";
        public readonly Dictionary<string, string> Files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly string Version;
        public string Get(string relative)
        {
            string path;
            return Files.TryGetValue(relative.Replace('/', '\\'), out path) ? path : null;
        }

        public GameLayout(string baseFolder, string updateFolder, string runtime)
        {
            ValidateSource(baseFolder, runtime);
            var baseSfo = ReadSfo(Path.Combine(baseFolder, "sce_sys", "param.sfo"));
            ValidateTitle(baseSfo, "Base game");
            if (!File.Exists(Path.Combine(baseFolder, "eboot.bin")) ||
                !Directory.Exists(Path.Combine(baseFolder, "contents")) ||
                !Directory.EnumerateFiles(Path.Combine(baseFolder, "contents"), "*.vol").Any())
                throw new InvalidDataException("Select the complete base game: eboot.bin, sce_sys, and contents with the gt*.vol archives.");
            AddFiles(baseFolder, "");
            if (!string.IsNullOrWhiteSpace(updateFolder))
            {
                ValidateSource(updateFolder, runtime);
                var updateSfo = ReadSfo(Path.Combine(updateFolder, "sce_sys", "param.sfo"));
                ValidateTitle(updateSfo, "Update");
                if (!File.Exists(Path.Combine(updateFolder, "eboot.bin")))
                    throw new InvalidDataException("The update folder needs its decrypted eboot.bin.");
                Version baseVersion, updateVersion;
                if (System.Version.TryParse(Value(baseSfo, "APP_VER"), out baseVersion) &&
                    System.Version.TryParse(Value(updateSfo, "APP_VER"), out updateVersion) && updateVersion < baseVersion)
                    throw new InvalidDataException("The selected update is older than the base/merged game.");
                AddFiles(updateFolder, "");
            }
            foreach (string required in new[] { "eboot.bin", "contents\\gt.idx", "sce_module\\libc.prx", "sce_module\\libSceFios2.prx" })
                if (Get(required) == null) throw new InvalidDataException("The selected game and update are missing " + required + ".");
            if (!Files.Keys.Any(p => p.StartsWith("contents\\", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".vol", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("The base game needs its contents folder with the gt*.vol archives.");
            Version = Value(ReadSfo(Get("sce_sys\\param.sfo")), "APP_VER");
        }

        static string Value(Dictionary<string, string> values, string key)
        { string value; return values.TryGetValue(key, out value) ? value : "unknown"; }

        static void ValidateTitle(Dictionary<string, string> sfo, string label)
        {
            if (Value(sfo, "TITLE_ID") != TitleId)
                throw new InvalidDataException(label + " must be Gran Turismo 7 (" + TitleId + "), but its title ID is " + Value(sfo, "TITLE_ID") + ".");
        }

        static void ValidateSource(string source, string runtime)
        {
            if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
                throw new DirectoryNotFoundException("Pick the extracted game/update folder containing eboot.bin and sce_sys.");
            string a = Path.GetFullPath(source).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string b = Path.GetFullPath(runtime).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Keep the launcher folder separate from the source game and update folders.");
        }

        void AddFiles(string root, string relative)
        {
            string folder = Path.Combine(root, relative);
            foreach (string file in Directory.GetFiles(folder))
                Files[Path.Combine(relative, Path.GetFileName(file))] = file;
            foreach (string child in Directory.GetDirectories(folder))
            {
                // Dumps should be ordinary folders; do not follow cycles or external junctions.
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Game dump folders must not contain junctions or symbolic links: " + child);
                AddFiles(root, Path.Combine(relative, Path.GetFileName(child)));
            }
        }

        internal static Dictionary<string, string> ReadSfo(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            var result = new Dictionary<string, string>();
            if (data.Length < 20 || BitConverter.ToUInt32(data, 0) != 0x46535000)
                throw new InvalidDataException("Invalid sce_sys\\param.sfo: " + path);
            uint keys = BitConverter.ToUInt32(data, 8), values = BitConverter.ToUInt32(data, 12), count = BitConverter.ToUInt32(data, 16);
            if (count > (data.Length - 20) / 16 || keys > data.Length || values > data.Length)
                throw new InvalidDataException("Truncated param.sfo: " + path);
            for (int i = 0; i < count; i++)
            {
                int entry = 20 + i * 16;
                long keyAt = (long)keys + BitConverter.ToUInt16(data, entry);
                long valueAt = (long)values + BitConverter.ToUInt32(data, entry + 12);
                uint length = BitConverter.ToUInt32(data, entry + 4);
                if (keyAt >= data.Length || valueAt + length > data.Length)
                    throw new InvalidDataException("Invalid param.sfo entry: " + path);
                int end = Array.IndexOf(data, (byte)0, (int)keyAt);
                if (end < 0) throw new InvalidDataException("Unterminated param.sfo key.");
                string key = Encoding.UTF8.GetString(data, (int)keyAt, end - (int)keyAt);
                if (BitConverter.ToUInt16(data, entry + 2) == 0x204)
                    result[key] = Encoding.UTF8.GetString(data, (int)valueAt, (int)length).TrimEnd('\0');
            }
            return result;
        }

        internal static bool PatchKnownExecutable(byte[] elf)
        {
            // Only the tested CUSA24769 1.00 ELF. Other updates must never receive these offsets.
            string hash;
            using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(elf)).Replace("-", "");
            if (hash != "938D2C720D2036E451D66AC474ECF21386D4E5AE5A50E60FF4F3647BC326BDFA") return false;
            // Two FDEs include four bytes of the following instruction; the converter needs the
            // actual instruction boundary. These are file offsets in this exact ELF fingerprint.
            int ph = checked((int)BitConverter.ToUInt64(elf, 0x20));
            int count = BitConverter.ToUInt16(elf, 0x38);
            foreach (long[] fix in new[] { new long[] { 0x4d4575c, 0x1fe }, new long[] { 0x4d4578c, 0x200 } })
            {
                bool done = false;
                for (int i = 0; i < count; i++)
                {
                    int at = ph + i * 56;
                    long va = (long)BitConverter.ToUInt64(elf, at + 16), size = (long)BitConverter.ToUInt64(elf, at + 32);
                    if (fix[0] < va || fix[0] + 16 > va + size) continue;
                    int offset = checked((int)((long)BitConverter.ToUInt64(elf, at + 8) + fix[0] - va + 12));
                    BitConverter.GetBytes((int)fix[1]).CopyTo(elf, offset);
                    done = true;
                    break;
                }
                if (!done) throw new InvalidDataException("Could not locate the known GT7 conversion fix.");
            }
            return true;
        }
    }
}
