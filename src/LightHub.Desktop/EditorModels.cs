using System.ComponentModel;
using System.Runtime.CompilerServices;
using LightHub.Core;
using LightHub.Application;

namespace LightHub.Desktop;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; PropertyChanged?.Invoke(this, new(name)); return true; }
    protected void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
}
public sealed class DpiStage(int index, int value) : Observable
{
    public int Index { get; } = index;
    public string DpiLabel => $"DPI {Index}";
    private bool enabled = value != 0;
    private decimal? dpi = value == 0 ? 800 : value;
    public bool Enabled { get => enabled; set => Set(ref enabled, value); }
    public decimal? Value { get => dpi; set => Set(ref dpi, value); }
    private bool current;
    private string stateLabel = "";
    public bool IsCurrent => current;
    public string StateLabel => stateLabel;
    public void Describe(bool isCurrent, bool isDefault, Strings language)
    {
        Set(ref current, isCurrent, nameof(IsCurrent));
        Set(ref stateLabel, string.Join(" · ", new[] { isCurrent ? language["ReadStage"] : "", isDefault ? language["DefaultStage"] : "" }.Where(s => s.Length != 0)), nameof(StateLabel));
    }
}
public sealed record BindingOption(string Label, byte[] Bytes)
{
    public override string ToString() => Label;
    public static IReadOnlyList<BindingOption> All(Strings l)
    {
        string Pick(string en, string zh) => l.Chinese ? zh : en;
        List<BindingOption> b = [new(Pick("Left click", "左键"), [128, 1, 0, 1]), new(Pick("Right click", "右键"), [128, 1, 0, 2]), new(Pick("Middle click", "中键"), [128, 1, 0, 4]), new(Pick("Back", "后退"), [128, 1, 0, 8]), new(Pick("Forward", "前进"), [128, 1, 0, 16]), new("DPI +", [144, 3, 0, 0]), new("DPI -", [144, 4, 0, 0]), new(Pick("DPI cycle", "DPI 循环"), [144, 5, 0, 0]), new("DPI Shift", [144, 7, 0, 0]), new(l["Disabled"], [255, 0, 0, 0]), new("Ctrl+C", [128, 2, 1, 6]), new("Ctrl+V", [128, 2, 1, 25]), new("Ctrl+Z", [128, 2, 1, 29]), new(Pick("Play / pause", "播放 / 暂停"), [128, 3, 0, 205]), new(Pick("Mute", "静音"), [128, 3, 0, 226]), new(Pick("Volume +", "音量 +"), [128, 3, 0, 233]), new(Pick("Volume -", "音量 -"), [128, 3, 0, 234])];
        for (int i = 0; i < 26; i++) b.Add(new(((char)('A' + i)).ToString(), [128, 2, 0, (byte)(4 + i)]));
        for (int i = 1; i <= 12; i++) b.Add(new("F" + i, [128, 2, 0, (byte)(57 + i)]));
        b.Add(new("Enter", [128, 2, 0, 40])); b.Add(new("Escape", [128, 2, 0, 41])); b.Add(new("Space", [128, 2, 0, 44])); b.Add(new("Tab", [128, 2, 0, 43]));
        for (int i = 0; i <= 9; i++) b.Add(new(i.ToString(), [128, 2, 0, (byte)(i == 0 ? 39 : 29 + i)]));
        return b;
    }
}
public sealed class ButtonEditor : Observable
{
    public int Number { get; }
    public string Label { get; }
    public string Position { get; }
    public double MapX { get; }
    public double MapY { get; }
    private bool highlighted;
    public bool Highlighted { get => highlighted; set => Set(ref highlighted, value); }
    public bool Editable { get; }
    public System.Collections.ObjectModel.ObservableCollection<BindingOption> Options { get; }
    private BindingOption selected;
    public BindingOption Selected { get => selected; set => Set(ref selected, value); }
    public ButtonEditor(int number, byte[] raw, Strings l, DeviceControl? control = null)
    {
        Number = number;
        Position = control is not null ? l[control.LabelKey] : l["Button"] + " " + number;
        Label = control is not null ? number + " · " + Position : Position;
        if (control is not null) (MapX, MapY) = (control.X, control.Y);
        Editable = number != 1; Options = new(BindingOption.All(l));
        selected = Options.FirstOrDefault(b => b.Bytes.SequenceEqual(raw)) ?? new("Raw · " + Convert.ToHexString(raw), raw);
        if (!Options.Contains(selected)) Options.Insert(0, selected);
    }
}
public sealed record ProfileItem(int Sector, string Label) { public override string ToString() => Label; }
public sealed record BackupItem(StoredBackup Backup, Strings L)
{
    public string Name => Backup.Name;
    public string Path => Backup.Path;
    public string Title => $"{Backup.Metadata.Label} {Backup.Date.LocalDateTime:g} · {Backup.Model ?? L["UnknownBackup"]}".Trim();
    public string Details => $"{Backup.Bytes / 1024.0:F1} KB · {(Backup.Protected ? L["ProtectedBackup"] : Backup.Readable ? L["VerifiedBackup"] : L["InvalidBackup"])}";
    public override string ToString() => Title;
}
