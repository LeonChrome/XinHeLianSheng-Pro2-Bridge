# PRO2 手柄助手 4.0 · 蓝牙转发

## 连接一代 Switch

1. 用 USB 数据线把 Pro2 或 Pro1 接到电脑。首页选择对应的 USB 输入，再点「蓝牙转发至其他设备」，选择「Pro1」。
2. 保留默认的一代 Switch 模式，在列表中选要用的蓝牙发射器。若显示需要准备，按提示授权；停止转发后，助手会恢复本轮临时切换的驱动。
3. 首次配对时，在 Switch 打开「手柄 → 更改握法／顺序」，用接电脑的手柄按 L＋R，再按 A。配对记录随所选发射器保存；后续可直接连接。

助手只打开你选中的 USB 设备实例。同型号插入两只时，请根据实例路径区分。已实测 Realtek 0BDA:0852。已就绪的标准 WinUSB 蓝牙 HCI 发射器可尝试，实际兼容性取决于是否支持经典蓝牙及主机所需命令。系统驱动、当前不可用和已知不兼容设备会显示原因。本机 Actions 10D7:B012 曾在加密后返回不支持的 HCI 命令 0x1408。

## 用实体 amiibo

用 Pro2 USB 连接后，在游戏里进入 amiibo 扫描，把卡贴稳在 Pro2 的 NFC 感应区。也可以展开首页「外发设置、反馈与 NFC」，点「读取实体 NFC」，提前读好卡再进游戏。若用 Pro1 有线输入，请载入已有标签文件。首次测试可在《塞尔达传说：旷野之息》启用 amiibo 能力，选中它并按 L。[任天堂操作说明](https://www.nintendo.com/en-gb/Support/Nintendo-Switch/How-to-Use-amiibo-Figures-Legend-of-Zelda-Breath-of-the-Wild--1510347.html)

- 「载入标签文件」接受 540 字节 NTAG215 快照，或包含额外 32 字节签名的 572 字节文件。助手校验标签长度、容量标记和 UID 校验字节。
- 「移除标签」让接收端恢复无卡状态。此模式供游戏读取标签使用，写入角色、注册昵称或保存战绩需要可写设备。
- 在 NFC 状态区查看卡是否就绪、主机是否发起读取、标签分块是否已发出。游戏接受结果以 Switch 画面为准。

## 本机与其他外发模式

本机游玩可选 Pro2、Pro1、Xbox 和 PS5 四种身份。蓝牙外发会在接收设备上分别显示为 `Pro Controller`、`Xbox Wireless Controller` 或 `Wireless Controller`，一次转发一只实体手柄。Xbox 支持双马达和 Guide；PS5 支持陀螺仪、软件触控手势和兼容马达反馈，蓝牙触觉音频、自适应扳机和主机认证尚未实现。amiibo 仅适用于 Pro1 外发。

## 诊断与恢复

本轮外发日志位于 `%LOCALAPPDATA%\PRO2WirelessReceiverControlBoard\wireless_relay_logs\时间戳\`。可在「外发设置、反馈与 NFC」打开。蓝牙驱动备份位于同一应用目录下的 `radio\时间戳\`。若 Windows 没有自动恢复该蓝牙设备，请使用对应目录的 `Restore-Bluetooth.cmd`。

v4.0 EXE 已包含转发组件。运行包随附 BTstack、btkeyLib 及其他组件的许可，含 BTstack 的构建限个人非商业用途。
