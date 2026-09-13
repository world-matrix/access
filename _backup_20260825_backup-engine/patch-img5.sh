#!/bin/bash
# 给 access.img 打补丁 v5：server.py v5 版
#   · copyout ProcessPool→ThreadPool（跨进程进度丢失修）
#   · dd conv=fsync 去（巨量写放大修）
#   · dd iflag count_bytes+skip_bytes 去
#   · dd stderr 逐字节→4KB 缓冲
set -e
IMG="/mnt/d/work/KARLS_LIGHT_ACCESS/KARL'S LIGHT ACCESS FOR WINDOWS/app/image/access.img"
SRC="/mnt/d/work/KARLS_LIGHT_ACCESS/kla/live/config/includes.chroot/opt/kla"
WORK=/root/img-patch5
MNT=/mnt/kla-img5
OUT_SQ="$WORK/filesystem-new.squashfs"

echo "v5 patch start (server.py copyout ThreadPool + dd fsync remove + 4KB buf)"

rm -rf "$WORK"; mkdir -p "$WORK" "$MNT"
umount "$MNT" 2>/dev/null || true

echo "=== 1. ro 挂载取 squashfs ==="
mount -o ro,loop "$IMG" "$MNT"
ls -la "$MNT/live/" 2>/dev/null | head
cp "$MNT/live/filesystem.squashfs" "$WORK/filesystem.squashfs"
SRC_SZ=$(stat -c %s "$WORK/filesystem.squashfs")
echo "  source squashfs size: $SRC_SZ"
umount "$MNT"

echo "=== 2. unsquashfs ==="
unsquashfs -q -d "$WORK/root" "$WORK/filesystem.squashfs"
BEFORE=$(md5sum "$WORK/root/opt/kla/server.py" | cut -d' ' -f1)
AFTER=$(md5sum  "$SRC/server.py"               | cut -d' ' -f1)
echo "  md5 in img  : $BEFORE"
echo "  md5 expected: $AFTER"
if [ "$BEFORE" = "$AFTER" ]; then
  echo "  ! server.py 已相同，本次补丁其实不需要。"
  exit 2
fi

echo "=== 3. 替换 server.py ==="
cp -f "$SRC/server.py" "$WORK/root/opt/kla/server.py"
cmp -s "$SRC/server.py" "$WORK/root/opt/kla/server.py" && echo "  OK server.py" || { echo "  FAIL server.py mismatch after copy"; exit 1; }

echo "=== 4. 语法自检 ==="
python3 -m py_compile "$WORK/root/opt/kla/server.py" && echo "  server.py syntax OK"

echo "=== 5. 重压 squashfs (xz -b 1024K -Xbcj x86) ==="
mksquashfs "$WORK/root" "$OUT_SQ" -comp xz -b 1024K -Xbcj x86 \
    -no-exports -noappend -quiet
ls -la "$OUT_SQ"

echo "=== 6. rw 挂载写回 access.img ==="
mount -o rw,loop "$IMG" "$MNT"
OLD="$MNT/live/filesystem.squashfs"
OLD_SZ=$(stat -c %s "$OLD")
NEW_SZ=$(stat -c %s "$OUT_SQ")
echo "  old size: $OLD_SZ, new size: $NEW_SZ"
if [ "$NEW_SZ" -gt "$OLD_SZ" ]; then
  echo "  WARN: new sq is larger than old; using rm+cp (ext4 should have slack if available)"
fi
rm -f "$OLD"
cp "$OUT_SQ" "$OLD"
md5sum "$OUT_SQ" | awk '{print $1"  live/filesystem.squashfs"}' > "$MNT/live/filesystem.squashfs.md5"
sync
cmp -s "$OUT_SQ" "$OLD" && echo "  writeback compare OK" || { echo "  FAIL: writeback cmp failed"; exit 1; }
ls -la "$MNT/live/"
umount "$MNT"

echo "PATCH_V5_DONE"
