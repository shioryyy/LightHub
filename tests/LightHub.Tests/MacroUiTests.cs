using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LightHub.Application;
using LightHub.Desktop;
using Xunit;

namespace LightHub.Tests;

public sealed partial class UiTests
{
    [AvaloniaFact]
    public async Task MacroRefreshClearsAFileRemovedByAnotherEditor()
    {
        string root = Path.Combine(Path.GetTempPath(), "lighthub-macro-removed-" + Guid.NewGuid().ToString("N"));
        try
        {
            var library = new MacroLibrary(root); var entry = library.Save(MacroTests.CopyMacro(), null);
            var editor = new MacroEditorViewModel(library, new Strings(false)); await editor.RefreshAsync(); editor.Open(Assert.Single(editor.Items));
            library.Recycle(entry); Assert.True(await editor.RefreshAsync());
            Assert.False(editor.HasEditor); Assert.False(editor.CanExport); Assert.Empty(editor.Items);
        }
        finally { Directory.Delete(root, true); }
    }
    [AvaloniaFact]
    public async Task ClosingMacroEditorCanCancelOrKeepIncompleteDraft()
    {
        string root = Path.Combine(Path.GetTempPath(), "lighthub-macro-close-" + Guid.NewGuid().ToString("N"));
        var window = new MainWindow(true, new TransactionStore(root));
        try
        {
            window.Show(); await window.Initialization;
            window.Model.Macros.New(); window.Model.Macros.Add("down");
            var view = window.FindControl<MacroEditorView>("MacroEditor")!;
            var cancel = view.ResolveEditsAsync(); Dispatcher.UIThread.RunJobs();
            var dialog = Assert.Single(window.OwnedWindows);
            dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, window.Model.L["Cancel"])).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.False(await cancel); Assert.True(window.Model.Macros.Dirty);
            var closed = new TaskCompletionSource(); window.Closed += (_, _) => closed.TrySetResult();
            window.Close(); Dispatcher.UIThread.RunJobs(); Assert.True(window.IsVisible);
            var closingDialog = Assert.Single(window.OwnedWindows);
            closingDialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, window.Model.L["MacroSaveDraft"])).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var draft = Assert.Single(new MacroLibrary(root).List().Entries); Assert.True(draft.IsDraft); Assert.Equal("down", Assert.Single(draft.Macro.Events).Kind);
        }
        finally { window.Model.Macros.DiscardEdits(); window.Close(); Directory.Delete(root, true); }
    }
    [AvaloniaFact]
    public async Task MacroDraftSurvivesReopenAndDoesNotTouchDeviceState()
    {
        string root = Path.Combine(Path.GetTempPath(), "lighthub-macro-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            var workspace = new Workspace(true, new TransactionStore(root)); workspace.LoadDemo();
            var baseline = workspace.Snapshot!.Copy(); var editor = workspace.Macros;
            await editor.RefreshAsync(); editor.New(); editor.Name = "Incomplete"; editor.Add("down");
            Assert.False(editor.CanSave); Assert.True(editor.CanSaveDraft); Assert.False(editor.CanBind);
            Assert.Throws<InvalidOperationException>(editor.New);
            Assert.True(await editor.SaveAsync(true)); Assert.False(editor.Dirty);
            var reopened = new MacroEditorViewModel(new MacroLibrary(root), new Strings(false));
            Assert.True(await reopened.RefreshAsync()); reopened.Open(Assert.Single(reopened.Items));
            Assert.False(reopened.CanSave); Assert.Contains("Release", reopened.Validation);
            reopened.Add("up"); Assert.True(reopened.CanSave);
            Assert.True(await reopened.SaveAsync()); Assert.True(reopened.CanExport);
            Assert.Contains("not bound", reopened.EditorState); Assert.Empty(reopened.Validation);
            Assert.True(baseline.SameState(workspace.Snapshot)); Assert.False(workspace.Dirty);
            Assert.False(Directory.Exists(Path.Combine(root, "backups")));
            Assert.False(Directory.Exists(Path.Combine(root, "transactions")));
            Assert.Single(new MacroLibrary(root).List().Entries);
            workspace.DisposeSession();
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task MacroEditorPreservesConflictDraftAndReordersWithoutSaving()
    {
        string root = Path.Combine(Path.GetTempPath(), "lighthub-macro-ui-conflict-" + Guid.NewGuid().ToString("N"));
        try
        {
            var library = new MacroLibrary(root); var original = library.Save(MacroTests.CopyMacro(), null);
            var editor = new MacroEditorViewModel(library, new Strings(false)); await editor.RefreshAsync(); editor.Open(Assert.Single(editor.Items));
            editor.Name = "My edits"; editor.SelectedStep = editor.Steps[1]; editor.MoveStep(-1);
            Assert.Equal(6, editor.Steps[0].Key.Usage); editor.MoveStep(1);
            library.Save(original.Macro with { Name = "External" }, original.Revision);
            Assert.False(await editor.SaveAsync()); Assert.True(editor.Dirty); Assert.Equal("My edits", editor.Name);
            Assert.Equal("External", Assert.Single(library.List().Entries).Macro.Name);
            editor.SetLanguage(new Strings(true)); Assert.True(editor.Dirty); Assert.Equal("My edits", editor.Name);
            Assert.True(await editor.DuplicateAsync()); Assert.Equal(2, library.List().Entries.Count);
            Assert.NotEqual(original.Key, editor.Selected!.Entry.Key);
            editor.SelectedStep = editor.Steps[1]; editor.RemoveStep(); Assert.False(editor.Valid);
            editor.DiscardEdits(); Assert.True(editor.Valid); Assert.Equal(4, editor.Steps.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaTheory]
    [InlineData(960, 680, false)]
    [InlineData(960, 680, true)]
    [InlineData(1120, 800, false)]
    [InlineData(1120, 800, true)]
    public async Task MacroEditorButtonsAndLayoutsWorkOffline(int width, int height, bool chinese)
    {
        string root = Path.Combine(Path.GetTempPath(), "lighthub-macro-layout-" + Guid.NewGuid().ToString("N"));
        MainWindow? window = null;
        try
        {
            window = new MainWindow(true, new TransactionStore(root)) { Width = width, Height = height };
            window.Show(); await window.Initialization; window.FindControl<ComboBox>("Language")!.SelectedIndex = chinese ? 1 : 0;
            var tabs = window.FindControl<TabControl>("Tabs")!; tabs.SelectedItem = window.FindControl<TabItem>("MacrosTab");
            Dispatcher.UIThread.RunJobs();
            var view = window.FindControl<MacroEditorView>("MacroEditor")!;
            view.FindControl<Button>("NewMacro")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            view.FindControl<TextBox>("MacroName")!.Text = chinese ? "复制选中文本" : "Copy selection";
            view.FindControl<Button>("AddMacroDown")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.False(view.FindControl<Button>("SaveMacro")!.IsEnabled);
            window.Model.Macros.SelectedStep!.Key = MacroKey.All.Single(k => k.Usage == 0xe0);
            window.Model.Macros.Add("down"); window.Model.Macros.SelectedStep!.Key = MacroKey.All.Single(k => k.Usage == 6);
            window.Model.Macros.Add("up"); window.Model.Macros.Add("up"); window.Model.Macros.SelectedStep!.Key = MacroKey.All.Single(k => k.Usage == 0xe0);
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.FindControl<Button>("SaveMacro")!.IsEnabled); Assert.False(view.FindControl<Button>("BindMacro")!.IsEnabled);
            Assert.True(await window.Model.Macros.SaveAsync()); Dispatcher.UIThread.RunJobs();
            Assert.False(window.FindControl<Button>("HardwareApply")!.IsVisible);
            var nameBox = view.FindControl<TextBox>("MacroName")!; nameBox.Focus(); Assert.True(nameBox.IsFocused);
            window.KeyPress(Avalonia.Input.Key.Tab, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Tab, null);
            window.KeyRelease(Avalonia.Input.Key.Tab, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Tab, null);
            Assert.True(view.FindControl<Button>("AddMacroDown")!.IsFocused);
            foreach (var control in new Control[] { view.FindControl<Button>("SaveMacro")!, view.FindControl<ListBox>("MacroList")!, view.FindControl<ListBox>("MacroSteps")! })
            {
                var point = control.TranslatePoint(default, window); Assert.NotNull(point);
                Assert.True(control.Bounds.Width >= 70 && control.Bounds.Height >= 30, $"{control.Name}: {control.Bounds}");
                Assert.InRange(point.Value.X, 0, width - control.Bounds.Width + 1);
                Assert.InRange(point.Value.Y, 0, height - control.Bounds.Height + 1);
            }
            if (Environment.GetEnvironmentVariable("LIGHTHUB_SCREENSHOTS") is { } output)
            {
                Directory.CreateDirectory(output); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using (var bitmap = window.CaptureRenderedFrame()) { Assert.NotNull(bitmap); bitmap.Save(Path.Combine(output, $"macros-{width}x{height}-{(chinese ? "zh" : "en")}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default); }
                var scroll = view.FindControl<ScrollViewer>("MacroEditorScroll")!; scroll.Offset = new Vector(0, scroll.Extent.Height);
                Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using var capability = window.CaptureRenderedFrame(); Assert.NotNull(capability); capability.Save(Path.Combine(output, $"macro-capabilities-{width}-{(chinese ? "zh" : "en")}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
        }
        finally { if (window is not null) { window.Model.Macros.DiscardEdits(); window.Close(); } Directory.Delete(root, true); }
    }
}
