using System.Security.Cryptography;
using System.Text.Json;
using LightHub.Core;

namespace LightHub.Application;

public sealed record MacroDraft(int SchemaVersion, LocalMacro Macro, string? BaseRevision);
public sealed record MacroEntry(string Key, LocalMacro Macro, string Revision, bool IsDraft, string? BaseRevision);
public sealed record MacroFileError(string FileName, string Code);
public sealed record MacroListing(IReadOnlyList<MacroEntry> Entries, IReadOnlyList<MacroFileError> Errors);

public interface IMacroReferences
{
    // Called inside the library lease. Future reference writers must acquire the same lease.
    IReadOnlyList<string> Find(string macroId);
}

public sealed class MacroLibrary
{
    public const int MaxFileBytes = 2 * 1024 * 1024;
    private readonly string root;
    private readonly IMacroReferences? references;
    private readonly Action<string, string> write;
    public MacroLibrary(string root, IMacroReferences? references = null, Action<string, string>? write = null)
    {
        // System-owned parent aliases (notably /var -> /private/var on macOS)
        // are legitimate. Canonicalize the chosen parent, but keep rejecting a
        // redirected library root or any managed child path.
        this.root = CanonicalParent(root);
        this.references = references;
        this.write = write ?? AtomicFile.Write;
    }

