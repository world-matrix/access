#!/bin/bash
# 给 access.img（ext4 镜像）里的 squashfs 打补丁：替换 server.py/app.js/index.html
set -e
IMG="/mnt/d/work/KARLS_LIGHT_ACCESS/KARL'S LIGHT ACCESS FOR WINDOWS/app/image/access.img"
SRC="/mnt/d/work/KARLS_LIGHT_ACCESS/kla/live/config/includes.chroot/opt/kla"
WORK=/root/img-patch
MNT=/mnt/kla-img
OUT_SQ="$WORK/filesystem-new.squashfs"

rm -rf "$WORK"; mkdir -p "$WORK" "$MNT"
umount "$MNT" 2>/dev/null || true

echo "=== 1. 只读挂载，取出 squashfs ==="
mount -o ro,loop "$IMG" "$MNT"
cp "$MNT/live/filesystem.squashfs" "$WORK/filesystem.squashfs"
ls -la "$WORK/filesystem.squashfs"
umount "$MNT"

echo "=== 2. 解包 squashfs ==="
unsquashfs -q -d "$WORK/root" "$WORK/filesystem.squashfs"

echo "=== 3. 替换 3 个文件 ==="
cp -f "$SRC/server.py"      "$WORK/root/opt/kla/server.py"
cp -f "$SRC/www/app.js"     "$WORK/root/opt/kla/www/app.js"
cp -f "$SRC/www/index.html" "$WORK/root/opt/kla/www/index.html"
cmp -s "$SRC/server.py" "$WORK/root/opt/kla/server.py" && echo "server.py 替换一致"
cmp -s "$SRC/www/app.js" "$WORK/root/opt/kla/www/app.js" && echo "app.js 替换一致"

echo "=== 4. 语法自检（chroot 里的 python3）==="
"$WORK/root/usr/bin/python3" -m py_compile "$WORK/root/opt/kla/server.py" && echo "server.py 语法 OK"

echo "=== 5. 重压 squashfs（xz）==="
mksquashfs "$WORK/root" "$OUT_SQ" -comp xz -b 1024K -Xbcj x86 \
    -no-exports -noappend -quiet
ls -la "$OUT_SQ"

echo "=== 6. rw 挂载写回 ==="
mount -o rw,loop "$IMG" "$MNT"
rm -f "$MNT/live/filesystem.squashfs"
cp "$OUT_SQ" "$MNT/live/filesystem.squashfs"
md5sum "$OUT_SQ" | awk '{print $1"  live/filesystem.squashfs"}' > "$MNT/live/filesystem.squashfs.md5"
sync
# 校验写回
cmp "$OUT_SQ" "$MNT/live/filesystem.squashfs" && echo "写回校验：一致"
ls -la "$MNT/live/"
umount "$MNT"

echo "PATCH_DONE"
