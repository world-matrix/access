# KARL'S LIGHT FOR LINUX

Linux 版工程目录（规划中）。

## 现状

急救系统（KLA_RESCUE）本身运行在 Debian Linux 上，已经能同时服务
Windows 和 Linux 两类用户：

- 进入急救系统时选择救援目标（Windows / Linux），主界面按目标过滤工具；
- 「Linux 工具」分类提供 8 个常见适配性修复（更新中断 / WiFi 固件 /
  黑屏 nomodeset / 无声 / NVIDIA 回退 / 重建 GRUB / 重置密码 / fsck），
  实现方式是 chroot 进目标 Linux 分区执行标准修复命令；
- 硬件诊断（含显卡诊断）、文件救援、安装全新系统（Linux ISO 支持
  整盘 dd）、SSH、网络代理等工具平台无关。

构建产物在 `../kla/live/`（live-build 工程，两个平台共用）。

## 这个目录将来放什么

Linux 桌面侧的主程序（对标 Windows 版 KarlsLightAccess.exe）：
- 备份当前 Linux 系统（squashfs/tar 两条路线）
- 管理还原点
- 「重启进入 ACCESS」的 GRUB 条目管理

技术上可复用 Windows 版的分层：Storage / Deployment / Firmware 的
Linux 等价实现（lsblk / sgdisk / efibootmgr / grub-editenv）。
