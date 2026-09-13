#!/bin/bash
IMG="/mnt/d/work/KARLS_LIGHT_ACCESS/KARL'S LIGHT ACCESS FOR WINDOWS/app/image/access.img"
mkdir -p /mnt/kla-check /mnt/kla-sq
umount /mnt/kla-sq /mnt/kla-check 2>/dev/null
mount -o ro,loop "$IMG" /mnt/kla-check 2>/dev/null
mount -o ro,loop /mnt/kla-check/live/filesystem.squashfs /mnt/kla-sq 2>/dev/null

echo "== iwlwifi-*.ucode 数量 =="
ls /mnt/kla-sq/lib/firmware/ 2>/dev/null | grep -c '^iwlwifi-'
ls /mnt/kla-sq/lib/firmware/iwlwifi-*.ucode 2>/dev/null | head -4

echo "== 固件目录体积 =="
du -sh /mnt/kla-sq/lib/firmware 2>/dev/null

echo "== 网络工具 =="
ls -la /mnt/kla-sq/sbin/ip /mnt/kla-sq/usr/sbin/iw /mnt/kla-sq/usr/sbin/iwlist 2>/dev/null | awk '{print $NF}'

echo "== NetworkManager =="
ls /mnt/kla-sq/usr/sbin/NetworkManager 2>/dev/null || echo "无 NetworkManager"

echo "== 无线相关服务（systemd）=="
ls /mnt/kla-sq/etc/systemd/system/multi-user.target.wants/ 2>/dev/null | grep -i -E 'network|wpa' || echo "（无）"

umount /mnt/kla-sq /mnt/kla-check 2>/dev/null
echo "== done =="
