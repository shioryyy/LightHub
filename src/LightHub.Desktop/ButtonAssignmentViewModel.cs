using System.ComponentModel;

namespace LightHub.Desktop;

public sealed record ActionGroupItem(int Index, string Label) { public override string ToString() => Label; }
public sealed class ButtonAssignmentViewModel : Observable
{
    private ButtonEditor? button;
    private string search = "";
    private int group;
    private ActionGroupItem[] groups;
    public Strings L { get; private set; }
    public ButtonAssignmentViewModel(Strings l) { L = l; groups = MakeGroups(); }
    public ButtonEditor? Button
    {
        get => button;
        set
        {
            if (button == value) return;
            if (button is not null) button.PropertyChanged -= ButtonChanged;
            button = value;
            if (button is not null) button.PropertyChanged += ButtonChanged;
            search = ""; group = 0;
            foreach (string property in new[] { nameof(Button), nameof(Search), nameof(Group), nameof(SelectedGroup), nameof(CurrentAction), nameof(Options), nameof(CanAssign), nameof(PrimaryProtected) }) Changed(property);
        }
    }
    public string Search { get => search; set { if (Set(ref search, value)) Changed(nameof(Options)); } }
    public int Group { get => group; set { if (value < 0 || value >= groups.Length || !Set(ref group, value)) return; Changed(nameof(SelectedGroup)); Changed(nameof(Options)); } }
    public IReadOnlyList<ActionGroupItem> Groups => groups;
    public ActionGroupItem SelectedGroup { get => groups[group]; set { if (value is not null && groups.Contains(value)) Group = value.Index; } }
    private ActionGroupItem[] MakeGroups() => new[] { "ActionsAll", "ActionsMouse", "ActionsKeyboard", "ActionsDpi", "ActionsMedia" }.Select((key, i) => new ActionGroupItem(i, L[key])).ToArray();
    public BindingOption? CurrentAction => Button?.Selected;
    public bool CanAssign => Button?.Editable == true;
    public bool PrimaryProtected => Button?.Editable == false;
    public IReadOnlyList<BindingOption> Options => Button?.Options.Where(o =>
        (string.IsNullOrWhiteSpace(Search) || o.Label.Contains(Search, StringComparison.CurrentCultureIgnoreCase)) &&
        (Group == 0 || Group == 1 && o.Bytes[0] == 128 && o.Bytes[1] == 1 || Group == 2 && o.Bytes[0] == 128 && o.Bytes[1] == 2 ||
         Group == 3 && o.Bytes[0] == 144 || Group == 4 && o.Bytes[0] == 128 && o.Bytes[1] == 3)).ToArray() ?? [];
    public void Assign(BindingOption option)
    {
        if (CanAssign && Button!.Options.Contains(option)) Button.Selected = option;
    }
    private void ButtonChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ButtonEditor.Selected)) { Changed(nameof(CurrentAction)); Changed(nameof(Options)); }
    }
    public void SetLanguage(Strings language) { L = language; groups = MakeGroups(); Changed(nameof(L)); Changed(nameof(Groups)); Changed(nameof(SelectedGroup)); Changed(nameof(Options)); }
}
