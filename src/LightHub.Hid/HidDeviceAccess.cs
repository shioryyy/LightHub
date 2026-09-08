using LightHub.Application;
using LightHub.Core;

namespace LightHub.Hid;

public sealed class HidDeviceAccess(DeviceEndpoint endpoint, string root) : IDeviceAccess
{
    public IDisposable Acquire() => new DeviceLease(endpoint.PhysicalKey, root);
    public OnboardDevice Open() => Hardware.Open(endpoint);
    public void CheckCompetition() => Hardware.CheckCompetingSoftware();
}
