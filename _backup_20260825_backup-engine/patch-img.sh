#!/bin/bash
# 直接给 access.img 里的 squashfs 打补丁：替换 server.py / app.js / index.html。
# 比完整 lb build 快得多（几分钟 vs 不可用），风险面小（只换 3 个文件）。
set -e
IMG="/mnt/d/work/KARLS_LIGHT_ACCESS/KARL'S LIGHT ACCESS FOR WINDOWS/app/image/access.img"
SRC="/mnt/d/work/KARLS_LIGHT_ACCESS/kla/live/config/includes.chroot/opt/kla"
WORK=/root/img-patch
OUT_SQ="$WORK/filesystem-new.squashfs"

echo "=== 0. 备份原 img（同盘，1.5GB）==="
cp -f "$IMG" "$IMG.bak-wifi"
ls -la "$IMG.bak-wifi"

echo "=== 1. 从 img 里拷出 squashfs ==="
rm -rf "$WORK"; mkdir -p "$WORK"
mcopy -i "$IMG" ::/live/filesystem.squashfs "$WORK/filesystem.squashfs"
ls -la "$WORK/filesystem.squashfs"

echo "=== 2. 解包（只解需要的部分，-e 过滤）==="
# 全解要几分钟+占几 GB；用 -e 只解 opt/kla 附近 + 保留全目录结构没法局部打包。
# mksquashfs 不能从"部分树"重建完整镜像——所以还是全解。
unsquashfs -q -d "$WORK/root" "$WORK/filesystem.squashfs"

echo "=== 3. 替换 3 个文件 ==="
cp -f "$SRC/server.py"          "$WORK/root/opt/kla/server.py"
cp -f "$SRC/www/app.js"         "$WORK/root/opt/kla/www/app.js"
cp -f "$SRC/www/index.html"     "$WORK/root/opt/kla/www/index.html"
echo "替换后 md5："
md5sum "$SRC/server.py" "$WORK/root/opt/kla/server.py"
md5sum "$SRC/www/app.js" "$WORK/root/opt/kla/www/app.js"

echo "=== 4. 语法自检（用 chroot 里的 python3）==="
"$WORK/root/usr/bin/python3" -m py_compile "$WORK/root/opt/kla/server.py" && echo "server.py 语法 OK"
node -c "$WORK/root/opt/kla/www/app.js" 2>/dev/null || echo "（无 node，跳过 JS 检查）"

echo "=== 5. 重压 squashfs（xz，与 lb --compression xz 一致）==="
mksquashfs "$WORK/root" "$OUT_SQ" -comp xz -b 1024K -Xbcj x86 \
    -no-exports -noappend -quiet
ls -la "$OUT_SQ"

echo "=== 6. 写回 img ==="
# mdel + mcopy：FAT32 内替换
mdel -i "$IMG" ::/live/filesystem.squashfs
mcopy -i "$IMG" "$OUT_SQ" ::/live/filesystem.squashfs
mdel -i "$IMG" ::/live/filesystem.squashfs.md5 2>/dev/null || true
(cd /tmp && find . -maxdepth 0 >/dev/null; md5sum "$OUT_SQ" | awk '{print $1}' > /tmp/sq.md5)
mcopy -i "$IMG" /tmp/sq.md5 ::/live/filesystem.squashfs.md5

echo "=== 7. 校验 ==="
mdir -i "$IMG" ::/live | head -10
mcopy -i "$IMG" ::/live/filesystem.squashfs "$WORK/verify.squashfs"
cmp "$OUT_SQ" "$WORK/verify.squashfs" && echo "IMG 写回校验：一致"
md5sum "$WORK/verify.squashfs"

echo "PATCH_DONE"
