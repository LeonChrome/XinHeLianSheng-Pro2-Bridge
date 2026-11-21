# 许可与第三方组件

本项目自有源码沿用根目录 Apache-2.0 许可证。第三方代码、程序、图片和固件保留各自的版权及许可，不能据根目录许可推断整个分发包均可商用。

- 蓝牙转发后端使用 BlueKitchen BTstack，固定版本 `e38553977a25fb0b55b383c72c289be0975f422c`。其许可第 4 条限定个人、非商业用途。本次完整构建及附带的 BTstack 源码按免费个人、非商业用途分发；商用需另行解决上游许可。[BTstack 原许可](source/final-three-in-one/experiments/windows-wireless-pro1/Notices/BTstack-LICENSE.txt)。
- 一代 Switch 后端采用 mizuyoukanao/btstack 的固定提交 `4b49cb955d08dc80a82673083fc0463cbc0c2540`，内含 btkeyLib。原生通用后端也使用该提交的 btkeyLib 示例。上游许可允许保留版权及免责条款的源码和二进制分发，要求分发、使用和修改均限个人利益、非商业且非营利。两套 BTstack 的原许可随发布包提供。[上游源码](https://github.com/mizuyoukanao/btstack/tree/4b49cb955d08dc80a82673083fc0463cbc0c2540) · [btkeyLib 原许可](source/final-three-in-one/experiments/windows-wireless-pro1/native/third_party/btkeylib/LICENSE)
- VIIPER 后端保留 GPL-3.0 许可，修改后的对应源码位于 `source/final-three-in-one/tools/viiper/haptic-src`。[原许可](source/final-three-in-one/tools/viiper/haptic-src/LICENSE.txt)。
- USBIP 安装器按仓库保留的[原许可](source/final-three-in-one/tools/usbip-win2/v0.9.7.7/LICENSE.txt)分发。程序不会替换系统蓝牙文件或取消驱动签名检查。
- .NET、WPF、Go、PicoSwitch2、控制器协议参考及产品图来源见 [Notices](source/final-three-in-one/experiments/windows-wireless-pro1/Notices)。
- Realtek 运行固件只装入控制器 RAM；原许可与来源随 EXE 内置运行包及源码依赖包分发。

本项目独立开发，与 Nintendo、Sony、Microsoft、Valve 或蓝牙硬件厂商无隶属或背书关系。
