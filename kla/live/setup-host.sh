#!/bin/bash
# 在现有的 WSL Debian 里 debootstrap 出一个干净的 trixie 根文件系统，
# 打包成 tar.gz 供 `wsl --import` 导入成独立的构建宿主。
#
# 为什么不直接把现有的 bullseye 升级到 trixie：
#   1. 微软商店那个 Debian 包带的是 2021 年的 bullseye（Debian 11），
#      跨两个大版本 dist-upgrade 要先自举 apt/dpkg，失败点多
#   2. bullseye 的 LTS 已到期，backports 已下架（apt-get update 报 404）
#   3. 升上来的系统里会留一堆过渡包和旧配置，而 debootstrap 一次成型
#   4. 构建宿主与目标发行版同版本，能排除掉一整类版本偏差问题
#
# 用法（在 WSL Debian 里以 root 运行）：
#   sudo bash setup-host.sh
#
# 环境变量：
#   KLA_MIRROR      Debian 镜像，默认清华 TUNA
#   KLA_SUITE       目标套件，默认 trixie
#   KLA_OUT_TAR     输出的 rootfs 包路径
set -euo pipefail

MIRROR="${KLA_MIRROR:-http://mirrors.tuna.tsinghua.edu.cn/debian}"
SUITE="${KLA_SUITE:-trixie}"
ROOT_DIR="${KLA_ROOTFS_DIR:-/var/tmp/kla-${SUITE}-root}"
OUT_TAR="${KLA_OUT_TAR:-/var/tmp/kla-${SUITE}-rootfs.tar.gz}"

# Debian 的归档签名公钥发布在这个固定路径下，按大版本号命名。
# trixie = Debian 13。
KEY_URL="https://ftp-master.debian.org/keys/archive-key-13.asc"
KEYRING="/usr/share/keyrings/kla-debian-${SUITE}.gpg"

if [ "$(id -u)" -ne 0 ]; then
    echo "需要 root 运行。" >&2
    exit 1
fi

. /etc/os-release
echo "==> 宿主: ${PRETTY_NAME}"
echo "==> 目标: Debian ${SUITE}"
echo "==> 镜像: ${MIRROR}"
echo

