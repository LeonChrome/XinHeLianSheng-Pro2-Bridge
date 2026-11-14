# v3.2.1 · 虚拟手柄代码 19 修复入口

- 将 Windows 代码 19 单独识别为设备配置异常，停止按普通挂载丢失自动反复 detach / attach。
- 首页显示“修复虚拟手柄”，多手柄窗口可单独修复故障槽位。
- 点击并确认后请求管理员授权：核对当前槽位序列号、USBIP 归属和端口；备份设备配置；仅移除这一只虚拟设备实例，再重新挂载并检查 Windows 输入接口。不会仅凭命令返回成功显示已恢复。
- 保留实体连接、其他槽位、驱动包和共享类筛选器。不改蓝牙参数，不自动重启 Windows。
- 配置诊断可在首页展开；系统与蓝牙信息也会包含服务、驱动 INF、设备和类筛选器的只读信息，以及本次修复记录。虚拟输出故障时不再把“输入频率可用”写成“可以游玩”。

备份和日志保存在 `%ProgramData%\PRO2ControllerAssistant\VirtualDeviceRepair\操作编号`，包含 `before.reg`、`before.json`、操作日志和 `Restore.cmd`。重建失败可停止输出后重新进入游戏；恢复脚本用于重新扫描设备，然后在助手重新连接。无需手动导入 Enum 注册表备份。

代码 19 可能来自设备实例配置或共享筛选驱动。重建不能保证修复后者；若诊断列出服务未注册，应修复或卸载提供该筛选驱动的软件。程序不会盲删整个 UpperFilters / LowerFilters。蓝牙输入频率波动是另一条链路，不能用虚拟设备重建保证 133Hz。

本轮验证为离线协议回放、故障分类、身份隔离和界面检查；未在反馈电脑上复现并治愈代码 19。

验证结果：929641 项离线断言通过；原生 EXE 启动检查通过；错误修复命令参数返回 7，未进入管理器或设备操作。测试未执行真实设备移除。

参考：[微软代码 19 说明](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/cm-prob-registry)、[PnPUtil 命令与系统支持范围](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-command-syntax)。
