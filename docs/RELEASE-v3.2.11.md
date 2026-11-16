# v3.2.11 · amiibo 格式校验修复

- 修复读完 600 字节后仍以“容量或 UID/BCC 校验”拒绝 amiibo 的兼容问题：旧版错误要求普通 NDEF NTAG215 的 `3E` 标记，未识别 amiibo 常见的 `F1 10 FF EE` 页 3 格式。
- 区分 amiibo 与普通 NTAG215 NDEF 格式。保留长度、元数据 UID、内容 UID、BCC0、BCC1 和已知 2KB 标签检查；数据按原样导出，不改 UID、BCC 或页 3 内容。
- 日志新增 `PRO2_NFC_VALIDATE`，将每项检查分别记录，失败提示具体原因；日志不包含实际 UID、签名或完整标签内容。
- 读卡窗口显示本次完整性校验结果，移除固定的“当前未完成本机实卡验收”界面文字。

本机 v3.2.10 已确认两轮按 84 字节分块收齐 600 字节，最后一块为 12 字节，其余为传输填充；本版修复最终格式判断，完整实卡导出仍待复测。

仍是无线只读实验：不支持 USB NFC、写卡、完整 2KB 读取或游戏自动 NFC 透传；完整性检查不等于真实性认证。

格式参考：[amiibo 读取实现](https://github.com/arnaldolima1/totk-recomp/blob/main/runtime/amiibo.c)、[TagMo 实卡页 3 记录](https://github.com/HiddenRamblings/TagMo/issues/121)。仅参考公开格式事实，未引入完整上游程序或密钥。

验证：932108 项离线断言通过，包含 amiibo / 普通 NDEF、84 字节分块、内容保持原样、UID 与 BCC 损坏拒绝、逐项诊断和取消清理。构建无警告、无错误；完整实卡导出仍待本版复测。
