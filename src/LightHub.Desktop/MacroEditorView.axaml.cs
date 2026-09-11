using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

namespace LightHub.Desktop;

public partial class MacroEditorView : UserControl
{
    private bool syncing, deciding;
    private MacroEditorViewModel Model => (MacroEditorViewModel)DataContext!;
    public Window? DialogOwner { get; set; }
    private Window Owner => DialogOwner ?? (Window)TopLevel.GetTopLevel(this)!;
    private static readonly FilePickerFileType MacroType = new("LightHub macro") { Patterns = ["*.lhmacro"] };
    public MacroEditorView() { AvaloniaXamlLoader.Load(this); }

    // MainWindow uses this for close; switching hardware never discards macro edits.
    public async Task<bool> ResolveEditsAsync()
    {
        if (Model.Busy || deciding) return false;
        if (!Model.Dirty) return true;
        deciding = true;
        try
        {
            int choice = await Choose(Model.L["MacroDirtyQuestion"], ("MacroSaveDraft", 1, Model.CanSaveDraft), ("MacroDiscard", 2, true), ("Cancel", 0, true));
            if (choice == 1) return await Model.SaveAsync(true);
            if (choice == 2) { Model.DiscardEdits(); return true; }
            return false;
        }
        finally { deciding = false; SyncSelection(); }
    }
    private async Task<int> Choose(string text, params (string Label, int Value, bool Enabled)[] choices)
    {
        var dialog = new Window { Title = Model.L["Macros"], Width = 460, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var body = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        body.Children.Add(new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        foreach (var choice in choices)
        {
            var button = new Button { Content = Model.L[choice.Label], IsEnabled = choice.Enabled, IsDefault = choice.Value == 0, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
            button.Click += (_, _) => dialog.Close(choice.Value); body.Children.Add(button);
        }
        dialog.Content = body; return await dialog.ShowDialog<int>(Owner);
    }
    private void SyncSelection() { syncing = true; this.FindControl<ListBox>("MacroList")!.SelectedItem = Model.Selected; syncing = false; }
    private async void NewClicked(object? sender, RoutedEventArgs e) { if (await ResolveEditsAsync()) { Model.New(); SyncSelection(); this.FindControl<TextBox>("MacroName")!.Focus(); } }
    private async void SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (syncing || DataContext is not MacroEditorViewModel || Model.Busy || sender is not ListBox { SelectedItem: MacroListItem item } || item == Model.Selected) return;
        if (await ResolveEditsAsync()) Model.Open(item); SyncSelection();
    }
    private void AddDownClicked(object? sender, RoutedEventArgs e) => Model.Add("down");
    private void AddUpClicked(object? sender, RoutedEventArgs e) => Model.Add("up");
    private void AddDelayClicked(object? sender, RoutedEventArgs e) => Model.Add("delay");
    private void MoveUpClicked(object? sender, RoutedEventArgs e) => Model.MoveStep(-1);
    private void MoveDownClicked(object? sender, RoutedEventArgs e) => Model.MoveStep(1);
    private void CopyStepClicked(object? sender, RoutedEventArgs e) => Model.CopyStep();
    private void RemoveStepClicked(object? sender, RoutedEventArgs e) => Model.RemoveStep();
    private async void SaveClicked(object? sender, RoutedEventArgs e) { await Model.SaveAsync(); SyncSelection(); }
    private async void SaveDraftClicked(object? sender, RoutedEventArgs e) { await Model.SaveAsync(true); SyncSelection(); }
    private async void DuplicateClicked(object? sender, RoutedEventArgs e) { await Model.DuplicateAsync(); SyncSelection(); }
    private async void DiscardClicked(object? sender, RoutedEventArgs e) { if (await Choose(Model.L["MacroDirtyQuestion"], ("MacroDiscard", 2, true), ("Cancel", 0, true)) == 2) Model.DiscardEdits(); SyncSelection(); }
    private async void RefreshClicked(object? sender, RoutedEventArgs e) { await Model.RefreshAsync(); SyncSelection(); }
    private async void ImportClicked(object? sender, RoutedEventArgs e)
    {
        if (!await ResolveEditsAsync()) return;
        var files = await Owner.StorageProvider.OpenFilePickerAsync(new() { Title = Model.L["MacroImport"], FileTypeFilter = [MacroType], AllowMultiple = false });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) { await Model.ImportAsync(path); SyncSelection(); }
    }
    private async void ExportClicked(object? sender, RoutedEventArgs e)
    {
        if (!Model.CanExport) return;
        var file = await Owner.StorageProvider.SaveFilePickerAsync(new() { Title = Model.L["MacroExport"], SuggestedFileName = "LightHub-macro.lhmacro", FileTypeChoices = [MacroType] });
        if (file?.TryGetLocalPath() is { } path) await Model.ExportAsync(path);
    }
    private async void RecycleClicked(object? sender, RoutedEventArgs e)
    {
        if (!Model.CanRecycle) return;
        if (await Choose(Model.L["MacroRecycleQuestion"], ("MacroRecycle", 1, true), ("Cancel", 0, true)) == 1) { await Model.RecycleAsync(); SyncSelection(); }
    }
    private async void TrashClicked(object? sender, RoutedEventArgs e)
    {
        await Model.RefreshAsync();
        var dialog = new Window { Title = Model.L["Trash"], Width = 520, Height = 420, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var grid = new Grid { RowDefinitions = new("Auto,*,Auto"), Margin = new Thickness(20), RowSpacing = 12 };
        var status = new TextBlock { Text = Model.Trash.Count == 0 ? Model.L["MacroTrashEmpty"] : "", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var list = new ListBox { ItemsSource = Model.Trash }; Grid.SetRow(list, 1);
        var restore = new Button { Content = Model.L["MacroRecover"], IsEnabled = false }; Grid.SetRow(restore, 2);
        list.SelectionChanged += (_, _) => restore.IsEnabled = list.SelectedItem is MacroListItem;
        restore.Click += async (_, _) => { if (list.SelectedItem is MacroListItem item) { restore.IsEnabled = false; await Model.RecoverAsync(item); status.Text = Model.Status; SyncSelection(); } };
        dialog.Closing += (_, e) => { if (Model.Busy) e.Cancel = true; };
        grid.Children.Add(status); grid.Children.Add(list); grid.Children.Add(restore); dialog.Content = grid; await dialog.ShowDialog(Owner);
    }
}
