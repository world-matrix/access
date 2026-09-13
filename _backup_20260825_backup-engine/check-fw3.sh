#!/bin/bash
IMG="/mnt/d/work/KARLS_LIGHT_ACCESS/KARL'S LIGHT ACCESS FOR WINDOWS/app/image/access.img"
mkdir -p /mnt/kla-check /mnt/kla-sq
umount /mnt/kla-sq /mnt/kla-check 2>/dev/null
mount -o ro,loop "$IMG" /mnt/kla-check 2>/dev/null
mount -o ro,loop /mnt/kla-check/live/filesystem.squashfs /mnt/kla-sq 2>/dev/null

echo "== BE200/Wi-Fi7 (gl 系列) 固件 =="
ls /mnt/kla-sq/lib/firmware/iwlwifi-gl-* 2>/dev/null || echo "无 —— BE200 无法驱动"

echo "== 各代 Intel 固件族 =="
for f in qu bz so a0 cc ty sc ma; do
  n=$(ls /mnt/kla-sq/lib/firmware/ 2>/dev/null | grep -c "^iwlwifi-.*$f.*ucode" || true)
  echo "  $f 系列: $n"
done
ls /mnt/kla-sq/lib/firmware/ | grep '^iwlwifi-' | sed 's/-[0-9]*\.ucode//' | sort -u | head -30

echo "== firmware-iwlwifi 包版本 =="
grep -A2 'Package: firmware-iwlwifi' /mnt/kla-sq/var/lib/dpkg/status 2>/dev/null | head -4

echo "== 内核模块签名（iwlwifi）=="
modinfo /mnt/kla-sq/lib/modules/*/kernel/drivers/net/wireless/intel/iwlwifi/iwlwifi.ko.xz 2>/dev/null | grep -E '^version|^filename' | head -3

umount /mnt/kla-sq /mnt/kla-check 2>/dev/null
echo "== done =="
