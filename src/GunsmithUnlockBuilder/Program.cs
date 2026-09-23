using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace AttachmentUnlock
{
    /// <summary>
    /// Reads the weapon data out of the game's mods folder, adds every sensible vanilla attachment to
    /// each weapon's allowed list, and packages the patched files as an OpenIV .oiv installer.
    ///
    /// usage: AttachmentUnlock [gtaDir] [workDir] [outputOiv] [--mounts] [--verified [Mounts.txt]] [--from pristineDir]
    ///
    ///   --mounts    also give guns the mounts R* left out (an optic on every base pistol, ...)
    ///   --verified  only add a mount where the gun's model really has the bone, using the report
    ///               GunsmithBoneReport writes in game (defaults to plugins\Gunsmith\Mounts.txt)
    /// </summary>
    internal static class Program
    {
        sealed class Source
        {
            public string ArchiveRel;   // e.g. update\update.rpf
            public string Inner;        // e.g. common/data/ai/weapons.meta
            public string Key;          // path inside the .oiv content folder
            public XDocument Doc;
            public bool Changed;
        }

        sealed class Comp
        {
            public string Model, Kind;
        }

        /// <summary>
        /// The only sights a handgun slide gets: the two made for one (the Pistol Mk II's and the SNS
        /// Mk II's mounted scopes). Rifle glass hangs off the slide's scope point at rifle-rail height
        /// and floats above the gun, overhanging the muzzle - seen in game on the Pistol Mk II.
        /// </summary>
        static readonly string[] PistolOptics = { "COMPONENT_AT_PI_RAIL", "COMPONENT_AT_PI_RAIL_02" };

        /// <summary>--from: a directory of untouched metas to patch instead of the ones in the archives.</summary>
        static string origin;

        /// <summary>
        /// --verified: the mount bones each weapon MODEL really has, keyed by weapon hash, read from the
        /// Mounts.txt that GunsmithBoneReport writes in game. Null when we're guessing.
        /// </summary>
        static Dictionary<uint, HashSet<string>> verified;

        /// <summary>Which model bones can carry each family of part, best first.</summary>
        static readonly Dictionary<string, string[]> MountBones = new Dictionary<string, string[]>
        {
            ["optic"] = new[] { "WAPScop", "WAPScop_2", "WAPScop_3" },
            ["muzzle"] = new[] { "WAPSupp", "WAPSupp_2", "WAPSupp_3" },
            ["light"] = new[] { "WAPFlshLasr", "WAPFlshLasr_2", "WAPFlsh", "WAPFlsh_2" },
            ["grip"] = new[] { "WAPGrip", "WAPGrip_2", "WAPGrip_3" },
        };

        static readonly string[] FirearmGroups =
        {
            "GROUP_PISTOL", "GROUP_SMG", "GROUP_RIFLE", "GROUP_MG", "GROUP_SHOTGUN", "GROUP_SNIPER", "GROUP_HEAVY", "GROUP_STUNGUN",
        };

        static int Main(string[] args)
        {
            // double-clicked: find the game, build both packages, explain, wait for a key
            if (args.Length == 0) return Builder.Interactive();
            if (args.Length > 1 && args[0] == "--find") { Find(args.Length > 2 ? args[2] : @"C:\Program Files (x86)\Steam\steamapps\common\Grand Theft Auto V", args[1]); return 0; }
            bool mounts = args.Contains("--mounts");
            // Rebuild from the pristine copies of an earlier run instead of what's in the archives now,
            // so a corrected package lands in one install whatever state the game files are in.
            int fi = Array.IndexOf(args, "--from");
            if (fi >= 0 && fi + 1 < args.Length) origin = args[fi + 1];
            // --verified [Mounts.txt]: the path is optional, it defaults to where the plugin writes it
            int vi = Array.IndexOf(args, "--verified");
            bool vpath = vi >= 0 && vi + 1 < args.Length && args[vi + 1].EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
            string mountsFile = vpath ? args[vi + 1] : null;
            args = args.Where((a, i) => a != "--mounts" && a != "--from" && !(fi >= 0 && i == fi + 1)
                                        && a != "--verified" && !(vpath && i == vi + 1)).ToArray();
            string gta = args.Length > 0 ? args[0] : @"C:\Program Files (x86)\Steam\steamapps\common\Grand Theft Auto V";
            if (vi >= 0)
            {
                mountsFile = mountsFile ?? Path.Combine(gta, "plugins", "Gunsmith", "Mounts.txt");
                if (!File.Exists(mountsFile))
                {
                    Console.Error.WriteLine("no mount report at " + mountsFile);
                    Console.Error.WriteLine("run GunsmithBoneReport in the RPH console (F4) first - it writes that file.");
                    return 1;
                }
                verified = ReadMounts(mountsFile);
                Console.WriteLine("mount report: " + verified.Count + " weapon models, from " + mountsFile);
                if (verified.Count == 0) { Console.Error.WriteLine("the mount report is empty"); return 1; }
            }
            string work = args.Length > 1 ? args[1] : "work";
            string oiv = args.Length > 2 ? args[2] : Path.Combine("dist", "Gunsmith Attachment Unlock.oiv");
            return Build(gta, work, oiv, mounts) == null ? 1 : 0;
        }

        /// <summary>
        /// Where the builder keeps the files it saw before it ever touched them, so a later run - with
        /// the unlock already installed - still builds from, and reverts to, the real originals. Null
        /// in the developer's command-line mode, which uses --from instead.
        /// </summary>
        internal static string originals;

        /// <summary>Written into every file the unlock rewrites, so the builder can tell its own work from an original.</summary>
        internal const string Marker = " Gunsmith Attachment Unlock - rewritten by the builder. The file it replaced is kept in its Originals folder. ";

        /// <summary>What one build did, for the summary.</summary>
        internal sealed class Result
        {
            public int Weapons, Files, Parts, NewMounts, Originals, OursReplaced, OursWithoutOriginal;
            public bool LooksUnlocked;
            public string Package, Revert;
            public readonly List<string> Report = new List<string>();
        }

        internal static void ResetMounts(Dictionary<uint, HashSet<string>> report) => verified = report;

        /// <summary>Reads, expands and packages. Null when there was nothing readable to work on.</summary>
        internal static Result Build(string gta, string work, string oiv, bool mounts)
        {
            var result = new Result();
            lastResult = result;
            string vanilla = Path.Combine(work, "vanilla"), patched = Path.Combine(work, "patched");
            Directory.CreateDirectory(vanilla);
            Directory.CreateDirectory(patched);

            var sources = Collect(gta, vanilla);
            if (sources.Count == 0) { if (Verbose) Console.Error.WriteLine("no readable weapon data found in the mods folder"); return null; }
            Say("readable weapon data files: " + sources.Count);

            // An unlock from before files were signed looks like an original. Stock guns never list more
            // than a few parts on one mount, so a dozen means it's been unlocked already.
            // (Only real sights and muzzle devices count - the Pistol Mk II keeps a dozen slide camos on a scope bone.)
            result.LooksUnlocked = sources.Any(s => Weapons(s.Doc).Any(w => AttachPoints(w).Any(ap =>
            {
                string fam = Family((string)ap.Element("AttachBone"));
                return (fam == "optic" || fam == "muzzle") && ComponentNames(ap).Count(c => Looks(fam, c)) >= 12;
            })));

            // 1. every component we know about, with its model and class
            var comps = new Dictionary<string, Comp>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in sources)
                foreach (var item in s.Doc.Descendants("Item"))
                {
                    string type = (string)item.Attribute("type");
                    if (type == null || !type.StartsWith("CWeaponComponent")) continue;
                    string name = (string)item.Element("Name");
                    if (name == null || comps.ContainsKey(name)) continue;
                    comps[name] = new Comp { Model = (string)item.Element("Model"), Kind = type };
                }

            // 2. learn which mount each component belongs on, from every weapon we can read
            var pools = new Dictionary<string, List<string>>();
            var weaponCount = 0;
            foreach (var s in sources)
                foreach (var w in Weapons(s.Doc))
                {
                    weaponCount++;
                    foreach (var ap in AttachPoints(w))
                    {
                        string family = Family((string)ap.Element("AttachBone"));
                        if (family == null) continue;
                        if (!pools.TryGetValue(family, out var pool)) pools[family] = pool = new List<string>();
                        foreach (var name in ComponentNames(ap))
                            if (Looks(family, name) && !pool.Contains(name, StringComparer.OrdinalIgnoreCase)) pool.Add(name);
                    }
                }
            Say("weapons: " + weaponCount + ", components known: " + comps.Count);
            foreach (var kv in pools.OrderBy(k => k.Key))
                Say("  pool " + kv.Key + ": " + kv.Value.Count + " parts");

            // 3. expand each weapon
            var report = new List<string>();
            int totalAdded = 0;
            var newMounts = new SortedDictionary<string, int>();
            var notInReport = new SortedSet<string>();
            var noBone = new SortedSet<string>();
            foreach (var s in sources)
                foreach (var w in Weapons(s.Doc))
                {
                    string wname = (string)w.Element("Name") ?? "?";
                    string group = (string)w.Element("Group") ?? "";
                    if (!FirearmGroups.Contains(group)) continue;
                    int added = 0;
                    var before = new List<string>();

                    // --mounts: guns R* never gave a mount at all (no base pistol lists an optic) get
                    // one. Guessing, that's an optic on WAPScop - a bone the model may not have, and a
                    // part on a missing bone renders nowhere, so the guess ships as its own package.
                    // With --verified we know which bones each model really has: every family the
                    // model can carry but the data leaves out gets a mount, on the bone that exists.
                    var aps = w.Element("AttachPoints");
                    if (mounts && aps != null && MountWorthy(group, wname))
                    {
                        HashSet<string> real = null;
                        if (verified != null && !verified.TryGetValue(Joaat(wname), out real)) notInReport.Add(wname);
                        else
                        {
                            // bones already in use - the Pistol Mk II hangs its slide camos off WAPScop_2,
                            // and a second mount on the same bone would fight it
                            var taken = new HashSet<string>(AttachPoints(w).Select(p => (string)p.Element("AttachBone"))
                                                                            .Where(b => b != null), StringComparer.OrdinalIgnoreCase);
                            foreach (string family in verified == null ? new[] { "optic" } : new[] { "optic", "muzzle", "light", "grip" })
                            {
                                bool has = AttachPoints(w).Any(p => Family((string)p.Element("AttachBone")) == family
                                                                    && ComponentNames(p).Any(c => Looks(family, c)));
                                if (has || !pools.TryGetValue(family, out var pool)) continue;
                                string bone = verified == null
                                    ? "WAPScop"
                                    : MountBones[family].FirstOrDefault(b => real.Contains(b) && !taken.Contains(b));
                                if (bone == null)
                                {
                                    if (family == "optic") noBone.Add(wname);
                                    continue;
                                }
                                bool pistol = group == "GROUP_PISTOL" || group == "GROUP_STUNGUN";
                                var fit = (family == "optic" && pistol ? PistolOptics : pool.Where(c => Suitable(family, c, comps, group))).ToList();
                                if (fit.Count == 0) continue;
                                aps.Add(new XElement("Item",
                                    new XElement("AttachBone", bone),
                                    new XElement("Components", fit.Select(c => new XElement("Item",
                                        new XElement("Name", c),
                                        new XElement("Default", new XAttribute("value", "false")))))));
                                taken.Add(bone);
                                added += fit.Count;
                                before.Add(family + " mount added on " + bone);
                                newMounts[family] = (newMounts.TryGetValue(family, out int n) ? n : 0) + 1;
                            }
                        }
                    }

                    foreach (var ap in AttachPoints(w))
                    {
                        string bone = (string)ap.Element("AttachBone");
                        string family = Family(bone);
                        if (family == null || !pools.TryGetValue(family, out var pool)) continue;
                        var list = ap.Element("Components");
                        if (list == null) continue;
                        var have = new HashSet<string>(ComponentNames(ap), StringComparer.OrdinalIgnoreCase);
                        // Only expand a real mount. Some guns park camo/slide variants on a WAP* bone
                        // (the Pistol Mk II keeps its slide camos on WAPScop_2), and a scope hung there
                        // would float in mid air.
                        if (!have.Any(h => Looks(family, h))) continue;
                        // A handgun's scope point only gets more pistol sights if it was built for one.
                        // The Heavy Revolver Mk II's top rail takes rifle glass from the factory; a slide
                        // plate on it would sit wrong, so it keeps just its own.
                        bool pistolGun = group == "GROUP_PISTOL" || group == "GROUP_STUNGUN";
                        if (family == "optic" && pistolGun && !have.Any(h => PistolMade(h, comps))) continue;
                        before.Add(family + " " + have.Count);
                        foreach (string cand in pool)
                        {
                            if (have.Contains(cand) || !Suitable(family, cand, comps, group)) continue;
                            list.Add(new XElement("Item",
                                new XElement("Name", cand),
                                new XElement("Default", new XAttribute("value", "false"))));
                            have.Add(cand);
                            added++;
                        }
                    }
                    if (added <= 0) continue;
                    totalAdded += added;
                    s.Changed = true;
                    report.Add(wname.PadRight(34) + "+" + added + "   (was " + string.Join(", ", before) + ")");
                }

            // 4. write the patched files, each signed so a later run knows it's ours and not an original
            foreach (var s in sources.Where(x => x.Changed))
            {
                string dest = Path.Combine(patched, s.Key.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                s.Doc.Root?.AddFirst(new XComment(Marker));
                s.Doc.Save(dest, SaveOptions.DisableFormatting);
            }

            // 5. build the .oiv - unless there was nothing to add, when an empty package would only confuse
            if (!sources.Any(x => x.Changed))
            {
                Say("nothing to add: every mount already takes everything it can");
                return result;   // Package stays null
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(oiv)));
            string extra = !mounts ? null
                : verified != null
                    ? "Also adds the mounts the game left out - but only where the gun's own model has the mount point, checked in game with GunsmithBoneReport, so every part you can pick actually shows up."
                    : "Also gives guns without an optic mount one (every base pistol, the shotguns). Some of those models have no scope point, so on them the optic may not appear.";
            BuildOiv(oiv, sources.Where(x => x.Changed).ToList(), patched, false, extra);
            // A one-click way back: the same package built from the untouched files we pulled out first.
            string revert = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(oiv)),
                Path.GetFileNameWithoutExtension(oiv) + " - REVERT.oiv");
            BuildOiv(revert, sources.Where(x => x.Changed).ToList(), vanilla, true, null);

            File.WriteAllLines(Path.Combine(work, "report.txt"), report.OrderBy(r => r));
            File.WriteAllLines(Path.Combine(work, "pools.txt"), pools.OrderBy(k => k.Key).SelectMany(kv =>
                new[] { "[" + kv.Key + "]" }.Concat(kv.Value.OrderBy(v => v).Select(v =>
                    "  " + v.PadRight(46) + (comps.TryGetValue(v, out var c) ? c.Model : "(model unknown - defined in an encrypted file)")))));
            Say("");
            Say("expanded " + report.Count + " weapons; patched files: " + sources.Count(x => x.Changed));
            foreach (var line in report.OrderBy(r => r).Take(12)) Say("  " + line);
            if (mounts)
            {
                Say("new mounts: " + (newMounts.Count == 0 ? "none" : string.Join(", ", newMounts.Select(kv => kv.Value + " " + kv.Key))));
                if (noBone.Count > 0)
                    Say("no optic - the model has no free scope bone: " + string.Join(", ", noBone));
                if (notInReport.Count > 0)
                    Say("left alone - not in the mount report (add-on or unreleased guns?): " + string.Join(", ", notInReport));
            }
            Say("package: " + oiv);

            result.Weapons = report.Count;
            result.Report.AddRange(report.OrderBy(r => r));
            result.Files = sources.Count(x => x.Changed);
            result.Parts = totalAdded;
            result.NewMounts = newMounts.Values.Sum();
            result.Package = oiv;
            result.Revert = revert;
            return result;
        }

        /// <summary>False while the double-click builder runs a build: it prints its own, shorter summary.</summary>
        internal static bool Verbose = true;
        static Result lastResult;

        static void Say(string line) { if (Verbose) Console.WriteLine(line); }

        // ---- the mount report ------------------------------------------------------------------

        /// <summary>
        /// Reads GunsmithBoneReport's Mounts.txt: "0x1B06D571 = WAPFlshLasr, WAPSupp, WAPClip   ; Pistol".
        /// A weapon listed with nothing after the '=' was checked and has no mounts at all.
        /// </summary>
        internal static Dictionary<uint, HashSet<string>> ReadMounts(string path)
        {
            var map = new Dictionary<uint, HashSet<string>>();
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw;
                int semi = line.IndexOf(';');
                if (semi >= 0) line = line.Substring(0, semi);
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string key = line.Substring(0, eq).Trim();
                if (key.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) key = key.Substring(2);
                if (!uint.TryParse(key, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hash)) continue;
                map[hash] = new HashSet<string>(line.Substring(eq + 1).Split(',').Select(b => b.Trim()).Where(b => b.Length > 0),
                                                StringComparer.OrdinalIgnoreCase);
            }
            return map;
        }

        /// <summary>GTA's name hash (Jenkins one-at-a-time over the lower-cased name) - the report keys weapons by it.</summary>
        static uint Joaat(string s)
        {
            uint h = 0;
            foreach (char c in s.ToLowerInvariant())
            {
                h += c;
                h += h << 10;
                h ^= h >> 6;
            }
            h += h << 3;
            h ^= h >> 11;
            h += h << 15;
            return h;
        }

        // ---- looking things up -----------------------------------------------------------------

        /// <summary>Prints every file in the mods folder whose path contains <paramref name="needle"/>, nested archives included.</summary>
        static void Find(string gta, string needle)
        {
            var archives = new List<string>();
            string mods = Path.Combine(gta, "mods");
            foreach (var f in Directory.Exists(mods) ? Directory.GetFiles(mods, "*.rpf") : new string[0]) archives.Add(f);
            string upd = Path.Combine(mods, "update", "update.rpf");
            if (File.Exists(upd)) archives.Add(upd);
            string dlcpacks = Path.Combine(mods, "update", "x64", "dlcpacks");
            if (Directory.Exists(dlcpacks))
                foreach (var dir in Directory.GetDirectories(dlcpacks))
                    if (File.Exists(Path.Combine(dir, "dlc.rpf"))) archives.Add(Path.Combine(dir, "dlc.rpf"));

            foreach (var a in archives)
            {
                try
                {
                    using (var rpf = new Rpf(a))
                    {
                        if (!rpf.IsOpenArchive) { Console.WriteLine("(sealed) " + a.Substring(gta.Length + 1)); continue; }
                        Walk(rpf, a.Substring(gta.Length + 1), needle, 0);
                    }
                }
                catch (Exception ex) { Console.WriteLine("(failed) " + a + " - " + ex.Message); }
            }
        }

        static void Walk(Rpf rpf, string label, string needle, int depth)
        {
            foreach (var e in rpf.Entries)
            {
                if (e.Path.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    Console.WriteLine("  " + label + " :: " + e.Path + (e.Encrypted ? "  [sealed]" : "") + "  " + e.UncompressedSize + " bytes");
                if (depth >= 2 || !e.Path.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase)) continue;
                byte[] data = null;
                try { data = rpf.Read(e); } catch { }
                if (data == null || data.Length < 16) { if (e.Encrypted) Console.WriteLine("  (sealed nested) " + label + " :: " + e.Path); continue; }
                try
                {
                    using (var inner = new Rpf(data))
                        if (inner.IsOpenArchive) Walk(inner, label + " :: " + e.Path, needle, depth + 1);
                        else Console.WriteLine("  (sealed nested) " + label + " :: " + e.Path);
                }
                catch { }
            }
        }

        // ---- reading ---------------------------------------------------------------------------

        static List<Source> Collect(string gta, string vanillaDir)
        {
            var list = new List<Source>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Take(string archiveAbs, string archiveRel, string tag)
            {
                if (!File.Exists(archiveAbs)) return;
                using (var rpf = new Rpf(archiveAbs))
                {
                    if (!rpf.IsOpenArchive) return;
                    foreach (var e in rpf.Entries)
                    {
                        string p = e.Path.ToLowerInvariant();
                        bool wanted = p.EndsWith(".meta") && p.Contains("/data/ai/") &&
                                      (Path.GetFileName(p).StartsWith("weapon") || Path.GetFileName(p).StartsWith("weapons"));
                        if (!wanted || e.Encrypted || e.IsResource) continue;
                        byte[] data = rpf.Read(e);
                        if (data == null || data.Length == 0) continue;
                        string text = Encoding.UTF8.GetString(data);
                        if (!text.Contains("CWeaponInfo") && !text.Contains("CWeaponComponent")) continue;

                        string key = tag + "/" + Path.GetFileName(e.Path);
                        int n = 2;
                        while (!used.Add(key)) key = tag + "/" + Path.GetFileNameWithoutExtension(e.Path) + "_" + n++ + ".meta";

                        string local = Path.Combine(vanillaDir, key.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(local));
                        string pristine = origin == null ? null : Path.Combine(origin, key.Replace('/', Path.DirectorySeparatorChar));
                        if (pristine != null && File.Exists(pristine)) File.Copy(pristine, local, true);
                        else if (originals != null) KeepOriginal(key, text, data, local);
                        else File.WriteAllBytes(local, data);
                        try
                        {
                            list.Add(new Source
                            {
                                ArchiveRel = archiveRel,
                                Inner = e.Path,
                                Key = key,
                                Doc = XDocument.Load(local, LoadOptions.PreserveWhitespace),
                            });
                        }
                        catch (Exception ex) { Console.WriteLine("  skipped " + e.Path + " (" + ex.Message + ")"); }
                    }
                }
            }

            Take(Path.Combine(gta, "mods", "update", "update.rpf"), @"update\update.rpf", "update");
            string dlcpacks = Path.Combine(gta, "mods", "update", "x64", "dlcpacks");
            if (Directory.Exists(dlcpacks))
                foreach (var dir in Directory.GetDirectories(dlcpacks))
                {
                    string name = Path.GetFileName(dir);
                    Take(Path.Combine(dir, "dlc.rpf"), @"update\x64\dlcpacks\" + name + @"\dlc.rpf", name);
                }
            return list;
        }

        /// <summary>
        /// The builder's memory of what was there first. A file carrying our marker is our own earlier
        /// output, so the build works from - and the revert goes back to - the original we kept for it.
        /// Anything else is the current original (stock, or another mod's edit), and is kept as such.
        /// </summary>
        static void KeepOriginal(string key, string text, byte[] data, string local)
        {
            string kept = Path.Combine(originals, key.Replace('/', Path.DirectorySeparatorChar));
            if (text.Contains(Marker))
            {
                if (File.Exists(kept)) { File.Copy(kept, local, true); if (lastResult != null) lastResult.OursReplaced++; }
                else
                {
                    // expanding our own output again changes nothing, but the revert can only go back this far
                    File.WriteAllBytes(local, data);
                    if (lastResult != null) lastResult.OursWithoutOriginal++;
                }
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(kept));
            File.WriteAllBytes(kept, data);
            File.WriteAllBytes(local, data);
            if (lastResult != null) lastResult.Originals++;
        }

        static IEnumerable<XElement> Weapons(XDocument doc) =>
            doc.Descendants("Item").Where(i => (string)i.Attribute("type") == "CWeaponInfo");

        static IEnumerable<XElement> AttachPoints(XElement weapon)
        {
            var aps = weapon.Element("AttachPoints");
            return aps == null ? Enumerable.Empty<XElement>() : aps.Elements("Item");
        }

        static IEnumerable<string> ComponentNames(XElement attachPoint)
        {
            var list = attachPoint.Element("Components");
            if (list == null) yield break;
            foreach (var i in list.Elements("Item"))
            {
                string n = (string)i.Element("Name");
                if (!string.IsNullOrWhiteSpace(n)) yield return n;
            }
        }

        // ---- rules -----------------------------------------------------------------------------

        /// <summary>Whether it makes any sense to bolt an optic onto this thing.</summary>
        static bool MountWorthy(string group, string weapon)
        {
            if (group == "GROUP_HEAVY" || group == "GROUP_STUNGUN") return false;   // rockets, miniguns, tasers
            string n = weapon.ToUpperInvariant();
            foreach (string no in new[]
            {
                "ROCKET", "AIRSTRIKE", "VEHICLE", "PASSENGER", "MINIGUN", "RAILGUN", "STINGER", "LAUNCHER",
                "FIREWORK", "FLAREGUN", "MUSKET", "SAWNOFF", "DBSHOTGUN", "RAYPISTOL", "RAYCARBINE", "RAYMINIGUN",
            })
                if (n.Contains(no)) return false;
            return true;
        }

        /// <summary>Groups the mounts into families so parts learned from one weapon apply to others.</summary>
        static string Family(string bone)
        {
            if (string.IsNullOrEmpty(bone)) return null;
            if (bone.StartsWith("WAPScop", StringComparison.OrdinalIgnoreCase)) return "optic";
            if (bone.StartsWith("WAPSupp", StringComparison.OrdinalIgnoreCase)) return "muzzle";
            if (bone.StartsWith("WAPFlsh", StringComparison.OrdinalIgnoreCase)) return "light";
            if (bone.StartsWith("WAPGrip", StringComparison.OrdinalIgnoreCase)) return "grip";
            return null; // clips, camos, finishes and Mk II kits stay weapon-specific
        }

        /// <summary>True when the component name reads like a part of that family (and not a weapon-specific one).</summary>
        static bool Looks(string family, string comp)
        {
            string n = Bare(comp);
            if (n.Contains("_CLIP") || n.Contains("CAMO") || n.Contains("VARMOD") || n.Contains("UPGRADE") || n.Contains("_SLIDE")) return false;
            switch (family)
            {
                case "optic": return !n.Contains("RAILCOVER") && (n.Contains("SCOPE") || n.Contains("SIGHT") || n.Contains("RAIL") || n.Contains("HOLO"));
                case "muzzle": return n.Contains("SUPP") || n.Contains("MUZZLE") || n.Contains("COMP") || n.Contains("BARREL");
                case "light": return n.Contains("FLSH") || n.Contains("FLASH") || n.Contains("LASR") || n.Contains("RAILCOVER");
                case "grip": return n.Contains("GRIP");
                default: return false;
            }
        }

        /// <summary>A part modelled for a handgun: a w_at_pi_* model, or a COMPONENT_AT_PI_* name when the model is sealed away.</summary>
        static bool PistolMade(string comp, Dictionary<string, Comp> comps)
        {
            comps.TryGetValue(comp, out var c);
            return (c?.Model ?? "").ToLowerInvariant().Contains("_pi_") || Bare(comp).Contains("_PI_");
        }

        /// <summary>The name without its "COMPONENT_" prefix - which otherwise makes every part contain "COMP".</summary>
        static string Bare(string comp)
        {
            string n = (comp ?? "").ToUpperInvariant();
            return n.StartsWith("COMPONENT_") ? n.Substring("COMPONENT_".Length) : n;
        }

        static bool Suitable(string family, string comp, Dictionary<string, Comp> comps, string group)
        {
            if (!Looks(family, comp)) return false;
            string n = Bare(comp);
            comps.TryGetValue(comp, out var c);
            string model = (c?.Model ?? "").ToLowerInvariant();
            string kind = c?.Kind ?? "";
            if (kind.Contains("ClipInfo") || kind.Contains("VariantModel")) return false;   // never share a magazine or a finish
            // Only the game's universal COMPONENT_AT_* parts get shared around. Anything named after a
            // weapon (COMPONENT_CERAMICPISTOL_SUPP, COMPONENT_MILITARYRIFLE_SIGHT_01) was modelled for
            // that gun alone, so it stays where it is.
            if (!n.StartsWith("AT_")) return false;

            bool pistolWeapon = group == "GROUP_PISTOL" || group == "GROUP_STUNGUN";
            bool pistolPart = model.Contains("_pi_") || n.Contains("_PI_");

            switch (family)
            {
                case "optic":
                    return pistolWeapon
                        ? pistolPart                   // slide sights only - rifle glass floats on a pistol
                        : !pistolPart;
                case "muzzle":
                    if (n.Contains("BARREL")) return false;                                // heavy barrels are Mk II frames
                    return pistolWeapon
                        ? pistolPart || n.Contains("MUZZLE") || n.Contains("COMP")          // no rifle cans on pistols
                        : !pistolPart;
                default:
                    return true;                                                           // lights and grips fit any rail
            }
        }

        // ---- packaging -------------------------------------------------------------------------

        static void BuildOiv(string path, List<Source> changed, string patchedDir, bool revert, string extra)
        {
            // the package is named after its file, so the + Pistol Optics build says so in OpenIV too
            string title = Path.GetFileNameWithoutExtension(path);
            if (title.EndsWith(" - REVERT", StringComparison.OrdinalIgnoreCase)) title = title.Substring(0, title.Length - " - REVERT".Length);
            title = System.Security.SecurityElement.Escape(title);
            if (File.Exists(path)) File.Delete(path);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                var content = new StringBuilder();
                foreach (var byArchive in changed.GroupBy(s => s.ArchiveRel))
                {
                    content.AppendLine("    <archive path=\"" + byArchive.Key + "\" createIfNotExist=\"False\" type=\"RPF7\">");
                    foreach (var s in byArchive)
                        content.AppendLine("      <add source=\"" + s.Key.Replace('/', '\\') + "\">" + s.Inner.Replace('/', '\\') + "</add>");
                    content.AppendLine("    </archive>");
                }

                string name = revert ? title + " (REVERT)" : title;
                string blurb = revert
                    ? "Puts your weapon files back exactly as they were before the unlock package. Install to the same place you installed the unlock."
                    : @"Lets every firearm take the game's full set of optics, muzzle devices, lights and grips instead of the two or three each gun normally allows.
" + (extra == null ? "" : "\n" + extra + "\n") + @"
Built from YOUR current weapon files, so any edits from your other weapon mods are kept. Install to the mods folder.

Magazines, camos and finishes stay weapon-specific, since those models only fit their own gun. Handguns only get handgun sights - rifle optics float above a pistol slide.";
                string assembly =
@"<?xml version=""1.0"" encoding=""UTF-8""?>
<package version=""2.0"" id=""{8E6E8A1C-3D2B-4F3A-9C6E-7A1B2C3D4E5" + (revert ? "0" : "F") + @"}"" target=""Five"">
  <metadata>
    <name>" + name + @"</name>
    <version>
      <major>1</major>
      <minor>1</minor>
    </version>
    <author>
      <displayName>Rely</displayName>
    </author>
    <description><![CDATA[" + blurb + @"]]></description>
  </metadata>
  <colors>
    <headerBackground useBlackTextColor=""False"">$FF09101A</headerBackground>
    <iconBackground>$FF113A54</iconBackground>
  </colors>
  <content>
" + content + @"  </content>
</package>
";
                AddText(zip, "assembly.xml", assembly);
                // The packages OpenIV ships with carry folder entries too, so match that.
                var dirs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in changed)
                {
                    var parts = ("content/" + s.Key).Split('/');
                    for (int i = 1; i < parts.Length; i++) dirs.Add(string.Join("/", parts.Take(i)) + "/");
                }
                foreach (var d in dirs) zip.CreateEntry(d);
                foreach (var s in changed)
                {
                    string local = Path.Combine(patchedDir, s.Key.Replace('/', Path.DirectorySeparatorChar));
                    zip.CreateEntryFromFile(local, "content/" + s.Key, CompressionLevel.Optimal);
                }
            }
        }

        static void AddText(ZipArchive zip, string name, string text)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (var w = new StreamWriter(entry.Open(), new UTF8Encoding(false))) w.Write(text);
        }
    }
}
