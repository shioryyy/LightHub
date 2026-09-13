using System.Collections.ObjectModel;
using System.Text.Json;
using System.Diagnostics;
using Avalonia.Threading;
using LightHub.Core;
using LightHub.Application;
using LightHub.Hid;

namespace LightHub.Desktop;

public sealed class Workspace : Observable
{
    public Strings L { get; private set; } = new();
    public bool Demo { get; }
    public TransactionStore Store { get; }
    public LocalAssets Assets { get; }
    public MacroEditorViewModel Macros { get; }
    public ButtonAssignmentViewModel ButtonAssignment { get; }
    private DeviceSession? session;
    private bool refreshingRuntime;
    private bool runtimeStale;
    private DateTimeOffset nextBatteryRead;
    private Telemetry? lastTelemetry;
    public ObservableCollection<LocalPreset> Presets { get; } = [];
    public LocalPreset? SelectedPreset { get; set; }
    public string PresetName { get; set; } = "";
    public int ReplacementStage { get; set; } = -1;
    public bool NeedsReplacementStage => Snapshot is { } s && s.ActiveSector == Sector && s.DpiIndex < Stages.Count && !Stages[s.DpiIndex].Enabled;
    private readonly DispatcherTimer draftTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer previewTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    public bool Previewing => session?.Previewing == true;
    public bool CanEndPreview => !busy && Previewing;
    private IReadOnlyDictionary<DeviceOperation, SupportDecision> operations = new Dictionary<DeviceOperation, SupportDecision>();
    public bool SelectedSlotDisabled => Snapshot?.Directory().FirstOrDefault(e => e.Sector == Sector)?.Enabled == false;
    public bool CanActivate => !busy && !Demo && !dirty && !connectionChanged && !RecoveryRequired && !Previewing &&
        operations.GetValueOrDefault(DeviceOperation.Activate)?.CanWrite == true && (!SelectedSlotDisabled || operations.GetValueOrDefault(DeviceOperation.EnableProfile)?.CanWrite == true);
    public string ActivationReason => operations.GetValueOrDefault(SelectedSlotDisabled ? DeviceOperation.EnableProfile : DeviceOperation.Activate)?.Reason ?? "Read device capabilities first.";
    public string ActivateLabel => L[SelectedSlotDisabled ? "EnableAndActivate" : "Activate"];
    public string ActivationSummary()
    {
        if (Snapshot is null || DpiCaps is null) throw new InvalidOperationException(L["Read"]);
        var plan = SlotActivation.Prepare(Snapshot, Sector, DpiCaps, Rates, SelectedSlotDisabled);
        return string.Format(L["ActivationDetails"], plan.Slot, plan.TargetDpi, L[plan.EnablesSlot ? "EnableAndActivate" : "Activate"]);
    }
    public bool RecoveryRequired { get; private set; }
    public ObservableCollection<DeviceEndpoint> Devices { get; } = [];
    public ObservableCollection<ProfileItem> Profiles { get; } = [];
    public ObservableCollection<DpiStage> Stages { get; } = [];
    public ObservableCollection<ButtonEditor> Buttons { get; } = [];
    public ObservableCollection<BackupItem> Backups { get; } = [];
    public IReadOnlyList<BackupItem> SelectedBackups { get; private set; } = [];
    private string backupSummary = "";
    public string BackupSummary { get => backupSummary; private set => Set(ref backupSummary, value); }
    public bool CanDeleteBackups => !busy && !Demo && SelectedBackups.Count > 0 && SelectedBackups.All(b => !b.Backup.Protected);
    public bool CanExportBackup => !busy && !Demo && SelectedBackups.Count == 1 && SelectedBackups[0].Backup.Readable;
    public bool CanRestoreSelectedBackup => CanExportBackup && CanRead;
    public bool CanManageBackups => !busy && !Demo;
    private static readonly DeviceCatalog Definitions = DeviceCatalog.Load();
    public IReadOnlyList<DeviceRule> Catalog => Definitions.Rules;
    private DeviceRule? DisplayRule => Snapshot is null ? null : Definitions.FindRule(Snapshot.Identity);
    public string CompatibilityText => L.Chinese ? "HID++ 游戏鼠标 · Windows / Linux / macOS\n设备可被识别不等于已验证可写。可写状态由型号、内存格式和平台的验证记录共同决定。" : "HID++ gaming mice · Windows / Linux / macOS\nDiscovery is not a write-support claim. Write access requires matching model, memory layout and platform evidence.";
    private string recoveryText = "";
    public string RecoveryText => recoveryText;
    public DeviceEndpoint? Endpoint { get; set; }
    public DeviceSnapshot? Snapshot { get; private set; }
    public SupportDecision? Support { get; private set; }
    public DpiCapabilities? DpiCaps { get; private set; }
    public int[] Rates { get; private set; } = [125, 250, 500, 1000];
    private decimal? currentDpi;
    public decimal? CurrentDpi { get => currentDpi; set { if (Set(ref currentDpi, value) && Previewing) { previewTimer.Stop(); previewTimer.Start(); } } }
    public decimal MinimumDpi => DpiCaps?.Minimum ?? 100;
    public decimal MaximumDpi => DpiCaps?.Maximum ?? 25600;
    public decimal DpiStep => DpiCaps?.Step is > 0 ? DpiCaps.Step : 50;
    public bool CanSetCurrentDpi => !busy && !Demo && !connectionChanged && !RecoveryRequired && operations.GetValueOrDefault(DeviceOperation.RuntimeDpi)?.CanWrite == true;
    public int Sector { get; private set; }
    public string ButtonProfileLabel => Profiles.FirstOrDefault(p => p.Sector == Sector)?.Label ?? L["Buttons"];
    public bool HasButtonMap => Snapshot is { } s && DisplayRule is { ButtonMap: "gpw-top" } r && r.Controls.Length == s.Layout.ButtonCount;
    private ButtonEditor? selectedButton;
    public ButtonEditor? SelectedButton
    {
        get => selectedButton;
        set
        {
            if (!Set(ref selectedButton, value)) return;
            ButtonAssignment.Button = value;
            foreach (var button in Buttons) button.Highlighted = button == value;
        }
    }
    public string[] StageNames => Enumerable.Range(1, 5).Select(i => L["Stage"] + " " + i).ToArray();
    public string[] ShiftNames => new[] { L["None"] }.Concat(StageNames).ToArray();
    private bool busy, dirty, activate, loading, writing, profileReadable, connectionChanged;
    private int defaultIndex, shiftSelection, rate;
    private string status = "", details = "", telemetry = "", diagnostics = "", summary = "";
    private CancellationTokenSource? cancellation;
    public bool Busy => busy;
    public bool Idle => !busy;
    public bool Dirty => dirty;
    public bool HasSnapshot => Snapshot is not null;
    public bool CanRead => !busy && Endpoint is not null && !Demo;
    public bool CanEdit => !busy && HasSnapshot && profileReadable;
    public bool CanWrite => CanEdit && Support?.CanWrite == true && !Demo && !connectionChanged && !runtimeStale && !RecoveryRequired && !Previewing && Snapshot?.Mode == 1;
    public bool CanUndo => !busy && dirty;
    public bool CanSaveToSlot => CanWrite && dirty;
    public int EditingSlot => Snapshot?.Directory().FirstOrDefault(e => e.Sector == Sector)?.Slot ?? 0;
    public string SaveTargetLabel => EditingSlot > 0 ? string.Format(L["SaveToSlot"], EditingSlot) : L["Apply"];
    public string EditingTargetText => EditingSlot > 0 ? string.Format(L["EditingSlot"], EditingSlot) : L["Select"];
    public string ActiveSlotText => Snapshot is { } s ? string.Format(L["ActiveSlotReading"], s.Directory().Single(e => e.Sector == s.ActiveSector).Slot) : "";
    public bool CanCancel => busy && !writing;
    public string DirtyText => dirty ? L["Dirty"] : Demo ? L["Demo"] : "";
    public string DeviceName => Snapshot?.Identity.Name ?? L["Select"];
    public string DeviceDetails { get => details; private set => Set(ref details, value); }
    public string TelemetryText { get => telemetry; private set => Set(ref telemetry, value); }
    public string DiagnosticText { get => diagnostics; private set => Set(ref diagnostics, value); }
    public string DiscoverySummary { get => summary; private set => Set(ref summary, value); }
    public string SupportText => Demo ? L["Demo"] : Support is null ? "" : (Support.CanWrite ? L["Writable"] : L["ReadOnly"]) + " · " + Support.Reason;
    public string SupportBadge => Demo ? L["Demo"] : Support is null ? "" : L[Support.CanWrite ? "BasicProfileWritable" : "ReadOnly"];
    public string Status { get => status; set => Set(ref status, value); }
    public bool Activate { get => activate; set { if (Set(ref activate, value)) MarkDirty(); } }
    public int DefaultIndex { get => defaultIndex; set { if (Set(ref defaultIndex, value)) MarkDirty(); } }
    public int ShiftSelection { get => shiftSelection; set { if (Set(ref shiftSelection, value)) MarkDirty(); } }
    public int Rate { get => rate; set { if (Set(ref rate, value)) MarkDirty(); } }
    public Workspace(bool demo, TransactionStore? store = null)
    {
        Demo = demo; Store = store ?? new(demo ? Path.Combine(Path.GetTempPath(), "LightHub-demo", Guid.NewGuid().ToString("N")) : null); Assets = new(Store.Root); Status = L["Ready"]; PresetName = L["NewPreset"];
        Macros = new(new MacroLibrary(Store.Root), L);
        ButtonAssignment = new(L) { MacroDescribe = DescribeOnboardMacro };
        draftTimer.Tick += async (_, _) =>
        {
            draftTimer.Stop(); if (!dirty || Demo) return;
            try { var draft = CreatePreset(); await Task.Run(() => Assets.SaveDraft(draft)); }
            catch (Exception ex) { Store.Log(ex); Status = L["DraftNotSaved"]; }
        };
        previewTimer.Tick += async (_, _) =>
        {
            if (busy) return;
            previewTimer.Stop(); if (!Previewing || !CanSetCurrentDpi) return;
            try { await Run(L["SetCurrentDpi"], _ => ApplyCurrentDpi(), true); }
            catch (Exception ex) { Store.Log(ex); }
        };
    }
    // Read-only: describes what a macro-pointer binding refers to. No byte is ever
    // rewritten; unsupported or damaged content is reported verbatim per its state.
    private string? DescribeOnboardMacro(byte[] binding)
    {
        if (Snapshot is null || !OnboardMacroFormat.IsMacroPointer(binding)) return null;
        ushort sector = OnboardMacroFormat.PointerSector(binding);
        var macro = OnboardMacroFormat.Decode(Snapshot, OnboardMacroFormat.MacroSectorIds(Snapshot), sector, OnboardMacroFormat.PointerOffset(binding));
        string state = macro.State switch
        {
            "complete" => string.Join(" → ", macro.Steps.Take(8).Select(s => s.ToString())) + (macro.Steps.Count > 8 ? " …" : ""),
            "blank" => L["MacroEmpty"],
            "unsupported-opcode" => L["MacroUnsupported"],
            "truncated" => L["MacroTruncated"],
            "loop" => L["MacroLoop"],
            "invalid-crc" => L["MacroCrc"],
            _ => L["MacroOutside"],
        };
        return (L.Chinese ? $"板载宏（扇区 {sector}）：" : $"Onboard macro (sector {sector}): ") + state;
    }

