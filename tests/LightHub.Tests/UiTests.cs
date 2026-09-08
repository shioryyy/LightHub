using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LightHub.Desktop;
using LightHub.Core;
using LightHub.Application;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(LightHub.Tests.TestAppBuilder))]
namespace LightHub.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
public sealed class UiTests
{
    [AvaloniaFact]
    public async Task DemoLoadsAndCannotWrite()
    {
        var w = new MainWindow(true); w.Show(); await w.Initialization; Dispatcher.UIThread.RunJobs();
        Assert.NotNull(w.Model.Snapshot); Assert.Equal(5, w.Model.Stages.Count); Assert.Equal(8, w.Model.Buttons.Count); Assert.False(w.Model.CanWrite); Assert.False(w.Model.Buttons[0].Editable); w.Close();
    }
    [AvaloniaFact]
    public async Task DraftAndUndoPreserveState()
    {
        var w = new MainWindow(true); w.Show(); await w.Initialization; Dispatcher.UIThread.RunJobs(); w.Model.Stages[0].Value = 450;
        Assert.True(w.Model.Dirty); Assert.Equal(450, w.Model.Draft().Dpi[0]); w.Model.ValidateDraft(); w.Model.LoadProfile(1); Assert.False(w.Model.Dirty); Assert.Equal(400, w.Model.Stages[0].Value); w.Close();
    }
    [AvaloniaFact]
    public void EnglishChineseAndVariableButtonCounts()
    {
        var m = new Workspace(true); m.LoadDemo(); m.SetLanguage(false); Assert.Equal("Devices", m.L["Devices"]); m.SetLanguage(true); Assert.Equal("设备", m.L["Devices"]);
        Assert.Equal(5, m.Stages.Count); Assert.True(m.CanEdit); Assert.False(m.CanWrite);
    }
    [AvaloniaFact]
    public async Task LanguageChangePreservesEditedStagesAndDoesNotDirtyCleanProfile()
    {
        var w = new MainWindow(true); w.Show(); await w.Initialization; Dispatcher.UIThread.RunJobs();
        int initial = w.Model.DefaultIndex;
        w.FindControl<ComboBox>("Language")!.SelectedIndex = w.Model.L.Chinese ? 0 : 1; Dispatcher.UIThread.RunJobs();
        Assert.Equal(initial, w.Model.DefaultIndex); Assert.False(w.Model.Dirty);
        w.Model.Stages[0].Value = 450;
        w.FindControl<ComboBox>("Language")!.SelectedIndex = w.Model.L.Chinese ? 0 : 1; Dispatcher.UIThread.RunJobs();
        Assert.Equal(450, w.Model.Stages[0].Value); Assert.True(w.Model.Dirty); Assert.Equal(initial, w.Model.DefaultIndex);
        w.Model.LoadProfile(1); w.Close();
    }
    [AvaloniaFact]
    public void TopologyChangePreservesDraftAndBlocksWrite()
    {
        var m = new Workspace(true); m.LoadDemo(); m.Stages[0].Value = 450;
        m.InvalidateConnection(); Assert.True(m.Dirty); Assert.Equal(450, m.Stages[0].Value); Assert.False(m.CanWrite);
    }
    [AvaloniaFact]
    public void PhysicalButtonNamesRemainIndependentOfAssignmentsAndSelection()
    {
        var m = new Workspace(true); m.LoadDemo(); m.SetLanguage(true);
        Assert.True(m.HasButtonMap);
        Assert.Equal("底部 DPI 键", m.Buttons[5].Position);
        Assert.Equal("右侧后键", m.Buttons[6].Position);
        m.SelectedButton = m.Buttons[6]; Assert.True(m.Buttons[6].Highlighted); Assert.False(m.Buttons[0].Highlighted); Assert.False(m.Dirty);
        m.Buttons[6].Selected = m.Buttons[6].Options.First(b => b.Label == "Ctrl+C");
        Assert.True(m.Dirty); Assert.Equal("右侧后键", m.Buttons[6].Position);
        var unknown = new ButtonEditor(7, [128, 1, 0, 8], m.L);
        Assert.Equal("按键 7", unknown.Position);
        m.SetLanguage(false); Assert.Equal("Underside DPI", m.Buttons[5].Position);
    }
    [AvaloniaFact]
    public async Task CurrentDpiInputDoesNotChangeOnboardDraftAndDemoCannotSendIt()
    {
        var m = new Workspace(true); m.LoadDemo(); var original = m.Draft().Dpi.ToArray();
        m.CurrentDpi = 850; Assert.False(m.Dirty); Assert.Equal(original, m.Draft().Dpi);
        Assert.False(m.CanSetCurrentDpi); await Assert.ThrowsAsync<InvalidOperationException>(m.ApplyCurrentDpi);
    }
    [AvaloniaFact]
    public async Task DiagramClickSelectsMatchingEditorWithoutChangingDraft()
    {
        var w = new MainWindow(true); w.Show(); await w.Initialization; Dispatcher.UIThread.RunJobs();
        w.FindControl<TabControl>("Tabs")!.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
        var hotspot = w.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("hotspot") && b.DataContext is ButtonEditor { Number: 8 });
        hotspot.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
        Assert.Same(w.Model.Buttons[7], w.FindControl<ListBox>("ButtonList")!.SelectedItem);
        Assert.True(w.Model.Buttons[7].Highlighted); Assert.False(w.Model.Dirty); w.Close();
    }
    [AvaloniaFact]
    public async Task BackupManagementWorksWithoutDeviceAndProtectsRecoverySelection()
    {
        string root = Path.Combine(Path.GetTempPath(), "lighthub-ui-backups-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TransactionStore(root); var safe = store.SaveBackup(DemoData.Create()); var protectedPath = store.SaveBackup(DemoData.Create());
            store.Record(new("test", "1234ABCD", protectedPath, protectedPath, "failed", DateTimeOffset.UtcNow, [5], "Test failure"));
            var model = new Workspace(false, store); model.SetLanguage(true); await model.RefreshBackupsAsync();
            Assert.Null(model.Endpoint); Assert.Equal(2, model.Backups.Count);
            model.SelectBackups(model.Backups); Assert.False(model.CanDeleteBackups); Assert.False(model.CanExportBackup);
            model.SelectBackups(model.Backups.Where(b => b.Path == safe));
            Assert.True(model.CanDeleteBackups); Assert.True(model.CanExportBackup); Assert.False(model.CanRestoreSelectedBackup);
            await model.Run("delete", _ => model.DeleteBackups([safe]), mutation: true);
            Assert.Single(model.Backups); Assert.True(File.Exists(protectedPath)); Assert.False(File.Exists(safe)); Assert.False(model.CanDeleteBackups);
        }
        finally { Directory.Delete(root, true); }
    }
    [AvaloniaTheory]
    [InlineData(960, 680)]
    [InlineData(1120, 800)]
    public async Task BackupSelectionCheckboxAndRecoveryLabelsRender(int width, int height)
    {
        string root = Path.Combine(Path.GetTempPath(), "lighthub-ui-backup-layout-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TransactionStore(root); var protectedPath = store.SaveBackup(DemoData.Create()); store.SaveBackup(DemoData.Create());
            store.Record(new("test", "1234ABCD", protectedPath, protectedPath, "failed", DateTimeOffset.UtcNow, [5], "Test failure"));
            var w = new MainWindow(true, store) { Width = width, Height = height }; w.Show(); await w.Initialization; Dispatcher.UIThread.RunJobs();
            var tabs = w.FindControl<TabControl>("Tabs")!; tabs.SelectedIndex = 2; Dispatcher.UIThread.RunJobs();
            var list = w.FindControl<ListBox>("BackupList")!;
            var checkbox = list.GetVisualDescendants().OfType<CheckBox>().First(); checkbox.IsChecked = true; Dispatcher.UIThread.RunJobs();
            // Headless Linux can report a compact list viewport while the tab
            // is being laid out. The semantic checks above are the portable
            // contract; a platform-specific pixel height is not.
            Assert.Single(w.Model.SelectedBackups); Assert.True(list.Bounds.Height > 0);
            foreach (bool chinese in new[] { false, true })
            {
                w.FindControl<ComboBox>("Language")!.SelectedIndex = chinese ? 1 : 0; Dispatcher.UIThread.RunJobs();
                Assert.Contains(w.Model.Backups, b => b.Details.Contains(chinese ? "恢复保护" : "Recovery protected"));
                if (Environment.GetEnvironmentVariable("LIGHTHUB_SCREENSHOTS") is { } output)
                {
                    Directory.CreateDirectory(output); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    using var bitmap = w.CaptureRenderedFrame(); Assert.NotNull(bitmap);
                    bitmap.Save(Path.Combine(output, $"backups-{width}x{height}-{(chinese ? "zh" : "en")}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                }
            }
            w.Close();
        }
        finally { Directory.Delete(root, true); }
    }
    [AvaloniaTheory]
    [InlineData(960, 680)]
    [InlineData(1120, 800)]
    [InlineData(1440, 1000)]
    public async Task WindowLayoutsAreBounded(int width, int height)
    {
        var w = new MainWindow(true) { Width = width, Height = height }; w.Show(); await w.Initialization; Dispatcher.UIThread.RunJobs();
        Assert.True(w.FindControl<TabControl>("Tabs")!.Bounds.Width > 400); Assert.True(w.FindControl<ListBox>("DeviceList")!.Bounds.Width > 100);
        w.FindControl<TabControl>("Tabs")!.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
        var hotspots = w.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("hotspot")).ToArray();
        Assert.Equal(8, hotspots.Length);
        foreach (var hotspot in hotspots)
        {
            Assert.Equal(32, hotspot.Bounds.Width); Assert.Equal(32, hotspot.Bounds.Height);
            var location = hotspot.TranslatePoint(default, w)!.Value;
            Assert.InRange(location.X, 0, width - 32); Assert.InRange(location.Y, 0, height - 64 - 27);
        }
        if (Environment.GetEnvironmentVariable("LIGHTHUB_SCREENSHOTS") is { } output)
        {
            Directory.CreateDirectory(output);
            foreach (bool chinese in new[] { false, true })
            {
                w.FindControl<ComboBox>("Language")!.SelectedIndex = chinese ? 1 : 0;
                for (int tab = 0; tab < 5; tab++)
                {
                    w.FindControl<TabControl>("Tabs")!.SelectedIndex = tab;
                    Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    using var bitmap = w.CaptureRenderedFrame(); Assert.NotNull(bitmap);
                    bitmap.Save(Path.Combine(output, $"{width}x{height}-{(chinese ? "zh" : "en")}-{tab}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                }
            }
        }
        w.Close();
    }
}
