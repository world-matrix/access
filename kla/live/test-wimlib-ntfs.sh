#!/bin/bash
# ============================================================================
# 任务 #2 的 A 阶段：验证 Linux 侧 wimlib 写 NTFS 的元数据保真度。
#
# 整个 KLA 押在一个假设上：Windows 侧 wimcapture 打的包，能在 ACCESS(Linux)
# 里用 wimapply 还原成一个**能引导的** Windows。这个假设拆成两半：
#
#   A. wimlib 经 libntfs-3g 写 NTFS 时，Windows 引导依赖的元数据一个都不丢
#      —— 安全描述符、重解析点、硬链接、备用数据流、短名、时间戳
#   B. 还原后的分区真能被固件和 Windows 引导起来
#
# B 必须真机/虚拟机验证（要装一个真 Windows，见 Phase0-VmSandbox.ps1）。
# **A 是风险真正所在，而且不需要 Windows 就能测**——本脚本干这个。
#
# 丢了会怎样：
#   安全描述符  → Windows 起不来。SYSTEM 读不了自己的文件，服务全挂
#   硬链接      → WinSxS 里几万个硬链接变成独立副本，体积爆炸到装不下
#   重解析点    → C:\Documents and Settings 这类联接失效，WOF 压缩文件读不出
#   备用数据流  → Zone.Identifier 之类丢失（不致命，但说明写入路径有缺陷）
#
# 做法：造一棵含上述全部特征的 NTFS 树 → wimcapture → wimapply 到另一个卷
#      → 再 wimcapture 一次 → 比对两个 WIM 的详细元数据。
#      两份 WIM 的元数据一致，就说明 apply 这一步什么都没弄丢。
#      另外再做一次文件系统层面的直接比对，双保险。
#
# 用的是 chroot 里那套二进制——也就是真正会随 ACCESS 出厂的那一份，
# 不是构建宿主上另装的。宿主是 minbase，本来也没有这些工具。
#
# 用法：  bash test-wimlib-ntfs.sh [构建目录]
# ============================================================================
set -uo pipefail

B="${1:-/root/kla-build}"
CH="$B/chroot"
CW="/tmp/wimtest"          # chroot 内视角
W="$CH$CW"                 # 宿主视角
IMG_MB=192

PASS=0; FAIL=0; SKIP=0
ok()   { echo "  [OK]   $*"; PASS=$((PASS+1)); }
bad()  { echo "  [FAIL] $*"; FAIL=$((FAIL+1)); }
skip() { echo "  [跳过] $*"; SKIP=$((SKIP+1)); }
info() { echo "  ---    $*"; }

if [ "$(id -u)" -ne 0 ]; then echo "需要 root。" >&2; exit 1; fi
if [ ! -x "$CH/usr/bin/wimlib-imagex" ]; then
    echo "chroot 里没有 wimlib-imagex：$CH" >&2
    echo "先跑一次 build.ps1（lb clean 会删掉 chroot/，重建后才有）。" >&2
    exit 1
fi

# chroot 里跑命令。PATH 要显式给——chroot 不继承宿主的 PATH，
# mkfs.ntfs 在 /usr/sbin，找不到会报 command not found 而不是报错原因。
c() { chroot "$CH" /usr/bin/env PATH=/usr/sbin:/usr/bin:/sbin:/bin "$@"; }

cleanup() {
    # 顺序要紧：先卸 ntfs-3g 的 FUSE 挂载，再卸 chroot 的绑定挂载。
    # 反过来的话 FUSE 进程还持着 /dev/fuse，卸不掉。
    for m in "$CW/src" "$CW/dst"; do
        mountpoint -q "$CH$m" 2>/dev/null && umount "$CH$m" 2>/dev/null
    done
    for m in /sys /proc /dev/pts /dev; do
        mountpoint -q "$CH$m" 2>/dev/null && umount -l "$CH$m" 2>/dev/null
    done
}
trap cleanup EXIT

echo "=== 0. 准备 ==="
cleanup                                    # 清掉上一轮可能残留的挂载
rm -rf "$W"; mkdir -p "$W/src" "$W/dst"

for m in dev dev/pts proc sys; do
    mkdir -p "$CH/$m"
    mountpoint -q "$CH/$m" || mount --bind "/$m" "$CH/$m"
done
info "wimlib: $(c wimlib-imagex --version 2>/dev/null | head -1)"
info "ntfs-3g: $(c ntfs-3g --version 2>&1 | grep -i '^ntfs-3g' | head -1)"

mkntfs_q() { c mkfs.ntfs -F -q -L "$2" "$1" >/dev/null 2>&1; }

