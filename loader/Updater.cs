// SPDX-License-Identifier: GPL-2.0-or-later
// The launcher's updater: finds a newer ryGT7 release on GitLab and installs its program files over
// this folder, keeping a backup to roll back to. User data is never written.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace RyGT
{
    /// A release on GitLab: its version (the tag without "gt7-"), notes and the package zip.
    class ReleaseInfo
    {
        public string Version, Tag, Name, Notes, ZipName, ZipUrl, Sha256;

        /// The first lines of the notes' "What's new" (or of the notes), for the home view.
        public string Summary(int maxLength)
        {
            string text = Notes ?? "";
            int at = text.IndexOf("What's new", StringComparison.OrdinalIgnoreCase);
            if (at >= 0) text = text.Substring(at + 10);
            var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("- ") || l.StartsWith("* ")).ToList();
            string summary = string.Join(" " + (char)183 + " ", lines.Select(l => Regex.Replace(l.Substring(2), @"\*\*|`", "")));
            if (summary.Length == 0) summary = Regex.Replace(text, @"\s+|\*\*|`|#", " ").Trim();
            return summary.Length <= maxLength ? summary : summary.Substring(0, maxLength - 1).TrimEnd() + (char)0x2026;
        }
    }

    static class Updater
    {
        // rygt-team/rygt7 (project 88084032): its releases and their package files can be read
        // without signing in. By number: .NET Framework turns an escaped "%2F" back into "/".
        const string ReleasesUrl = "https://gitlab.com/api/v4/projects/88084032/releases?per_page=5";

        // Folders and files that hold the player's data or the converted game: an update never
        // writes, moves or deletes anything under them, whatever a package lists.
        static readonly string[] ProtectedFolders = { "user", "shaders", "app0", "sys", "_sd", "convert", "data", "logs", "temp",
                                                       "temp0", "download0", "decals", "update", "dlss", "Photos" };
        static readonly string[] ProtectedFiles = { "RyGT7.ini", "RyGT.ini", "GT7.exe", "GT7.exe.converted", "GT7.registry.json", "setup-out.txt" };

        public static string UpdateFolder(string root) { return Path.Combine(root, "update"); }

        /// The installed package's version, from manifest.json's name ("RyGT7-2026-10-30-prerelease"),
        /// or null (a build that didn't come from a release package).
        public static string InstalledVersion(string root)
        {
            try
            {
                string path = Path.Combine(root, "manifest.json");
                if (!File.Exists(path)) return null;
                var manifest = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
                object name;
                if (manifest == null || !manifest.TryGetValue("name", out name)) return null;
                var match = Regex.Match(name as string ?? "", @"^RyGT7-(.+?)(-prerelease)?$");
                return match.Success ? match.Groups[1].Value : null;
            }
            catch { return null; }
        }

        /// Release versions are dates with an optional letter for a hotfix (2026-10-27b), so they
        /// sort as text.
        public static bool IsNewer(string candidate, string installed)
        {
            return installed != null && candidate != null && string.CompareOrdinal(candidate, installed) > 0;
        }

        static WebClient Client()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var client = new WebClient();
            client.Headers[HttpRequestHeader.UserAgent] = "ryGT7-launcher";
            return client;
        }

        /// The newest release with a package zip, or null.
        public static ReleaseInfo Latest()
        {
            string json;
            using (var client = Client()) json = client.DownloadString(ReleasesUrl);
            var releases = new JavaScriptSerializer { MaxJsonLength = 16 << 20 }.DeserializeObject(json) as object[];
            if (releases == null) return null;
            foreach (Dictionary<string, object> release in releases.OfType<Dictionary<string, object>>())
            {
                string tag = release["tag_name"] as string ?? "";
                if (!tag.StartsWith("gt7-")) continue;
                var info = new ReleaseInfo
                {
                    Tag = tag, Version = tag.Substring(4), Name = release["name"] as string ?? tag,
                    Notes = release.ContainsKey("description") ? release["description"] as string : ""
                };
                var assets = release["assets"] as Dictionary<string, object>;
                var links = assets != null && assets.ContainsKey("links") ? assets["links"] as object[] : null;
                foreach (Dictionary<string, object> link in (links ?? new object[0]).OfType<Dictionary<string, object>>())
                {
                    string name = link["name"] as string ?? "";
                    if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                    info.ZipName = name;
                    info.ZipUrl = link["url"] as string;
                    break;
                }
                if (info.ZipUrl == null) continue;
                // The notes give the zip's SHA-256 next to its name ("sha256 <hex>").
                var sha = Regex.Match(info.Notes ?? "", @"\b([0-9a-fA-F]{64})\b");
                info.Sha256 = sha.Success ? sha.Groups[1].Value.ToLowerInvariant() : null;
                return info;
            }
            return null;
        }

        static string Sha256Of(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        static bool IsProtected(string relative)
        {
            string path = relative.Replace('/', '\\').TrimStart('\\');
            if (path.Contains("..") || Path.IsPathRooted(path)) return true;
            string first = path.Split('\\')[0];
            if (path.Contains("\\") && ProtectedFolders.Any(f => string.Equals(f, first, StringComparison.OrdinalIgnoreCase))) return true;
            return ProtectedFiles.Any(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
        }

        /// Leftovers of an update: the staging folder and the previous RyGT7.exe, which couldn't be
        /// deleted while it was running.
        public static void CleanUp(string root)
        {
            foreach (string name in new[] { "staging", "download", "trash" })
            {
                try { string dir = Path.Combine(UpdateFolder(root), name); if (Directory.Exists(dir)) Directory.Delete(dir, true); }
                catch { }
            }
        }

        static void CheckGameClosed(string root)
        {
            foreach (var process in Process.GetProcessesByName("GT7"))
            {
                try
                {
                    if (string.Equals(Path.GetDirectoryName(process.MainModule.FileName), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Close the game before updating.");
                }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }

        /// Downloads the release, checks it and installs its program files over root. progress gets
        /// a line of text and a fraction (or -1). The replaced files go to update\backup-<old version>.
        public static void Install(string root, ReleaseInfo release, Action<string, double> progress)
        {
            if (string.IsNullOrEmpty(release.Sha256))
                throw new InvalidDataException("The release notes don't give the package's SHA-256, so it can't be checked.");
            CheckGameClosed(root);
            CleanUp(root);
            string folder = UpdateFolder(root), download = Path.Combine(folder, "download"), staging = Path.Combine(folder, "staging");
            Directory.CreateDirectory(download);
            string zip = Path.Combine(download, release.ZipName);

            progress("Downloading " + release.ZipName + "...", 0);
            using (var client = Client())
            {
                var done = new System.Threading.ManualResetEvent(false);
                Exception failure = null;
                client.DownloadProgressChanged += (s, e) =>
                    progress("Downloading " + release.ZipName + ": " + (e.BytesReceived >> 20) + " of " + (e.TotalBytesToReceive >> 20) + " MB",
                             e.TotalBytesToReceive > 0 ? e.BytesReceived / (double)e.TotalBytesToReceive : -1);
                client.DownloadFileCompleted += (s, e) => { failure = e.Error; done.Set(); };
                client.DownloadFileAsync(new Uri(release.ZipUrl), zip + ".part");
                done.WaitOne();
                if (failure != null) throw failure;
            }
            File.Move(zip + ".part", zip);

            progress("Checking the download...", -1);
            string sha = Sha256Of(zip);
            if (sha != release.Sha256)
                throw new InvalidDataException("The download is damaged (SHA-256 " + sha + ", expected " + release.Sha256 + ").");

            progress("Unpacking...", -1);
            ZipFile.ExtractToDirectory(zip, staging);
            string manifestPath = Directory.GetFiles(staging, "manifest.json", SearchOption.AllDirectories)
                                           .OrderBy(p => p.Length).FirstOrDefault();
            if (manifestPath == null) throw new InvalidDataException("The package has no manifest.json.");
            string packageRoot = Path.GetDirectoryName(manifestPath);
            var manifest = (Dictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = 16 << 20 }.DeserializeObject(File.ReadAllText(manifestPath));
            var files = new List<string>();
            foreach (Dictionary<string, object> file in ((object[])manifest["files"]).OfType<Dictionary<string, object>>())
            {
                string relative = ((string)file["path"]).Replace('/', '\\');
                if (IsProtected(relative)) continue;
                string source = Path.Combine(packageRoot, relative);
                if (!File.Exists(source) || Sha256Of(source) != ((string)file["sha256"]).ToLowerInvariant())
                    throw new InvalidDataException("The package's " + relative + " doesn't match its manifest.");
                files.Add(relative);
            }
            files.Add("manifest.json"); // last, so a failed update still reports the old version

            string installed = InstalledVersion(root) ?? "unknown";
            string backup = Path.Combine(folder, "backup-" + installed);
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            Directory.CreateDirectory(backup);
            var replaced = new List<string>();
            var added = new List<string>();
            try
            {
                int n = 0;
                foreach (string relative in files)
                {
                    progress("Installing " + relative, ++n / (double)files.Count);
                    string target = Path.Combine(root, relative), source = Path.Combine(packageRoot, relative);
                    if (File.Exists(target))
                    {
                        string saved = Path.Combine(backup, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(saved));
                        // A move, not a copy: RyGT7.exe is running and can only be renamed.
                        File.Move(target, saved);
                        replaced.Add(relative);
                    }
                    else
                    {
                        added.Add(relative);
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Copy(source, target);
                }
            }
            catch
            {
                Restore(root, backup, replaced, added);
                Directory.Delete(backup, true);
                throw;
            }
            WriteRecord(backup, installed, release.Version, replaced, added);
            CleanUp(root);
            progress("Updated to " + release.Version + ".", 1);
        }

        static void WriteRecord(string backup, string from, string to, List<string> replaced, List<string> added)
        {
            var record = new Dictionary<string, object> { { "from", from }, { "to", to }, { "replaced", replaced }, { "added", added } };
            File.WriteAllText(Path.Combine(backup, "rollback.json"), new JavaScriptSerializer().Serialize(record));
        }

        static void Restore(string root, string backup, List<string> replaced, List<string> added)
        {
            string trash = Path.Combine(UpdateFolder(root), "trash");
            Directory.CreateDirectory(trash);
            foreach (string relative in added.Concat(replaced))
            {
                string target = Path.Combine(root, relative);
                if (!File.Exists(target)) continue;
                // Moved aside rather than deleted: one of them may be the running RyGT7.exe.
                string aside = Path.Combine(trash, Guid.NewGuid().ToString("N"));
                File.Move(target, aside);
            }
            foreach (string relative in replaced)
            {
                string saved = Path.Combine(backup, relative), target = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Move(saved, target);
            }
        }

        /// The update that can be rolled back (the newest backup), as "from" and "to" versions.
        public static Tuple<string, string, string> LastUpdate(string root)
        {
            string folder = UpdateFolder(root);
            if (!Directory.Exists(folder)) return null;
            var newest = Directory.GetDirectories(folder, "backup-*")
                                  .Where(d => File.Exists(Path.Combine(d, "rollback.json")))
                                  .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, "rollback.json"))).FirstOrDefault();
            if (newest == null) return null;
            var record = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Path.Combine(newest, "rollback.json")));
            return Tuple.Create(newest, (string)record["from"], (string)record["to"]);
        }

        /// Puts the files of the last update back as they were before it.
        public static void RollBack(string root)
        {
            CheckGameClosed(root);
            var last = LastUpdate(root);
            if (last == null) throw new InvalidOperationException("There is no update to roll back.");
            var record = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Path.Combine(last.Item1, "rollback.json")));
            var replaced = ((object[])record["replaced"]).Cast<string>().ToList();
            var added = ((object[])record["added"]).Cast<string>().ToList();
            Restore(root, last.Item1, replaced, added);
            Directory.Delete(last.Item1, true);
        }

        /// Starts the (new) launcher and closes this one.
        public static void Restart(string root)
        {
            Process.Start(new ProcessStartInfo(Path.Combine(root, "RyGT7.exe")) { WorkingDirectory = root, UseShellExecute = false });
            System.Windows.Forms.Application.Exit();
        }
    }
}
