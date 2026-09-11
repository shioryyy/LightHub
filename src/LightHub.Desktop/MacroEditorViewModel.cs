using System.Collections.ObjectModel;
using System.ComponentModel;
using LightHub.Application;

namespace LightHub.Desktop;

public sealed record MacroKey(int Usage, string Label)
{
    public override string ToString() => Label;
    public static IReadOnlyList<MacroKey> All { get; } = Make();
    private static IReadOnlyList<MacroKey> Make()
    {
        var names = "Enter,Esc,Backspace,Tab,Space,-,=,[,],\\,Non-US #,;,',`,Comma,Period,/,Caps Lock".Split(',');
        var navigation = "Print Screen,Scroll Lock,Pause,Insert,Home,Page Up,Delete,End,Page Down,Right,Left,Down,Up,Num Lock,KP /,KP *,KP -,KP +,KP Enter,KP 1,KP 2,KP 3,KP 4,KP 5,KP 6,KP 7,KP 8,KP 9,KP 0,KP .,Non-US \\,Application,Power,KP =".Split(',');
        string[] modifiers = ["Left Ctrl", "Left Shift", "Left Alt", "Left Super", "Right Ctrl", "Right Shift", "Right Alt", "Right Super"];
        return Enumerable.Range(4, 0x73 - 3).Concat(Enumerable.Range(0xe0, 8)).Select(u => new MacroKey(u,
            u <= 29 ? ((char)('A' + u - 4)).ToString() : u <= 39 ? (u == 39 ? "0" : (u - 29).ToString()) :
            u <= 57 ? names[u - 40] : u <= 69 ? "F" + (u - 57) : u <= 103 ? navigation[u - 70] :
            u <= 115 ? "F" + (u - 91) : modifiers[u - 0xe0])).ToArray();
    }
}

public sealed class MacroStep : Observable
{
    private Strings l;
    private int index, kind;
    private MacroKey key;
    private decimal? delay;
    public MacroStep(MacroEvent e, Strings l)
    {
        this.l = l; kind = e.Kind == "down" ? 0 : e.Kind == "up" ? 1 : 2;
        key = MacroKey.All.First(k => k.Usage == (e.Usage == 0 ? 4 : e.Usage)); delay = e.DelayMs == 0 ? 100 : e.DelayMs;
    }
    public int Number { get => index; set { Set(ref index, value); Changed(nameof(Label)); } }
    public int Kind { get => kind; set { if (value is < 0 or > 2 || !Set(ref kind, value)) return; Changed(nameof(IsDelay)); Changed(nameof(IsKey)); Changed(nameof(Label)); } }
    public MacroKey Key { get => key; set { if (value is not null && Set(ref key, value)) Changed(nameof(Label)); } }
    public decimal? Delay { get => delay; set { if (Set(ref delay, value)) Changed(nameof(Label)); } }
    public bool IsDelay => Kind == 2;
    public bool IsKey => !IsDelay;
    public string Label => $"{Number:00}   {l[Kind == 0 ? "MacroDown" : Kind == 1 ? "MacroUp" : "MacroDelay"]}   {(IsDelay ? Delay + " ms" : Key.Label)}";
    public void SetLanguage(Strings language) { l = language; Changed(nameof(Label)); }
    public MacroEvent Event() => !IsDelay ? new(Kind == 0 ? "down" : "up", Key.Usage, 0) :
        Delay is >= 1 and <= 10000 && decimal.Truncate(Delay.Value) == Delay.Value ? new("delay", 0, (int)Delay.Value) : throw new MacroLibraryException("DelayInvalid");
}

public sealed record MacroListItem(MacroEntry Entry, Strings L)
{
    public string Label => (string.IsNullOrWhiteSpace(Entry.Macro.Name) ? L["MacroUntitled"] : Entry.Macro.Name) + (Entry.IsDraft ? " · " + L["MacroDraft"] : "");
    public override string ToString() => Label;
}