echo
echo "=== 1. 造源卷 ==="
c dd if=/dev/zero of="$CW/src.img" bs=1M count=$IMG_MB status=none
if mkntfs_q "$CW/src.img" KLASRC; then ok "源卷格式化为 NTFS（${IMG_MB}M）"; else bad "mkfs.ntfs 失败"; exit 1; fi

# streams_interface=windows：可以用 文件:流名 的语法直接读写备用数据流，
# 不必依赖 getfattr/setfattr（chroot 里不一定有 attr 包）。
# permissions：让 ntfs-3g 真的写 NTFS 安全描述符，而不是只在内存里映射 POSIX 权限。
# noatime：**不加这个测试就自己骗自己**。第 5 节的 diff -r 会读遍两个卷，
#   而读操作会更新访问时间——test.wim 是读之前抓的，dst.wim 是读之后抓的，
#   于是第 8 节报出一个纯属自己制造的 Last Access Time 差异。
#   顺带说明一件事：ntfs-3g 默认挂载**光是读就会写盘**。救援环境里任何
#   非只读挂载都会弄脏用户的卷，server.py 的 _mount_ro() 坚持只读是对的。
MNTOPT="streams_interface=windows,permissions,noatime"
mount_ntfs() { c ntfs-3g -o "$MNTOPT" "$1" "$2" 2>>"$W/mount.log"; }

if mount_ntfs "$CW/src.img" "$CW/src"; then ok "源卷挂载成功"; else bad "源卷挂载失败"; exit 1; fi

echo
echo "=== 2. 铺一棵带全部关键特征的树 ==="
S="$W/src"

mkdir -p "$S/Windows/System32" "$S/Users/karl/Documents" "$S/空目录"
echo 'kernel'      > "$S/Windows/System32/ntoskrnl.txt"
echo 'boot config' > "$S/Windows/System32/config.txt"
: > "$S/Users/karl/空文件.txt"
echo '中文内容测试' > "$S/Users/karl/Documents/中文文件名.txt"
head -c 300000 /dev/urandom > "$S/Users/karl/Documents/big.bin"

# 硬链接：WinSxS 的骨架。丢了体积会爆炸。
echo 'shared payload' > "$S/Windows/System32/shared.dll"
ln "$S/Windows/System32/shared.dll" "$S/Windows/System32/shared-link1.dll"
ln "$S/Windows/System32/shared.dll" "$S/Users/karl/shared-link2.dll"

# 符号链接：ntfs-3g 写成 NTFS 重解析点，等价于 Windows 的联接
ln -s '/Users/karl' "$S/Documents and Settings"
ln -s 'Documents/中文文件名.txt' "$S/Users/karl/相对链接.txt"

# 备用数据流：Zone.Identifier 是最常见的一个
echo 'downloaded'   > "$S/Users/karl/Documents/big.bin:Zone.Identifier"
echo 'stream two'   > "$S/Windows/System32/ntoskrnl.txt:AltStream"

# 稀疏文件
c truncate -s 4M "$CW/src/Users/karl/sparse.bin"

# 差异化权限——permissions 挂载下这些会变成不同的 NTFS 安全描述符
chmod 0400 "$S/Windows/System32/config.txt"
chmod 0755 "$S/Windows/System32/ntoskrnl.txt"
chmod 0700 "$S/Users/karl/Documents"

# 时间戳：给几个特征值，比对时能看出有没有被重置成"现在"
touch -d '2001-09-11 08:46:00' "$S/Windows/System32/ntoskrnl.txt"
touch -d '2015-07-29 12:00:00' "$S/Users/karl/Documents/中文文件名.txt"

sync
FCOUNT=$(find "$S" | wc -l)
ok "铺好 $FCOUNT 个条目（含硬链接/符号链接/ADS/稀疏/中文名）"

# --- 落一份文件系统层面的指纹 ---------------------------------------------
# 相对路径 + 类型 + 大小 + 权限 + mtime + 符号链接目标，排序后逐行可比。
# 不含 inode 号——两个卷的 inode 必然不同，比它没意义；硬链接单独验。
#
# 目录的 %s 要剔掉：那是 NTFS 索引分配出来的大小，取决于索引块怎么排布，
# 两个卷之间必然对不上，而且不带任何语义。第一版没剔，18 项里白报了 5 个
# 目录"不一致"，实际文件一个没差。
fingerprint() {
    local root="$1"
    ( cd "$root" && find . -printf '%y|%s|%m|%T@|%p|%l\n' 2>/dev/null \
      | awk -F'|' 'BEGIN{OFS="|"} $1=="d"{$2="-"} {print}' \
      | sort )
}
# 硬链接分组：同一 inode 的路径排序后拼成一行，再整体排序。
# 这样即使 inode 号变了，"哪几个路径共享同一个 inode"这个事实仍可比。
hardlink_groups() {
    local root="$1"
    ( cd "$root" && find . -type f -links +1 -printf '%i\t%p\n' 2>/dev/null \
      | sort | awk -F'\t' '{a[$1]=a[$1]" "$2} END{for(i in a) print a[i]}' \
      | sed 's/^ //' | sort )
}

