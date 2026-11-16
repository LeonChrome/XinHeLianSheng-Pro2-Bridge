# Acknowledgements

This project was inspired by community research around Nintendo-style controller compatibility on Windows and Steam.

Early experiments explored several Switch controller compatibility paths. The final V5.9 implementation is an ESP32-S3 bridge with BLE input from the real controller and native USB output to the host PC.

Thanks to the authors and maintainers of the open-source projects and references that made this work possible, including ESP-IDF, TinyUSB, SDL / Steam Input research, esptool, and community Switch controller compatibility notes.

This repository is independent and is not affiliated with Nintendo, Sony, Microsoft, Valve, Espressif, WCH, or related companies.

The Windows Pro2 BLE NFC read-only implementation follows the publicly captured native-reader sequence, framing and page ranges in [PicoSwitch2 v2.6.0](https://github.com/notsosaelin/PicoSwitch2/tree/v2.6.0), notably `tools/nfc_probe.ps1`, `firmware/src/nfc/ns2_nfc_mirror.c`, and the 2026-07-25 physical-tag experiment. These are reimplemented in the existing C# transport; Pico hardware and the upstream application are not required. PicoSwitch2 is licensed under Apache-2.0; its license is distributed in the portable package's `Notices/PicoSwitch2-LICENSE.txt`. Upstream hardware validation is distinct from validation of this Windows port.
