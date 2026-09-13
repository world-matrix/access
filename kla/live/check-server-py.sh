#!/bin/bash
# 用 chroot 里那个 python3 编译 server.py。
#
# 为什么不在构建宿主上另装一个 python：宿主是 minbase，没有 python3；
# 而且真正跑 server.py 的是救援系统里的解释器（trixie 的 3.13），
# 版本对不上的语法检查没有意义。chroot 目录在 lb build 之后还留着，
# 直接借它自己的解释器最贴近实际。
set -uo pipefail

B="${1:-/root/kla-build}"
CH="$B/chroot"

if [ ! -x "$CH/usr/bin/python3" ]; then
    echo "[跳过] $CH 里没有 python3（chroot 可能已被 lb clean 删掉）"
    echo "       重新构建后再跑，或用 unsquashfs 取出 squashfs 里的解释器。"
    exit 2
fi

echo "==> chroot python: $(chroot "$CH" /usr/bin/python3 --version 2>&1)"
echo

RC=0
for f in /opt/kla/server.py; do
    if chroot "$CH" /usr/bin/python3 -m py_compile "$f" 2>&1; then
        echo "  [OK]   $f 语法通过"
    else
        echo "  [FAIL] $f 语法错误"
        RC=1
    fi
done

# 编译产物别留在镜像里
rm -rf "$CH/opt/kla/__pycache__"

echo
echo "==> 顺带跑一遍 server.py 的自检导入（不启动监听）"
# 只 import 不 run：能抓到模块级的 NameError / 缺依赖，
# 又不会真的把端口占上。
if chroot "$CH" /usr/bin/python3 -c "
import importlib.util, sys
spec = importlib.util.spec_from_file_location('klasrv', '/opt/kla/server.py')
m = importlib.util.module_from_spec(spec)
try:
    spec.loader.exec_module(m)
except SystemExit:
    pass
print('  [OK]   模块级代码可以正常导入')
" 2>&1; then
    :
else
    echo "  [FAIL] 导入失败（上面是 traceback）"
    RC=1
fi
rm -rf "$CH/opt/kla/__pycache__"

exit $RC
