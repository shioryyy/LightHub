using HidSharp;

namespace LightHub.Hid;

public sealed class DeviceWatcher : IDisposable
{
    public event EventHandler? Changed;
    public DeviceWatcher() => DeviceList.Local.Changed += OnChanged;
    private void OnChanged(object? sender, DeviceListChangedEventArgs e) => Changed?.Invoke(this, EventArgs.Empty);
    public void Dispose() => DeviceList.Local.Changed -= OnChanged;
}
