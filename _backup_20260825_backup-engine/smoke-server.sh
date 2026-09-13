#!/bin/bash
# 冒烟测试：起 server.py，curl /api/sysinfo，杀掉。
cd /mnt/d/work/KARLS_LIGHT_ACCESS/kla/live/config/includes.chroot/opt/kla
python3 server.py &
PID=$!
sleep 2
echo "== /api/sysinfo =="
curl -s http://127.0.0.1:8760/api/sysinfo | python3 -m json.tool | head -40
echo "== /api/wifi =="
curl -s http://127.0.0.1:8760/api/wifi | head -c 300
echo
echo "== /api/system =="
curl -s http://127.0.0.1:8760/api/system | python3 -m json.tool | head -10
kill $PID 2>/dev/null
echo SMOKE-DONE
