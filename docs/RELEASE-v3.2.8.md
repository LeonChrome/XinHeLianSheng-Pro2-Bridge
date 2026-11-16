# v3.2.8 · Pro2 NFC 只读实验

- 首页 **NFC 读卡…** 支持选择已连接的 Pro2 BLE 或 Pro1 蓝牙手柄。
- Pro2 BLE 可扫描 NTAG215、读取 UID 与 540 字节原始数据；可选附带签名字段导出 572 字节文件。
- 读卡支持进度、取消、超时、校验和清理；不会发送写卡、配对、固件或闪存修改命令。
- Pro2 保留 FD2 接收，读卡时暂时暂停该手柄震动、电量查询与休眠；不改连接间隔、输入报告格式或 133Hz descriptor。日志区分读卡前、期间、结束后的真实输入频率。
- 超时应答可能迟到且缺少事务编号，程序会停止本轮并要求重新连接，不盲目重试或导出不确定分块。

**范围：BLE 只读实验。** Pro2 USB、实体写卡、2KB 标签完整读取和游戏自动 NFC 透传尚未接入。导出的文件由支持它的模拟器自行载入。UID/BCC 检查不等于真实性认证。

协议依据：[PicoSwitch2 v2.6.0 主动读卡工具](https://github.com/notsosaelin/PicoSwitch2/blob/v2.6.0/tools/nfc_probe.ps1)、[真实 Pro2 读实体卡记录](https://github.com/notsosaelin/PicoSwitch2/blob/v2.6.0/docs/experiments/pro2-native-nfc-read-2026-07-25.md)。本次移植保持现有 Windows 架构，不需要外置 Pico。

验证：931805 项离线断言通过，包含 NFC 应答回放、异常与取消清理、正常初始化应答兼容检查；NFC 页面与最终 EXE 启动检查通过。未执行本机实体卡读取，不同固件与读卡期间输入稳定性仍待验证，不将上游实验直接当成本程序已完成实机验收。
