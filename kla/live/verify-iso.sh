#!/bin/bash
# ISO 产物核对。不需要真机也不需要虚拟机——直接翻 binary/ 树和 squashfs 内容，
# 把「能在构建机上确认的事」全确认掉，剩下的才留给真机验收。
#
# 注意这里用 here-string（<<<）而不是 `printf ... | grep -q`：
# 本脚本开了 pipefail，而 grep -q 命中后立刻退出，会给上游的 printf 一个
# SIGPIPE（退出码 141），于是整条管线被判定为失败——**命中反而报 FAIL**。
# 第一版就栽在这上面，十几个明明存在的文件全被报成缺失。
set -uo pipefail

B="${1:-/root/kla-build}"
ISO="$B/kla-rescue-amd64.hybrid.iso"
SQ="$B/binary/live/filesystem.squashfs"

PASS=0; FAIL=0
ok()   { echo "  [OK]   $*"; PASS=$((PASS+1)); }
bad()  { echo "  [FAIL] $*"; FAIL=$((FAIL+1)); }
info() { echo "  ---    $*"; }

echo "=== 1. Secure Boot 引导链 ==="
# --uefi-secure-boot auto 只是「能签就签」，必须实际确认签名的 shim 落进了
# ESP 镜像。bootx64.efi 应当是 shim（微软签名），再由它去验 grubx64.efi。
for f in EFI/boot/bootx64.efi EFI/boot/grubx64.efi; do
    if [ -f "$B/binary/$f" ]; then
        ok "$f ($(numfmt --to=iec --format='%.1f' "$(stat -c %s "$B/binary/$f")"))"
    else
        bad "$f 缺失"
    fi
done
if [ -f "$B/binary/EFI/boot/bootx64.efi" ]; then
    if grep -qa 'MokManager\|shim' "$B/binary/EFI/boot/bootx64.efi"; then
        ok "bootx64.efi 确认是 shim —— Secure Boot 开着也能启动"
    else
        bad "bootx64.efi 不像 shim，Secure Boot 下会被拒绝"
    fi
fi
# mmx64.efi = MokManager，只有「自己签名、需要用户注册 MOK」时才用得上。
# 这张 ISO 用的是 Debian 的微软签名 shim，走的是已受信任的链，不需要它。
# 但阶段 4 把 ACCESS 装进隐藏分区、若改用自签名内核，就必须带上 MokManager。
if [ -f "$B/binary/EFI/boot/mmx64.efi" ]; then
    ok "mmx64.efi（MokManager）在"
else
    info "mmx64.efi 不在 —— ISO 用微软签名链，不需要；阶段 4 若自签名则需补上"
fi

echo
if [ ! -f "$SQ" ]; then
    bad "找不到 $SQ，后续检查全部跳过"
    exit 1
fi
LIST=$(unsquashfs -l "$SQ" 2>/dev/null)
info "squashfs 条目数：$(wc -l <<< "$LIST")"

has() { grep -q "$1" <<< "$LIST"; }

echo
echo "=== 2. 关键工具是否在 squashfs 里 ==="
check_bin() {
    # 二进制可能在 /bin /sbin /usr/bin /usr/sbin 任意一处
    if grep -qE "squashfs-root/(usr/)?s?bin/$1\$" <<< "$LIST"; then
        ok "$1  — $2"
    else
        bad "$1 缺失  — $2"
    fi
}
check_bin wimlib-imagex  "还原 Windows 镜像（整个项目的关键假设）"
check_bin ntfs-3g        "挂载 NTFS 读写"
check_bin ntfsfix        "修 Fast Startup 留下的脏 NTFS"
check_bin rsync          "增量备份"
check_bin parted         "分区操作"
check_bin sgdisk         "GPT 操作"
check_bin smartctl       "硬盘健康诊断"
check_bin memtester      "内存诊断"
check_bin chromium       "UI 外壳"
check_bin Xorg           "X 服务"
check_bin python3        "本地 API 服务"
check_bin efibootmgr     "改启动项（ACCESS 自救的关键）"
check_bin lsblk          "枚举分区"
check_bin NetworkManager "网络栈"
check_bin nmcli          "server.py 靠它配网"
# trixie 的 NetworkManager 默认后端是 dhcpcd，不是老的 dhclient。
# 认死 dhclient 会误报——这里两个认一个就行。
if grep -qE 'squashfs-root/(usr/)?s?bin/(dhcpcd|dhclient|udhcpc)$' <<< "$LIST"; then
    ok "DHCP 客户端  — 取 IP"
else
    bad "没有任何 DHCP 客户端，有网卡也拿不到 IP"
fi
check_bin wpa_supplicant "无线认证"

