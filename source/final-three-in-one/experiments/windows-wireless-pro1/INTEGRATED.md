# PRO2 手柄助手 v3.5.0 · 统一蓝牙转发

首页直接选择：输入手柄 → 本机游玩或蓝牙转发 → 手柄身份 → 发射适配器。无需再开转发窗口。

- 本机：保留 Pro2、Pro1、Xbox、PS5 四种身份和现有 USBIP 输出。
- 蓝牙外发：Pro1 / Pro Controller、Xbox One S、PS5 / DualSense。三者使用独立的 Classic HID 描述符、报告及反馈解析，不只是更改蓝牙名称。原生 Pro2 BLE 外发未实现，对应按钮在转发时禁用。
- 输入仍共用四种选择：Pro2 BLE / USB、Pro1 蓝牙 / USB。USB 输入只需一只发射器；无线输入还需另一只 Windows 系统蓝牙。Pro1 无线输入需先由 Windows 配对并出现在输入列表中。
- 多个发射器时必须手动选择；只接管选中的精确实例，不会在拔出后自动换用别的设备。当前原生后端仅覆盖 Realtek 0BDA:0852、Actions 10D7:B012。列表可显示其他设备，但不代表可以接管。
- 点击“连接并转发”，按 Windows 要求批准所选设备的驱动准备。先备份后切换；停止、切回本机或退出时恢复系统驱动。现有受信任 WinUSB 包不足时会停止并说明原因，不自动下载未知驱动。

接收端名称：Pro1 为 `Pro Controller`，Xbox 为 `Xbox Wireless Controller`，PS5 为 `Wireless Controller`。切换身份或重开转发后，接收端可能需要忘记旧条目再配对。当前只保留进程内配对密钥，一次转发一个实体手柄。

Pro1 保留陀螺仪、原生 HD 震动转换及只读 NFC 快照。PS5 接入按键、摇杆、陀螺仪、软件触控手势和兼容马达反馈；尚未实现蓝牙触觉音频、自适应扳机和 PS5 主机认证。Xbox 接入按键、摇杆、独立 Guide 报告和普通双马达反馈，无陀螺仪或 NFC；Xbox 主机认证未实现。背键 / C 键、位置映射和震动强度沿用主程序配置。Xbox / PS5 不提供 amiibo 通道。

外发上限可选 66.7、133、250Hz，只消费真实输入，不用重复状态补足频率。USB 250Hz 不等于接收端必然收到 250Hz；HCI 完成回执也不是接收端或游戏端实测。Xbox 和 PS5 外发本轮完成离线检查，实际配对、系统识别及效果等待接收设备验收，不能承诺各设备即插即用。

日志：`%LOCALAPPDATA%\PRO2WirelessReceiverControlBoard\wireless_relay_logs\时间戳\`。首页“输入、反馈与 NFC”可打开。记录身份、原始输入、外发提交、HCI 回执、覆盖 / 丢弃、协议请求、反馈以及恢复结果，标签内容不写入运行日志。

原驱动备份：`%LOCALAPPDATA%\PRO2WirelessReceiverControlBoard\radio\时间戳\`。恢复失败时使用该目录的 `Restore-Bluetooth.cmd`。不替换系统蓝牙驱动文件、不取消签名检查、不导入系统配对密钥。

单个 EXE 包含所需运行组件。原有 3.4.1 启动就绪握手、10054 处理和选中设备错误 10/43 恢复保护继续保留。Pro1 / Pro2 NFC 读卡能力以现有实体输入支持范围为准；可载入 540 / 572 字节快照，格式与 UID/BCC 检查不等于真实性验证。

无线后端 BTstack 原许可具有个人、非商业用途限制。本构建按免费个人、非商业测试用途发布；原文见运行包 `BTstack-LICENSE.txt`，其他来源见 `Notices`。
