# Pro1 / Pro2 实体输入与 NFC

3.0 在现有四槽位、四种虚拟输出中增加实体 HID 输入，不替换 Pro2 BLE 路线。

- **Pro1 蓝牙**：Windows 先配对 Pro Controller，助手再打开传统蓝牙 HID。不是 BLE。
- **Pro1 USB**：数据线连接，执行 USB 握手、读取工厂校准，启用完整输入与陀螺仪。
- **Pro2 USB**：通过厂商 USB 命令接口初始化，再接收 HID `05` 输入；`02` 输出发送震动。厂商接口需已有 WinUSB 绑定；程序不会替换 HID、整只复合设备或蓝牙驱动。
- 四种输出保持 Pro2、Pro1、Xbox、PS5，每个槽位独立选择。Xbox 协议本身没有陀螺仪。

从首页 **输入 / NFC** 选择槽位和实体设备。回到首页或多手柄窗口选择输出模式。原 Pro2 BLE 仍从首页连接，也可在输入窗口切回。

Pro2 USB 请先接入助手，再启动 Steam。Steam 可能独占厂商 USB 接口；遇到拒绝访问时先正常退出 Steam 及其他手柄工具，不要因此重装驱动。v3.0.1 已兼容系统写入的单数／复数接口 GUID，并去掉本机固件不应答的可选 NFC 旁路初始化。

## 频率与功能边界

Pro2 USB 以真实约 250Hz 为目标，按实际 HID 输入测量；新接入路线尚待实机验收。Pro1 使用原生报告频率，不承诺真实 250Hz。不把三个 IMU 样本、ACK、NFC 响应、重复虚拟输出算作新按键报告。

日志分开记录输入、非输入报告、设备时钟变化、完全相同原包、P50/P95/最大间隔、队列丢弃和震动写入失败。单个短窗口的高数值不是长期稳定性证明。

Pro1 实体没有 Pro2 的背键和 C 键。背键/C 键映射仍适用于实体 Pro2 转换到四种输出。Pro1 使用只读工厂校准和三秒陀螺仪校准；不会写入控制器闪存。物理 Pro1 在 Windows 中仍可能被游戏直接看见；请在游戏/Steam 中选择正确的输出手柄，避免同时接收两个设备。

## 震动

Pro1 输出模式转回实体 Pro1：100% 强度时保留原生八字节 HD 震动；调整强度只修改幅度编码。实体 Pro2 USB 保留 Pro2 原始马达块。

跨代转换、Xbox 普通震动和 PS5 触觉仍使用现有转换链路；两代马达编码和频率控制不同，不能宣传为所有模式无损或与原生手柄完全等效。队列有界，发生丢弃和写入错误会记录。

## NFC / amiibo

本版实现 **Pro1 传统蓝牙只读实验**：开启 MCU、等待 NFC 就绪、检测标签、读取 135 页 NTAG215，检查长度、UID 与 BCC；可导出 540 字节 `.bin` 及读取信息。无标签、取消、超时和异常都尝试恢复 MCU 挂起和 `30` 游戏报告模式。

读卡期间仅暂停本槽位的游戏输入和震动，其他槽位保持运行。NFC 响应不计入游戏输入频率。程序不写标签、不写固件、不提供 amiibo 密钥，不声称通过真实性认证。

**Pro2 NFC、Pro1 USB NFC，以及虚拟手柄自动 NFC 透传尚未接入。** 这不代表硬件不支持 NFC。当前文件导出供支持此格式的模拟器自行载入，不等于主机或游戏自动识别。

## 验证

新增测试使用生产解析器和生产虚拟输入/震动循环，覆盖三种输入帧、四种输出、USB 无 ACK 的 ForceUSB、同一设备重复占用、取消/失败后的资源释放、输入源替换、NFC 成功/超时/取消与输入恢复。

回放不需要实体手柄；**回放通过不等于已完成全部硬件验收**。v3.0.1 在本机 Pro2 USB 完成约 30 秒真实输入测试：249.99Hz，P50 4.00ms、P95 4.02ms，解析失败 0。Pro1、实卡 NFC、各输出模式的长时间游戏仍需验证；完整验收还应记录震动压力与拔插恢复。

协议参考：[SDL Pro1 驱动](https://github.com/libsdl-org/SDL/blob/main/src/joystick/hidapi/SDL_hidapi_switch.c)、[SDL Pro2 驱动](https://github.com/libsdl-org/SDL/blob/main/src/joystick/hidapi/SDL_hidapi_switch2.c)、[Nintendo Switch 协议研究](https://github.com/dekuNukem/Nintendo_Switch_Reverse_Engineering)、[Switch 2 协议研究](https://github.com/ndeadly/switch2_controller_research)。实现保留在现有 C# 输入和 VIIPER 输出架构中；未引入其他项目的完整程序或内核驱动。
