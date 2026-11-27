using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Multiplayer.Client.Util;

// FIXES row 512. Pure part of the settings check (System only, so Tests link this file): one line per settings store file,
// "<path relative to the save data folder, with />\t<sha256 hex>", sorted by path. Stores:
//   Config/Mod_*.xml                          (Mod classes with ModSettings; the game's own settings files)
//   HugsLib/ModSettings.xml                   (HugsLib handles)
//   Config/ModSettingsFramework*.xml          (MSF: one file for all its mods)
//   OwnFileStores                             (mods that keep settings in their own file; checker A lists them as unverified, so only MSF so far)
// Files of mods Multiplayer ignores (SyncConfigs.ignoredConfigsModIds) are skipped: ignoredFolders holds those mods' FOLDER names,
// because Mod_<folder>_<Class>.xml is named by the mod folder (LoadedModManager.GetSettingsFilename).
public static class SettingsStoreHash
{
    public const string ErrorHash = "ERROR";
    public static readonly string[] OwnFileStores = [];   // extra relative paths (stage 2: fill from checker A once the files are verified)

    public static bool IsIgnored(string fileName, ICollection<string> ignoredFolders)
    {
        if (ignoredFolders == null || !fileName.StartsWith("Mod_", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var f in ignoredFolders)
            if (!string.IsNullOrEmpty(f) && fileName.StartsWith("Mod_" + f + "_", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // Relative store paths that exist under root, sorted ordinal.
    public static List<string> ListStoreFiles(string root, ICollection<string> ignoredFolders)
    {
        var list = new List<string>();
        var config = Path.Combine(root, "Config");
        if (Directory.Exists(config))
        {
            foreach (var path in Directory.GetFiles(config, "*.xml", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(path);
                if (name.StartsWith("Mod_", StringComparison.OrdinalIgnoreCase) && !IsIgnored(name, ignoredFolders))
                    list.Add("Config/" + name);
                else if (name.StartsWith("ModSettingsFramework", StringComparison.OrdinalIgnoreCase))
                    list.Add("Config/" + name);
            }
        }

        var hugs = Path.Combine(root, "HugsLib", "ModSettings.xml");
        if (File.Exists(hugs)) list.Add("HugsLib/ModSettings.xml");

        foreach (var rel in OwnFileStores)
            if (File.Exists(Path.Combine(root, rel))) list.Add(rel);

        return list.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    public static string[] ComputeLines(string root, ICollection<string> ignoredFolders)
    {
        var lines = new List<string>();
        foreach (var rel in ListStoreFiles(root, ignoredFolders))
            lines.Add(rel + "\t" + HashFile(Path.Combine(root, rel)));
        return lines.ToArray();
    }

    public static string HashFile(string path)
    {
        try
        {
            using var sha = SHA256.Create();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }
        catch (Exception)
        {
            return ErrorHash;
        }
    }

    // Paths whose hash differs, or that only one list has (a missing file), sorted.
    public static List<string> Diff(IEnumerable<string> mine, IEnumerable<string> theirs)
    {
        var a = ToMap(mine);
        var b = ToMap(theirs);
        var differ = new List<string>();
        foreach (var kv in a)
            if (!b.TryGetValue(kv.Key, out var other) || other != kv.Value) differ.Add(kv.Key);
        foreach (var kv in b)
            if (!a.ContainsKey(kv.Key)) differ.Add(kv.Key);
        return differ.OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    static Dictionary<string, string> ToMap(IEnumerable<string> lines)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (lines == null) return map;
        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line)) continue;
            var tab = line.IndexOf('\t');
            if (tab < 0) map[line] = ""; else map[line.Substring(0, tab)] = line.Substring(tab + 1);
        }
        return map;
    }

    public static string Pack(string[] lines) => string.Join("\n", lines);
    public static string[] Unpack(string payload) =>
        string.IsNullOrEmpty(payload) ? [] : payload.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    // "Config/Mod_*.xml", "HugsLib", "MSF", "own" for the report
    public static string Kind(string rel) =>
        rel.StartsWith("HugsLib/", StringComparison.Ordinal) ? "HugsLib"
        : rel.StartsWith("Config/Mod_", StringComparison.OrdinalIgnoreCase) ? "Mod_*.xml"
        : rel.StartsWith("Config/ModSettingsFramework", StringComparison.OrdinalIgnoreCase) ? "MSF"
        : "own";
}