echo
echo "=== 3. KLA 自己的文件 ==="
for p in opt/kla/server.py opt/kla/www/index.html opt/kla/www/app.js \
         opt/kla/www/app.css usr/local/bin/kla-session \
         etc/systemd/system/kla-ui.service \
         etc/systemd/system/multi-user.target.wants/kla-ui.service; do
    if has "squashfs-root/$p\$"; then ok "$p"; else bad "$p 缺失"; fi
done

echo
echo "=== 4. 中文字体（缺了 UI 全是豆腐块）==="
FONTS=$(grep -iE 'squashfs-root/usr/share/fonts/.*(noto.*cjk|wqy|droid.*fallback|source.*han)' <<< "$LIST")
if [ -n "$FONTS" ]; then
    ok "找到 CJK 字体 $(wc -l <<< "$FONTS") 个"
    sed 's|.*squashfs-root/|         |' <<< "$FONTS" | head -5
else
    bad "没有 CJK 字体，中文会显示成豆腐块"
fi

echo
echo "=== 5. 网卡固件抽查 ==="
# 无线固件是「进了 ACCESS 能不能联网」的分水岭，抽查主流几家。
for fw in iwlwifi ath11k ath10k rtw88 rtw89 mediatek brcm; do
    N=$(grep -c "squashfs-root/usr/lib/firmware/.*$fw" <<< "$LIST")
    if [ "$N" -gt 0 ]; then ok "$fw: $N 个文件"; else bad "$fw: 无"; fi
done
for fw in rtl_nic; do
    N=$(grep -c "squashfs-root/usr/lib/firmware/$fw" <<< "$LIST")
    if [ "$N" -gt 0 ]; then ok "有线 $fw: $N 个文件"; else bad "有线 $fw: 无"; fi
done

echo
echo "=== 6. 内核模块抽查 ==="
# 模块文件名用连字符不是下划线（xhci-pci.ko 而非 xhci_pci.ko）——
# modprobe 两种都认，但文件系统里只有连字符那一种。
for m in nvme.ko ntfs3.ko ext4.ko xhci-pci.ko usbhid.ko ahci.ko \
         usb-storage.ko squashfs.ko overlay.ko; do
    if has "squashfs-root/usr/lib/modules/.*/$m"; then ok "$m"; else bad "$m 缺失"; fi
done

echo
echo "=== 7. GRUB 菜单与热键 ==="
GCFG="$B/binary/boot/grub/grub.cfg"
if [ -f "$GCFG" ]; then
    ok "grub.cfg"
    grep -E 'menuentry|--hotkey' "$GCFG" | head -4 | sed 's/^/         /'
    if grep -q -- '--hotkey' "$GCFG"; then
        ok "grub.cfg 里已有 --hotkey（阶段 3 热键方案的前提）"
    else
        info "grub.cfg 没有 --hotkey —— 阶段 3 会自己写菜单，这里只是确认 GRUB 支持"
    fi
else
    bad "grub.cfg 缺失"
fi

echo
echo "=== 8. ISO 本体与 El Torito ==="
if [ -f "$ISO" ]; then
    ok "$(ls -lh "$ISO" | awk '{print $5}')  $(basename "$ISO")"
    if command -v xorriso >/dev/null 2>&1; then
        # 必须同时有 BIOS 和 UEFI 两个引导镜像，否则只能在一种固件上启动
        ET=$(xorriso -indev "$ISO" -report_el_torito plain 2>/dev/null)
        if grep -q 'BIOS' <<< "$ET"; then ok "含 BIOS 引导镜像"; else bad "无 BIOS 引导镜像"; fi
        if grep -q 'UEFI' <<< "$ET"; then ok "含 UEFI 引导镜像"; else bad "无 UEFI 引导镜像"; fi
    fi
else
    bad "找不到 ISO"
fi

echo
echo "=== 9. 体积预算 ==="
TOTAL=0
for f in "$B/binary/live/filesystem.squashfs" "$B/binary/live/vmlinuz" "$B/binary/live/initrd.img"; do
    [ -f "$f" ] || continue
    TOTAL=$((TOTAL + $(stat -c %s "$f")))
done
BUDGET=$((1024*1024*1024))
if [ "$TOTAL" -gt 0 ] && [ "$TOTAL" -le "$BUDGET" ]; then
    ok "上盘 $(numfmt --to=iec --format='%.1f' "$TOTAL")，占 1 GB 预算 $((TOTAL*100/BUDGET))%"
else
    bad "上盘 $(numfmt --to=iec --format='%.1f' "$TOTAL") 超出 1 GB 预算"
fi

echo
echo "================================"
echo "  通过 $PASS 项，失败 $FAIL 项"
echo "================================"
[ "$FAIL" -eq 0 ]