public sealed class MacroEditorViewModel : Observable
{
    private readonly MacroLibrary library;
    private bool loading, busy, dirty, hasEditor, isDraft;
    private string name = "", id = "", validation = "", statusKey = "MacroReady", detail = "";
    private string? baseRevision, draftRevision;
    private MacroListItem? selected;
    private MacroStep? step;
    public Strings L { get; private set; }
    public ObservableCollection<MacroListItem> Items { get; } = [];
    public ObservableCollection<MacroListItem> Trash { get; } = [];
    public ObservableCollection<MacroStep> Steps { get; } = [];
    public IReadOnlyList<MacroKey> Keys => MacroKey.All;
    public string[] Kinds => [L["MacroDown"], L["MacroUp"], L["MacroDelay"]];
    public MacroListItem? Selected { get => selected; private set => Set(ref selected, value); }
    public MacroStep? SelectedStep { get => step; set { Set(ref step, value); Notify(); } }
    public string Name { get => name; set { if (Set(ref name, value)) Edited(); } }
    public bool Dirty => dirty;
    public bool Busy => busy;
    public bool Idle => !busy;
    public bool HasEditor => hasEditor;
    public bool Editable => Idle && HasEditor;
    public bool CanAdd => Editable && Steps.Count < MacroValidator.MaxEvents;
    public bool CanChangeStep => Editable && SelectedStep is not null;
    public bool CanMoveUp => CanChangeStep && Steps.IndexOf(SelectedStep!) > 0;
    public bool CanMoveDown => CanChangeStep && Steps.IndexOf(SelectedStep!) < Steps.Count - 1;
    public bool CanCopyStep => CanAdd && SelectedStep is { } s && (!s.IsDelay || s.Delay is >= 1 and <= 10000 && decimal.Truncate(s.Delay.Value) == s.Delay);
    public bool Valid => validation.Length == 0 && HasEditor;
    public bool CanSave => Editable && Valid && (Dirty || isDraft);
    public bool CanSaveDraft => Editable && Dirty && ShapeIsValid();
    public bool CanDuplicate => Editable && Valid;
    public bool CanExport => Editable && Valid && !Dirty && !isDraft;
    public bool CanRecycle => Idle && Selected is not null && !Dirty;
    public bool CanBind => false;
    public string Validation => validation;
    public string Status => L[statusKey];
    public string ErrorDetails { get => detail; private set => Set(ref detail, value); }
    public string FilesWarning { get; private set; } = "";
    public string EditorState => L[!HasEditor ? "MacroEmptyLibrary" : Dirty ? "MacroUnsaved" : isDraft ? "MacroDraftSaved" : "MacroLocalSaved"];
    public string Summary => string.Format(L["MacroSummary"], Steps.Count, Steps.Where(s => s.IsDelay).Sum(s => s.Delay ?? 0));
    public string CapabilityText => string.Join("\n", MacroCapabilities.Current.Select(c => L["Macro" + c.Reason]));
    public MacroEditorViewModel(MacroLibrary library, Strings l) { this.library = library; L = l; }

