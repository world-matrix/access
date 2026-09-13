#!/bin/bash
# 在 Debian 13 (trixie) 或 WSL2 Debian 中构建 KLA 救援 ISO。
#
# 注意：live-build 不能在 /mnt/d 这类 DrvFs 挂载点上构建——需要扩展属性、
# 设备节点和 chroot 语义，DrvFs 都不支持。build.ps1 会先把配置复制到
# WSL 原生文件系统再调用本脚本。
set -euo pipefail

BUILD_DIR="${KLA_BUILD_DIR:-$HOME/kla-build}"
OUT_NAME="kla-rescue-amd64.hybrid.iso"

if [ "$(id -u)" -ne 0 ]; then
    echo "需要 root 权限（live-build 要 chroot）。请用 sudo 运行。" >&2
    exit 1
fi

case "$BUILD_DIR" in
    /mnt/*) echo "错误：构建目录不能在 $BUILD_DIR（DrvFs 不支持 chroot）。" >&2; exit 1 ;;
esac

echo "==> 检查构建依赖"
MISSING=()
for pkg in live-build debootstrap xorriso squashfs-tools; do
    dpkg -s "$pkg" >/dev/null 2>&1 || MISSING+=("$pkg")
done
if [ ${#MISSING[@]} -gt 0 ]; then
    echo "    安装缺失的包: ${MISSING[*]}"
    apt-get update
    apt-get install -y "${MISSING[@]}"
fi

echo "==> 构建目录: $BUILD_DIR"
cd "$BUILD_DIR"

# 改了包列表/钩子后要重跑 chroot 阶段，但没必要重下几百 MB 的包。
#   KLA_CLEAN=1       lb clean —— 删掉 chroot/ 和 binary/，**保留** cache/，
#                     重建时包直接从本地缓存装，几分钟就好。改包列表用这个。
#   KLA_CLEAN=purge   lb clean --purge —— 连缓存一起删，全部重下。
#                     只有怀疑缓存本身坏了才用。
case "${KLA_CLEAN:-0}" in
    purge)
        echo "==> lb clean --purge（连包缓存一起删，会重新下载全部）"
        lb clean --purge
        ;;
    1)
        echo "==> lb clean（保留包缓存）"
        lb clean
        ;;
    *)
        ;;
esac

echo "==> lb config"
chmod +x auto/config
chmod +x config/hooks/normal/*.hook.chroot 2>/dev/null || true
lb config

# 上一轮跑完留下的 kla-rescue-*.iso 必须先挪开，否则下面那个 ./*.iso 通配
# 会把它一起匹配上——而且 'k' 排在 'live-image' 的 'l' 前面，head -n1 拿到的
# 正是这个**旧**文件，于是 mv 把它往自己身上搬，报 "are the same file"。
# 实测踩过：构建本身 P: Build completed successfully，却因为这行退出 1。
#
# 挪走而不是删掉：万一这次构建失败，上一版 ISO 还是唯一能用的产物。
# 成功之后才删。.prev 后缀不匹配 *.iso，通配从此干净。
if [ -f "$OUT_NAME" ]; then
    echo "==> 上一版 ISO 挪到 $OUT_NAME.prev（构建成功后删除）"
    mv -f "$OUT_NAME" "$OUT_NAME.prev"
fi
rm -f "$OUT_NAME.sha256"

echo "==> lb build（首次约 20–40 分钟，取决于网速）"
lb build 2>&1 | tee build.log

# 排除自己的产出名，只认 live-build 刚吐出来的那个
ISO=$(ls -1 ./*.iso 2>/dev/null | grep -v "$OUT_NAME" | head -n1 || true)
if [ -z "$ISO" ]; then
    echo "构建失败：没有产出 ISO。检查 $BUILD_DIR/build.log" >&2
    exit 1
fi

mv -f "$ISO" "$OUT_NAME"
sha256sum "$OUT_NAME" > "$OUT_NAME.sha256"
rm -f "$OUT_NAME.prev"

# ---------------------------------------------------------------------
# 体积预算核算
#
# KLA_RESCUE 分区只需要装下 squashfs + 内核 + initrd，不需要装下整个 ISO
# （ISO 还含 EFI 引导镜像、memtest、isolinux 等，那些要么进 ESP 要么不用）。
# 所以这里单独算「上盘体积」，并对着 1 GB 预算给结论。
# ---------------------------------------------------------------------
echo
echo "==> 体积预算核算（目标：上盘 ≤ 1 GB）"

BUDGET_BYTES=$((1024 * 1024 * 1024))
ONDISK=0
LIVE_DIR="$BUILD_DIR/binary/live"

printf '    %-28s %10s\n' "组件" "大小"
printf '    %-28s %10s\n' "----------------------------" "----------"
for f in "$LIVE_DIR/filesystem.squashfs" "$LIVE_DIR/vmlinuz" "$LIVE_DIR/initrd.img"; do
    [ -f "$f" ] || continue
    SZ=$(stat -c %s "$f")
    ONDISK=$((ONDISK + SZ))
    printf '    %-28s %10s\n' "$(basename "$f")" "$(numfmt --to=iec --format='%.1f' "$SZ")"
done

if [ "$ONDISK" -eq 0 ]; then
    echo "    警告：找不到 $LIVE_DIR 下的产物，跳过核算" >&2
else
    printf '    %-28s %10s\n' "----------------------------" "----------"
    printf '    %-28s %10s\n' "上盘合计" "$(numfmt --to=iec --format='%.1f' "$ONDISK")"
    PCT=$((ONDISK * 100 / BUDGET_BYTES))
    if [ "$ONDISK" -le "$BUDGET_BYTES" ]; then
        echo "    ✅ 占 1 GB 预算的 ${PCT}%，余量 $(numfmt --to=iec --format='%.1f' $((BUDGET_BYTES - ONDISK)))"
    else
        echo "    ❌ 超出 1 GB 预算 $(numfmt --to=iec --format='%.1f' $((ONDISK - BUDGET_BYTES)))（${PCT}%）" >&2
        echo "       照 build.log 里 [kla-slim] 的分项报告继续砍，或把分区调到 1.5 GB。" >&2
    fi
fi

echo
echo "==> 完成"
ls -lh "$OUT_NAME"
cat "$OUT_NAME.sha256"