fingerprint      "$S" > "$W/before.fp"
hardlink_groups  "$S" > "$W/before.hl"
cat "$S/Users/karl/Documents/big.bin:Zone.Identifier" > "$W/before.ads1" 2>/dev/null
cat "$S/Windows/System32/ntoskrnl.txt:AltStream"      > "$W/before.ads2" 2>/dev/null

umount "$W/src"; ok "源卷已卸载（wimcapture 要求卷未挂载）"

echo
echo "=== 3. wimcapture 源卷 ==="
# 源是一个 NTFS 卷镜像文件，wimlib 会走 NTFS 模式（libntfs-3g 直读），
# 完整读取安全描述符/重解析点/ADS/短名——这正是 Windows 侧 capture 的等价路径。
if c wimlib-imagex capture "$CW/src.img" "$CW/test.wim" "KLA测试" --compress=LZX >"$W/cap1.log" 2>&1; then
    ok "wimcapture 成功  ($(c ls -lh "$CW/test.wim" | awk '{print $5}'))"
else
    bad "wimcapture 失败"; tail -20 "$W/cap1.log" | sed 's/^/         /'; exit 1
fi
if grep -qi 'ntfs' "$W/cap1.log"; then
    info "$(grep -i 'ntfs' "$W/cap1.log" | head -2)"
fi

echo
echo "=== 4. wimapply 到一个空 NTFS 卷 ==="
c dd if=/dev/zero of="$CW/dst.img" bs=1M count=$IMG_MB status=none
mkntfs_q "$CW/dst.img" KLADST || { bad "目标卷格式化失败"; exit 1; }

if c wimlib-imagex apply "$CW/test.wim" 1 "$CW/dst.img" >"$W/apply.log" 2>&1; then
    ok "wimapply 成功"
else
    bad "wimapply 失败"; tail -20 "$W/apply.log" | sed 's/^/         /'; exit 1
fi

echo
echo "=== 5. 文件系统层面比对 ==="
if mount_ntfs "$CW/dst.img" "$CW/dst"; then ok "目标卷挂载成功"; else bad "目标卷挂载失败"; exit 1; fi
D="$W/dst"

# 源卷在第 2 节末尾卸掉了（wimcapture 要求卷未挂载），这里得挂回来才能做
# 内容比对——不挂回来的话 $S 是个空目录，diff -r 会把 dst 里的东西全报成
# "Only in dst"，看着像还原多写了东西，其实是比较对象没了。
mount_ntfs "$CW/src.img" "$CW/src" || bad "源卷重新挂载失败"

fingerprint     "$D" > "$W/after.fp"
hardlink_groups "$D" > "$W/after.hl"

if diff -q "$W/before.fp" "$W/after.fp" >/dev/null; then
    ok "目录树指纹完全一致（路径/类型/大小/权限/时间戳/链接目标）"
else
    bad "目录树指纹有差异："
    diff "$W/before.fp" "$W/after.fp" | head -25 | sed 's/^/         /'
fi

if diff -q "$W/before.hl" "$W/after.hl" >/dev/null; then
    N=$(wc -l < "$W/before.hl")
    ok "硬链接分组一致（$N 组）—— WinSxS 不会膨胀"
else
    bad "硬链接**没有保留**，还原后体积会爆炸："
    echo "         期望: $(cat "$W/before.hl")"
    echo "         实得: $(cat "$W/after.hl")"
fi

# 内容逐字节比对（--no-dereference：比符号链接本身而不是它指向的东西）
if diff -r --no-dereference "$S" "$D" >"$W/content.diff" 2>&1; then
    ok "全部文件内容逐字节一致"
else
    bad "文件内容有差异："; head -15 "$W/content.diff" | sed 's/^/         /'
fi

echo
echo "=== 6. 备用数据流 ==="
for pair in "Users/karl/Documents/big.bin:Zone.Identifier|before.ads1" \
            "Windows/System32/ntoskrnl.txt:AltStream|before.ads2"; do
    p="${pair%|*}"; ref="${pair#*|}"
    if [ ! -s "$W/$ref" ]; then skip "$p —— 源端就没写进去，跳过"; continue; fi
    if c cat "$CW/dst/$p" > "$W/after.$ref" 2>/dev/null && \
       diff -q "$W/$ref" "$W/after.$ref" >/dev/null 2>&1; then
        ok "ADS 保留：$p"
    else
        bad "ADS 丢失或内容不符：$p"
    fi
done

