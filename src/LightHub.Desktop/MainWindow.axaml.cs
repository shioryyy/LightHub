using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using LightHub.Core;
using LightHub.Application;
using LightHub.Hid;

namespace LightHub.Desktop;

public partial class MainWindow : Window
{
    public Workspace Model { get; }
    public Task Initialization { get; private set; } = Task.CompletedTask;
    private bool syncing, allowClose;
    private readonly DeviceWatcher? watcher;
    private readonly Avalonia.Threading.DispatcherTimer topologyTimer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    public MainWindow() : this(false) { }
    public MainWindow(bool demo, TransactionStore? store = null)
    {
        Model = new(demo, store); AvaloniaXamlLoader.Load(this); DataContext = Model;
        syncing = true; this.FindControl<ComboBox>("Language")!.SelectedIndex = Model.L.Chinese ? 1 : 0; syncing = false;
        this.FindControl<MacroEditorView>("MacroEditor")!.DialogOwner = this;
        this.FindControl<MacroEditorView>("MacroEditor")!.DataContext = Model.Macros;
        this.FindControl<TabControl>("Tabs")!.SelectionChanged += TabChanged;
        Opened += (_, _) => Initialization = Safe(async () => { await Model.Macros.RefreshAsync(); await Refresh(); });
        if (!demo)
        {
            watcher = new();
            watcher.Changed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => { topologyTimer.Stop(); topologyTimer.Start(); });
            topologyTimer.Tick += async (_, _) =>
            {
                if (Model.Busy) return;
                topologyTimer.Stop();
                if (Model.Endpoint is { } selected && !selected.Channels.All(c => HidSharp.DeviceList.Local.GetHidDevices(0x046d).Any(d => d.DevicePath == c.DevicePath))) Model.InvalidateConnection();
            };
        }
        Closed += (_, _) => { topologyTimer.Stop(); watcher?.Dispose(); Model.DisposeSession(); };
        Closing += async (_, e) =>
        {
            if (allowClose) return;
            if (Model.Busy || Model.Macros.Busy) { e.Cancel = true; Model.Status = Model.L["Busy"]; return; }
            if (Model.Dirty || Model.Previewing || Model.Macros.Dirty)
            {
                e.Cancel = true;
                await Safe(async () => { if (await this.FindControl<MacroEditorView>("MacroEditor")!.ResolveEditsAsync() && await Discard()) { allowClose = true; Close(); } });
            }
        };
    }
    private void TabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.Source is not TabControl tabs || tabs.Name != "Tabs") return;
        bool hardware = tabs.SelectedItem is not TabItem { Name: "MacrosTab" };
        if (this.FindControl<Grid>("DeviceHeading") is { } heading) heading.IsVisible = hardware;
        if (this.FindControl<Button>("HardwareUndo") is { } undo) undo.IsVisible = hardware;
        if (this.FindControl<Button>("HardwareApply") is { } apply) apply.IsVisible = hardware;
    }
    private async Task Safe(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { Model.Store.Log(ex); await Message(Model.L["Error"], ex.Message); }
    }
    private async Task<bool> Confirm(string text) => await Message(Model.L["Confirm"], text, true);
    private Task<bool> Message(string title, string text, bool confirm = false)
    {
        var dialog = new Window { Title = title, Width = 520, MaxHeight = 640, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var body = new StackPanel { Margin = new Thickness(24), Spacing = 20 };
        body.Children.Add(new ScrollViewer { MaxHeight = 460, Content = new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap } });
        var actions = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        if (confirm) { var cancel = new Button { Content = Model.L["Cancel"] }; cancel.Click += (_, _) => dialog.Close(false); actions.Children.Add(cancel); }
        var ok = new Button { Content = Model.L["Confirm"], IsDefault = !confirm }; ok.Click += (_, _) => dialog.Close(true); actions.Children.Add(ok); body.Children.Add(actions); dialog.Content = body; return dialog.ShowDialog<bool>(this);
    }
    private async Task<bool> Discard()
    {
        if (Model.Previewing) await Model.Run(Model.L["EndPreview"], _ => Model.EndPreview(), true);
        if (!Model.Dirty) return true;
        var dialog = new Window { Title = Model.L["Dirty"], Width = 480, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 14 };
        panel.Children.Add(new TextBlock { Text = Model.L["Dirty"], TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        foreach (var item in new[] { ("SaveDraft", 1), ("Undo", 2), ("Cancel", 0) })
        { var button = new Button { Content = Model.L[item.Item1], IsDefault = item.Item2 == 0 }; button.Click += (_, _) => dialog.Close(item.Item2); panel.Children.Add(button); }
        dialog.Content = panel; int choice = await dialog.ShowDialog<int>(this);
        if (choice == 1) await Model.SaveDraft();
        return choice != 0;
    }
    private async Task Refresh(bool keepSelection = false)
    {
        if (!await Discard()) return;
        string? selected = Model.Endpoint?.Id;
        syncing = true;
        try { await Model.Run(Model.L["Scanning"], Model.Scan); }
        finally { syncing = false; }
        if (Model.Demo) SyncProfiles();
        else if (Model.Devices.Count > 0)
        {
            var match = Model.Devices.FirstOrDefault(d => d.Id == selected);
            this.FindControl<ListBox>("DeviceList")!.SelectedItem = match ?? (keepSelection && selected is not null ? null : Model.Devices[0]);
        }
    }
    private async void ScanClicked(object? sender, RoutedEventArgs e) => await Safe(() => Refresh());
    private async void ReadClicked(object? sender, RoutedEventArgs e) => await Safe(async () => { if (await Discard()) { await Model.Run(Model.L["Reading"], Model.Read); SyncProfiles(); } });
    private async void DeviceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (syncing || Model.Busy || sender is not ListBox list || list.SelectedItem is not DeviceEndpoint endpoint) return;
        await Safe(async () =>
        {
            if (!await Discard()) { syncing = true; list.SelectedItem = Model.Endpoint; syncing = false; return; }
            Model.Endpoint = endpoint;
            await Model.Run(Model.L["Reading"], Model.Read); SyncProfiles();
        });
    }
    private void SyncProfiles() { syncing = true; this.FindControl<ComboBox>("ProfileList")!.SelectedItem = Model.Profiles.FirstOrDefault(p => p.Sector == Model.Sector); syncing = false; }
    private async void ProfileChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (syncing || Model.Busy || sender is not ComboBox combo || combo.SelectedItem is not ProfileItem item || item.Sector == Model.Sector) return;
        await Safe(async () => { if (!await Discard()) { SyncProfiles(); return; } Model.LoadProfile(item.Sector); });
    }
    private void UndoClicked(object? sender, RoutedEventArgs e) => Model.LoadProfile(Model.Sector);
    private void ButtonMapClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ButtonEditor button }) return;
        Model.SelectedButton = button;
    }
    private void ActionSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (Model.CanEdit && e.AddedItems.OfType<BindingOption>().FirstOrDefault() is { } action) Model.ButtonAssignment.Assign(action);
    }
    private void ActionGroupSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.OfType<ActionGroupItem>().FirstOrDefault() is { } group) Model.ButtonAssignment.Group = group.Index;
    }
    private void OpenMacrosClicked(object? sender, RoutedEventArgs e) => this.FindControl<TabControl>("Tabs")!.SelectedItem = this.FindControl<TabItem>("MacrosTab");
    private void CancelClicked(object? sender, RoutedEventArgs e) => Model.Cancel();
    private async void CurrentDpiClicked(object? sender, RoutedEventArgs e) => await Safe(() => Model.Run(Model.L["Busy"], _ => Model.ApplyCurrentDpi(), mutation: true));
    private async void EndPreviewClicked(object? sender, RoutedEventArgs e) => await Safe(() => Model.Run(Model.L["EndPreview"], _ => Model.EndPreview(), true));
    private async void ActivateClicked(object? sender, RoutedEventArgs e) => await Safe(async () =>
    {
        if (await Confirm(Model.L["ActivateQuestion"] + "\n\n" + Model.ActivationSummary())) await Model.Run(Model.ActivateLabel, _ => Model.ActivateSelected(), true);
    });
    private async void ApplyClicked(object? sender, RoutedEventArgs e) => await Safe(async () =>
    {
        string summary = Model.ChangeSummary();
        if (!await Confirm(Model.L["WriteQuestion"] + "\n\n" + summary)) return;
        await Model.Run(Model.L["Busy"], _ => Model.Apply(), mutation: true); SyncProfiles();
    });
    private async void ExportClicked(object? sender, RoutedEventArgs e) => await Safe(async () =>
    {
        var file = await StorageProvider.SaveFilePickerAsync(new() { Title = Model.L["Export"], SuggestedFileName = "LightHub-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".lhbackup", FileTypeChoices = [BackupType] });
        if (file?.TryGetLocalPath() is { } path) await Model.Run(Model.L["Export"], c => Model.Export(path, c));
    });
    private static readonly FilePickerFileType BackupType = new("LightHub backup v2") { Patterns = ["*.lhbackup"] };
    private async void RestoreClicked(object? sender, RoutedEventArgs e) => await Safe(async () =>
    {
        var files = await StorageProvider.OpenFilePickerAsync(new() { Title = Model.L["Restore"], AllowMultiple = false, FileTypeFilter = [BackupType] });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) await Restore(path);
    });
    private async Task Restore(string path)
    {
        if (!await Discard()) return; _ = BackupFile.Load(path);
        if (await Confirm(Model.L["RestoreQuestion"] + "\n" + Path.GetFileName(path))) { await Model.Run(Model.L["Restore"], _ => Model.Restore(path), mutation: true); SyncProfiles(); }
    }
    private void BackupSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox list) Model.SelectBackups(list.SelectedItems?.Cast<BackupItem>() ?? []);
    }
    private async void RefreshBackupsClicked(object? sender, RoutedEventArgs e) => await Safe(Model.RefreshBackupsAsync);
    private async void ExportSelectedBackupClicked(object? sender, RoutedEventArgs e) => await Safe(async () =>
    {
        if (!Model.CanExportBackup) return;
        var item = Model.SelectedBackups[0];
        var file = await StorageProvider.SaveFilePickerAsync(new() { Title = Model.L["ExportSelectedBackup"], SuggestedFileName = item.Name, FileTypeChoices = [BackupType] });
        if (file?.TryGetLocalPath() is { } path)
            await Model.Run(Model.L["ExportSelectedBackup"], _ => Task.Run(() => Model.Store.ExportStoredBackup(item.Path, path)), mutation: true);
    });
    private async void RestoreSelectedBackupClicked(object? sender, RoutedEventArgs e) => await RestoreSelectedBackup();
    private async void BackupDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e) => await RestoreSelectedBackup();
    private Task RestoreSelectedBackup() => Safe(async () => { if (Model.CanRestoreSelectedBackup) await Restore(Model.SelectedBackups[0].Path); });
    private async void DeleteBackupsClicked(object? sender, RoutedEventArgs e) => await Safe(async () =>
    {
        if (!Model.CanDeleteBackups) return;
        var selected = Model.SelectedBackups.ToArray();
        string question = string.Format(Model.L["DeleteBackupsQuestion"], selected.Length) + "\n\n" + string.Join("\n", selected.Select(b => b.Title + "\n" + b.Name));
        if (await Confirm(question)) await Model.Run(Model.L["DeleteBackups"], _ => Model.DeleteBackups(selected.Select(b => b.Path)), mutation: true);
    });
    private async void CleanupBackupsClicked(object? sender, RoutedEventArgs e) => await Safe(async () =>
    {
        if (!Model.CanManageBackups) return;
        var old = await Task.Run(() => Model.Store.CleanupCandidates());
        if (old.Count == 0) { Model.Status = Model.L["NoCleanup"]; return; }
        string question = string.Format(Model.L["CleanupBackupsQuestion"], old.Count) + "\n\n" + string.Join("\n", old.Select(b => $"{b.Model} · {b.Date.LocalDateTime:g}\n{b.Name}"));
        if (await Confirm(question)) await Model.Run(Model.L["CleanupBackups"], _ => Model.DeleteBackups(old.Select(b => b.Path)), mutation: true);
    });
    private async void DiagnosticsClicked(object? sender, RoutedEventArgs e) => await Safe(async () =>
    {
        var file = await StorageProvider.SaveFilePickerAsync(new() { Title = Model.L["Diagnostics"], SuggestedFileName = "LightHub-diagnostics.json" });
        if (file?.TryGetLocalPath() is { } path) AtomicFile.Write(path, Model.RedactedDiagnostics());
    });
    private async void LanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (syncing || DataContext is null || sender is not ComboBox combo) return;
        syncing = true;
        try { Model.SetLanguage(combo.SelectedIndex == 1); SyncProfiles(); }
        finally { syncing = false; }
        await Model.RefreshBackupsAsync();
    }
    private async void AboutClicked(object? sender, RoutedEventArgs e) => await Message("LightHub", Program.VersionText + "\nMIT License\n\nAvalonia 12.1.2 · .NET 10 · HidSharp 2.6.4\n\n" + (Model.L.Chinese ? "独立开源项目，与 Logitech 无隶属关系。" : "Independent open-source project, not affiliated with Logitech."));
    private async void SavePresetClicked(object? sender, RoutedEventArgs e) => await Safe(() => Model.Run(Model.L["SavePreset"], _ => Model.SavePreset()));
    private async void LoadPresetClicked(object? sender, RoutedEventArgs e) => await Safe(async () => { var selected = Model.SelectedPreset; if (selected is not null && await Discard()) Model.LoadPreset(selected); });
    private async void DeletePresetClicked(object? sender, RoutedEventArgs e) => await Safe(async () => { var selected = Model.SelectedPreset; if (selected is not null && await Confirm(Model.L["DeletePresetQuestion"])) await Model.Run(Model.L["DeleteBackups"], _ => Task.Run(() => Model.Assets.Delete(selected))); });
    private static readonly FilePickerFileType PresetType = new("LightHub preset") { Patterns = ["*.lhpreset"] };
    private async void ImportPresetClicked(object? sender, RoutedEventArgs e) => await Safe(async () =>
    {
        var files = await StorageProvider.OpenFilePickerAsync(new() { FileTypeFilter = [PresetType] });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
            await Model.Run(Model.L["ImportPreset"], _ => Task.Run(() => { var preset = LocalAssets.Read(path); Model.Assets.Save(preset with { Id = Guid.NewGuid().ToString("N") }); }));
    });
    private async void ExportPresetClicked(object? sender, RoutedEventArgs e) => await Safe(async () =>
    {
        var selected = Model.SelectedPreset; if (selected is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new() { SuggestedFileName = "preset.lhpreset", FileTypeChoices = [PresetType] });
        if (file?.TryGetLocalPath() is { } path) await Task.Run(() => LocalAssets.Export(selected, path));
    });
    private async void RecoverDraftClicked(object? sender, RoutedEventArgs e) => await Safe(async () => { var draft = await Task.Run(Model.Assets.LoadDraft); if (draft is not null && await Discard()) Model.LoadPreset(draft); });
    private async void PinBackupClicked(object? sender, RoutedEventArgs e) => await Safe(async () =>
    {
        var selected = Model.SelectedBackups.ToArray();
        await Model.Run(Model.L["PinBackup"], _ => Task.Run(() => { foreach (var item in selected) Model.Store.SetBackupMetadata(item.Path, item.Backup.Metadata with { Pinned = !item.Backup.Metadata.Pinned }); }));
    });
    private async void NameBackupClicked(object? sender, RoutedEventArgs e) => await Safe(async () =>
    {
        if (Model.SelectedBackups.Count != 1) return;
        var item = Model.SelectedBackups[0];
        var dialog = new Window { Title = Model.L["NameBackup"], Width = 420, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 16 };
        var name = new TextBox { Text = item.Backup.Metadata.Label, MaxLength = 120 }; var milestone = new CheckBox { Content = Model.L["Milestone"], IsChecked = item.Backup.Metadata.Milestone };
        var save = new Button { Content = Model.L["Confirm"] }; save.Click += (_, _) => dialog.Close(true);
        panel.Children.Add(name); panel.Children.Add(milestone); panel.Children.Add(save); dialog.Content = panel;
        if (await dialog.ShowDialog<bool>(this))
        {
            var metadata = item.Backup.Metadata with { Label = name.Text ?? "", Milestone = milestone.IsChecked == true };
            await Model.Run(Model.L["NameBackup"], _ => Task.Run(() => Model.Store.SetBackupMetadata(item.Path, metadata)));
        }
    });
    private async void RenamePresetClicked(object? sender, RoutedEventArgs e) => await Safe(async () =>
    {
        var selected = Model.SelectedPreset; if (selected is null) return;
        string name = Model.PresetName;
        await Model.Run(Model.L["RenamePreset"], _ => Task.Run(() => Model.Assets.Save(selected with { Name = name })));
    });
    private async void TrashClicked(object? sender, RoutedEventArgs e) => await Safe(async () =>
    {
        var dialog = new Window { Title = Model.L["Trash"], Width = 680, Height = 460, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new DockPanel { Margin = new Thickness(18) }; var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12 };
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        var list = new ListBox { ItemsSource = await Task.Run(Model.Store.ListTrash) }; panel.Children.Add(list);
        var restore = new Button { Content = Model.L["RecoverTrash"] }; var erase = new Button { Content = Model.L["EraseTrash"] };
        buttons.Children.Add(restore); buttons.Children.Add(erase);
        restore.Click += async (_, _) => await Safe(async () => { if (list.SelectedItem is TrashEntry entry) { await Task.Run(() => Model.Store.RecoverTrash(entry.Id)); list.ItemsSource = await Task.Run(Model.Store.ListTrash); await Model.RefreshBackupsAsync(); } });
        erase.Click += async (_, _) => await Safe(async () => { if (list.SelectedItem is TrashEntry entry && await Confirm(Model.L["EraseQuestion"] + "\n" + entry.OriginalName)) { await Task.Run(() => Model.Store.PermanentlyDeleteTrash(entry.Id)); list.ItemsSource = await Task.Run(Model.Store.ListTrash); } });
        dialog.Content = panel; await dialog.ShowDialog(this);
    });
    private async void CustomKeyClicked(object? sender, RoutedEventArgs e) => await Safe(async () =>
    {
        if (sender is not Control { DataContext: ButtonEditor editor }) return;
        var dialog = new Window { Title = Model.L["Custom"], Width = 420, Height = 230, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 20 }; var mods = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 18 };
        var checks = new[] { "Ctrl", "Shift", "Alt", "Win" }.Select(m => new CheckBox { Content = m }).ToArray(); foreach (var c in checks) mods.Children.Add(c); panel.Children.Add(mods);
        var keys = BindingOption.All(Model.L).Where(b => b.Bytes[0] == 128 && b.Bytes[1] == 2 && b.Bytes[2] == 0).ToArray();
        var choice = new ComboBox { ItemsSource = keys, SelectedIndex = 0 }; panel.Children.Add(choice);
        var ok = new Button { Content = Model.L["Confirm"] }; panel.Children.Add(ok); ok.Click += (_, _) => dialog.Close(true); dialog.Content = panel;
        if (await dialog.ShowDialog<bool>(this) && choice.SelectedItem is BindingOption key)
        {
            var raw = (byte[])key.Bytes.Clone(); raw[2] = (byte)checks.Select((c, i) => c.IsChecked == true ? 1 << i : 0).Sum();
            var option = new BindingOption(string.Join("+", checks.Where(c => c.IsChecked == true).Select(c => c.Content!.ToString()).Append(key.Label)), raw); editor.Options.Insert(0, option); editor.Selected = option;
        }
    });
}
