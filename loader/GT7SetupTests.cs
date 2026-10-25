// Setup regression checks. Built with test-gt7.cmd; uses synthetic dumps beside its folder.
// SPDX-License-Identifier: GPL-2.0-only
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace RyGT
{
    static class GT7SetupTests
    {
        static int checks;
        static void Check(bool value, string message)
        { checks++; if (!value) throw new Exception(message); }
        static void Reject(Action action, string message)
        { try { action(); } catch (InvalidDataException) { checks++; return; } throw new Exception(message); }
        static void Put(string root, string name, string value)
        {
            string path = Path.Combine(root, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, value);
        }
        static void Sfo(string folder, string id, string version)
        {
            Directory.CreateDirectory(Path.Combine(folder, "sce_sys"));
            byte[] keys = Encoding.UTF8.GetBytes("TITLE_ID\0APP_VER\0");
            byte[] a = Encoding.UTF8.GetBytes(id + "\0"), b = Encoding.UTF8.GetBytes(version + "\0");
            using (var stream = File.Create(Path.Combine(folder, "sce_sys", "param.sfo")))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(0x46535000); writer.Write(0x101); writer.Write(52); writer.Write(52 + keys.Length); writer.Write(2);
                writer.Write((ushort)0); writer.Write((ushort)0x204); writer.Write(a.Length); writer.Write(a.Length); writer.Write(0);
                writer.Write((ushort)9); writer.Write((ushort)0x204); writer.Write(b.Length); writer.Write(b.Length); writer.Write(a.Length);
                writer.Write(keys); writer.Write(a); writer.Write(b);
            }
        }
        static void Dump(string folder)
        {
            Sfo(folder, GameLayout.TitleId, "01.00");
            foreach (string name in new[] { "eboot.bin", "contents\\gt.idx", "contents\\gt01.vol", "sce_module\\libc.prx", "sce_module\\libSceFios2.prx", "contents\\base-only.dat", "nested\\base.txt" })
                Put(folder, name, "base:" + name);
        }
        [STAThread]
        static int Main(string[] args)
        {
            // The synthetic dumps go beside the test's launcher folder, never inside it: setup
            // rejects a game or update folder nested in the launcher folder (or the reverse).
            string runtime = AppDomain.CurrentDomain.BaseDirectory;
            string root = Path.Combine(Path.GetFullPath(Path.Combine(runtime, "..")), "rygt7-tests-" + Guid.NewGuid().ToString("N"));
            string baseDir = Path.Combine(root, "base"), update = Path.Combine(root, "update");
            try
            {
                Dump(baseDir);
                var merged = new GameLayout(baseDir, null, runtime);
                Check(merged.Version == "01.00", "Merged version");
                Sfo(update, GameLayout.TitleId, "01.01");
                Put(update, "eboot.bin", "updated executable");
                Put(update, "contents\\gt.idx", "updated index");
                Put(update, "nested\\update.txt", "update-only");
                var separate = new GameLayout(baseDir, update, runtime);
                Check(separate.Version == "01.01", "Update version");
                Check(separate.Get("eboot.bin") == Path.Combine(update, "eboot.bin"), "Updated executable selected");
                Check(separate.Get("contents/gt.idx") == Path.Combine(update, "contents", "gt.idx"), "Updated index selected");
                Check(separate.Get("contents/base-only.dat") != null, "Base contents preserved");
                Check(separate.Get("nested/base.txt") != null && separate.Get("nested/update.txt") != null, "Nested overlay union");
                Check(separate.Get("sce_module/libc.prx") == Path.Combine(baseDir, "sce_module", "libc.prx"), "Module fallback");
                Reject(() => new GameLayout(baseDir, update, baseDir), "Runtime/source overlap allowed");
                Sfo(update, "CUSA03220", "01.69");
                Reject(() => new GameLayout(baseDir, update, runtime), "Wrong game update accepted");
                Sfo(update, GameLayout.TitleId, "00.99");
                Reject(() => new GameLayout(baseDir, update, runtime), "Older update accepted");
                Sfo(update, GameLayout.TitleId, "01.01");
                File.Delete(Path.Combine(update, "eboot.bin"));
                Reject(() => new GameLayout(baseDir, update, runtime), "Incomplete update accepted");
                Put(update, "eboot.bin", "updated executable");
                Put(update, "sce_sys\\param.sfo", "bad metadata");
                Reject(() => new GameLayout(baseDir, update, runtime), "Bad SFO accepted");
                Sfo(update, GameLayout.TitleId, "01.01");
                string converted = Path.Combine(root, "converted");
                Put(converted, "libc.prx", "converted-library");
                using (var form = new LoaderForm())
                {
                    typeof(LoaderForm).GetMethod("LinkGameFiles", BindingFlags.NonPublic | BindingFlags.Instance)
                        .Invoke(form, new object[] { separate, converted });
                }
                Check(File.ReadAllText(Path.Combine(runtime, "app0", "contents", "gt.idx")) == "updated index", "Linked update");
                Check(File.ReadAllText(Path.Combine(runtime, "app0", "nested", "base.txt")) == "base:nested\\base.txt", "Linked base fallback");
                Check(File.ReadAllText(Path.Combine(runtime, "app0", "sce_module", "libc.prx")) == "converted-library", "Converted module retained");
                Check(File.ReadAllText(Path.Combine(baseDir, "contents", "gt.idx")) == "base:contents\\gt.idx", "Source modified");
                File.Delete(Path.Combine(baseDir, "contents", "gt01.vol"));
                Reject(() => new GameLayout(baseDir, null, runtime), "Missing base volumes accepted");
                Check(!GameLayout.PatchKnownExecutable(new byte[64]), "Unknown executable patched");
                if (args.Length == 2)
                {
                    byte[] elf = File.ReadAllBytes(args[0]);
                    Check(GameLayout.PatchKnownExecutable(elf), "Known GT7 ELF not recognized");
                    Check(elf.SequenceEqual(File.ReadAllBytes(args[1])), "Conversion fix differs from verified ELF");
                }
                Console.WriteLine("PASS: " + checks + " GT7 setup checks");
                return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e); return 1; }
            // Test files remain under the temporary folder for inspection; never remove game dumps.
        }
    }
}
