#!/bin/bash
# 验证 access.img 里有没有网卡固件。
# access.img 是救援分区镜像：dd 出来的 FAT32/整盘镜像，里面有 squashfs rootfs。
IMG="/mnt/d/work/KARLS_LIGHT_ACCESS/KARL'S LIGHT ACCESS FOR WINDOWS/app/image/access.img"

echo "== file type =="
file "$IMG"

echo "== 分区表 =="
fdisk -l "$IMG" 2>/dev/null | sed -n '1,12p'

# 尝试整镜像直挂（有些镜像无分区表，直接就是文件系统）
mkdir -p /mnt/kla-check
umount /mnt/kla-check 2>/dev/null

mount_try() {
  local dev="$1"
  mount -o ro,loop,offset=${2:-0} "$dev" /mnt/kla-check 2>/dev/null && return 0
  mount -o ro,loop "$dev" /mnt/kla-check 2>/dev/null && return 0
  return 1
}

if mount_try "$IMG"; then
  echo "== 整镜像挂载成功 =="
else
  # 带分区表：算第一个分区偏移
  OFF=$(fdisk -l "$IMG" 2>/dev/null | awk '/^\/mnt/ && $2=="*"?{print $3} /^\/mnt/ && $1!~"Device"{if($2=="*")print $3*512; else print $2*512}' | head -1)
  if [ -n "$OFF" ] && [ "$OFF" -gt 0 ] 2>/dev/null; then
    if mount -o ro,loop,offset=$OFF "$IMG" /mnt/kla-check 2>/dev/null; then
      echo "== 分区1(偏移 $OFF)挂载成功 =="
    else
      echo "挂载失败"; exit 1
    fi
  else
    echo "未找到分区偏移"; exit 1
  fi
fi

echo "== 根目录 =="
ls /mnt/kla-check | head -20

echo "== 找 squashfs / live 目录 =="
find /mnt/kla-check -maxdepth 3 -name '*.squashfs' -o -maxdepth 3 -name 'filesystem.squashfs' 2>/dev/null | head

SQ=$(find /mnt/kla-check -maxdepth 4 -name 'filesystem.squashfs' 2>/dev/null | head -1)
if [ -z "$SQ" ]; then
  # 直接搜任意 squashfs
  SQ=$(find /mnt/kla-check -maxdepth 4 -name '*.squashfs' 2>/dev/null | head -1)
fi
echo "squashfs = [$SQ]"

if [ -n "$SQ" ]; then
  mkdir -p /mnt/kla-sq
  umount /mnt/kla-sq 2>/dev/null
  if mount -o ro,loop "$SQ" /mnt/kla-sq 2>/dev/null; then
    echo "== 固件目录统计 =="
    ls /mnt/kla-sq/lib/firmware/ 2>/dev/null | wc -l
    echo "== 网卡固件抽查 =="
    for d in iwlwifi rtlwifi rtl_nic ath10k ath11k ath12k brcm mediatek; do
      n=$(ls /mnt/kla-sq/lib/firmware/$d 2>/dev/null | wc -l)
      echo "$d : $n 个文件"
    done
    echo "== 无线工具 =="
    ls /mnt/kla-sq/sbin/wpa* /mnt/kla-sq/usr/sbin/wpa* 2>/dev/null | head -3
    echo "== 内核 virtio/iwlwifi 模块 =="
    K=$(ls -d /mnt/kla-sq/lib/modules/*/ 2>/dev/null | head -1)
    echo "kernel: $K"
    [ -n "$K" ] && find "$K" -name 'iwlwifi.ko*' -o -name 'virtio_net.ko*' 2>/dev/null | head -4
    umount /mnt/kla-sq
  else
    echo "squashfs 挂载失败"
  fi
fi

umount /mnt/kla-check 2>/dev/null
echo "== done =="