    public void SetLanguage(bool chinese)
    {
        // Preserve editor coordinates and invalid input verbatim. Serializing a
        // semantic profile here would compact holes and change the active mapping.
        var stageEdits = Stages.Select(s => (s.Enabled, s.Value)).ToArray();
        var bindings = Buttons.Select(b => (byte[])b.Selected.Bytes.Clone()).ToArray();
        bool preserve = Snapshot is not null && profileReadable;
        var selections = (DefaultIndex, ShiftSelection, Rate, ReplacementStage);
        bool wasDirty = dirty;
        loading = true;
        L = new(chinese); foreach (string name in new[] { nameof(L), nameof(StageNames), nameof(ShiftNames), nameof(CompatibilityText), nameof(SupportText), nameof(SupportBadge), nameof(DeviceName) }) Changed(name);
        Macros.SetLanguage(L);
        ButtonAssignment.SetLanguage(L);
        ButtonAssignment.RefreshMacroInfo();
        if (Demo) { Status = L["Demo"]; DiscoverySummary = L["Demo"]; }
        if (Snapshot is not null)
        {
            FillProfiles(); LoadProfile(Sector);
            if (preserve && stageEdits.Length == Stages.Count && bindings.Length == Buttons.Count)
            {
                loading = true;
                for (int i = 0; i < Stages.Count; i++) { Stages[i].Enabled = stageEdits[i].Enabled; Stages[i].Value = stageEdits[i].Value; }
                for (int i = 0; i < Buttons.Count; i++)
                {
                    var option = Buttons[i].Options.FirstOrDefault(o => o.Bytes.SequenceEqual(bindings[i])) ?? new BindingOption(L["PreservedAction"], bindings[i]);
                    if (!Buttons[i].Options.Contains(option)) Buttons[i].Options.Add(option);
                    Buttons[i].Selected = option;
                }
                DefaultIndex = selections.DefaultIndex; ShiftSelection = selections.ShiftSelection; Rate = selections.Rate; ReplacementStage = selections.ReplacementStage;
                dirty = wasDirty;
            }
        }
        loading = false;
        Changed(nameof(DefaultIndex)); Changed(nameof(ShiftSelection)); Changed(nameof(Rate));
        NotifyState();
    }
    public void MarkDirty() { if (loading || Snapshot is null) return; dirty = true; if (!Demo) { draftTimer.Stop(); draftTimer.Start(); } NotifyState(); }
    public void InvalidateConnection()
    {
        connectionChanged = true;
        session?.Invalidate();
        Status = L.Chinese ? "设备连接已变化，请刷新。未保存的编辑已保留。" : "Device connection changed. Refresh to reconnect; unsaved edits are retained.";
        NotifyState();
    }
    private void NotifyState() { foreach (var stage in Stages) stage.Describe(Snapshot?.ActiveSector == Sector && Snapshot?.DpiIndex == stage.Index - 1, DefaultIndex == stage.Index - 1, L); foreach (string name in new[] { nameof(Busy), nameof(Idle), nameof(Dirty), nameof(HasSnapshot), nameof(HasButtonMap), nameof(CanRead), nameof(CanEdit), nameof(CanWrite), nameof(CanSetCurrentDpi), nameof(MinimumDpi), nameof(MaximumDpi), nameof(DpiStep), nameof(CanUndo), nameof(CanCancel), nameof(DirtyText), nameof(SupportBadge), nameof(CanDeleteBackups), nameof(CanExportBackup), nameof(CanRestoreSelectedBackup), nameof(CanManageBackups), nameof(NeedsReplacementStage), nameof(CanSaveToSlot), nameof(SaveTargetLabel), nameof(EditingTargetText), nameof(ActiveSlotText), nameof(CanActivate), nameof(ActivationReason), nameof(ActivateLabel), nameof(SelectedSlotDisabled), nameof(Previewing), nameof(CanEndPreview) }) Changed(name); }
    public async Task Run(string message, Func<CancellationToken, Task> action, bool mutation = false)
    {
        if (busy) return; busy = true; writing = mutation; cancellation = new(); Status = message; NotifyState();
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { Status = L["Cancel"]; }
        catch (Exception ex) { Store.Log(ex); Status = L["Error"] + ": " + ex.Message; DiagnosticText = ex.Message; throw; }
        finally { cancellation.Dispose(); cancellation = null; busy = writing = false; await RefreshBackupsAsync(); NotifyState(); }
    }
    public void Cancel() { if (!writing) cancellation?.Cancel(); }
    public async Task Scan(CancellationToken cancel)
    {
        if (Demo) { LoadDemo(); await RefreshBackupsAsync(); return; }
        var result = await Task.Run(() => HidDiscovery.Scan(cancel), cancel);
        Devices.Clear(); foreach (var d in result.Devices) Devices.Add(d);
        DiscoverySummary = result.Devices.Count == 0 ? L["NoDevice"] : result.Devices.Count + " " + L["Devices"];
        DiagnosticText = string.Join("\n", result.Warnings); Status = DiscoverySummary;
        Snapshot = null; Support = null; Endpoint = null; Profiles.Clear(); Stages.Clear(); Buttons.Clear(); dirty = false; Changed(nameof(DeviceName)); Changed(nameof(SupportText));
        DeviceDetails = TelemetryText = "";
    }
    public async Task Read(CancellationToken cancel)
    {
        if (Endpoint is null) return;
        var endpoint = Endpoint;
        Snapshot = null; Support = null; Profiles.Clear(); Stages.Clear(); Buttons.Clear(); dirty = false;
        DeviceDetails = TelemetryText = ""; Changed(nameof(DeviceName)); Changed(nameof(SupportText)); NotifyState();
        session?.Dispose(); session = new(new HidDeviceAccess(endpoint, Store.Root), Store);
        var result = await session.Read(cancel);
        operations = result.Operations;
        Snapshot = result.Snapshot; Support = result.Support; DpiCaps = result.DpiCaps; Rates = result.Rates; connectionChanged = runtimeStale = false; Changed(nameof(Rates));
        DeviceDetails = $"{string.Join(" / ", Snapshot.Identity.ProductIds)} · {Snapshot.Identity.Firmware} · {DeviceCatalog.Platform}";
        UpdateTelemetry(result.Telemetry);
        FillProfiles(); LoadProfile(Snapshot.ActiveSector); DiagnosticText = RedactedDiagnostics();
        Status = Store.Pending().Any(r => r.UnitId == Snapshot.Identity.UnitId) ? L["Pending"] : L["Reconnected"];
        if (session.HasUnfinishedPreview) Status = L["PreviewUnknown"];
        Changed(nameof(DeviceName)); Changed(nameof(SupportText));
    }
    private void UpdateTelemetry(Telemetry t)
    {
        lastTelemetry = t;
        Set(ref currentDpi, (decimal?)t.Dpi, nameof(CurrentDpi));
        string battery = t.BatteryPercent is { } percent
            ? (L.Chinese ? $"电量 {percent}%" : $"Battery {percent}%")
            : t.BatteryMillivolts is int millivolts && millivolts > 0
                ? (L.Chinese ? $"电量未知 · 电压 {millivolts / 1000.0:F3} V" : $"Battery unknown · {millivolts / 1000.0:F3} V")
                : (L.Chinese ? "电量未知" : "Battery unknown");
        TelemetryText = $"{t.Dpi?.ToString() ?? "?"} DPI   ·   {t.PollingRate?.ToString() ?? "?"} Hz   ·   {(Snapshot!.Mode == 1 ? L["Onboard"] : L["Host"])}   ·   {battery}";
    }
    public async Task RefreshRuntime()
    {
        if (busy || refreshingRuntime || Demo || connectionChanged || Previewing || session is null || Snapshot is null) return;
        refreshingRuntime = true; var currentSession = session;
        try
        {
            bool battery = DateTimeOffset.UtcNow >= nextBatteryRead;
            var state = await currentSession.ReadRuntime(battery);
            if (session != currentSession || busy || Snapshot is null) return;
            bool changedState = Snapshot.Mode != state.Mode || Snapshot.ActiveSector != state.ActiveSector || Snapshot.DpiIndex != state.DpiIndex || Snapshot.SensorDpi != state.Telemetry.Dpi;
            // Keep the edit baseline for conflict detection. A hardware action must not silently rebase a draft.
            if (changedState) { runtimeStale = true; Status = L.Chinese ? "鼠标运行状态已变化；显示已更新。保存前请重新读取，草稿已保留。" : "Mouse state changed; display updated. Read again before saving; draft retained."; }
            var telemetry = state.Telemetry;
            if (!battery && lastTelemetry is { } previous) telemetry = telemetry with { BatteryPercent = previous.BatteryPercent, BatteryMillivolts = previous.BatteryMillivolts };
            if (battery) nextBatteryRead = DateTimeOffset.UtcNow.AddSeconds(30);
            UpdateTelemetry(telemetry);
            TelemetryText += L.Chinese ? $"   ·   当前槽 {state.ActiveSector} / 档位 {state.DpiIndex + 1}" : $"   ·   Active slot {state.ActiveSector} / stage {state.DpiIndex + 1}";
            NotifyState();
        }
        catch (Exception ex)
        {
            Store.Log(ex); TelemetryText = L.Chinese ? "设备状态读取失败，请唤醒鼠标并重新读取。" : "Device state unavailable. Wake the mouse and read again.";
            connectionChanged = true; NotifyState();
        }
        finally { refreshingRuntime = false; }
    }
    private void FillProfiles()
    {
        Profiles.Clear(); foreach (var entry in Snapshot!.Directory()) Profiles.Add(new(entry.Sector, $"{L["Profile"]} {entry.Slot} · {(entry.Enabled ? L["Enabled"] : L["Disabled"])}{(Snapshot.ActiveSector == entry.Sector ? " ●" : "")}"));
    }
    public void LoadProfile(int sector)
    {
        if (Snapshot is null) return; int selectedNumber = selectedButton?.Number ?? 1; loading = true; Sector = sector;
        try
        {
            var p = MouseProfile.Decode(Snapshot.Sectors[sector], Snapshot.Layout); profileReadable = true;
            Stages.Clear(); Buttons.Clear();
            for (int i = 0; i < 5; i++) { var stage = new DpiStage(i + 1, p.Dpi[i]); stage.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(DpiStage.Enabled) or nameof(DpiStage.Value)) MarkDirty(); }; Stages.Add(stage); }
            for (int i = 0; i < p.Bindings.Length; i++) { var button = new ButtonEditor(i + 1, p.Bindings[i], L, HasButtonMap ? DisplayRule!.Controls[i] : null); button.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ButtonEditor.Selected)) MarkDirty(); }; Buttons.Add(button); }
            SelectedButton = Buttons.ElementAtOrDefault(selectedNumber - 1) ?? Buttons.FirstOrDefault();
            DefaultIndex = p.DefaultIndex; ShiftSelection = p.ShiftIndex == 255 ? 0 : p.ShiftIndex + 1; Rate = p.Rate; Activate = false; ReplacementStage = -1; dirty = false;
        }
        catch (Exception ex) when (ex is IOException) { Stages.Clear(); Buttons.Clear(); Status = ex.Message; profileReadable = false; }
        finally { loading = false; Changed(nameof(ButtonProfileLabel)); NotifyState(); }
    }
    public MouseProfile Draft()
    {
        var enabled = Stages.Select((stage, index) => (stage, index)).Where(x => x.stage.Enabled).ToArray();
        var values = enabled.Select(x => x.stage.Value is { } dpi && dpi == decimal.Truncate(dpi) ? checked((int)dpi) : throw new InvalidDataException(L["DpiInteger"])).ToList();
        while (values.Count < 5) values.Add(0);
        int MapIndex(int selected, string error) => selected >= 0 && selected < Stages.Count && Stages[selected].Enabled
            ? enabled.TakeWhile(x => x.index != selected).Count() : throw new InvalidDataException(L[error]);
        int defaultIndex = MapIndex(DefaultIndex, "DefaultStageRequired");
        int shiftIndex = ShiftSelection == 0 ? 255 : MapIndex(ShiftSelection - 1, "ShiftStageRequired");
        return new(Rate, values.ToArray(), defaultIndex, shiftIndex, Buttons.Select(b => b.Selected.Bytes).ToArray());
    }
    public void ValidateDraft()
    {
        if (Snapshot is null || DpiCaps is null) throw new InvalidOperationException("No editable device.");
        _ = Draft().Encode(Snapshot.Sectors[Sector], Snapshot.Layout, DpiCaps, Rates);
        _ = RuntimeStageForDraft();
    }
    public int? RuntimeStageForDraft()
    {
        if (Snapshot is not { } snapshot || Sector != snapshot.ActiveSector) return null;
        int source = snapshot.DpiIndex;
        if (source >= Stages.Count) throw new InvalidDataException(L["ReplacementStageRequired"]);
        if (!Stages[source].Enabled) source = ReplacementStage;
        if (source < 0 || source >= Stages.Count || !Stages[source].Enabled) throw new InvalidDataException(L["ReplacementStageRequired"]);
        return Stages.Take(source).Count(stage => stage.Enabled);
    }
    public string ChangeSummary()
    {
        ValidateDraft(); var draft = Draft(); var old = MouseProfile.Decode(Snapshot!.Sectors[Sector], Snapshot.Layout);
        var lines = new List<string> { $"{Snapshot.Identity.Name} · {L["Profile"]} {Profiles.First(p => p.Sector == Sector).Label}", $"DPI: {string.Join(" / ", draft.Dpi.Where(d => d > 0))}", $"{L["Default"]}: {draft.DefaultIndex + 1}; {L["Shift"]}: {(draft.ShiftIndex == 255 ? L["None"] : (draft.ShiftIndex + 1).ToString())}", $"{L["Rate"]}: {draft.Rate} Hz", $"{L["Activate"]}: {Activate}" };
        lines.AddRange(Enumerable.Range(0, Buttons.Count).Where(i => !old.Bindings[i].SequenceEqual(draft.Bindings[i])).Select(i => $"{Buttons[i].Label}: {Buttons[i].Selected.Label}")); return string.Join("\n", lines);
    }
    public async Task Apply()
    {
        if (Demo || Endpoint is null || Snapshot is null) throw new InvalidOperationException("Hardware writes are unavailable.");
        var original = Snapshot; var draft = Draft(); int sector = Sector;
        var timer = Stopwatch.StartNew();
        var result = await session!.Save(original, sector, draft, RuntimeStageForDraft(), p => Dispatcher.UIThread.Post(() => Status = L[p]));
        Snapshot = result.Snapshot; UpdateTelemetry(result.Telemetry);
        FillProfiles(); LoadProfile(sector); DiagnosticText = RedactedDiagnostics();
        Status = L["Saved"] + $" ({timer.Elapsed.TotalSeconds:F2} s)";
    }
    public async Task ApplyCurrentDpi()
    {
        if (Demo || Endpoint is null || Snapshot is null || connectionChanged || RecoveryRequired)
            throw new InvalidOperationException("Current DPI control is unavailable.");
        int dpi = checked((int)(CurrentDpi ?? throw new InvalidDataException("DPI is empty.")));
        if (CurrentDpi != dpi) throw new InvalidDataException("DPI must be an integer.");
        var baseline = Snapshot; var timer = Stopwatch.StartNew();
        try
        {
            CurrentDpi = await session!.Preview(baseline, dpi); Status = L["CurrentDpiApplied"] + $" ({timer.Elapsed.TotalMilliseconds:F0} ms)";
        }
        catch (Exception ex) when (ex is DeviceException or IOException or TimeoutException)
        { connectionChanged = true; NotifyState(); throw; }
    }
    public async Task Export(string path, CancellationToken cancel)
    {
        if (Demo || Endpoint is null) return;
        await session!.Export(path, cancel); Status = path;
    }
    public async Task Restore(string path)
    {
        if (Demo || Endpoint is null) return;
        await session!.Restore(path, p => Dispatcher.UIThread.Post(() => Status = p));
        await Read(CancellationToken.None); Status = L["Saved"];
    }
    public string RedactedDiagnostics() => JsonSerializer.Serialize(new { app = "LightHub 0.3.0-alpha.1", platform = DeviceCatalog.Platform, runtime = Environment.Version.ToString(), os = Environment.OSVersion.VersionString, model = Snapshot?.Identity.Name, productIds = Snapshot?.Identity.ProductIds, firmware = Snapshot?.Identity.Firmware, layout = Snapshot?.Layout, support = Support, operations, dpiCapabilities = DpiCaps, rates = Rates }, Json.Options);
    public async Task RefreshBackupsAsync()
    {
        try
        {
            var result = await Task.Run(() => (Files: Store.ListBackups(), Pending: Store.Pending(), Presets: Assets.List()));
            Backups.Clear(); SelectedBackups = []; foreach (var file in result.Files) Backups.Add(new(file, L));
            Presets.Clear(); foreach (var preset in result.Presets) Presets.Add(preset);
            RecoveryRequired = result.Pending.Any(r => r.UnitId == Snapshot?.Identity.UnitId);
            recoveryText = result.Pending.Count > 0 ? L["Pending"] : ""; Changed(nameof(RecoveryText));
            BackupSummary = string.Format(L["BackupSummary"], result.Files.Count, result.Files.Sum(f => f.Bytes) / 1024.0, result.Files.Count(f => f.Protected));
        }
        catch (Exception ex) { RecoveryRequired = true; BackupSummary = L["BackupUnavailable"] + " " + ex.Message; }
        NotifyState();
    }
    public async Task EndPreview()
    {
        previewTimer.Stop();
        if (session is null) return;
        bool restored;
        try { restored = await session.EndPreview(); }
        catch (DeviceException) { session.Dispose(); restored = false; }
        Status = restored ? L["PreviewEnded"] : L["PreviewUnknown"];
        if (!restored) connectionChanged = true;
        NotifyState();
    }
    public void DisposeSession() { draftTimer.Stop(); previewTimer.Stop(); session?.Dispose(); session = null; }
    public async Task ActivateSelected()
    {
        if (dirty || Snapshot is null || session is null) throw new InvalidOperationException("Save or discard the draft first.");
        var result = await session.Activate(Snapshot, Sector, enableDisabled: SelectedSlotDisabled); Snapshot = result.Snapshot; UpdateTelemetry(result.Telemetry); FillProfiles(); NotifyState();
    }
    public void SetDraft(MouseProfile profile, bool changed = true)
    {
        loading = true;
        try
        {
            for (int i = 0; i < Stages.Count; i++) { Stages[i].Enabled = profile.Dpi[i] > 0; Stages[i].Value = profile.Dpi[i] > 0 ? profile.Dpi[i] : 800; }
            for (int i = 0; i < Buttons.Count; i++)
            {
                var option = Buttons[i].Options.FirstOrDefault(o => o.Bytes.SequenceEqual(profile.Bindings[i])) ?? new BindingOption("Raw · " + Convert.ToHexString(profile.Bindings[i]), profile.Bindings[i]);
                if (!Buttons[i].Options.Contains(option)) Buttons[i].Options.Add(option); Buttons[i].Selected = option;
            }
            Rate = profile.Rate; DefaultIndex = profile.DefaultIndex; ShiftSelection = profile.ShiftIndex == 255 ? 0 : profile.ShiftIndex + 1; dirty = changed;
        }
        finally { loading = false; NotifyState(); }
    }
    public LocalPreset CreatePreset() => LocalAssets.FromProfile(PresetName, DisplayRule?.Id ?? Support?.ModelId ?? "unknown", Draft());
    public async Task SavePreset() { var preset = CreatePreset(); await Task.Run(() => Assets.Save(preset)); await RefreshBackupsAsync(); }
    public void LoadPreset(LocalPreset preset)
    {
        if (Snapshot is null || DpiCaps is null) throw new InvalidOperationException("Select a target device first.");
        var profile = LocalAssets.Map(preset, DisplayRule?.Id ?? Support?.ModelId ?? "unknown", Draft());
        _ = profile.Encode(Snapshot.Sectors[Sector], Snapshot.Layout, DpiCaps, Rates); SetDraft(profile);
    }
    public async Task SaveDraft() { var preset = CreatePreset(); await Task.Run(() => Assets.SaveDraft(preset)); dirty = false; NotifyState(); }
    public void RefreshBackups()
    {
        Backups.Clear(); SelectedBackups = [];
        try
        {
            var files = Store.ListBackups();
            foreach (var file in files) Backups.Add(new(file, L));
            BackupSummary = string.Format(L["BackupSummary"], files.Count, files.Sum(f => f.Bytes) / 1024.0, files.Count(f => f.Protected));
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or UnauthorizedAccessException)
        {
            Store.Log(ex); BackupSummary = L["BackupUnavailable"] + " " + ex.Message;
        }
        NotifyState();
    }
    public void SelectBackups(IEnumerable<BackupItem> items) { SelectedBackups = items.ToArray(); NotifyState(); }
    public async Task DeleteBackups(IEnumerable<string> paths)
    {
        if (Demo) throw new InvalidOperationException("Backup deletion is unavailable in demo mode.");
        var selected = paths.ToArray();
        await Task.Run(() => Store.DeleteBackups(selected)); Status = string.Format(L["BackupsDeleted"], selected.Length);
    }
    public void LoadDemo()
    {
        Snapshot = DemoData.Create(); Support = new(false, "demo", "Demo"); DpiCaps = new([], 100, 25600, 50); Rates = [125, 250, 500, 1000];
        FillProfiles(); LoadProfile(1); CurrentDpi = 1600; DeviceDetails = "DEMO · HID++ 2.0"; TelemetryText = "1600 DPI · 1000 Hz"; Status = L["Demo"]; DiscoverySummary = L["Demo"]; DiagnosticText = RedactedDiagnostics(); Changed(nameof(DeviceName)); Changed(nameof(SupportText)); NotifyState();
    }
}
public static class DemoData
{
    public static DeviceSnapshot Create(int profiles = 5, int buttons = 8, int size = 255, int sectors = 16)
    {
        byte[] raw = [1, 3, 1, (byte)profiles, 1, (byte)buttons, (byte)sectors, (byte)(size >> 8), (byte)size, 10, 4, 0, 0, 0, 0, 0];
        var layout = MemoryLayout.Parse(raw); var map = new SortedDictionary<int, byte[]>(); for (int i = 0; i < sectors; i++) map[i] = Enumerable.Repeat((byte)255, size).ToArray();
        for (int i = 0; i < profiles; i++) { map[0][i * 4] = 0; map[0][i * 4 + 1] = (byte)(i + 1); map[0][i * 4 + 2] = (byte)(i == 0 ? 1 : 0); map[0][i * 4 + 3] = 0; }
        Wire.UpdateCrc(map[0]);
        for (int i = 1; i <= profiles; i++)
        {
            var data = map[i]; data[0] = 1; data[1] = 2; data[2] = 0;
            for (int j = 0; j < 5; j++) System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(3 + j * 2, 2), (ushort)(400 << j));
            for (int j = 0; j < buttons; j++) new byte[] { 128, 1, 0, (byte)(1 << Math.Min(j, 4)) }.CopyTo(data, 32 + j * 4);
            Wire.UpdateCrc(data);
        }
        return new(2, new("G PRO Wireless", "1234ABCD", ["4079", "C088"], "BOT 74.02.0026", 3), layout, 1, 1, 2, map);
    }
}
