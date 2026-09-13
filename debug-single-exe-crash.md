# [OPEN] 单 EXE 安装包闪退调试会话 (single-exe-crash)
创建时间：2026-08-25

## 现象
用户双击 `installer\release\KARLS LIGHT ACCESS Setup.exe`（1.01GB 单个 PE Overlay Blob 安装包），程序**立即闪退**，无法正常进入 5 页安装向导，也没看到 PreparingWindow（正在准备安装文件 进度条）。
预期：双击 → UAC 提升（因为 admin.manifest）→ 弹出 PreparingWindow 自解压 → 完了关 PreparingWindow → 进 5 页向导 Welcome。

## 环境
- 产物：`installer\release\KARLS LIGHT ACCESS Setup.exe` = KLA-Setup.exe(659KB) + 尾部 Overlay Blob (KLAP footer)
- 本机：Windows 10 19045 x64，ThinkPad，UEFI，用户账户 THINK\Think (非内置 Admin，需 UAC 提升)
- 上次合成时 Stage 3/4 通过 BlobVerifier 和 E2E (字节级比对 access.img) PASS
- 目录版 `KLA-Setup.exe --selftest` 5/5 PASS（RoundTrip 自解压格式）

## 可证伪假设（按先验概率降序）
1. **H1 [最高概率]：PreparingWindow 自解压时抛未捕获异常，STA Dispatcher 里崩溃 → 进程秒退**  
   - SetupProgram.Main 在 HasEmbeddedBlob=true 分支：`new Thread(ExtractEmbeddedPayload)` 后台跑 + Dispatcher.BeginInvoke 更新 UI + `prepWin.ShowDialog()` 阻塞。  
   - 可能崩溃点：ExtractEmbeddedPayload %TEMP% 目录 ACL 写权限失败 / 路径含空格 guid12B 解析失败 / index 条目数 6 对不上 / 256KB 流复制时 FileStream 句柄泄漏 / dispatcher 跨线程操作不是 BeginInvoke 而是 Invoke 死锁。
   - 证据位：%TEMP%\KLA-Payload-* 目录是否已创建（哪怕只写了一两个文件）；Windows 事件查看器 → Windows 日志 → 应用程序，来源=".NET Runtime" ID=1026 或 "Application Error" id=1000 有堆栈。
2. **H2：SetupProgram.Main 最开头的 admin.manifest + UAC 提升后，当前工作目录 C:\Windows\System32，后续相对路径写日志或找资源失败 → 立即崩溃**。
   - 证据位：`_make-single.exe --build-single` 合成时写的最终文件尾部 12B footer 是否被 UAC 下的 Defender 实时保护拦截并改写。
3. **H3：PE Overlay Blob 被 Windows Defender / SmartScreen 实时扫描判定为可疑 → 启动时杀进程**。
   - 证据位：Windows 安全中心 → 病毒和威胁防护 → 保护历史记录，有「已阻止不受信任的启动项」或 Behavior:Win32/Hiveshield。
4. **H4：Setup.exe 的 AppDomain 加载 WPF 主题时，Theme.XamlReader.Parse 在沙箱/管理员身份下运行，找不到某个 assembly → 立即抛出 XamlParseException 未被 try/catch**。
   - 目录版 `Setup.exe --selftest`（非自解压路径）是 PASS 的，它也走了同样 XamlParse，所以这个概率低。
5. **H5：KLAP footer 在合成时 `indexOffset` 溢出或为 0 → HasEmbeddedBlob=false → FindPayloadDir 找不到 payload\ 就 Silent Exit**。
   - 合成完 Stage 4.2 独立 BlobVerifier 读 PASS + E2E 字节级比对 PASS。所以 EFI 变量损坏用户机器上是否会出现是本假设的疑点。概率低，先做 Blob 校验一锤定音。

## 已收集证据
- （空，等待插桩与复现）

## 下一步计划
1. 不改业务逻辑：先跑 Blob 完整性校验（独立读 1.01GB release EXE 的 footer + index 摘要）→ 否掉 H5。
2. 读事件查看器应用程序日志（近 10 分钟内 .NET Runtime/Application Error）→ 直接拿到未捕获异常堆栈 → 否掉或坐实 H1 / H4。
3. 没日志就做**最小插桩**：在 SetupProgram.Main 每个分支开头写 `File.AppendAllText` 文本文件（比如 `C:\ProgramData\KLA\setup-boot.log`），并包一个全局 `DispatcherUnhandledException` + `AppDomain.UnhandledException` 两个钩子 → 异常时写堆栈 → 让用户双击 → 读日志 → 定位具体哪一步。
4. 按证据最小修：要么改 PreparingWindow 线程逻辑，要么改 Main 工作目录 SetCurrentDirectory，要么写签名绕过 SmartScreen（后两个不现实，先排除前几个）。
5. 重新 Build Stage 3 → 合成 1GB 单 EXE → 请用户双击确认不再闪退。

## 关联文件
- installer/src/Setup.cs (SetupProgram.Main L45-140, PreparingWindow L1340, Blob 方法 L1095-1339)
- installer/build/admin.manifest
- app/build/Build.ps1 Stage 3/4
