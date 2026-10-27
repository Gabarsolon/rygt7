// GT7 setup: validates the player's dump and overlays separate updates file by file.
// SPDX-License-Identifier: GPL-2.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

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
            if (Sha256(elf) != "938D2C720D2036E451D66AC474ECF21386D4E5AE5A50E60FF4F3647BC326BDFA") return false;
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

        internal static string Sha256(byte[] data)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "");
        }
    }

    // The community "Offline mode" patch (ps-patch-system XML, Proper Offline Patch v1.0), which gives
    // the dealerships a local catalog. Releases bundle patches\CUSA24769.xml; only its
    // Offline mode group is read, never the file's other groups (DLC unlocks, debug settings). Its
    // addresses are the console's, with the executable at 0x400000, and it is only applied to the
    // verified executable. Its injected code lies past the end of the first and last loadable segments,
    // so those mappings are extended before conversion and the bytes written into the converted program
    // afterwards: the converter never decodes the new code with the game's original function metadata.
    internal sealed class OfflinePatch
    {
        public const string GroupName = "Offline mode";
        // The ELF after unwrapping and the 1.00 conversion fix, the one tested with this patch.
        const string VerifiedElf = "E84B7AB142D2CB71ED11F5FD1F6D2CB8625F5AF7D0B1120C5E897837E429CE09";
        const long ConsoleBase = 0x400000;
        // The converter keeps every loadable segment in a .elfN section at its guest address + 0x10000.
        const long GuestRva = 0x10000;

        public readonly List<KeyValuePair<long, byte[]>> Writes = new List<KeyValuePair<long, byte[]>>();
        public readonly string Version;

        OfflinePatch(string version) { Version = version; }

        public static OfflinePatch Load(string path)
        {
            var document = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
                document.Load(reader);
            var id = document.SelectSingleNode("/Patch/TitleID/ID");
            if (id == null || id.InnerText.Trim() != GameLayout.TitleId)
                throw new InvalidDataException(path + " is not a patch for " + GameLayout.TitleId + ".");
            var groups = document.SelectNodes("/Patch/Metadata[@Name='" + GroupName + "']");
            if (groups.Count != 1) throw new InvalidDataException(path + " needs exactly one \"" + GroupName + "\" patch group.");
            var group = (XmlElement)groups[0];
            if (!group.GetAttribute("AppElf").Equals("eboot.bin", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The " + GroupName + " patch does not target eboot.bin.");
            var patch = new OfflinePatch(group.GetAttribute("PatchVer"));
            var written = new Dictionary<long, byte>();
            foreach (XmlElement line in group.SelectNodes("PatchList/Line"))
            {
                if (line.GetAttribute("Type") != "bytes")
                    throw new InvalidDataException("Unsupported " + GroupName + " patch line type \"" + line.GetAttribute("Type") + "\".");
                string text = line.GetAttribute("Address"), hex = line.GetAttribute("Value");
                if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text.Substring(2);
                long address = text.Length > 0 && text.Length <= 15 && text.All(c => Uri.IsHexDigit(c)) ? Convert.ToInt64(text, 16) - ConsoleBase : -1;
                if (address < 0 || hex.Length == 0 || hex.Length % 2 != 0 || !hex.All(c => Uri.IsHexDigit(c)))
                    throw new InvalidDataException("Invalid patch line at \"" + line.GetAttribute("Address") + "\".");
                var value = new byte[hex.Length / 2];
                for (int i = 0; i < value.Length; i++) value[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
                for (int i = 0; i < value.Length; i++)
                {
                    byte previous;
                    if (written.TryGetValue(address + i, out previous) && previous != value[i])
                        throw new InvalidDataException("Conflicting patch lines at " + line.GetAttribute("Address") + ".");
                    written[address + i] = value[i];
                }
                patch.Writes.Add(new KeyValuePair<long, byte[]>(address, value));
            }
            if (patch.Writes.Count == 0) throw new InvalidDataException("The " + GroupName + " patch group is empty.");
            return patch;
        }

        public byte[] ExtendLayout(byte[] elf)
        {
            if (GameLayout.Sha256(elf) != VerifiedElf)
                throw new InvalidDataException("The " + GroupName + " patch is only applied to the verified CUSA24769 executable. Remove patches\\" + GameLayout.TitleId + ".xml to set up this one.");
            return Extend(elf, Writes);
        }

        public int Apply(string exe, byte[] layout)
        {
            byte[] image = File.ReadAllBytes(exe);
            int removed = Overlay(image, layout, Writes);
            File.WriteAllBytes(exe, image);
            return removed;
        }

        // Every loadable segment grows to hold the writes between its address and the next segment's.
        // A segment without uninitialized data grows in place over the file's padding when that reaches
        // the next segment's data; otherwise it moves to the end of the file, its uninitialized part
        // written as zeros, along with the headers inside it (TLS, process parameters).
        internal static byte[] Extend(byte[] elf, List<KeyValuePair<long, byte[]>> writes)
        {
            if (elf.Length < 64 || BitConverter.ToUInt32(elf, 0) != 0x464c457f || elf[4] != 2 || elf[5] != 1 || BitConverter.ToUInt16(elf, 0x36) != 56)
                throw new InvalidDataException("Unsupported executable for the " + GroupName + " patch.");
            int ph = checked((int)BitConverter.ToUInt64(elf, 0x20));
            int count = BitConverter.ToUInt16(elf, 0x38);
            // Program header fields: type at 0, offset 8, address 16, file size 32, memory size 40, alignment 48.
            Func<byte[], int, long> field = (data, at) => checked((long)BitConverter.ToUInt64(data, at));
            var headers = Enumerable.Range(0, count).Select(i => ph + i * 56).ToList();
            var loads = headers.Where(at => BitConverter.ToUInt32(elf, at) == 1).OrderBy(at => field(elf, at + 16)).ToList();
            if (loads.Count == 0 || writes.Any(w => w.Key < field(elf, loads[0] + 16)))
                throw new InvalidDataException("A " + GroupName + " patch line lies outside the executable.");
            byte[] result = (byte[])elf.Clone();
            for (int index = 0; index < loads.Count; index++)
            {
                int at = loads[index];
                long address = field(result, at + 16), offset = field(result, at + 8), fileSize = field(result, at + 32), memorySize = field(result, at + 40);
                long limit = index + 1 < loads.Count ? field(result, loads[index + 1] + 16) : long.MaxValue;
                long end = address + fileSize;
                foreach (var write in writes.Where(w => w.Key >= address && w.Key < limit))
                {
                    if (write.Key + write.Value.Length > limit) throw new InvalidDataException("A " + GroupName + " patch line crosses into the next segment.");
                    end = Math.Max(end, write.Key + write.Value.Length);
                }
                if (end <= address + fileSize) continue;
                long size = Math.Max(end, address + memorySize) - address;
                long nextData = headers.Where(other => field(result, other + 32) > 0).Select(other => field(result, other + 8))
                    .Where(o => o >= offset + fileSize).DefaultIfEmpty((long)result.Length).Min();
                if (memorySize == fileSize && offset + size <= nextData)
                {
                    BitConverter.GetBytes(size).CopyTo(result, at + 32);
                    BitConverter.GetBytes(size).CopyTo(result, at + 40);
                    continue;
                }
                long alignment = Math.Max(1, field(result, at + 48));
                if ((alignment & (alignment - 1)) != 0) throw new InvalidDataException("Unsupported segment alignment.");
                long moved = ((result.Length + alignment - 1) & ~(alignment - 1)) + (address & (alignment - 1));
                if (moved < result.Length) moved += alignment;
                var grown = new byte[checked(moved + size)];
                Buffer.BlockCopy(result, 0, grown, 0, result.Length);
                Buffer.BlockCopy(result, checked((int)offset), grown, checked((int)moved), checked((int)fileSize));
                foreach (int other in headers)
                {
                    long otherOffset = field(grown, other + 8);
                    if (other != at && otherOffset >= offset && otherOffset < offset + fileSize)
                        BitConverter.GetBytes(otherOffset + moved - offset).CopyTo(grown, other + 8);
                }
                BitConverter.GetBytes(moved).CopyTo(grown, at + 8);
                BitConverter.GetBytes(size).CopyTo(grown, at + 32);
                BitConverter.GetBytes(size).CopyTo(grown, at + 40);
                result = grown;
            }
            return result;
        }

        // Writes the patch into the converted program after checking that each target still holds the
        // layout ELF's bytes there. Base relocations inside the new bytes would rebase the injected code
        // when Windows loads the program elsewhere, so they are turned into padding entries.
        internal static int Overlay(byte[] image, byte[] layout, List<KeyValuePair<long, byte[]>> writes)
        {
            int pe = image.Length >= 0x40 ? BitConverter.ToInt32(image, 0x3c) : -1;
            if (pe < 0 || pe > image.Length - 24 || BitConverter.ToUInt32(image, pe) != 0x4550 || BitConverter.ToUInt16(image, pe + 24) != 0x20b)
                throw new InvalidDataException("The converted GT7.exe is not a 64-bit Windows program.");
            int sectionCount = BitConverter.ToUInt16(image, pe + 6), optional = pe + 24;
            int table = optional + BitConverter.ToUInt16(image, pe + 20);
            // Section header fields: name at 0, virtual address 12, raw size 16, raw offset 20.
            Func<long, int, bool, int> fileOffset = (rva, length, guest) =>
            {
                for (int i = 0; i < sectionCount; i++)
                {
                    int at = table + i * 40;
                    long start = BitConverter.ToUInt32(image, at + 12), raw = BitConverter.ToUInt32(image, at + 16);
                    if (guest && Encoding.ASCII.GetString(image, at, 4) != ".elf") continue;
                    if (rva >= start && rva + length <= start + raw) return checked((int)(BitConverter.ToUInt32(image, at + 20) + rva - start));
                }
                throw new InvalidDataException("The converted GT7.exe has no section at 0x" + rva.ToString("x") + " for the " + GroupName + " patch.");
            };
            var targets = writes.Select(w => fileOffset(w.Key + GuestRva, w.Value.Length, true)).ToList();
            int ph = checked((int)BitConverter.ToUInt64(layout, 0x20));
            var loads = Enumerable.Range(0, BitConverter.ToUInt16(layout, 0x38)).Select(i => ph + i * 56).Where(at => BitConverter.ToUInt32(layout, at) == 1).ToList();
            for (int i = 0; i < writes.Count; i++)
            {
                long address = writes[i].Key;
                int length = writes[i].Value.Length;
                int segment = loads.FirstOrDefault(at => address >= (long)BitConverter.ToUInt64(layout, at + 16) &&
                    address + length <= (long)BitConverter.ToUInt64(layout, at + 16) + (long)BitConverter.ToUInt64(layout, at + 32));
                if (segment == 0) throw new InvalidDataException("The executable's layout was not extended for the " + GroupName + " patch.");
                long source = (long)BitConverter.ToUInt64(layout, segment + 8) + address - (long)BitConverter.ToUInt64(layout, segment + 16);
                for (int b = 0; b < length; b++)
                    if (image[targets[i] + b] != layout[source + b])
                        throw new InvalidDataException("The converted GT7.exe does not hold the game code where the " + GroupName + " patch expects it.");
            }

            // The patched guest ranges, merged, for the relocation check.
            var ranges = new List<long[]>();
            foreach (var range in writes.Select(w => new[] { w.Key + GuestRva, w.Key + GuestRva + w.Value.Length }).OrderBy(r => r[0]))
            {
                if (ranges.Count > 0 && range[0] <= ranges[ranges.Count - 1][1]) ranges[ranges.Count - 1][1] = Math.Max(ranges[ranges.Count - 1][1], range[1]);
                else ranges.Add(range);
            }
            var starts = ranges.Select(r => r[0]).ToArray();
            int removed = 0;
            if (BitConverter.ToUInt32(image, optional + 108) > 5)
            {
                long relocations = BitConverter.ToUInt32(image, optional + 112 + 5 * 8);
                int remaining = checked((int)BitConverter.ToUInt32(image, optional + 112 + 5 * 8 + 4));
                int block = remaining > 0 ? fileOffset(relocations, remaining, false) : 0;
                while (remaining >= 8)
                {
                    long page = BitConverter.ToUInt32(image, block);
                    int blockSize = checked((int)BitConverter.ToUInt32(image, block + 4));
                    if (blockSize < 8 || blockSize > remaining) throw new InvalidDataException("Invalid base relocations in the converted GT7.exe.");
                    for (int entry = block + 8; entry + 2 <= block + blockSize; entry += 2)
                    {
                        int value = BitConverter.ToUInt16(image, entry);
                        if (value >> 12 == 0) continue;
                        // A relocated field is at most 8 bytes: any overlap with the patched bytes counts.
                        long rva = page + (value & 0xfff);
                        int found = Array.BinarySearch(starts, rva + 8);
                        int before = (found >= 0 ? found : ~found) - 1;
                        if (before < 0 || ranges[before][1] <= rva) continue;
                        BitConverter.GetBytes((ushort)(value & 0xfff)).CopyTo(image, entry);
                        removed++;
                    }
                    block += blockSize;
                    remaining -= blockSize;
                }
            }
            for (int i = 0; i < writes.Count; i++) Buffer.BlockCopy(writes[i].Value, 0, image, targets[i], writes[i].Value.Length);
            return removed;
        }
    }
}
