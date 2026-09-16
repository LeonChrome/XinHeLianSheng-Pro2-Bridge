# 新和联胜三合一控制中心

这是 Pico 2 W、ESP32-S3 和纯 Windows BLE/VIIPER 三条路线的统一测试入口。

## 设计边界

- 控制中心把每个后端追加在主 EXE 的数据区并逐项校验，不需要用户另外寻找对应 EXE，也不会因大型托管资源拖慢主界面启动。
- 三条实时链路仍保持独立，避免一次重构同时破坏固件刷写、BLE 和虚拟 USB。
- 同一时间只允许一个后端运行，防止 COM、Windows BLE、USBIP 或 VIIPER 被重复占用。
- 关闭后端采用正常窗口关闭请求，刷写时不会强制杀死进程。
- 第一次打开路线时会解包到 `%LOCALAPPDATA%\XinHeLianSheng\ControlCenter\backends`。

## 内置版本

- Pico 2 W：R40 候选，保留 PS5 / Edge 停振状态机，并按真实 Switch Pro 的 0x3F → 0x30 流程修正 NS1 按键布局、握手回复和连续查询队列。
- ESP32-S3：V5.9.19 候选，保留独立的 V5.9.18 Switch 1 R4 实验入口。
- 纯软件：V6.2.32 Stick Calibration Test R2。

当前是整合测试版，不会上传 GitHub Release，也不会替代各路线已经发布的正式版本。
