# PRO2 手柄助手 v2.5

让 Nintendo Switch 2 Pro 手柄无线连接 Windows，用于 Steam，或切换为 Pro1、Xbox、PS5 兼容手柄。

## 本次更新

- **自动休眠**：在“设置”中勾选“闲置自动休眠”，可选 5 / 10 / 15 / 20 / 30 / 60 分钟，默认关闭。闲置后手柄熄灯休眠，虚拟手柄保持在线；按手柄按键唤醒后自动重连。
- **Win10 连接引导**：首页增加一键优化入口。按提示授权后，自动备份参数、配置蓝牙、尝试重启适配器并实测速率；高级入口保留手动测试和恢复。
- **界面调整**：设置页更紧凑，默认窗口下显示常用选项和新增休眠功能。

## 自动休眠配对提醒

**启用自动休眠后，软件会把本机写入手柄的配对记录，可能挤掉原来保存的 NS2（Nintendo Switch 2）主机记录。若回到 NS2 时连不上，重新与主机配对即可。**

## 下载与使用

- [标准版 Windows EXE](https://github.com/LeonChrome/XinHeLianSheng-Pro2-Bridge/releases/download/pro2-v2.5/PRO2ControllerAssistant-v2.5-win-x64.exe)
- [兼容启动版](https://github.com/LeonChrome/XinHeLianSheng-Pro2-Bridge/releases/download/pro2-v2.5/PRO2ControllerAssistant-v2.5-ui-compatible-win-x64.exe)：标准版启动即退出、窗口不显示时使用，手柄功能相同。

先从托盘退出旧助手，再打开新 EXE，旧设置继续沿用。支持 Windows 10 2004 及以上 / Windows 11 x64；内置 .NET、后端和 USBIP 安装器，首次使用仍需按提示安装驱动。

目标是真实 133Hz，达到 60Hz 也允许游玩；不同系统、蓝牙适配器和驱动不保证同样速率。Win10 优化会修改全局 BLE 默认参数，可能使其他蓝牙设备暂时断开，必要时需重启 Windows；支持从高级入口恢复。

保留四模式、背键 / C 键、震动、体感、电量和游戏按键图标管理。完整离线回归通过 768,402 项断言；本次打包核验未追加手柄实测，具体休眠与唤醒表现以本机使用结果为准。

[完整使用说明](https://github.com/LeonChrome/XinHeLianSheng-Pro2-Bridge/blob/main/README.md)
