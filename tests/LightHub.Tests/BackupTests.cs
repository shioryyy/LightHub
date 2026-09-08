using LightHub.Core;
using LightHub.Application;
using LightHub.Desktop;
using Xunit;

namespace LightHub.Tests;

public sealed class BackupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lighthub-backup-tests-" + Guid.NewGuid().ToString("N"));
    private TransactionStore Store => new(root);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private string Save(string unit = "1234ABCD", int age = 0)
    {
        var snapshot = DemoData.Create(); snapshot = snapshot with { Identity = snapshot.Identity with { UnitId = unit } };
        var path = Store.SaveBackup(snapshot); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-age)); return path;
    }
    private void Protect(string backup, string before, string status = "pending") => Store.Record(new(Guid.NewGuid().ToString("N"), "1234ABCD", backup, before, status, DateTimeOffset.UtcNow, [5], null));

    [Fact]
    public void ListsEveryBackupBeyondTheOldHundredItemLimit()
    {
        for (int i = 0; i < 105; i++) Save(age: i);
        var items = Store.ListBackups(); Assert.Equal(105, items.Count);
        Assert.All(items, b => { Assert.True(b.Readable); Assert.True(b.Bytes > 0); Assert.Equal("G PRO Wireless", b.Model); });
        Assert.True(items.First().Date > items.Last().Date);
    }
    [Fact]
    public void DeletionIsAllOrNothingWhenARecoveryReferenceIsSelected()
    {
        var safe = Save(); var backup = Save(); var before = Save(); Protect(backup, before, "failed");
        Assert.Equal(2, Store.ListBackups().Count(b => b.Protected));
        Assert.Throws<InvalidOperationException>(() => Store.DeleteBackups([safe, backup]));
        Assert.Throws<InvalidOperationException>(() => Store.DeleteBackups([safe, before]));
        Assert.True(File.Exists(safe)); Assert.True(File.Exists(backup)); Assert.True(File.Exists(before));
        Store.Resolve("1234ABCD"); Store.DeleteBackups([backup, before]); Assert.Single(Store.ListBackups());
    }
    [Fact]
    public void CleanupKeepsTenPerDeviceAndProtectedOrUnreadableFiles()
    {
        for (int i = 0; i < 12; i++) Save(age: i);
        for (int i = 0; i < 11; i++) Save("9876ABCD", i);
        var protectedFile = Store.ListBackups().Last(b => b.UnitId == "1234ABCD").Path; Protect(protectedFile, protectedFile);
        var invalid = Save(); File.WriteAllText(invalid, "invalid");
        var old = Store.CleanupCandidates(); Assert.Equal(2, old.Count);
        Assert.DoesNotContain(old, b => b.Path == protectedFile || b.Path == invalid);
        Store.DeleteBackups(old.Select(b => b.Path));
        Assert.Equal(22, Store.ListBackups().Count); Assert.True(File.Exists(protectedFile)); Assert.True(File.Exists(invalid));
    }
    [Fact]
    public void DeletionRechecksProtectionAfterTheCleanupPreview()
    {
        for (int i = 0; i < 11; i++) Save(age: i);
        var old = Assert.Single(Store.CleanupCandidates()); Protect(old.Path, old.Path);
        Assert.Throws<InvalidOperationException>(() => Store.DeleteBackups([old.Path])); Assert.True(File.Exists(old.Path));
    }
    [Fact]
    public void RefusesExternalPathsAndInvalidJournalsBeforeDeleting()
    {
        var safe = Save(); var outside = Path.Combine(root, "outside.lhbackup"); BackupFile.Save(outside, DemoData.Create());
        Assert.Throws<ArgumentException>(() => Store.DeleteBackups([safe, outside])); Assert.True(File.Exists(safe)); Assert.True(File.Exists(outside));
        Protect(safe, safe, "unexpected-status");
        Assert.Throws<InvalidDataException>(() => Store.DeleteBackups([safe])); Assert.True(File.Exists(safe));
    }
    [Fact]
    public void CleanupCannotRunDuringADeviceTransaction()
    {
        var safe = Save(); using var lease = Store.LockBackups();
        Assert.Equal(FailureKind.Conflict, Assert.Throws<DeviceException>(() => Store.DeleteBackups([safe])).Kind);
        Assert.True(File.Exists(safe));
    }
    [Fact]
    public void ExportPreservesSnapshotAndCannotOverwriteManagedStorage()
    {
        var safe = Save(); var protectedFile = Save(); Protect(protectedFile, protectedFile);
        Assert.Throws<InvalidOperationException>(() => Store.ExportStoredBackup(safe, protectedFile));
        var exported = Path.Combine(Path.GetTempPath(), "lighthub-export-" + Guid.NewGuid().ToString("N") + ".lhbackup");
        try { Store.ExportStoredBackup(safe, exported); Assert.True(BackupFile.Load(exported).SameState(BackupFile.Load(safe))); }
        finally { File.Delete(exported); }
    }
    [Fact]
    public void MalformedBackupStaysVisibleButIsExcludedFromAutomaticCandidates()
    {
        var broken = Save(); File.WriteAllText(broken, "{broken");
        Assert.False(Assert.Single(Store.ListBackups()).Readable); Assert.Empty(Store.CleanupCandidates(1));
        Assert.Throws<InvalidOperationException>(() => Store.DeleteBackups([broken])); Assert.Single(Store.ListBackups());
    }
}
