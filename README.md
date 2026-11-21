# PRO2 手柄助手

在 Windows 上用 Nintendo Switch 2 Pro（Pro2）或初代 Pro（Pro1）手柄玩游戏，也可以让电脑把手柄输入转发给一代 Switch。

**[下载 v4.0 · Windows x64](https://github.com/LeonChrome/XinHeLianSheng-Pro2-Bridge/releases/download/pro2-v4.0/PRO2ControllerAssistant-v4.0-win-x64.exe)** · [源码与构建依赖 ZIP](https://github.com/LeonChrome/XinHeLianSheng-Pro2-Bridge/releases/download/pro2-v4.0/PRO2ControllerAssistant-v4.0-source.zip) · [v4.0 更新说明](docs/RELEASE-v4.0.md)

适用于 Windows 10 2004 及以上版本和 Windows 11 x64。EXE 自带 .NET 和运行组件。升级时先从托盘退出旧版，再打开新版。

## 本机游玩

1. 在首页选择实体手柄与输入方式。Pro2 无线在助手中连接，Pro1 无线先与 Windows 配对。有线连接请使用 USB 数据线。
2. 选择“本机游玩”和游戏需要的手柄身份，再点连接。可选 Pro2、Pro1、Xbox 或 PS5，最多支持四个独立槽位。首次使用按提示安装 USBIP 驱动。用 Pro2 USB 玩 Steam 游戏时，请先连接助手再打开 Steam。
3. 在“键位映射”调整 ABXY、背键和 C 键。默认按实际位置映射，例如下方 B 对应 Xbox A 或 PS5 ×。可调节震动强度、摇杆死区，或校准体感。Xbox 身份仅提供其协议支持的输入和震动。

## 转发给一代 Switch

1. 用 USB 线将 Pro2 或 Pro1 接入电脑。在首页选择对应的 USB 输入、“蓝牙转发至其他设备”和 Pro1 身份。
2. 保留默认的一代 Switch 模式，选中用于发射的蓝牙适配器，按提示准备并启动转发。首次配对时在 Switch 打开“手柄 → 更改握法／顺序”，用接电脑的手柄按 L＋R，再按 A。
3. 需要 amiibo 时，在游戏中进入扫描，将实体卡贴在 Pro2 的 NFC 感应区。也可以在首页的“外发设置、反馈与 NFC”中读取实体卡，或载入已有的 540／572 字节 NTAG215 文件。Pro1 USB 输入使用文件方式。转发支持读取，游戏中的写卡功能需要可写设备。

一代 Switch 模式已在 Realtek `0BDA:0852` 上做过手柄连接测试。其他已就绪的标准 WinUSB 经典蓝牙 HCI 适配器可按界面提示尝试。普通外发另支持 Pro1、Xbox 和 PS5 身份，一次转发一只手柄；USB 输入需一只发射器，无线输入还需另一只系统蓝牙。准备发射器会暂时中断它原有的蓝牙连接。不同游戏的 amiibo 接受情况及不同接收设备的兼容性仍需实测。[转发、读卡与驱动恢复说明](source/final-three-in-one/experiments/windows-wireless-pro1/INTEGRATED.md)

## 窗口与托盘

点最小化后可从任务栏切回助手。点关闭按钮后，助手继续在托盘运行。启用登录时启动后，下次登录 Windows 会直接进入托盘。可从托盘恢复窗口或退出程序。

[源码构建](source/final-three-in-one/README.md) · [输入与 NFC](docs/physical-inputs.md) · [反馈问题](https://github.com/LeonChrome/XinHeLianSheng-Pro2-Bridge/issues) · [历史版本](https://github.com/LeonChrome/XinHeLianSheng-Pro2-Bridge/releases)

项目自有代码采用 Apache-2.0。含 BTstack／btkeyLib 的完整构建供个人非商业使用，第三方组件各自保留原许可。[许可与依赖](NOTICE.md)
