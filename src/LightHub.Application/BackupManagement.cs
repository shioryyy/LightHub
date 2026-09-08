using System.Text.Json;
using LightHub.Core;

namespace LightHub.Application;

public sealed record StoredBackup(string Name, string Path, DateTimeOffset Date, long Bytes, string? Model, string? UnitId, bool Protected, bool Readable)
{
    public string DeviceKey { get; init; } = "";
    public BackupMetadata Metadata { get; init; } = new("", false, false);
}
public sealed record BackupMetadata(string Label, bool Pinned, bool Milestone);
public sealed record BackupIndex(int SchemaVersion, Dictionary<string, BackupMetadata> Entries);
public sealed record TrashEntry(string Id, string OriginalName, DateTimeOffset Deleted, BackupMetadata Metadata)
{
    public override string ToString() => $"{Deleted.LocalDateTime:g}  {Metadata.Label}  {OriginalName}";
}

public sealed partial class TransactionStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Ticks, long Bytes, DeviceSnapshot? Snapshot)> backupCache = new();
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private string BackupDirectory => Path.GetFullPath(Path.Combine(Root, "backups"));

    // Hold through backup creation, journal creation and commit to close the deletion race.
    public IDisposable LockBackups()
    {
        try { return new FileStream(Path.Combine(Root, "backup-management.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new DeviceException(FailureKind.Conflict, "Another device write or backup management operation is in progress.", ex); }
    }

    private HashSet<string> RecoveryPaths() => Pending().SelectMany(r => new[] { r.BackupPath, r.BeforePath }).Select(Path.GetFullPath).ToHashSet(PathComparer);

    public IReadOnlyList<StoredBackup> ListBackups()
    {
        if (!Directory.Exists(BackupDirectory)) return [];
        CheckBackupDirectory();
        var protectedPaths = RecoveryPaths();
        var metadata = ReadIndex();
        return new DirectoryInfo(BackupDirectory).GetFiles("*.lhbackup").OrderByDescending(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal).Select(file =>
        {
            bool link = file.Attributes.HasFlag(FileAttributes.ReparsePoint);
            DeviceSnapshot? snapshot = null;
            if (!link)
            {
                try
                {
                    if (backupCache.TryGetValue(file.FullName, out var cached) && cached.Ticks == file.LastWriteTimeUtc.Ticks && cached.Bytes == file.Length) snapshot = cached.Snapshot;
                    else { snapshot = BackupFile.Load(file.FullName); if (backupCache.Count > 2048) backupCache.Clear(); backupCache[file.FullName] = (file.LastWriteTimeUtc.Ticks, file.Length, snapshot); }
                }
                catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or NullReferenceException or UnauthorizedAccessException) { }
            }
            var meta = metadata.Entries.GetValueOrDefault(file.Name) ?? new("", false, false);
            return new StoredBackup(file.Name, file.FullName, file.LastWriteTimeUtc, file.Length, snapshot?.Identity.Name, snapshot?.Identity.UnitId, link || protectedPaths.Contains(file.FullName) || meta.Pinned || meta.Milestone || snapshot is null, snapshot is not null)
            { Metadata = meta, DeviceKey = snapshot is null ? "" : snapshot.Identity.UnitId + ":" + string.Join(",", snapshot.Identity.ProductIds.Order()) };
        }).ToArray();
    }

    public IReadOnlyList<StoredBackup> CleanupCandidates(int keepPerDevice = 10)
    {
        if (keepPerDevice < 1) throw new ArgumentOutOfRangeException(nameof(keepPerDevice));
        return ListBackups().Where(b => b.Readable && b.UnitId is not null)
            .Where(b => !b.Metadata.Milestone).GroupBy(b => b.DeviceKey).SelectMany(group => group.Skip(keepPerDevice).Where(b => !b.Protected)).ToArray();
    }

    public void DeleteBackups(IEnumerable<string> paths)
    {
        using var lease = LockBackups();
        var files = paths.Select(ManagedBackupPath).Distinct(PathComparer).ToArray();
        var protectedPaths = ListBackups().Where(b => b.Protected).Select(b => b.Path).ToHashSet(PathComparer);
        // Validate the whole selection before deleting any of it, including both recovery references.
        if (files.Any(protectedPaths.Contains)) throw new InvalidOperationException("A selected backup is recovery-protected, pinned, a milestone, or unreadable.");
        var index = ReadIndex();
        foreach (string file in files)
        {
            string id = Guid.NewGuid().ToString("N"), name = Path.GetFileName(file);
            string directory = TrashDirectory(id);
            Directory.CreateDirectory(directory);
            var entry = new TrashEntry(id, name, DateTimeOffset.UtcNow, index.Entries.GetValueOrDefault(name) ?? new("", false, false));
            AtomicFile.Write(Path.Combine(directory, "entry.json"), JsonSerializer.Serialize(entry, Json.Options));
            File.Move(file, Path.Combine(directory, "backup.lhbackup"));
        }
    }

    public void ExportStoredBackup(string source, string destination)
    {
        using var lease = LockBackups();
        source = ManagedBackupPath(source);
        string fullDestination = Path.GetFullPath(destination), root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (fullDestination.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Export outside LightHub's managed storage directory.");
        // Export a validated snapshot, not an unchecked file or a live device read.
        BackupFile.Save(destination, BackupFile.Load(source));
    }

    private void CheckBackupDirectory()
    {
        if (File.GetAttributes(Root).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Linked storage roots are not supported for deletion.");
        if (File.GetAttributes(BackupDirectory).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Backup management does not follow linked directories.");
    }

    private BackupIndex ReadIndex()
    {
        string path = Path.Combine(Root, "backup-index.json");
        if (!File.Exists(path)) return new(1, []);
        if (new FileInfo(path).Length > 2_000_000 || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Backup index is invalid.");
        var index = JsonSerializer.Deserialize<BackupIndex>(File.ReadAllText(path), Json.Options);
        if (index is not { SchemaVersion: 1, Entries: not null }) throw new InvalidDataException("Unsupported backup index; preserve it and review before cleanup.");
        return index;
    }
    public void SetBackupMetadata(string path, BackupMetadata metadata)
    {
        using var lease = LockBackups(); path = ManagedBackupPath(path);
        if (metadata.Label.Length > 120) throw new InvalidDataException("Backup name is too long.");
        var index = ReadIndex(); index.Entries[Path.GetFileName(path)] = metadata;
        AtomicFile.Write(Path.Combine(Root, "backup-index.json"), JsonSerializer.Serialize(index, Json.Options));
    }
    private string TrashDirectory(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid recycle entry.");
        string trash = Path.Combine(Root, "trash"), directory = Path.Combine(trash, id);
        foreach (var p in new[] { Root, trash, directory })
            if (Directory.Exists(p) && File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Linked recycle paths are forbidden.");
        return directory;
    }
    public IReadOnlyList<TrashEntry> ListTrash()
    {
        string trash = Path.Combine(Root, "trash");
        if (!Directory.Exists(trash)) return [];
        var entries = new List<TrashEntry>();
        foreach (string path in Directory.GetDirectories(trash))
        {
            string dir = TrashDirectory(Path.GetFileName(path)), manifest = Path.Combine(dir, "entry.json"), backup = Path.Combine(dir, "backup.lhbackup");
            if (!File.Exists(backup)) continue;
            if (new FileInfo(manifest).Length > 8192 || File.GetAttributes(manifest).HasFlag(FileAttributes.ReparsePoint) || File.GetAttributes(backup).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Invalid recycle files.");
            var entry = JsonSerializer.Deserialize<TrashEntry>(File.ReadAllText(manifest), Json.Options) ?? throw new InvalidDataException("Invalid recycle entry.");
            if (entry.Id != Path.GetFileName(dir) || Path.GetFileName(entry.OriginalName) != entry.OriginalName || !entry.OriginalName.EndsWith(".lhbackup", StringComparison.Ordinal)) throw new InvalidDataException("Invalid recycle metadata.");
            entries.Add(entry);
        }
        return entries.OrderByDescending(e => e.Deleted).ToArray();
    }
    public void RecoverTrash(string id)
    {
        using var lease = LockBackups(); var entry = ListTrash().Single(e => e.Id == id);
        Directory.CreateDirectory(BackupDirectory); CheckBackupDirectory();
        string destination = Path.Combine(BackupDirectory, entry.OriginalName), source = Path.Combine(TrashDirectory(id), "backup.lhbackup");
        if (File.Exists(destination)) throw new IOException("An original backup already exists; nothing was overwritten.");
        _ = BackupFile.Load(source);
        var index = ReadIndex(); index.Entries[entry.OriginalName] = entry.Metadata;
        AtomicFile.Write(Path.Combine(Root, "backup-index.json"), JsonSerializer.Serialize(index, Json.Options));
        File.Move(source, destination);
    }
    public void PermanentlyDeleteTrash(string id)
    {
        using var lease = LockBackups(); var entry = ListTrash().Single(e => e.Id == id);
        var paths = RecoveryPaths(); string dir = TrashDirectory(id), file = Path.Combine(dir, "backup.lhbackup");
        if (paths.Contains(file) || paths.Contains(Path.Combine(BackupDirectory, entry.OriginalName)) || entry.Metadata.Pinned || entry.Metadata.Milestone)
            throw new InvalidOperationException("This backup is protected.");
        _ = BackupFile.Load(file);
        File.Delete(file); File.Delete(Path.Combine(dir, "entry.json")); Directory.Delete(dir);
    }

    private string ManagedBackupPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!PathComparer.Equals(Path.GetDirectoryName(full), BackupDirectory) || !string.Equals(Path.GetExtension(full), ".lhbackup", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only backups inside the managed backup directory can be modified.", nameof(path));
        CheckBackupDirectory();
        if (!File.Exists(full)) throw new FileNotFoundException("Backup no longer exists. Refresh the backup list.", full);
        if (File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Backup management does not follow linked files.");
        return full;
    }
}