    public IDisposable AcquireLease()
    {
        CheckPath(root); Directory.CreateDirectory(root);
        string path = Path.Combine(root, "macro-library.lock"); CheckPath(path);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new MacroLibraryException("Busy", ex); }
    }

    public MacroListing List(bool trash = false)
    {
        using var lease = AcquireLease();
        var entries = new List<MacroEntry>(); var errors = new List<MacroFileError>();
        foreach (string area in trash ? new[] { "macro-trash" } : new[] { "macros", "macro-drafts" })
        {
            string directory = Area(area);
            if (!Directory.Exists(directory)) continue;
            foreach (string path in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
            {
                if (Path.GetExtension(path) is not (".lhmacro" or ".lhdraft")) continue;
                try
                {
                    var entry = ReadEntry(path);
                    if (!trash && (entry.Key != Key(entry.Macro.Id) || entry.IsDraft != (area == "macro-drafts")))
                        throw new MacroLibraryException("Format");
                    entries.Add(entry);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                { errors.Add(new(Path.GetFileName(path), ex is MacroLibraryException m ? m.Code : "Unreadable")); }
            }
        }
        return new(entries.OrderBy(e => e.Macro.Name, StringComparer.OrdinalIgnoreCase).ToArray(), errors);
    }

    public MacroEntry Save(LocalMacro macro, string? expectedRevision, string? draftRevision = null)
    {
        MacroValidator.Validate(macro); macro = macro.Copy();
        using var lease = AcquireLease();
        string path = LivePath(macro.Id, false), draftPath = LivePath(macro.Id, true);
        Expect(path, expectedRevision); Expect(draftPath, draftRevision);
        write(path, JsonSerializer.Serialize(macro, Json.Options));
        // A completed save is durable before its now-obsolete draft is archived.
        if (draftRevision is not null)
        {
            try { MoveToTrash(draftPath); }
            catch (IOException ex) { throw new MacroLibraryException("SavedCleanup", ex); }
        }
        return ReadEntry(path);
    }

    public MacroEntry SaveDraft(LocalMacro macro, string? baseRevision, string? expectedDraftRevision)
    {
        MacroValidator.ValidateShape(macro); macro = macro.Copy(); ValidateRevision(baseRevision);
        using var lease = AcquireLease();
        string path = LivePath(macro.Id, true);
        Expect(path, expectedDraftRevision);
        write(path, JsonSerializer.Serialize(new MacroDraft(1, macro, baseRevision), Json.Options));
        return ReadEntry(path);
    }

    public static LocalMacro ReadImport(string path)
    {
        var entry = ReadEntry(path, allowDraft: false, requireKey: false);
        return entry.Macro.Copy();
    }

    public MacroEntry ImportCopy(string path) => Duplicate(ReadImport(path));
    public MacroEntry Duplicate(LocalMacro macro) { MacroValidator.Validate(macro); return Save(macro with { Id = Guid.NewGuid().ToString("N"), Events = (MacroEvent[])macro.Events.Clone() }, null); }

    public void Export(LocalMacro macro, string destination)
    {
        MacroValidator.Validate(macro); macro = macro.Copy();
        destination = CanonicalParent(destination); CheckPath(destination);
        string relative = Path.GetRelativePath(root, destination);
        // Exports may not bypass conflict/reference checks by targeting managed data.
        if (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            throw new MacroLibraryException("ManagedExport");
        if (File.Exists(destination)) _ = ReadImport(destination); // preserve unknown newer formats
        write(destination, JsonSerializer.Serialize(macro, Json.Options));
    }

    public void Recycle(MacroEntry entry)
    {
        using var lease = AcquireLease();
        string path = LivePath(entry.Macro.Id, entry.IsDraft); Expect(path, entry.Revision);
        if (!entry.IsDraft)
        {
            if (File.Exists(LivePath(entry.Macro.Id, true))) throw new MacroLibraryException("DraftExists");
            if (references is not null)
            {
                if (references.Find(entry.Macro.Id).Count != 0) throw new MacroLibraryException("Referenced");
            }
            else
            {
                // Current v1 presets cannot reference macros. Fail closed on a future format.
                string presets = Area("presets");
                if (Directory.Exists(presets))
                    foreach (string preset in Directory.EnumerateFiles(presets, "*.lhpreset"))
                    {
                        CheckPath(preset);
                        try { _ = LocalAssets.Read(preset); }
                        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException) { throw new MacroLibraryException("ReferencesUnknown", ex); }
                    }
            }
        }
        MoveToTrash(path);
    }

    public MacroEntry Recover(MacroEntry entry)
    {
        using var lease = AcquireLease();
        string source = Path.Combine(Area("macro-trash"), Key(entry.Key) + (entry.IsDraft ? ".lhdraft" : ".lhmacro"));
        Expect(source, entry.Revision);
        var saved = ReadEntry(source);
        string destination = LivePath(saved.Macro.Id, saved.IsDraft);
        Expect(destination, null); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(source, destination);
        return ReadEntry(destination);
    }

    private void MoveToTrash(string path)
    {
        string directory = Area("macro-trash"); Directory.CreateDirectory(directory);
        File.Move(path, Path.Combine(directory, Guid.NewGuid().ToString("N") + Path.GetExtension(path)));
    }

    private string Area(string name) { string path = Path.Combine(root, name); CheckPath(path); return path; }
    private string LivePath(string id, bool draft)
    {
        string path = Path.Combine(Area(draft ? "macro-drafts" : "macros"), Key(id) + (draft ? ".lhdraft" : ".lhmacro"));
        CheckPath(path); return path;
    }
    private static string Key(string id) => Guid.TryParseExact(id, "N", out var guid) ? guid.ToString("N") : throw new MacroLibraryException("Format");
    private static void ValidateRevision(string? revision)
    {
        if (revision is not null && (revision.Length != 64 || revision.Any(c => !char.IsAsciiHexDigit(c)))) throw new MacroLibraryException("Format");
    }
    private static string CanonicalParent(string path)
    {
        string full = Path.GetFullPath(path);
        string? parent = Path.GetDirectoryName(full);
        return parent is null ? full : Path.Combine(CanonicalDirectory(parent), Path.GetFileName(full));
    }
    private static string CanonicalDirectory(string path)
    {
        string candidate = CanonicalParent(path);
        var directory = new DirectoryInfo(candidate);
        if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return directory.ResolveLinkTarget(true)?.FullName ?? throw new MacroLibraryException("LinkedPath");
        return candidate;
    }
    private static void CheckPath(string path)
    {
        for (string? p = Path.GetFullPath(path); p is not null; p = Path.GetDirectoryName(p))
        {
            try
            {
                if (File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint)) throw new MacroLibraryException("LinkedPath");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    private static void Expect(string path, string? revision)
    {
        CheckPath(path);
        if (File.Exists(path))
        {
            if (revision is null || ReadEntry(path).Revision != revision) throw new MacroLibraryException("Conflict");
        }
        else if (revision is not null) throw new MacroLibraryException("Conflict");
    }

    private static MacroEntry ReadEntry(string path, bool allowDraft = true, bool requireKey = true)
    {
        path = CanonicalParent(path);
        CheckPath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxFileBytes) throw new MacroLibraryException("FileSize");
        // Bounded even if another process grows a file after the length check.
        byte[] bytes = new byte[MaxFileBytes + 1]; int size = 0, count;
        while (size < bytes.Length && (count = stream.Read(bytes, size, bytes.Length - size)) > 0) size += count;
        if (size > MaxFileBytes) throw new MacroLibraryException("FileSize");
        using var document = JsonDocument.Parse(bytes.AsMemory(0, size), new() { MaxDepth = 32 });
        JsonElement payload = document.RootElement;
        bool draft = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("macro", out _);
        string? baseRevision = null;
        if (draft)
        {
            if (!allowDraft) throw new MacroLibraryException("Format");
            Fields(payload, "schemaVersion", "macro", "baseRevision");
            var version = payload.GetProperty("schemaVersion");
            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int schema) || schema != 1) throw new MacroLibraryException("Format");
            var rev = payload.GetProperty("baseRevision");
            if (rev.ValueKind is not (JsonValueKind.Null or JsonValueKind.String)) throw new MacroLibraryException("Format");
            baseRevision = rev.GetString(); ValidateRevision(baseRevision);
            payload = payload.GetProperty("macro");
        }
        Fields(payload, "schemaVersion", "id", "name", "execution", "events");
        var events = payload.GetProperty("events");
        if (events.ValueKind != JsonValueKind.Array || events.GetArrayLength() > MacroValidator.MaxEvents) throw new MacroLibraryException("Format");
        foreach (var e in events.EnumerateArray()) Fields(e, "kind", "usage", "delayMs");
        var macro = payload.Deserialize<LocalMacro>(Json.Options) ?? throw new MacroLibraryException("Format");
        MacroValidator.ValidateShape(macro); if (!draft) MacroValidator.Validate(macro);
        if (requireKey && Path.GetExtension(path) != (draft ? ".lhdraft" : ".lhmacro")) throw new MacroLibraryException("Format");
        string key = requireKey ? Key(Path.GetFileNameWithoutExtension(path)) : Key(macro.Id);
        return new(key, macro, Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, size))), draft, baseRevision);
    }
    private static void Fields(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new MacroLibraryException("Format");
        var names = element.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Length != expected.Length || names.Distinct(StringComparer.Ordinal).Count() != names.Length || names.Except(expected).Any())
            throw new MacroLibraryException("Format");
    }
}
