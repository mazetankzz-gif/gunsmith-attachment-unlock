// Run: BuilderTests.exe <GunsmithUnlockBuilder.exe> <the work/unlock/vanilla/update folder> <scratch folder>
// Writes a fake game folder with an RPF7 archive of the untouched weapon files and drives the
// double-click builder through a fresh install, a re-run over its own package, another mod's edit,
// an unsigned older unlock and a sealed archive.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

// End-to-end check of the double-click builder against a fake game folder whose mods archive we write ourselves.
class Program
{
    static int fails;
    static void Ok(string what, bool ok, string detail = null)
    {
        if (!ok) fails++;
        Console.WriteLine("  [" + (ok ? "ok" : "FAIL") + "] " + what + (ok || detail == null ? "" : "  (" + detail + ")"));
    }

    static int Main(string[] args)
    {
        string builderExe = args[0], pristineDir = args[1], root = args[2];
        if (Directory.Exists(root)) Directory.Delete(root, true);
        string gta = Path.Combine(root, "Grand Theft Auto V");
        Directory.CreateDirectory(Path.Combine(gta, "mods", "update"));
        File.WriteAllBytes(Path.Combine(gta, "GTA5.exe"), new byte[0]);
        string builderDir = Path.Combine(gta, "Unlock Builder");
        Directory.CreateDirectory(builderDir);
        File.Copy(builderExe, Path.Combine(builderDir, Path.GetFileName(builderExe)));
        File.Copy(builderExe + ".config", Path.Combine(builderDir, Path.GetFileName(builderExe) + ".config"));

        // every weapon file the real game has (update.rpf's own in common\data\ai, the DLC packs' under
        // dlc_patch\<pack>), so the parts pools come out the size they do on a real install
        var stock = new Dictionary<string, byte[]>();
        foreach (string f in Directory.GetFiles(pristineDir, "*.meta"))
            stock["common/data/ai/" + Path.GetFileName(f)] = File.ReadAllBytes(f);
        foreach (string dir in Directory.GetDirectories(Path.GetDirectoryName(pristineDir)))
        {
            string tag = Path.GetFileName(dir);
            if (tag == "update") continue;
            foreach (string f in Directory.GetFiles(dir, "*.meta"))
                stock["dlc_patch/" + tag + "/common/data/ai/" + Path.GetFileName(f)] = File.ReadAllBytes(f);
        }
        string rpf = Path.Combine(gta, "mods", "update", "update.rpf");
        string exeName = Path.GetFileName(builderExe);

        Console.WriteLine("-- 1. a fresh install");
        WriteRpf(rpf, stock);
        string out1 = Run(builderDir, exeName);
        string plain = Path.Combine(builderDir, "Gunsmith Attachment Unlock.oiv");
        string optics = Path.Combine(builderDir, "Gunsmith Attachment Unlock + Pistol Optics.oiv");
        Ok("found the game next to it", out1.Contains("Game folder:  " + gta), First(out1, "Game folder"));
        Ok("built the plain package", File.Exists(plain) && Content(plain).Count > 0);
        Ok("built the pistol optics package", File.Exists(optics) && Content(optics).Count > 0);
        Ok("wrote both reverts", File.Exists(plain.Replace(".oiv", " - REVERT.oiv")) && File.Exists(optics.Replace(".oiv", " - REVERT.oiv")));
        var first = Content(optics);
        Ok("patched files are signed", first.Count > 0 && first.Values.All(t => t.Contains("Gunsmith Attachment Unlock - rewritten")));
        string keptPath = Path.Combine(builderDir, "Originals", "update", "weapons.meta");
        Ok("kept the originals", File.Exists(keptPath) && File.ReadAllBytes(keptPath).SequenceEqual(stock["common/data/ai/weapons.meta"]));
        var revert1 = Content(optics.Replace(".oiv", " - REVERT.oiv"));
        string stockText = Encoding.UTF8.GetString(stock["common/data/ai/weapons.meta"]).TrimStart((char)0xFEFF);
        Ok("the revert is the originals", revert1.TryGetValue("content/update/weapons.meta", out var r1) && r1.TrimStart((char)0xFEFF) == stockText);
        Ok("says what to do next", out1.Contains("Package Installer"));
        Ok("doesn't warn about an old unlock", !out1.Contains("already look unlocked"), First(out1, "NOTE"));
        Ok("writes a build report", File.Exists(Path.Combine(builderDir, "Build report.txt")));

        Console.WriteLine("-- 2. run again with the pistol optics package installed");
        var installed = new Dictionary<string, byte[]>(stock);
        ReadManifest(optics);
        foreach (var kv in first) installed[InnerFor(kv.Key, stock)] = Encoding.UTF8.GetBytes(kv.Value);
        WriteRpf(rpf, installed);
        string out2 = Run(builderDir, exeName);
        var second = Content(optics);
        Ok("builds the same package again", second.Count == first.Count && second.All(kv => first.TryGetValue(kv.Key, out var v) && v == kv.Value),
            second.Count + " vs " + first.Count);
        var revert2 = Content(optics.Replace(".oiv", " - REVERT.oiv"));
        Ok("and the revert still goes back to stock", revert2.TryGetValue("content/update/weapons.meta", out var r2) && r2 == r1);
        Ok("no false alarm about an old unlock", !out2.Contains("already look unlocked"), First(out2, "NOTE"));

        Console.WriteLine("-- 3. another weapon mod replaces weapons.meta afterwards");
        string modded = stockText.Replace("<CWeaponInfoBlob>", "<CWeaponInfoBlob>\r\n<!-- some other weapon mod -->");
        installed["common/data/ai/weapons.meta"] = Encoding.UTF8.GetBytes(modded);
        WriteRpf(rpf, installed);
        Run(builderDir, exeName);
        var third = Content(optics);
        Ok("the new mod's edit is kept in the package", third.TryGetValue("content/update/weapons.meta", out var t3) && t3.Contains("some other weapon mod"));
        Ok("and becomes the new original", File.ReadAllText(keptPath).Contains("some other weapon mod"));

        Console.WriteLine("-- 4. an old unlock that didn't sign its files");
        installed["common/data/ai/weapons.meta"] = Encoding.UTF8.GetBytes(first["content/update/weapons.meta"].Replace("Gunsmith Attachment Unlock - rewritten", "something else"));
        WriteRpf(rpf, installed);
        string out4 = Run(builderDir, exeName);
        Ok("warns the files already look unlocked", out4.Contains("already look unlocked"), First(out4, "NOTE"));
        if (!out4.Contains("already look unlocked")) Console.WriteLine(out4);

        Console.WriteLine("-- 5. nothing readable");
        File.WriteAllBytes(rpf, new byte[] { 0x37, 0x46, 0x50, 0x52, 0, 0, 0, 0, 0, 0, 0, 0, 0x4E, 0x47, 0x0E, 0x0F });
        string out5 = Run(builderDir, exeName);
        Ok("explains a sealed archive", out5.Contains("Couldn't read any weapon files"), First(out5, "Couldn"));

        Console.WriteLine();
        Console.WriteLine(fails == 0 ? "ALL GOOD" : fails + " FAILED");
        return fails == 0 ? 0 : 1;
    }

