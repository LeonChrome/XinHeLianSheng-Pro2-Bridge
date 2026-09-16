# 最终源码公开检查 / Final Source Publication Checks

检查日期 / Checked: 2026-09-16

这次只整理和公开现存工程，不修改固件、游戏输入路径或已发布 EXE。

This publication collects existing projects only. Firmware, gameplay input paths and released EXEs are unchanged.

## 通过项目 / Passed Checks

- 三合一控制中心、Pico 刷机工具、ESP V5.9.19 Manager、Windows V6.2.32 R2、独立 Switch 1 R4 Manager：五个 Debug 构建全部通过，0 警告、0 错误。
- Pico 刷机工具测试：16 项通过，failures=0。
- Windows 数据包映射测试：passed。
- Pico 协议测试：41 项通过，failures=0。
- Pico R41 配对契约测试对现存 R55 源码执行：13 项通过，failures=0。
- 公开清单不含运行日志、个人 BLE 设备记录、SDK、bin/obj 缓存。常见凭据格式扫描无匹配；这不是完整安全审计保证。
- ESP 两个后端现存的 VIIPER 源码内容一致，另行保存，与 Windows VIIPER 源码分开；保留原许可证。

All five Windows Debug builds passed with zero warnings/errors. Pico flasher tests passed (16), Pico protocol tests passed (41), pairing-contract tests against current R55 source passed (13), and the Windows packet-mapper test passed. Logs, personal BLE records, SDKs and generated caches are excluded. Common credential-pattern scanning found no matches; this is not a comprehensive security certification. Available ESP VIIPER source is preserved separately from the Windows variant, with original licenses.

## 检查边界 / Limits

没有进行新一轮真机配对、待机、震动、陀螺仪或主机兼容性验证。没有重新编译 Pico/ESP 全部固件或重新制作三合一 EXE。测试通过不等于已知蓝牙、震动或兼容性问题全部解决。

No new hardware pairing, idle, vibration, gyro or host-compatibility run was performed. Full Pico/ESP firmware and the bundled center EXE were not rebuilt. Software tests do not establish that all known Bluetooth, haptic or compatibility issues are fixed.

Pico 现存源码是 R55，已发布三合一内嵌的是 R40；版本来源限制见本目录 README。项目已停止更新，不承诺后续修复。

Available Pico firmware source is R55, while the released center embeds R40. See the README for provenance limits. The project is discontinued; no further fixes are promised.