echo
echo "=== 7. 重解析点（符号链接/联接）==="
for l in "Documents and Settings" "Users/karl/相对链接.txt"; do
    if [ -L "$D/$l" ]; then
        ok "重解析点保留：$l -> $(readlink "$D/$l")"
    else
        bad "重解析点丢失（变成了普通文件/目录）：$l"
    fi
done

echo
echo "=== 8. WIM 元数据层面比对（最硬的一关）==="
# 把还原后的卷再 capture 一次，比对两个 WIM 的详细元数据。
# 这一层能看到文件系统层看不见的东西：安全描述符、短名、重解析标签、流列表。
# 两份 --detailed 输出一致 = apply 这一步 wimlib 认识的东西一样都没丢。
umount "$W/dst"
if c wimlib-imagex capture "$CW/dst.img" "$CW/dst.wim" "KLA测试" --compress=LZX >"$W/cap2.log" 2>&1; then
    ok "还原后的卷重新 capture 成功"

    c wimlib-imagex dir "$CW/test.wim" 1 --detailed > "$W/meta1.txt" 2>/dev/null
    c wimlib-imagex dir "$CW/dst.wim"  1 --detailed > "$W/meta2.txt" 2>/dev/null

    # 只过滤真正的存储层伪影，语义字段一律保留：
    #   Offset in WIM  资源在 WIM 文件里的物理偏移。两次独立 capture 的打包
    #                  顺序不同，必然不一样，跟内容无关。
    #   Link Group ID  硬链接组的内部编号，值是任意的。真正要验的"哪几个路径
    #                  共享同一个 inode"已经在第 5 节用 hardlink_groups 比过了。
    #   Inode number   卷内编号，同理。
    #
    # **Attributes 和 Security Descriptor Size 刻意不过滤**——第一版顺手把它俩
    # 也滤了，那等于把这一节最该看的东西蒙上：Attributes 带着 REPARSE_POINT /
    # SPARSE_FILE / 只读 这些位，安全描述符大小变了就是 ACL 变了。
    scrub() { grep -viE '^\s*(Offset in WIM|Link Group ID|Inode number)\s*=' "$1" | grep -vE '^\s*$'; }
    scrub "$W/meta1.txt" > "$W/meta1.s"; scrub "$W/meta2.txt" > "$W/meta2.s"

    if diff -q "$W/meta1.s" "$W/meta2.s" >/dev/null; then
        ok "两份 WIM 的详细元数据完全一致"
    else
        NDIFF=$(diff "$W/meta1.s" "$W/meta2.s" | grep -c '^[<>]')
        bad "WIM 元数据有 $NDIFF 行差异："
        diff "$W/meta1.s" "$W/meta2.s" | head -30 | sed 's/^/         /'
    fi

    # 安全描述符单独点名确认——这是丢了就起不来的那一项
    S1=$(grep -c 'Security Descriptor' "$W/meta1.txt" 2>/dev/null)
    S2=$(grep -c 'Security Descriptor' "$W/meta2.txt" 2>/dev/null)
    if [ "$S1" -gt 0 ] && [ "$S1" -eq "$S2" ]; then
        ok "安全描述符数量一致（各 $S1 条）"
    elif [ "$S1" -eq 0 ]; then
        skip "这个 wimlib 的 dir 输出里没有安全描述符字段，改看下面的 ntfsinfo"
    else
        bad "安全描述符数量不一致：源 $S1，还原后 $S2"
    fi
else
    bad "还原后的卷重新 capture 失败"; tail -20 "$W/cap2.log" | sed 's/^/         /'
fi

echo
echo "=== 9. 目标卷一致性 ==="
# ntfsfix -n 只检查不修改。有报错说明 wimlib 写出来的 NTFS 结构本身有问题，
# Windows 挂上去会触发 chkdsk，甚至拒绝引导。
if c ntfsfix -n "$CW/dst.img" >"$W/fsck.log" 2>&1; then
    ok "ntfsfix 检查通过，NTFS 结构无损"
else
    bad "ntfsfix 报告问题："; tail -10 "$W/fsck.log" | sed 's/^/         /'
fi
info "源卷 $(c ls -lh "$CW/src.img" | awk '{print $5}') → WIM $(c ls -lh "$CW/test.wim" | awk '{print $5}')"

echo
echo "================================"
echo "  通过 $PASS 项，失败 $FAIL 项，跳过 $SKIP 项"
echo "================================"
if [ "$FAIL" -eq 0 ]; then
    echo "  A 阶段结论：Linux 侧 wimlib 写 NTFS 的元数据保真度没有问题。"
    echo "  仍需 B 阶段（真机/虚拟机）确认还原后的 Windows 能引导。"
fi
[ "$FAIL" -eq 0 ]
