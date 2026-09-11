# 本地宏编辑 / Local macro editing

当前开发版提供离线编辑；宏尚不能绑定鼠标或执行。This development build edits macros locally; it cannot bind or run them yet.

## 使用

1. 打开“宏”页，不需要先连接设备。点击“新建宏”，输入名称。
2. 添加“按下”“释放”或“延时”。选中步骤可改按键/动作/延时、上移下移、复制或删除。支持标准控件的 Tab、方向键和空格/Enter 导航；无需录制真实输入。
3. 比如 Ctrl+C：Left Ctrl 按下 → C 按下 → C 释放 → Left Ctrl 释放。Left/Right 是键盘左右修饰键，不是鼠标左右键。
4. 修复步骤下方的校验提示后，“保存到宏库”才可用。缺少释放等未完成序列可以“保留草稿”；草稿独立标记，不能导出为可执行宏。
5. “复制宏”保存独立 ID 的副本；“导入副本”始终分配新 ID，避免覆盖现有宏。“导出”仅用于已保存的有效宏，输出一个自包含 `.lhmacro` 文件。
6. “移入回收区”保留可找回的原文件。“回收区”中选择并找回；若原位置已存在另一个版本，会阻止覆盖。仍有草稿的宏先处理草稿；未知预设格式导致引用无法核实时也会阻止回收。

文件被其他进程修改时保存会拒绝覆盖，并保留编辑。可先“复制宏”保存有效内容或“保留草稿”，再刷新并重新打开原项。关闭软件、创建其他宏或打开其他项前，未保存编辑会提供保留草稿、放弃或取消。

宏编辑不改变现有 DPI、板载槽、按键映射或设备备份。启动和导入不会执行任何宏。执行能力区域显示“尚未验证/未实现”；容量未知，不承诺关闭软件后此宏有效。板载模式与软件执行均未开放，相关按钮保持禁用。

## English quick start

Open **Macros → New macro** without connecting a device. Add key down, key up and delay steps; select a step to edit its key/action or move, copy and remove it. Every pressed key must have a matching release. Save valid sequences to the library, or keep incomplete work as a clearly marked draft. Imports and duplication create new IDs. Export is available for saved, valid macros. Recycle and recover are local file operations with conflict and reference checks.

**Saved in library** means local persistence only. It does not mean bound, active, written to onboard memory or runnable. Neither onboard macro execution nor a software runner is currently supported.

## 文件与限制

使用现有 LightHub 用户数据根；已有 LightHubCommunity 数据根仍沿用，以免破坏历史恢复路径。通常 Windows 为 `%LOCALAPPDATA%/LightHub`。`--demo` 使用独立临时目录，适合查看界面，不用于长期保存宏。

| 路径 | 用途 |
| --- | --- |
| `macros/<uuid>.lhmacro` | 已校验宏，沿用 LocalMacro schemaVersion 1 |
| `macro-drafts/<uuid>.lhdraft` | 可不完整草稿，加上来源文件摘要；不作为可执行宏导入 |
| `macro-trash/<recycle-uuid>.lhmacro` 或 `.lhdraft` | 回收文件，内容保留原 ID；不自动永久删除 |
| `macro-library.lock` | 进程间宏库租约；程序退出后释放 |

单文件最多 2 MiB、JSON 深度 32、256 个事件；单次延时 1—10000 ms，总显式延时最多 30 秒，同时最多六个非修饰键。产品限制不是设备可执行能力。宏/草稿不含其他宏或程序的可执行引用；未知字段、重复字段或更高版本拒绝导入及覆盖。

格式见 [macro-v1](schemas/macro-v1.schema.json) 和 [macro-draft-v1](schemas/macro-draft-v1.schema.json)。原始设备备份 `LightHub/2` 和 `.lhpreset` v1 没有修改。未来预设语义宏引用需要新格式和单独迁移。

## 触发检查（开发诊断）

```text
LightHub.Cli list
LightHub.Cli trigger-inspect <endpoint-id>
```

该命令只查找候选 HID++ 功能及可用的控制元数据，使用接收器租约；不改变 diversion、模式、映射或板载数据，不捕获按键、不注入输入。输出中的 `executionVerified: false` 是预期结果。查询错误与固件明确返回“不存在”分别记录。

本机 GPW1 的结果和后续验证边界见[开发批次记录](validation/macros-20260912.md)。Raw Input 的事件识别与原动作抑制需分别验证，不能把收到事件等同于能够替换按键。