# --- 1. 宿主侧依赖 ----------------------------------------------------------
if ! command -v debootstrap >/dev/null 2>&1 \
   || ! command -v curl >/dev/null 2>&1 \
   || ! command -v gpg >/dev/null 2>&1; then
    echo "==> 安装宿主依赖 (debootstrap curl gnupg)"
    # bullseye-backports 随 LTS 到期已下架，留着会让 apt-get update 报 404，
    # set -e 下直接中断整个脚本。它不一定在 sources.list 里——实测那条 404
    # 来自 ftp.debian.org 而 main 来自 deb.debian.org，说明是独立的一份配置，
    # 所以两个位置都要清。
    sed -i '/backports/d' /etc/apt/sources.list
    if [ -d /etc/apt/sources.list.d ]; then
        # 整份文件都是 backports 的就直接改名留底，只有个别行的就删行
        for f in /etc/apt/sources.list.d/*.list /etc/apt/sources.list.d/*.sources; do
            [ -e "$f" ] || continue
            if grep -q backports "$f"; then
                echo "    摘掉 backports: $f"
                sed -i '/backports/d' "$f"
                # 清空的文件留着会让 apt 报 "Malformed entry"，一并挪走
                [ -s "$f" ] || mv "$f" "$f.kla-disabled"
            fi
        done
    fi
    # 宿主自己的 apt 也换成国内镜像。只装三个包，但 deb.debian.org 在这里
    # 光拉 8 MB 的 bullseye 索引就要几分钟，没必要忍。
    # bullseye 已是 oldoldstable 但仍在主镜像上（不在 archive.debian.org），
    # TUNA 同样带它，所以把源地址替换掉即可。
    cp -n /etc/apt/sources.list /etc/apt/sources.list.kla-orig 2>/dev/null || true
    sed -i -e "s#https\?://deb.debian.org/debian#${MIRROR}#g" \
           -e "s#https\?://security.debian.org/debian-security#${MIRROR}-security#g" \
           -e "s#https\?://ftp.debian.org/debian#${MIRROR}#g" \
           /etc/apt/sources.list

    apt-get update
    apt-get install -y --no-install-recommends debootstrap curl ca-certificates gnupg
fi

# --- 2. 目标发行版的签名密钥 ------------------------------------------------
# bullseye 的 debian-archive-keyring 里没有 trixie 的密钥，debootstrap 会因为
# 验证不了 Release 签名而拒绝工作。单独取一份密钥比 --no-check-gpg 安全得多——
# 后者等于对着一个不可信的网络下载整个根文件系统。
if [ ! -f "$KEYRING" ]; then
    echo "==> 获取 Debian ${SUITE} 归档签名密钥"
    curl -fsSL "$KEY_URL" -o "/tmp/kla-${SUITE}-key.asc"
    gpg --dearmor < "/tmp/kla-${SUITE}-key.asc" > "$KEYRING"
    rm -f "/tmp/kla-${SUITE}-key.asc"
fi
echo "    密钥: $(gpg --no-default-keyring --keyring "$KEYRING" --list-keys 2>/dev/null \
        | grep -m1 'Debian Archive' || echo "$KEYRING")"

# --- 3. debootstrap 的套件脚本 ----------------------------------------------
# bullseye 的 debootstrap 不带 trixie 脚本。Debian 的这些脚本本质上都是
# 指向 sid 的符号链接，内容一致，所以补一个链接即可。
SCRIPT_DIR=/usr/share/debootstrap/scripts
if [ ! -e "$SCRIPT_DIR/$SUITE" ]; then
    echo "==> 补 debootstrap 的 ${SUITE} 脚本（链到 sid）"
    ln -sf sid "$SCRIPT_DIR/$SUITE"
fi

# --- 4. bootstrap ------------------------------------------------------------
if [ -d "$ROOT_DIR" ] && [ -x "$ROOT_DIR/bin/bash" ]; then
    echo "==> ${ROOT_DIR} 已存在且看起来完整，跳过 debootstrap"
else
    echo "==> debootstrap ${SUITE} → ${ROOT_DIR}（约 3–8 分钟）"
    rm -rf "$ROOT_DIR"
    debootstrap \
        --variant=minbase \
        --keyring="$KEYRING" \
        --arch=amd64 \
        "$SUITE" "$ROOT_DIR" "$MIRROR"
fi

# --- 5. 预置新系统的配置 -----------------------------------------------------
echo "==> 预置 sources.list 与 wsl.conf"

# 用同一个国内镜像，否则新宿主里 apt 一样慢。
# trixie 起 non-free-firmware 是独立的归档区，救援盘要装网卡固件，必须带上。
cat > "$ROOT_DIR/etc/apt/sources.list" <<EOF
deb ${MIRROR} ${SUITE} main contrib non-free non-free-firmware
deb ${MIRROR} ${SUITE}-updates main contrib non-free non-free-firmware
deb ${MIRROR}-security ${SUITE}-security main contrib non-free non-free-firmware
EOF

# WSL 默认会把 Windows 的 PATH 追加进来，里面的空格和反斜杠会干扰构建脚本；
# 而 DrvFs 自动挂载保留着，因为要从 /mnt/d 读配置、往回写 ISO。
cat > "$ROOT_DIR/etc/wsl.conf" <<'EOF'
[automount]
enabled = true
options = "metadata"

[interop]
enabled = true
appendWindowsPath = false

[user]
default = root
EOF

# 不加这个的话 apt 在没有 /proc 的导入初期会报一堆无关警告
echo 'APT::Install-Recommends "false";' > "$ROOT_DIR/etc/apt/apt.conf.d/99kla-no-recommends"

# --- 6. 打包 -----------------------------------------------------------------
echo "==> 打包 → ${OUT_TAR}"
rm -f "$OUT_TAR"
# --numeric-owner 是必须的：wsl --import 不做用户名映射，
# 按名字解析 owner 会在导入侧全部落到错误的 uid 上。
tar --numeric-owner -C "$ROOT_DIR" -czf "$OUT_TAR" .

echo
echo "==> 完成"
ls -lh "$OUT_TAR"
echo
echo "    回到 Windows 侧导入："
echo "      wsl --import KLA-Build <目标目录> \\\\wsl.localhost\\Debian${OUT_TAR//\//\\\\}"
echo "    或者用 live\\Setup-BuildHost.ps1 一步完成。"
