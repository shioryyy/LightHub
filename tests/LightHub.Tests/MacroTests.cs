using System.Text.Json;
using LightHub.Application;
using LightHub.Core;
using Xunit;

namespace LightHub.Tests;

public sealed class MacroTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lighthub-macro-tests-" + Guid.NewGuid().ToString("N"));
    private MacroLibrary Library => new(root);
    internal static LocalMacro CopyMacro() => new(1, Guid.NewGuid().ToString("N"), "Copy", "single", [new("down", 0xe0, 0), new("down", 6, 0), new("up", 6, 0), new("up", 0xe0, 0)]);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact]
    public void LibraryRoundTripsAndExportsWithoutChangingSources()
    {
        var macro = CopyMacro(); var saved = Library.Save(macro, null);
        macro.Events[0] = new("delay", 0, 100);
        Assert.Equal("down", saved.Macro.Events[0].Kind);
        var read = Assert.Single(Library.List().Entries); Assert.Equal(saved.Revision, read.Revision);
        string export = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".lhmacro");
        try
        {
            Library.Export(saved.Macro, export); var imported = Library.ImportCopy(export);
            Assert.NotEqual(saved.Macro.Id, imported.Macro.Id); Assert.Equal(saved.Macro.Events, imported.Macro.Events);
            Assert.Equal(2, Library.List().Entries.Count); Assert.Equal(saved.Revision, Library.List().Entries.Single(e => e.Key == saved.Key).Revision);
        }
        finally { if (File.Exists(export)) File.Delete(export); }
        Assert.All(MacroCapabilities.Current, c => { Assert.False(c.Available); Assert.Null(c.CapacityBytes); });
        Assert.False(Directory.Exists(Path.Combine(root, "backups")));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("event-field")]
    [InlineData("missing")]
    [InlineData("null-events")]
    [InlineData("null-event")]
    [InlineData("event-version")]
    public void ImportsRejectUnknownOrMalformedFilesWithoutOverwriting(string change)
    {
        Directory.CreateDirectory(root); string path = Path.Combine(root, "input.lhmacro");
        string json = JsonSerializer.Serialize(CopyMacro(), Json.Options);
        json = change switch
        {
            "version" => json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2"),
            "unknown" => json.Insert(1, "\"runtime\":true,"),
            "duplicate" => json.Insert(1, "\"schemaVersion\":1,"),
            "event-field" => json.Replace("\"kind\": \"down\"", "\"kind\": \"down\", \"hidden\": 1"),
            "missing" => json.Replace("\"execution\": \"single\",", ""),
            "null-events" => json[..json.IndexOf("\"events\"")] + "\"events\":null}",
            "null-event" => json[..json.IndexOf("\"events\"")] + "\"events\":[null]}",
            _ => json.Replace("\"kind\": \"down\"", "\"kind\": \"script\"")
        };
        File.WriteAllText(path, json);
        Assert.Throws<MacroLibraryException>(() => Library.ImportCopy(path));
        Assert.Equal(json, File.ReadAllText(path)); Assert.Empty(Library.List().Entries);
    }

    [Fact]
    public void InvalidFileIsIsolatedAndOversizedInputIsBounded()
    {
        Library.Save(CopyMacro(), null); string bad = Path.Combine(root, "macros", Guid.NewGuid().ToString("N") + ".lhmacro");
        File.WriteAllText(bad, "{\"schemaVersion\":\"future\",\"macro\":{},\"baseRevision\":null}");
        var listing = Library.List(); Assert.Single(listing.Entries); Assert.Single(listing.Errors);
        File.WriteAllBytes(bad, new byte[MacroLibrary.MaxFileBytes + 1]);
        Assert.Equal("FileSize", Assert.Single(Library.List().Errors).Code);
        Assert.Equal("FileSize", Assert.Throws<MacroLibraryException>(() => MacroLibrary.ReadImport(bad)).Code);
    }

    [Fact]
    public void OptimisticRevisionAndLeaseProtectOtherEdits()
    {
        var entry = Library.Save(CopyMacro(), null);
        var newer = Library.Save(entry.Macro with { Name = "Newer" }, entry.Revision);
        Assert.Equal("Conflict", Assert.Throws<MacroLibraryException>(() => Library.Save(entry.Macro, entry.Revision)).Code);
        Assert.Equal("Conflict", Assert.Throws<MacroLibraryException>(() => Library.Recycle(entry)).Code);
        using (Library.AcquireLease()) Assert.Equal("Busy", Assert.Throws<MacroLibraryException>(() => Library.Save(CopyMacro(), null)).Code);
        Assert.Equal(newer.Revision, Assert.Single(Library.List().Entries).Revision);
    }

    [Fact]
    public void DraftCanBeIncompleteButPromotionNeedsValidityAndMatchingRevision()
    {
        var entry = Library.Save(CopyMacro(), null);
        var draftMacro = entry.Macro with { Events = [new("down", 4, 0)] };
        var draft = Library.SaveDraft(draftMacro, entry.Revision, null);
        Assert.Single(MacroValidator.Check(draft.Macro)); Assert.Equal(2, Library.List().Entries.Count);
        Assert.Throws<MacroLibraryException>(() => Library.Save(draft.Macro, draft.BaseRevision, draft.Revision));
        Assert.Equal("DraftExists", Assert.Throws<MacroLibraryException>(() => Library.Recycle(entry)).Code);
        var revisedDraft = Library.SaveDraft(draftMacro with { Events = [new("down", 4, 0), new("up", 4, 0)] }, entry.Revision, draft.Revision);
        Assert.Equal("Conflict", Assert.Throws<MacroLibraryException>(() => Library.Save(revisedDraft.Macro, entry.Revision, draft.Revision)).Code);
        var promoted = Library.Save(revisedDraft.Macro, entry.Revision, revisedDraft.Revision);
        Assert.False(promoted.IsDraft); Assert.Single(Library.List().Entries); Assert.Single(Library.List(true).Entries);
    }

    [Fact]
    public void InterruptedAtomicSaveKeepsPreviousFileAndDraft()
    {
        var entry = Library.Save(CopyMacro(), null);
        var draft = Library.SaveDraft(entry.Macro with { Name = "Candidate" }, entry.Revision, null);
        var failing = new MacroLibrary(root, write: (_, _) => throw new IOException("Storage failure before replacement"));
        Assert.Throws<IOException>(() => failing.Save(draft.Macro, entry.Revision, draft.Revision));
        Assert.Equal(entry.Revision, Library.List().Entries.Single(e => !e.IsDraft).Revision);
        Assert.Equal(draft.Revision, Library.List().Entries.Single(e => e.IsDraft).Revision);
    }

    [Fact]
    public void ReferencedMacroCannotBeRecycledAndRecoveryNeverOverwrites()
    {
        var entry = Library.Save(CopyMacro(), null);
        var referenced = new MacroLibrary(root, new References());
        Assert.Equal("Referenced", Assert.Throws<MacroLibraryException>(() => referenced.Recycle(entry)).Code);
        Library.Recycle(entry); Assert.Empty(Library.List().Entries);
        var trash = Assert.Single(Library.List(true).Entries);
        Library.Save(entry.Macro with { Name = "Replacement" }, null);
        Assert.Equal("Conflict", Assert.Throws<MacroLibraryException>(() => Library.Recover(trash)).Code);
        Assert.Single(Library.List(true).Entries);
        Library.Recycle(Assert.Single(Library.List().Entries));
        var recovered = Library.Recover(trash); Assert.Equal("Copy", recovered.Macro.Name);
    }
    private sealed class References : IMacroReferences { public IReadOnlyList<string> Find(string id) => ["test-profile"]; }

    [Fact]
    public void UnknownPresetBlocksDeletionAndExportCannotBypassLibrary()
    {
        var entry = Library.Save(CopyMacro(), null);
        Directory.CreateDirectory(Path.Combine(root, "presets")); File.WriteAllText(Path.Combine(root, "presets", "future.lhpreset"), "{\"schemaVersion\":2}");
        Assert.Equal("ReferencesUnknown", Assert.Throws<MacroLibraryException>(() => Library.Recycle(entry)).Code);
        Assert.Equal("ManagedExport", Assert.Throws<MacroLibraryException>(() => Library.Export(entry.Macro, Path.Combine(root, "macros", entry.Key + ".lhmacro"))).Code);
        Assert.Equal("Format", Assert.Throws<MacroLibraryException>(() => Library.Save(entry.Macro with { Id = "../escape" }, null)).Code);
    }

    [Fact]
    public void SemanticBoundariesCoverBalanceDurationAndHeldKeys()
    {
        var macro = CopyMacro();
        Assert.Empty(MacroValidator.Check(macro with { Events = Enumerable.Repeat(new MacroEvent("delay", 0, 10000), 3).ToArray() }));
        Assert.Contains(MacroValidator.Check(macro with { Events = Enumerable.Repeat(new MacroEvent("delay", 0, 10000), 4).ToArray() }), e => e.Code == "Duration");
        Assert.Contains(MacroValidator.Check(macro with { Events = [new("down", 4, 0), new("down", 4, 0), new("up", 4, 0)] }), e => e.Code == "DuplicateDown");
        Assert.Contains(MacroValidator.Check(macro with { Events = [new("up", 4, 0)] }), e => e.Code == "UnmatchedUp");
        var held = Enumerable.Range(4, 7).Select(k => new MacroEvent("down", k, 0)).Concat(Enumerable.Range(4, 7).Select(k => new MacroEvent("up", k, 0))).ToArray();
        Assert.Contains(MacroValidator.Check(macro with { Events = held }), e => e.Code == "HeldLimit");
        Assert.Throws<MacroLibraryException>(() => MacroValidator.Validate(macro with { Events = [new("down", 0, 0)] }));
        Assert.Throws<MacroLibraryException>(() => MacroValidator.Validate(macro with { Events = Enumerable.Repeat(new MacroEvent("delay", 0, 1), 257).ToArray() }));
    }
}
