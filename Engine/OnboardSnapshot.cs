namespace MuxSwarm.Engine;

/// <summary>
/// Backup taken when <c>/onboard</c> starts, before the onboarding agent may edit anything: the config files
/// (Config.json, Swarm.json) and the memory files (BRAIN.md, MEMORY.md) are copied into a timestamped folder under
/// <c>Configs/.onboard-backup-*</c>. <c>/onboard undo</c> puts the newest backup back.
/// </summary>
internal static class OnboardSnapshot
{
    internal const string Prefix = ".onboard-backup-";

    /// <summary>Files covered: the two config files and the two memory files. Missing ones are skipped.</summary>
    internal static IReadOnlyList<string> Files(string configPath, string swarmPath, string contextDir) =>
        [configPath, swarmPath, Path.Combine(contextDir, "BRAIN.md"), Path.Combine(contextDir, "MEMORY.md")];

    /// <summary>
    /// Copy the files that exist into <c>&lt;backupRoot&gt;/.onboard-backup-&lt;utc timestamp&gt;</c> and return that folder,
    /// or null when none exist. A manifest maps each copy back to its original path.
    /// </summary>
    internal static string? Take(IEnumerable<string> files, string backupRoot, DateTime utcNow)
    {
        var present = files.Where(File.Exists).ToList();
        if (present.Count == 0) return null;
        var dir = Path.Combine(backupRoot, Prefix + utcNow.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(dir);
        var manifest = new List<string>();
        for (int i = 0; i < present.Count; i++)
        {
            var copyName = $"{i}-{Path.GetFileName(present[i])}";
            File.Copy(present[i], Path.Combine(dir, copyName), overwrite: false);
            manifest.Add(copyName + "\t" + Path.GetFullPath(present[i]));
        }
        File.WriteAllLines(Path.Combine(dir, "manifest.tsv"), manifest);
        return dir;
    }

    /// <summary>The newest backup folder under <paramref name="backupRoot"/>, or null.</summary>
    internal static string? Latest(string backupRoot) =>
        Directory.Exists(backupRoot)
            ? Directory.GetDirectories(backupRoot, Prefix + "*").OrderByDescending(Path.GetFileName, StringComparer.Ordinal).FirstOrDefault()
            : null;

    /// <summary>
    /// Restore every file listed in <paramref name="backupDir"/>'s manifest to its original path and return the
    /// restored paths. Files created after the backup are left alone.
    /// </summary>
    internal static IReadOnlyList<string> Restore(string backupDir)
    {
        var restored = new List<string>();
        foreach (var line in File.ReadAllLines(Path.Combine(backupDir, "manifest.tsv")))
        {
            var tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            var source = Path.Combine(backupDir, line[..tab]);
            var target = line[(tab + 1)..];
            if (!File.Exists(source)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
            File.SetLastWriteTimeUtc(target, DateTime.UtcNow);   // a copy keeps the old mtime; mark it changed
            restored.Add(target);
        }
        return restored;
    }
}
