# 致谢

当前 Windows 程序及转发功能参考并使用以下项目，具体许可保留在源码和分发包中：

- VIIPER / usbip-win2：本机虚拟 USB 手柄与输出通道；修改后的 VIIPER 对应源码在仓库内提供。
- BlueKitchen BTstack：可选择适配器的原生蓝牙 HID 外发。固定版本 `e38553977a25fb0b55b383c72c289be0975f422c`，保留个人非商业许可。
- SDL、Switch 2 controller research、Nintendo Switch Reverse Engineering：输入、体感、震动及控制器协议参考。
- PicoSwitch2 v2.6.0：Pro2 BLE NFC 的公开主机记录、分块与读取流程参考；移植到现有 C# 传输，没有要求 Pico 硬件。[上游](https://github.com/notsosaelin/PicoSwitch2/tree/v2.6.0)及其 Apache-2.0 许可随包提供。
- Sony 维护的 Linux HID PlayStation 驱动、SDL Xbox 驱动、USB Host Shield 的 Xbox One S 解析记录：无线报告布局核对。
- DJm00n/ControllersInfo：设备捕获的 DualSense / Xbox One S 蓝牙 HID 描述符数据。
- .NET、WPF、Go、LLVM-MinGW，以及各自上游依赖。

手柄产品图用于辨识设备，来源与权利说明见 `source/final-three-in-one/experiments/windows-wireless-pro1/Notices/Controller-product-images-SOURCES.txt`。

原 ESP / Pico / 三合一目录仅为历史代码，不是当前 Windows 版本的运行前提。项目与 Nintendo、Sony、Microsoft、Valve 等厂商无隶属或背书关系。

[许可与第三方组件](NOTICE.md)
