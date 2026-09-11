# GPW1 启用验收程序

状态：已实现预检、事务编排和模拟测试；实机切换/恢复与物理效果仍待执行。普通设备目录尚未增加 Activate 或 EnableProfile 许可。

当前 GPW1 读取结果：BOT 74.02.0026，C539 接收器，mode 1，槽 1 启用、当前档位 2 / 1600 DPI；槽 2—5 停用。槽 2 已有通过编码预检的配置，默认 800 DPI。仅重复“启用当前槽”不足以验证备用槽闭环。

## 实现边界

- Activate 控制活动槽/模式切换，EnableProfile 独立控制目录启用标志。普通写配置权限不能借用为目录许可。
- SlotActivation.Prepare 只做本地预检：验证已有配置、有效 DPI/默认档位、普通动作和主键；不规范化或重写配置内容。
- 启用停用槽须显式意图，事务只写相应目录标志及 CRC，验证后才切换运行状态。失败不继续切换，不自动重试写入。
- 恢复目录启用标志同样需 EnableProfile 许可；恢复保持原始内存、模式、活动槽、档位和传感器 DPI。
- `ActivationTests` 覆盖两种权限不能互借、显式意图、仅运行状态切换、仅目录写入、完整恢复、不安全配置、冲突、部分写失败和恢复引用。

## 工程工具

`tools/LightHub.HardwareProbe` 不在桌面/CLI 发布包中。它仅对已具备普通写入证据的 Windows GPW1 / BOT 74.02.0026 / C539 组合临时开放本进程中的两项验收操作。该临时规则不是兼容证据，也不修改生产目录。

先在仓库根目录构建并只读预检：

```powershell
dotnet build tools/LightHub.HardwareProbe -c Release
dotnet run --no-build -c Release --project tools/LightHub.HardwareProbe -- plan ENDPOINT_ID 2
```

确认预检目标、恢复点和用户可配合后，再执行一次有界验收：

```powershell
dotnet run --no-build -c Release --project tools/LightHub.HardwareProbe -- activation ENDPOINT_ID 2 --execute-and-restore --hold-seconds 60
```

工具先创建私有恢复备份，切换后保留恢复引用；等待结束或 Ctrl+C 后尝试完整恢复。若失败或进程被强制终止，保留输出的备份路径并核对状态，不盲目重复测试：

```powershell
dotnet run --no-build -c Release --project tools/LightHub.HardwareProbe -- restore ENDPOINT_ID PRIVATE_BACKUP_PATH --execute
```

禁止测试时拔插或关电；正常关电持久性单独进行。强制终止不保证执行 finally；读回报告始终把 physicalInputVerified/powerCycleVerified 标为 false，需另附测试者实际观察。

## 物理与生命周期证据

1. 记录前后 DPI、活动槽、完整回读和恢复结果；实际移动鼠标并检查左右键/侧键，出现异常立刻终止保持并恢复。
2. 保存/启用已完整验证且静止后，单独检查退出软件后行为、正常开关电源后的保留；不在闪存提交期间断电。
3. 检查睡眠唤醒、接收器重连时是否只读、是否保留草稿及阻止过期写入。
4. 记录确切固件、连接、应用版本/源码、工具程序集摘要和测试者观察；未观察的项保持未验证。
5. 只有对应操作完整通过并审查后才更新生产能力目录；不以临时工具规则或一次回读取代物理与恢复证据。
