# Pico 2 W 跨平台配对说明

## 统一操作

1. 先把 Pro2 通过 Type-C 数据线接到背挂 Pico 2 W，并打开背挂电源。
2. 用实体模式键，或长按 `GL + GR + ZL + ZR + A/B/X/Y` 1.5 秒切换模式：
   - `A`：PS5 / DualSense
   - `B`：PS5 Edge
   - `X`：Xbox BLE
   - `Y`：Switch 1 Pro
3. 当前模式从未保存过主机时，会持续开放配对，不会在一分钟后由固件自动关闭。
4. 当前模式已有旧主机时，会优先重连旧主机。要换手机、电脑或主机，长按 `GL + GR + ZL + ZR + Plus + Minus` 2 秒。
5. Pro2 轻微低频震动两下，表示“当前模式的旧配对已清除，配对已重新开放”。随后在目标设备上完成配对。

组合键只清除当前无线模式的主机记录。例如在 PS5 模式重新配对，不会删除 Xbox、Edge 或 Switch 1 模式保存的设备。

## Windows 10 / 11 PC

- Xbox 模式：打开“设置 > 蓝牙和设备 > 添加设备 > 蓝牙”，选择 `Xbox Wireless Controller`。
- PS5 模式：选择 `Wireless Controller`。
- PS5 Edge 模式：选择 `DualSense Edge Wireless Controller`。
- 如果列表中保留着同名旧设备，先在 Windows 中“删除设备”，再使用重新配对组合键。
- 正常 PS5 / Edge 配对不应要求输入密码。若蓝牙适配器或驱动退回传统 PIN 窗口，只输入 `0000`；这是固件保留的兼容密码，不要尝试 `1234`。
- Windows 能完成系统配对不代表每个游戏都原生支持该身份；必要时使用 Steam Input。

## Android

打开“设置 > 已连接的设备 > 配对新设备”，选择当前模式名称。Xbox BLE、PS5 和 Edge 均可尝试，具体按键、陀螺仪和震动支持取决于 Android 版本与游戏。

## macOS / iPhone / iPad

打开系统蓝牙设置，选择当前模式名称。Apple 系统官方支持 Xbox Bluetooth、DualSense 和 DualSense Edge 控制器，但具体游戏支持的按键、震动和运动功能可能不同。

## Nintendo Switch 1

1. 切换到 Switch 1 Pro 模式。
2. Switch 主界面进入“手柄 > 更改握法/顺序”。
3. 首次配对或换主机时使用重新配对组合键；页面保持打开，等待出现 `Pro Controller`。

## Xbox 主机与 PS5 主机

- **Xbox 主机不支持**：本项目的 Xbox 身份使用 Bluetooth Low Energy；Xbox 主机手柄链路使用 Xbox Wireless，并不是同一种无线协议。
- **实体 PS5 主机不支持**：官方 DualSense 首次连接 PS5 需要控制器通过 USB 与主机完成配对。本项目 Pico 原生 USB 当前仅用于刷机和诊断，不提供 DualSense USB 身份与主机认证。
- 名称中的“PS5 / Edge 模式”表示面向 PC、Android 和 Apple 设备模拟相应蓝牙 HID 身份，不等于已经获得 Sony 主机认证。

## 配对迟迟不成功

1. 确认 Pro2 已通过 Pico USB Host 正常提供实时输入。
2. 在目标设备中删除同名旧控制器。
3. 长按重新配对组合键 2 秒，确认 Pro2 震动两下。
4. 关闭再打开目标设备蓝牙页面后重新扫描。
5. 不要同时让附近多台已经保存该控制器的设备保持蓝牙开启，否则旧主机可能先接走当前身份。
