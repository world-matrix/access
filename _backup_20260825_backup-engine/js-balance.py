import re, sys
path = r"/mnt/d/work/KARLS_LIGHT_ACCESS/kla/live/config/includes.chroot/opt/kla/www/app.js"
src = open(path, encoding="utf-8").read()

# 粗粒度：去掉字符串/模板串/注释/正则字面量后查三类括号是否平衡。
# 不追求完美解析，只为抓「编辑弄丢/多出一个大括号」这类低级错误。
out, i, n = [], 0, len(src)
mode = None  # None | 'line' | 'block' | "'" | '"' | '`'
while i < n:
    c = src[i]
    nxt = src[i+1] if i+1 < n else ''
    if mode is None:
        if c == '/' and nxt == '/': mode = 'line'; i += 2; continue
        if c == '/' and nxt == '*': mode = 'block'; i += 2; continue
        if c == "'" or c == '"' or c == '`': mode = c; i += 1; continue
        out.append(c); i += 1; continue
    if mode == 'line':
        if c == '\n': mode = None; out.append('\n')
        i += 1; continue
    if mode == 'block':
        if c == '*' and nxt == '/': mode = None; i += 2; continue
        i += 1; continue
    # 字符串
    if c == '\\': i += 2; continue
    if c == mode:
        # 模板串里的 ${...} 嵌套：粗略不管，仍按整串处理
        mode = None
    i += 1

code = ''.join(out)
stack, pairs = [], {'}': '{', ']': '[', ')': '('}
line = 1
ok = True
for ch in code:
    if ch == '\n': line += 1
    elif ch in '{[(': stack.append((ch, line))
    elif ch in '}])':
        if not stack or stack[-1][0] != pairs[ch]:
            print(f"MISMATCH {ch!r} at line {line}")
            ok = False
            break
        stack.pop()
if ok and stack:
    print(f"UNCLOSED {stack[-1][0]!r} opened at line {stack[-1][1]}")
    ok = False
print("BALANCE-OK" if ok else "BALANCE-FAIL")
