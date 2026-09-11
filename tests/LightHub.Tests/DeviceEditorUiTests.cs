using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using LightHub.Desktop;
using Xunit;

namespace LightHub.Tests;

public sealed partial class UiTests
{
    [AvaloniaFact]
    public void DpiCompactionPreservesActiveValueAndRequiresExplicitRemovedStageReplacement()
    {
        var model = new Workspace(true); model.LoadDemo(); model.Stages[1].Enabled = false;
        var draft = model.Draft(); Assert.Equal(1, model.RuntimeStageForDraft()); Assert.Equal(1600, draft.Dpi[model.RuntimeStageForDraft()!.Value]);
        Assert.Equal(1, draft.DefaultIndex); model.ValidateDraft();
        model.LoadProfile(1); model.Stages[2].Enabled = false; model.DefaultIndex = 1;
        Assert.True(model.NeedsReplacementStage); Assert.Throws<InvalidDataException>(model.ValidateDraft);
        model.ReplacementStage = 3; model.ValidateDraft(); Assert.Equal(2, model.RuntimeStageForDraft()); Assert.Equal(3200, model.Draft().Dpi[2]);
        model.Stages[0].Value = 400.5m; Assert.Throws<InvalidDataException>(model.Draft);
        model.LoadProfile(1); model.DisposeSession();
    }
    [AvaloniaFact]
    public void LanguageChangePreservesSparseAndInvalidDpiEdits()
    {
        var model = new Workspace(true); model.LoadDemo(); model.Stages[1].Enabled = false;
        model.SetLanguage(!model.L.Chinese);
        Assert.False(model.Stages[1].Enabled); Assert.Equal(1600, model.Stages[2].Value); Assert.Equal(1, model.RuntimeStageForDraft());
        model.Stages[2].Value = null; model.DefaultIndex = 1;
        model.SetLanguage(!model.L.Chinese);
        Assert.Null(model.Stages[2].Value); Assert.Equal(1, model.DefaultIndex); Assert.False(model.Stages[1].Enabled); Assert.True(model.Dirty);
        Assert.Throws<InvalidDataException>(model.ValidateDraft); model.LoadProfile(1); model.DisposeSession();
    }
    [AvaloniaFact]
    public async Task DeviceEditorShowsDraftTargetAndKeepsReadingIndependent()
    {
        var window = new MainWindow(true);
        try
        {
            window.Show(); await window.Initialization; var model = window.Model;
            Assert.False(model.Dirty); Assert.True(model.Stages[2].IsCurrent); Assert.Contains("1", model.SaveTargetLabel);
            var source = model.ActiveSlotText;
            model.LoadProfile(2); Assert.Contains("2", model.SaveTargetLabel); Assert.Equal(source, model.ActiveSlotText);
            Assert.All(model.Stages, s => Assert.False(s.IsCurrent)); Assert.False(model.Dirty);
            model.DefaultIndex = 1; Assert.True(model.Dirty); Assert.Contains(model.L["DefaultStage"], model.Stages[1].StateLabel);
            model.LoadProfile(1); model.SelectedButton = model.Buttons[4];
            window.FindControl<TabControl>("Tabs")!.SelectedIndex = 1;
            window.FindControl<ComboBox>("Language")!.SelectedIndex = model.L.Chinese ? 0 : 1;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(5, model.SelectedButton!.Number); Assert.False(model.Dirty);
            var group = window.FindControl<ComboBox>("ActionGroup")!;
            Assert.True(group.SelectedIndex == 0, $"Group index={group.SelectedIndex}, items={group.Items.Count}, selected={group.SelectedItem}, VM={model.ButtonAssignment.SelectedGroup}, source={group.DataContext?.GetType().Name}");
        }
        finally { window.Model.LoadProfile(1); window.Close(); }
    }
    [AvaloniaFact]
    public void ActionSearchDoesNotChangeDraftAndSelectedAssignmentDoes()
    {
        var workspace = new Workspace(true); workspace.LoadDemo(); workspace.SetLanguage(false);
        workspace.SelectedButton = workspace.Buttons[3];
        var panel = workspace.ButtonAssignment; var before = panel.CurrentAction;
        panel.Search = "Ctrl+C"; panel.Group = 2;
        Assert.Single(panel.Options); Assert.False(workspace.Dirty); Assert.Same(before, panel.CurrentAction);
        panel.Assign(panel.Options[0]); Assert.True(workspace.Dirty); Assert.Equal("Ctrl+C", panel.CurrentAction!.Label);
        Assert.Equal("Left rear side", workspace.SelectedButton.Position);
        workspace.SelectedButton = workspace.Buttons[0]; Assert.True(panel.PrimaryProtected);
        panel.Assign(workspace.Buttons[0].Options.First(o => o.Label == "Ctrl+C"));
        Assert.Equal("Left click", panel.CurrentAction!.Label);
        workspace.DisposeSession();
    }
}
