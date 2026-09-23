using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace AttachmentUnlock
{
    /// <summary>
    /// Double-click mode, for players: find the game, build both packages from this install's own
    /// weapon files, and say what to do with them. The command line (arguments to Main) stays for
    /// building by hand.
    /// </summary>
    internal static class Builder
    {
        const string Title = "Gunsmith Attachment Unlock - builder 1.1";

        public static int Interactive()
        {
            try { Console.Title = Title; } catch { }
            Line(Title);
            Line("Builds the attachment unlock from YOUR weapon files, so edits from your other mods are kept.");
            Line("It only reads your game files. Nothing is installed until you install a package yourself.");
            Line();
            try
            {
                string gta = FindGame() ?? AskForGame();
                if (gta == null) return Done(1);
                Line("Game folder:  " + gta);

                if (!File.Exists(Path.Combine(gta, "mods", "update", "update.rpf")))
                {
                    Line();
                    Line("There's no mods\\update\\update.rpf yet. Set up OpenIV's mods folder first:");
                    Line("  OpenIV -> Tools -> ASI Manager -> install \"OpenIV.ASI\", then install any .oiv to the");
                    Line("  mods folder (or copy update\\update.rpf into mods\\update\\ with OpenIV). Then run this again.");
                    return Done(1);
                }

                string home = OutputFolder();
                string temp = Path.Combine(Path.GetTempPath(), "GunsmithUnlockBuilder");
                if (Directory.Exists(temp)) try { Directory.Delete(temp, true); } catch { }
                Program.originals = Path.Combine(home, "Originals");
                Program.Verbose = false;
                Line("Packages go in:  " + home);
                Line();

                // 1. the safe one: more parts on the mounts each gun already has
                Program.ResetMounts(null);
                Line("Building the Attachment Unlock...");
                const string plainName = "Gunsmith Attachment Unlock";
                var plain = Program.Build(gta, Path.Combine(temp, "plain"), Path.Combine(home, plainName + ".oiv"), false);
                if (plain == null)
                {
                    Line();
                    Line("Couldn't read any weapon files in your mods folder. OpenIV keeps an archive sealed until");
                    Line("it has changed something in it. Fix: in OpenIV (Edit mode on) open mods\\update\\update.rpf,");
                    Line("go to common\\data\\ai, drag weapons.meta out and straight back in, then run this again.");
                    return Done(1);
                }
                Summary(plain, home, plainName);

                // 2. plus the mounts R* left out - checked against the real models if Gunsmith reported them
                string mountsFile = Path.Combine(gta, "plugins", "Gunsmith", "Mounts.txt");
                Dictionary<uint, HashSet<string>> report = null;
                if (File.Exists(mountsFile))
                    try { report = Program.ReadMounts(mountsFile); if (report.Count == 0) report = null; } catch { report = null; }
                Program.ResetMounts(report);
                string name = report != null ? "Gunsmith Attachment Unlock + Verified Mounts" : "Gunsmith Attachment Unlock + Pistol Optics";
                Line();
                Line(report != null
                    ? $"Building {name} (using Gunsmith's report of {report.Count} weapon models)..."
                    : $"Building {name}...");
                var more = Program.Build(gta, Path.Combine(temp, "mounts"), Path.Combine(home, name + ".oiv"), true);
                if (more != null) Summary(more, home, name);

                WriteReport(home, plain, more);
                try { Directory.Delete(temp, true); } catch { }

                Line();
                if (plain.LooksUnlocked || (more?.LooksUnlocked ?? false))
                {
                    Line("NOTE: your weapon files already look unlocked - probably by an older version of this that");
                    Line("didn't mark its files. The packages are fine, but their REVERT can only put back what's");
                    Line("there now. For a way back to stock, install the older version's REVERT first, then run this.");
                    Line();
                }
                if (plain.OursWithoutOriginal > 0)
                {
                    Line($"NOTE: {plain.OursWithoutOriginal} file(s) were made by this builder but their originals aren't in");
                    Line("the Originals folder any more, so the REVERT keeps the unlocked version of those.");
                    Line();
                }
                Line("NEXT: close Discord, then in OpenIV go to Tools -> Package Installer, pick ONE of the");
                Line("packages above, and install it to the mods folder. (Double-clicking an .oiv only opens OpenIV.)");
                Line("Each has a \"- REVERT\" twin that puts your files back.");
                Line("The first game load afterwards is slow while GTA rebuilds its cache - let it finish.");
                Line("Installed a new weapon mod later? Run this again and install the new package.");
                return Done(0);
            }
            catch (Exception ex)
            {
                Line();
                Line("Something went wrong: " + ex.Message);
                Line("Details are in the window above. Nothing in your game folder was changed.");
                return Done(1);
            }
        }

        static void Summary(Program.Result r, string home, string name)
        {
            if (r.Package == null)
            {
                // an old package of the same name from an earlier run would only mislead
                foreach (string stale in new[] { name + ".oiv", name + " - REVERT.oiv" })
                    try { File.Delete(Path.Combine(home, stale)); } catch { }
                Line("  Nothing to add - every mount already takes everything it can.");
                return;
            }
            Line($"  {r.Weapons} weapons expanded, {r.Parts:N0} attachment slots added, {r.Files} files" +
                 (r.NewMounts > 0 ? $", {r.NewMounts} new mounts" : ""));
            Line("  -> " + Path.GetFileName(r.Package));
            Line("  -> " + Path.GetFileName(r.Revert));
        }

        static void WriteReport(string home, Program.Result plain, Program.Result more)
        {
            try
            {
                var lines = new List<string> { Title, "Built " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"), "", "== " + Path.GetFileName(plain.Package) };
                lines.AddRange(plain.Report);
                if (more != null)
                {
                    lines.Add("");
                    lines.Add("== " + Path.GetFileName(more.Package));
                    lines.AddRange(more.Report);
                }
                File.WriteAllLines(Path.Combine(home, "Build report.txt"), lines);
            }
            catch { /* the report is a courtesy */ }
        }

        // ---- finding things --------------------------------------------------------------------

        /// <summary>The game folder: next to this exe, from the launchers' registry keys, or the usual places.</summary>
        static string FindGame()
        {
            var candidates = new List<string>();
            string here = AppDomain.CurrentDomain.BaseDirectory;
            candidates.Add(here);
            candidates.Add(Path.GetDirectoryName(here.TrimEnd('\\', '/')));

            foreach (var (key, value) in new[]
            {
                (@"SOFTWARE\WOW6432Node\Rockstar Games\Grand Theft Auto V", "InstallFolder"),
                (@"SOFTWARE\WOW6432Node\Rockstar Games\GTAV", "InstallFolderSteam"),
                (@"SOFTWARE\WOW6432Node\Rockstar Games\GTAV", "InstallFolderEpic"),
                (@"SOFTWARE\Rockstar Games\Grand Theft Auto V", "InstallFolder"),
            })
            {
                string v = Registry(RegistryHive.LocalMachine, key, value);
                if (v == null) continue;
                candidates.Add(v);
                candidates.Add(Path.GetDirectoryName(v.TrimEnd('\\', '/')));   // the Steam key points at a GTAV subfolder
            }

            string steam = Registry(RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamPath");
            if (steam != null)
            {
                candidates.Add(Path.Combine(steam, "steamapps", "common", "Grand Theft Auto V"));
                foreach (string lib in SteamLibraries(steam))
                    candidates.Add(Path.Combine(lib, "steamapps", "common", "Grand Theft Auto V"));
            }
            candidates.Add(@"C:\Program Files (x86)\Steam\steamapps\common\Grand Theft Auto V");
            candidates.Add(@"C:\Program Files\Rockstar Games\Grand Theft Auto V");
            candidates.Add(@"C:\Program Files\Epic Games\GTAV");

            foreach (string c in candidates)
                if (IsGame(c)) return Path.GetFullPath(c);
            return null;
        }

        static bool IsGame(string dir)
        {
            try { return !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "GTA5.exe")); }
            catch { return false; }
        }

        static string AskForGame()
        {
            Line("Couldn't find Grand Theft Auto V on its own.");
            for (int i = 0; i < 3; i++)
            {
                Console.Write("Paste your game folder (the one with GTA5.exe) and press Enter: ");
                string typed = (Console.ReadLine() ?? "").Trim().Trim('"');
                if (typed.Length == 0) return null;
                if (IsGame(typed)) return Path.GetFullPath(typed);
                Line("There's no GTA5.exe in that folder.");
            }
            return null;
        }

        /// <summary>Steam's other library folders, from libraryfolders.vdf ("path" lines).</summary>
        static IEnumerable<string> SteamLibraries(string steam)
        {
            string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) yield break;
            string[] lines;
            try { lines = File.ReadAllLines(vdf); } catch { yield break; }
            foreach (string raw in lines)
            {
                string l = raw.Trim();
                if (!l.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase)) continue;
                int a = l.IndexOf('"', 6), b = l.LastIndexOf('"');
                if (a < 0 || b <= a) continue;
                yield return l.Substring(a + 1, b - a - 1).Replace(@"\\", @"\");
            }
        }

        static string Registry(RegistryHive hive, string key, string value)
        {
            try
            {
                using (var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64))
                using (var k = root.OpenSubKey(key))
                    return k?.GetValue(value) as string;
            }
            catch { return null; }
        }

        /// <summary>
        /// Next to the exe if it can write there, otherwise in Documents. Double-clicked straight out of
        /// a zip, Windows runs it from a temp folder that gets cleaned away - the packages and the kept
        /// originals would go with it - so that counts as nowhere to write too.
        /// </summary>
        static string OutputFolder()
        {
            string here = AppDomain.CurrentDomain.BaseDirectory;
            bool inTemp = false;
            try { inTemp = Path.GetFullPath(here).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase); }
            catch { }
            if (!inTemp)
                try
                {
                    string probe = Path.Combine(here, ".write-test");
                    File.WriteAllText(probe, "");
                    File.Delete(probe);
                    return here.TrimEnd('\\', '/');
                }
                catch { }
            string docs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Gunsmith Attachment Unlock");
            Directory.CreateDirectory(docs);
            return docs;
        }

        // ---- the console ---------------------------------------------------------------------------

        static void Line(string s = "") => Console.WriteLine(s);

        static int Done(int code)
        {
            Line();
            Line("Press any key to close.");
            try { if (!Console.IsInputRedirected) Console.ReadKey(true); } catch { }
            return code;
        }
    }
}
