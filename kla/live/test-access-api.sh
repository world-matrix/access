#!/bin/bash
# server.py 新增的作业/浏览 API 的端到端测试。
#
# 用真的 NTFS 回环卷，不用临时目录假装：resolve_block_device 明确要求
# /dev 下那个东西是**块设备**，拿目录去测等于把这一层安全检查绕过去，
# 而那正是最该测的一层。
#
# 跑在 chroot 里那份 python3 —— 也就是真正会随 ACCESS 出厂的那一份。
set -uo pipefail
B="${1:-/root/kla-build}"
CH="$B/chroot"
CW="/tmp/apitest"
W="$CH$CW"
# 源码目录从脚本自身位置推导，不写死 /mnt/d/...：这套工程搬过一次家，
# 写死的路径在新位置上 cp 会失败，而 cp 失败后测试照跑，
# 报出来的是一堆莫名其妙的 404——看不出真因是文件根本没拷过去。
LIVE="$(cd "$(dirname "$0")" && pwd)"
c() { chroot "$CH" /usr/bin/env PATH=/usr/sbin:/usr/bin:/sbin:/bin "$@"; }

SRCLOOP=""; DSTLOOP=""; SRVPID=""
cleanup() {
    [ -n "$SRVPID" ] && kill "$SRVPID" 2>/dev/null
    for m in "$CW/src" /mnt/kla/*; do
        mountpoint -q "$CH$m" 2>/dev/null && umount -l "$CH$m" 2>/dev/null
    done
    [ -n "$SRCLOOP" ] && losetup -d "$SRCLOOP" 2>/dev/null
    [ -n "$DSTLOOP" ] && losetup -d "$DSTLOOP" 2>/dev/null
    for m in /sys /proc /dev/pts /dev; do
        mountpoint -q "$CH$m" 2>/dev/null && umount -l "$CH$m" 2>/dev/null
    done
}
trap cleanup EXIT

if [ ! -x "$CH/usr/bin/python3" ]; then
    echo "chroot 里没有 python3：$CH" >&2; exit 1
fi
for m in dev dev/pts proc sys; do
    mkdir -p "$CH/$m"; mountpoint -q "$CH/$m" || mount --bind "/$m" "$CH/$m"
done

echo "=== 0. 造两个 NTFS 卷 ==="
rm -rf "$W"; mkdir -p "$W/src"
c dd if=/dev/zero of="$CW/src.img" bs=1M count=96 status=none
c dd if=/dev/zero of="$CW/dst.img" bs=1M count=96 status=none
c mkfs.ntfs -F -q -L APISRC "$CW/src.img" >/dev/null 2>&1 || { echo "格式化源卷失败"; exit 1; }
c mkfs.ntfs -F -q -L APIDST "$CW/dst.img" >/dev/null 2>&1 || { echo "格式化目标卷失败"; exit 1; }
echo "  两个 96M NTFS 卷已就绪"

echo
echo "=== 1. 铺源卷内容 ==="
c ntfs-3g -o streams_interface=windows,permissions "$CW/src.img" "$CW/src" || { echo "挂载源卷失败"; exit 1; }
S="$W/src"
mkdir -p "$S/Windows/System32" "$S/Users/karl/Documents"
echo 'kernel'        > "$S/Windows/System32/ntoskrnl.txt"
echo '中文内容测试'  > "$S/Users/karl/中文文件名.txt"
echo 'doc'           > "$S/Users/karl/Documents/readme.txt"
head -c 200000 /dev/urandom > "$S/Users/karl/Documents/blob.bin"
# 模拟 Windows 卷上那种指向别处的联接。safe_join 必须顺着 realpath 拦下来。
ln -s '/etc' "$S/escape"
sync
echo "  铺好 $(find "$S" | wc -l) 个条目（含一个指向 /etc 的符号链接）"
umount "$W/src"

echo
echo "=== 2. 挂成回环块设备 ==="
# 关键：必须是真的块设备，resolve_block_device 会 stat 它并要求 S_ISBLK
SRCLOOP=$(losetup --find --show "$W/src.img") || { echo "losetup 源卷失败"; exit 1; }
DSTLOOP=$(losetup --find --show "$W/dst.img") || { echo "losetup 目标卷失败"; exit 1; }
SRCN=$(basename "$SRCLOOP"); DSTN=$(basename "$DSTLOOP")
echo "  源 $SRCLOOP ($SRCN)   目标 $DSTLOOP ($DSTN)"

echo
echo "=== 3. 起 server.py ==="
mkdir -p "$CH/opt/kla"
cp -f "$LIVE/config/includes.chroot/opt/kla/server.py" "$CH/opt/kla/server.py" || {
    echo "  拷 server.py 失败——源码目录推导错了？LIVE=$LIVE"; exit 1; }
mkdir -p "$CH/opt/kla/www"
cp -rf "$LIVE/config/includes.chroot/opt/kla/www/." "$CH/opt/kla/www/" 2>/dev/null
cp -f "$LIVE/.cache/api-client.py" "$CH/tmp/api-client.py" || {
    echo "  拷 api-client.py 失败：$LIVE/.cache/api-client.py 不存在"; exit 1; }

# 先做语法检查，语法错了下面起不来只会报个连不上，看不出真因
if ! c python3 -m py_compile /opt/kla/server.py; then
    echo "  server.py 语法检查未通过"; exit 1
fi
echo "  py_compile 通过"

chroot "$CH" /usr/bin/env PATH=/usr/sbin:/usr/bin:/sbin:/bin \
    python3 /opt/kla/server.py > "$W/server.log" 2>&1 &
SRVPID=$!
READY=0
for i in $(seq 1 40); do
    if c python3 -c 'import urllib.request,sys
try:
    urllib.request.urlopen("http://127.0.0.1:8760/api/tools",timeout=1); sys.exit(0)
except Exception: sys.exit(1)' 2>/dev/null; then READY=1; break; fi
    sleep 0.25
done
if ! kill -0 "$SRVPID" 2>/dev/null; then
    echo "  server.py 进程已退出："; sed 's/^/    /' "$W/server.log"; exit 1
fi
# 探测成功与否必须单独判。上一版只检查进程在不在，于是「进程活着但路由表写错」
# 这种情况会被判为就绪，测试照跑，报出来的是一堆连接/404 错误——看不出真因。
# 和之前 check-sd-nonempty.sh 里 TOT=0 报通过是同一类毛病：
# 一个什么都没测到却说通过的检查，比没有这个检查更坏。
if [ "$READY" -ne 1 ]; then
    echo "  10 秒内 /api/tools 一直没有响应，进程却还活着——服务没能正常起来"
    sed 's/^/    /' "$W/server.log"
    exit 1
fi
echo "  服务已就绪 (pid $SRVPID)"

echo
echo "=== 4. 跑 API 测试 ==="
# -u：不带的话 python 的 stdout 是块缓冲，测试中途抛异常时前面几十行结果
# 会连同缓冲区一起丢掉，只剩一个 traceback，看不出是哪一步开始坏的。
c python3 -u /tmp/api-client.py "$SRCN" "$DSTN"
RC=$?

echo
echo "=== 5. 前端冒烟（无头 chromium）==="
# 静态检查（Test-Frontend.ps1）能抓拼错的 id 和缺的图标，抓不到"渲染出来了
# 但按钮点了没反应"。这一步用真的浏览器加载真的页面、调真的后端，
# 走一遍从开机画面到拷贝完成的全流程。
SMOKE_RC=0
if ! chroot "$CH" /bin/sh -c 'command -v chromium' >/dev/null 2>&1; then
    echo "  chroot 里没有 chromium，跳过（这不算通过，只是没测）"
else
    # HOME 必须给：chromium 建不出 profile 目录会直接退出，而且退得很安静，
    # 看起来像"无头模式不支持"，实际只是没地方写文件。
    cp -f "$LIVE/.cache/smoke-driver.js" "$CH/opt/kla/www/__driver.js"
    sed 's#<script src="app.js"></script>#<script src="app.js"></script>\
<script src="__driver.js"></script>#' \
        "$CH/opt/kla/www/index.html" > "$CH/opt/kla/www/__smoke.html"

    # 先原样加载真的 index.html —— 驱动页是改过的副本，
    # 只测副本的话「出厂那份页面本身能不能起来」始终没人验证。
    PLAIN=$(chroot "$CH" /bin/sh -c "export PATH=/usr/sbin:/usr/bin:/sbin:/bin HOME=/tmp; \
        chromium --headless --no-sandbox --disable-gpu --disable-dev-shm-usage \
        --virtual-time-budget=15000 --dump-dom http://127.0.0.1:8760/" 2>/dev/null)
    if echo "$PLAIN" | grep -q 'class="nav-item'; then
        echo "  [OK]   出厂页面能起来（导航已渲染）"
    else
        echo "  [FAIL] 出厂页面没渲染出导航——boot() 没跑完或接口全挂"
        echo "$PLAIN" | head -20 | sed 's/^/    /'
        SMOKE_RC=1
    fi
    if echo "$PLAIN" | grep -q 'id="splash"'; then
        echo "  [FAIL] 启动画面没被移除，会一直盖住界面"
        SMOKE_RC=1
    else
        echo "  [OK]   启动画面已移除"
    fi

    DOM=$(chroot "$CH" /bin/sh -c "export PATH=/usr/sbin:/usr/bin:/sbin:/bin HOME=/tmp; \
        chromium --headless --no-sandbox --disable-gpu --disable-dev-shm-usage \
        --virtual-time-budget=90000 \
        --dump-dom 'http://127.0.0.1:8760/__smoke.html?src=$SRCN&dst=$DSTN'" 2>/dev/null)
    # 结果在 <pre id="__smoke"> 里，每步一行。驱动每完成一步就刷新一次，
    # 所以预算耗尽被掐掉也能看出走到哪一步——但那种情况必须判失败，
    # 否则"只跑了两步就断了"会和"全过了"长得一模一样。
    REPORT=$(echo "$DOM" | sed -n 's#.*<pre id="__smoke">\(.*\)</pre>.*#\1#p')
    if [ -z "$REPORT" ]; then
        REPORT=$(echo "$DOM" | awk '/<pre id="__smoke">/{f=1} f{print} /<\/pre>/{f=0}' |
                 sed 's#<pre id="__smoke">##; s#</pre>##')
    fi
    if [ -z "$REPORT" ]; then
        echo "  [FAIL] 驱动脚本没产生任何结果——页面可能整个没加载"
        echo "$DOM" | head -20 | sed 's/^/    /'
        SMOKE_RC=1
    else
        echo "$REPORT" | sed 's/&amp;/\&/g; s/&lt;/</g; s/&gt;/>/g; s/^/  /'
        NBAD=$(echo "$REPORT" | grep -c '^FAIL')
        NOK=$(echo "$REPORT" | grep -c '^OK')
        echo "  ---- 前端冒烟：$NOK 通过，$NBAD 失败 ----"
        # 断言总数：驱动跑完整条链是 36 条。少于这个数说明中途断了，
        # 而中途断掉时"失败 0 条"看起来和全过一模一样。
        TOTAL=$((NOK + NBAD))
        if [ "$TOTAL" -lt 36 ]; then
            echo "  [FAIL] 只跑了 $TOTAL 条断言（应有 36 条），驱动中途断了"
            SMOKE_RC=1
        fi
        [ "$NBAD" -gt 0 ] && SMOKE_RC=1
    fi
    rm -f "$CH/opt/kla/www/__driver.js" "$CH/opt/kla/www/__smoke.html"
fi
[ "$SMOKE_RC" -ne 0 ] && RC=1

if [ -s "$W/server.log" ]; then
    echo
    echo "--- 服务端日志（有输出就说明有异常）---"
    head -40 "$W/server.log" | sed 's/^/    /'
fi
exit $RC