    public void SetLanguage(Strings language)
    {
        L = language; foreach (var s in Steps) s.SetLanguage(L);
        var selectedEntry = Selected?.Entry;
        var items = Items.Select(i => i.Entry).ToArray(); Items.Clear(); foreach (var entry in items) Items.Add(new(entry, L));
        Selected = Items.FirstOrDefault(i => i.Entry == selectedEntry);
        var trash = Trash.Select(i => i.Entry).ToArray(); Trash.Clear(); foreach (var entry in trash) Trash.Add(new(entry, L));
        Changed(nameof(L)); Changed(nameof(Kinds)); Changed(nameof(Status)); Changed(nameof(CapabilityText)); Validate(); Notify();
    }
    public async Task<bool> RefreshAsync() => await Run(async () =>
    {
        await RefreshLists();
        if (!dirty && Selected is { } item) Load(item.Entry, item);
        else if (!dirty && hasEditor) Clear();
    });
    private async Task RefreshLists()
    {
        var result = await Task.Run(() => (Live: library.List(), Trash: library.List(true)));
        Items.Clear(); foreach (var e in result.Live.Entries) Items.Add(new(e, L));
        Trash.Clear(); foreach (var e in result.Trash.Entries) Trash.Add(new(e, L));
        FilesWarning = string.Join("\n", result.Live.Errors.Concat(result.Trash.Errors).Select(e => e.FileName + ": " + L["Macro" + e.Code]));
        Changed(nameof(FilesWarning));
        if (Selected is { } selectedItem) Selected = Items.FirstOrDefault(i => i.Entry.Key == selectedItem.Entry.Key && i.Entry.IsDraft == selectedItem.Entry.IsDraft);
    }
    public void New()
    {
        RequireClean(); Load(new("", new(1, Guid.NewGuid().ToString("N"), L["MacroUntitled"], "single", []), "", true, null), null);
        dirty = true; Notify();
    }
    public void Open(MacroListItem item) { RequireClean(); Load(item.Entry, item); }
    private void RequireClean() { if (Busy || Dirty) throw new InvalidOperationException("Resolve the macro draft first."); }
    private void Load(MacroEntry entry, MacroListItem? item)
    {
        loading = true;
        try
        {
            Selected = item; id = entry.Macro.Id; Name = entry.Macro.Name; baseRevision = entry.IsDraft ? entry.BaseRevision : entry.Revision;
            draftRevision = entry.IsDraft && entry.Revision.Length > 0 ? entry.Revision : null; isDraft = entry.IsDraft; hasEditor = true;
            foreach (var s in Steps) s.PropertyChanged -= StepChanged; Steps.Clear();
            foreach (var e in entry.Macro.Events) AddRow(e);
            SelectedStep = Steps.FirstOrDefault(); dirty = false; statusKey = "MacroReady"; ErrorDetails = "";
        }
        finally { loading = false; }
        Validate(); Notify(); Changed(nameof(Status));
    }
    private void AddRow(MacroEvent e)
    {
        var row = new MacroStep(e, L); row.PropertyChanged += StepChanged; Steps.Add(row); Renumber();
    }
    private void StepChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName is nameof(MacroStep.Kind) or nameof(MacroStep.Key) or nameof(MacroStep.Delay)) Edited(); }
    private void Renumber() { for (int i = 0; i < Steps.Count; i++) Steps[i].Number = i + 1; }
    public void Add(string kind)
    {
        if (!CanAdd) return;
        AddRow(kind == "delay" ? new(kind, 0, 100) : new(kind, SelectedStep?.Key.Usage ?? 4, 0));
        SelectedStep = Steps[^1]; Edited();
    }
    public void CopyStep()
    {
        if (!CanCopyStep) return;
        var source = SelectedStep!; int position = Steps.IndexOf(source) + 1;
        var row = new MacroStep(source.Event(), L); row.PropertyChanged += StepChanged; Steps.Insert(position, row); Renumber(); SelectedStep = row; Edited();
    }
    public void RemoveStep()
    {
        if (!CanChangeStep) return;
        int position = Steps.IndexOf(SelectedStep!); SelectedStep!.PropertyChanged -= StepChanged; Steps.RemoveAt(position);
        SelectedStep = Steps.ElementAtOrDefault(Math.Min(position, Steps.Count - 1)); Renumber(); Edited();
    }
    public void MoveStep(int delta)
    {
        if (!CanChangeStep) return;
        int source = Steps.IndexOf(SelectedStep!), destination = source + delta;
        if (destination < 0 || destination >= Steps.Count) return; Steps.Move(source, destination); Renumber(); Edited();
    }
    public LocalMacro Snapshot() => new(1, id, Name, "single", Steps.Select(s => s.Event()).ToArray());
    private bool ShapeIsValid() { try { MacroValidator.ValidateShape(Snapshot()); return true; } catch (MacroLibraryException) { return false; } }
    private void Edited() { if (loading) return; dirty = true; statusKey = "MacroReady"; Changed(nameof(Status)); Validate(); Notify(); }
    private void Validate()
    {
        try { validation = !HasEditor ? "" : string.Join("\n", MacroValidator.Check(Snapshot()).Select(i => (i.Step is { } n ? string.Format(L["MacroStepNumber"], n) + " · " : "") + L["Macro" + i.Code])); }
        catch (MacroLibraryException ex) { validation = L["Macro" + ex.Code]; }
        Changed(nameof(Validation));
    }
    public async Task<bool> SaveAsync(bool draft = false)
    {
        if (draft ? !CanSaveDraft : !CanSave) return false;
        var macro = Snapshot(); var baseline = baseRevision; var previousDraft = draftRevision;
        return await Run(async () =>
        {
            var entry = await Task.Run(() => draft ? library.SaveDraft(macro, baseline, previousDraft) : library.Save(macro, baseline, previousDraft));
            await RefreshLists(); Load(entry, Items.First(i => i.Entry.Key == entry.Key && i.Entry.IsDraft == entry.IsDraft));
        });
    }
    public async Task<bool> DuplicateAsync()
    {
        if (!CanDuplicate) return false; var macro = Snapshot();
        return await Run(async () => { var entry = await Task.Run(() => library.Duplicate(macro)); await RefreshLists(); Load(entry, Items.Single(i => i.Entry.Key == entry.Key && !i.Entry.IsDraft)); });
    }
    public async Task<bool> ImportAsync(string path)
    {
        RequireClean(); return await Run(async () => { var entry = await Task.Run(() => library.ImportCopy(path)); await RefreshLists(); Load(entry, Items.Single(i => i.Entry.Key == entry.Key && !i.Entry.IsDraft)); });
    }
    public async Task<bool> ExportAsync(string path)
    {
        if (!CanExport) return false; var macro = Snapshot(); return await Run(() => Task.Run(() => library.Export(macro, path)), "MacroExported");
    }
    public async Task<bool> RecycleAsync()
    {
        if (!CanRecycle) return false; var entry = Selected!.Entry;
        return await Run(async () => { await Task.Run(() => library.Recycle(entry)); Clear(); await RefreshLists(); }, "MacroRecycled");
    }
    public async Task<bool> RecoverAsync(MacroListItem item) => await Run(async () => { await Task.Run(() => library.Recover(item.Entry)); await RefreshLists(); }, "MacroRecovered");
    public void DiscardEdits()
    {
        if (Busy) return;
        if (Selected is { } original) Load(original.Entry, original); else Clear();
    }
    private void Clear()
    {
        foreach (var row in Steps) row.PropertyChanged -= StepChanged;
        loading = true; Steps.Clear(); Selected = null; SelectedStep = null; Name = ""; hasEditor = dirty = isDraft = false; loading = false; Validate(); Notify();
    }
    private async Task<bool> Run(Func<Task> action, string success = "MacroReady")
    {
        if (Busy) return false; busy = true; ErrorDetails = ""; Notify();
        try { await action(); statusKey = success; return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            statusKey = ex is MacroLibraryException m ? "Macro" + m.Code : "MacroStorageError";
            ErrorDetails = ex.Message; return false;
        }
        finally { busy = false; Changed(nameof(Status)); Notify(); }
    }
    private void Notify()
    {
        foreach (string property in new[] { nameof(Dirty), nameof(Busy), nameof(Idle), nameof(HasEditor), nameof(Editable), nameof(CanAdd), nameof(CanChangeStep), nameof(CanMoveUp), nameof(CanMoveDown), nameof(CanCopyStep), nameof(Valid), nameof(CanSave), nameof(CanSaveDraft), nameof(CanDuplicate), nameof(CanExport), nameof(CanRecycle), nameof(EditorState), nameof(Summary) }) Changed(property);
    }
}