    static Dictionary<string, string> manifest;

    /// <summary>Where a package file goes back into the archive, from the package's own assembly.xml.</summary>
    static string InnerFor(string packaged, Dictionary<string, byte[]> stock)
    {
        if (manifest == null) throw new InvalidOperationException("read the manifest first");
        return manifest[packaged];
    }

    static void ReadManifest(string oiv)
    {
        manifest = new Dictionary<string, string>();
        using (var z = ZipFile.OpenRead(oiv))
        using (var r = new StreamReader(z.GetEntry("assembly.xml").Open()))
        {
            var doc = System.Xml.Linq.XDocument.Parse(r.ReadToEnd());
            foreach (var add in doc.Descendants("add"))
                manifest["content/" + ((string)add.Attribute("source")).Replace('\\', '/')] = add.Value.Replace('\\', '/');
        }
    }

    static string First(string text, string needle) => text.Split('\n').FirstOrDefault(l => l.Contains(needle))?.Trim() ?? "(none)";

    static string Run(string dir, string exe)
    {
        var psi = new ProcessStartInfo(Path.Combine(dir, exe))
        {
            WorkingDirectory = dir, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using (var p = Process.Start(psi))
        {
            p.StandardInput.Close();
            string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(120000);
            return o;
        }
    }

    /// <summary>The files inside an .oiv's content folder, as text.</summary>
    static Dictionary<string, string> Content(string oiv)
    {
        var d = new Dictionary<string, string>();
        if (!File.Exists(oiv)) return d;
        using (var z = ZipFile.OpenRead(oiv))
            foreach (var e in z.Entries.Where(x => x.FullName.StartsWith("content/") && !x.FullName.EndsWith("/")))
                using (var r = new StreamReader(e.Open(), Encoding.UTF8)) d[e.FullName] = r.ReadToEnd();
        return d;
    }

    // ---- a minimal RPF7 writer: OPEN encryption, stored (uncompressed) files ----

    sealed class Node
    {
        public string Name;
        public SortedDictionary<string, Node> Dirs = new SortedDictionary<string, Node>(StringComparer.Ordinal);
        public SortedDictionary<string, byte[]> Files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
    }

    sealed class FileEntry { public string Name; public byte[] Data; }

    static void WriteRpf(string path, Dictionary<string, byte[]> files)
    {
        var root = new Node { Name = "" };
        foreach (var kv in files)
        {
            var parts = kv.Key.Split('/');
            var n = root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (!n.Dirs.TryGetValue(parts[i], out var c)) n.Dirs[parts[i]] = c = new Node { Name = parts[i] };
                n = c;
            }
            n.Files[parts[parts.Length - 1]] = kv.Value;
        }

        // breadth first, so every directory's children sit together after it
        var order = new List<object> { root };
        var firstChild = new Dictionary<Node, int>();
        for (int i = 0; i < order.Count; i++)
        {
            if (!(order[i] is Node dir)) continue;
            firstChild[dir] = order.Count;
            foreach (var d in dir.Dirs.Values) order.Add(d);
            foreach (var f in dir.Files) order.Add(new FileEntry { Name = f.Key, Data = f.Value });
        }

        var names = new MemoryStream();
        var nameAt = new Dictionary<object, int>();
        foreach (var e in order)
        {
            string nm = e is Node nd ? nd.Name : ((FileEntry)e).Name;
            nameAt[e] = (int)names.Length;
            var b = Encoding.ASCII.GetBytes(nm);
            names.Write(b, 0, b.Length);
            names.WriteByte(0);
        }
        while (names.Length % 16 != 0) names.WriteByte(0);

        long tocEnd = 16 + order.Count * 16 + names.Length;
        long block = (tocEnd + 511) / 512;
        var data = new MemoryStream();
        var entries = new MemoryStream();
        var w = new BinaryWriter(entries);
        foreach (var e in order)
        {
            if (e is Node dir)
            {
                w.Write((uint)nameAt[e]);
                w.Write(0x7FFFFF00u);
                w.Write((uint)firstChild[dir]);
                w.Write((uint)(dir.Dirs.Count + dir.Files.Count));
            }
            else
            {
                var f = (FileEntry)e;
                long at = block + data.Length / 512;
                ulong q = (ulong)(nameAt[e] & 0xFFFF) | ((ulong)at << 40);   // compressed size 0 = stored
                w.Write(q);
                w.Write((uint)f.Data.Length);
                w.Write(0u);
                data.Write(f.Data, 0, f.Data.Length);
                while (data.Length % 512 != 0) data.WriteByte(0);
            }
        }

        using (var fs = File.Create(path))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write(0x52504637u);
            bw.Write((uint)order.Count);
            bw.Write((uint)names.Length);
            bw.Write(0x4E45504Fu);
            bw.Write(entries.ToArray());
            bw.Write(names.ToArray());
            while (fs.Length < block * 512) bw.Write((byte)0);
            bw.Write(data.ToArray());
        }
    }
}
