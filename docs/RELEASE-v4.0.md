# PRO2 手柄助手 v4.0

**[下载 Windows x64 EXE](https://github.com/LeonChrome/XinHeLianSheng-Pro2-Bridge/releases/download/pro2-v4.0/PRO2ControllerAssistant-v4.0-win-x64.exe)** · [对应源码与构建依赖](https://github.com/LeonChrome/XinHeLianSheng-Pro2-Bridge/releases/download/pro2-v4.0/PRO2ControllerAssistant-v4.0-source.zip)

## 一代 Switch 转发与 amiibo

- 在首页选 Pro2 USB 或 Pro1 USB 输入，再选“蓝牙转发至其他设备”与 Pro1 身份，即可使用内置的一代 Switch 转发组件。启动时只打开发射器列表中选定的设备实例。
- Pro2 USB 可在游戏请求时读取实体 amiibo，也可先点“读取实体 NFC”。Pro1 USB 可载入已有标签文件。支持 540 字节 NTAG215 快照及带 32 字节签名的 572 字节文件，可在界面查看标签就绪和传输进度。
- 一代 Switch 的读卡转发为只读。主机是否接受标签请以游戏画面为准。Realtek `0BDA:0852` 已做过手柄连接测试，其他标准 WinUSB 经典蓝牙适配器的兼容性取决于芯片和驱动。[连接与读卡步骤](https://github.com/LeonChrome/XinHeLianSheng-Pro2-Bridge/blob/pro2-v4.0/source/final-three-in-one/experiments/windows-wireless-pro1/INTEGRATED.md)

## 震动与日常操作

- 用 Pro2 模拟 Pro1 时，左右握把分别还原高低频和强弱变化。多段震动按原顺序发送，游戏关闭震动时两侧一起停振。USB 和 BLE 都使用这套转换；与原装 Pro1 的手感一致程度仍待同场景实测。
- 点最小化保留任务栏入口，点关闭收起到系统托盘。启用登录时启动后，下次登录 Windows 会静默进入托盘。
- 断连提示缩短，便于快速看清连接状态。

适用于 Windows 10 2004 及以上版本和 Windows 11 x64。升级时先从托盘退出旧助手，再运行新版 EXE。完整构建供个人非商业使用，源码包保留两套固定版本蓝牙依赖、匹配固件和原许可。[构建说明](https://github.com/LeonChrome/XinHeLianSheng-Pro2-Bridge/blob/pro2-v4.0/source/final-three-in-one/README.md) · [许可与依赖](https://github.com/LeonChrome/XinHeLianSheng-Pro2-Bridge/blob/pro2-v4.0/NOTICE.md)
