// 卸载：把「部署」那 6 步按相反顺序撤销。
//
// 顺序：询问清备份 → 恢复 BootOrder + 删 BootXXXX → 清 ESP → 删救援分区 +
//       扩相邻 D 盘 → 清理日志/备份文件 → 清卸载注册表/快捷方式/安装目录。
// 每一步都「尽量执行」——中间某步失败不会把流程卡住，写日志继续往下走。
// 这样即使某个硬件上 WMI 删分区报错，用户也不会留下“卸载跑一半，
// 引导还在、分区不见了”这样最糟的半成品状态。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using System.Threading;

namespace KarlsLight.Access
{
    internal static class Uninstaller
    {
        private const string Ns = @"\\.\ROOT\Microsoft\Windows\Storage";
        private const string RecoveryGptType = "de94bba4-06d1-4d40-a16a-bfd50179d6ac";

        // 微软官方 bootx64.efi 大小通常 1.2~1.5MB；我们的 grubx64.efi 约 2.3MB。
        // 用大小粗筛区分 removable fallback 是不是被我们覆写的。
        // 同时用字节前 4 字节签名兜底：GRUB 的 EFI PE 头里一般带 "GRUB" 字符串，
        // 但比大小比较更脆弱；这里先用大小作为主判据。
        private const int BootxEfiMinOurSize = 1800000;

        private static StringBuilder _log;
        private static List<string> _warnings;

        public static int Run(bool quiet)
        {
            _log = new StringBuilder();
            _warnings = new List<string>();

            Log("=== " + Branding.ProductName + " 卸载 ===");
            Log("时间：" + DateTime.Now.ToString("s"));
            Log("模式：" + (quiet ? "静默" : "交互"));

            // 1) 管理员校验
            if (!IsElevated())
            {
                if (!quiet)
                    MessageBox.Show(
                        Branding.ProductName + " 卸载需要管理员权限。\n请右键程序图标，选择「以管理员身份运行」。",
                        Branding.ShortName, MessageBoxButton.OK, MessageBoxImage.Warning);
                Log("缺少管理员权限，中止。");
                FlushLog();
                return 5; // ERROR_ACCESS_DENIED
            }

            // 2) 读部署记录（决定删哪块分区 / 哪个 Boot slot）
            JsonValue record = Deployment.ReadRecord();
            bool hasDeploy = Deployment.IsInstalled();

            if (!quiet)
            {
                string msg = "您确定要卸载 " + Branding.ProductName + " 吗？\n\n" +
                             "系统会：\n" +
                             "  · 恢复 Windows 原始启动顺序，并删除 " + Branding.ProductName + " 的引导项；\n" +
                             "  · 删除隐藏的救援分区，并把 1 GB 空间归还给相邻的 Windows 分区；\n" +
                             "  · 清理 EFI 系统分区上的 GRUB 目录；\n" +
                             "  · 删除桌面与开始菜单快捷方式、卸载注册表项。\n\n" +
                             "是否同时清除所有备份文件和备份记录？\n\n" +
                             "    · 点“是(Y)”：同时删除备份目录（D:\\KLA）、启动顺序备份、\n" +
                             "                  deploy.log / app.log 等文件；\n" +
                             "    · 点“否(N)”：只卸载引导与隐藏分区，保留全部备份，\n" +
                             "                  以后重安装可以直接接着用。";
                MessageBoxResult r = MessageBox.Show(
                    msg, Branding.ShortName + " 卸载确认",
                    MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.No);
                if (r == MessageBoxResult.Cancel)
                {
                    Log("用户取消卸载。");
                    FlushLog();
                    return 0;
                }
                bool purge = (r == MessageBoxResult.Yes);
                DoRun(record, hasDeploy, purge, quiet);
            }
            else
            {
                // --quiet：默认不清备份（保守），避免静默时把用户的备份误删。
                DoRun(record, hasDeploy, false, true);
            }

            FlushLog();

            if (!quiet)
            {
                string doneMsg = Branding.ProductName + " 已卸载完毕。\n";
                if (_warnings.Count > 0)
                {
                    doneMsg += "\n卸载过程中发现 " + _warnings.Count.ToString() + " 条警告（已写入日志：";
                    doneMsg += LogFilePath() + "）：\n";
                    for (int i = 0; i < Math.Min(_warnings.Count, 8); i++)
                        doneMsg += "\n  · " + _warnings[i];
                    if (_warnings.Count > 8)
                        doneMsg += "\n  ……其余请查看日志。";
                    doneMsg += "\n";
                }
                else
                {
                    doneMsg += "所有分区与引导已正常恢复。";
                }
                MessageBox.Show(doneMsg, Branding.ShortName,
                    MessageBoxButton.OK, _warnings.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            return 0;
        }

        // ── 主顺序 ────────────────────────────────────────────────
        private static void DoRun(JsonValue record, bool hasDeploy, bool purgeBackups, bool quiet)
        {
            // Step 1：恢复 BootOrder + 删除 KLA BootXXXX
            // （这一步优先：即使后面删分区失败，重启也不会再进 KLA）
            Try("恢复固件启动顺序", delegate
            {
                int err;
                if (!Firmware.EnablePrivilege(out err))
                {
                    Warn("无法启用固件变量访问权限（错误码 " + err.ToString() + "）。");
                    return;
                }
                RestoreBootOrderFromBackup();
                DeleteAccessBootEntries();
            });

            // Step 2：清 ESP 上三棵树（EFI\KLA、\boot\grub、EFI\Boot\bootx64.efi）
            Try("清理 EFI 系统分区（KLA 引导/GRUB）", delegate
            {
                CleanEsp();
            });

            // Step 3：找救援分区 → 删除 → 相邻 D 卷 Extend
            Try("删除救援分区并归还给相邻分区", delegate
            {
                PartInfo rescue = FindRescueAny(record);
                if (rescue == null)
                {
                    Log("  （未检测到救援分区，跳过）");
                    return;
                }
                PartInfo adjacent = FindAdjacentVolumeToGrow(rescue, record);
                DeletePartition(rescue);
                if (adjacent != null)
                    ExtendPartition(adjacent, rescue.Size);
                else
                    Warn("没有找到可以合并的相邻 NTFS 分区，被删出来的 1 GB 将保留为未分配空间，" +
                         "可以稍后到「磁盘管理」里手动扩展。");
            });

            // Step 4：备份文件清理（可选）
            if (purgeBackups)
                Try("清除备份文件与备份记录", CleanBackups);

            // Step 5：清卸载注册表 + 删快捷方式 + 删 ProgramData KLA
            Try("清除注册表与快捷方式", delegate
            {
                CleanUninstallRegistry();
                CleanShortcuts();
            });

            // Step 6：记录 deploy.done + 删 deploy.json + 删备份引擎 wimlib
            Try("清理部署记录文件", delegate
            {
                string data = Firmware.DataDir();
                string deployJson = Path.Combine(data, "deploy.json");
                if (File.Exists(deployJson))
                {
                    try { File.Delete(deployJson); }
                    catch (Exception ex) { Warn("删除 deploy.json 失败：" + ex.Message); }
                }

                // 备份引擎 wimlib（%ProgramData%\KLA\bin\，Setup 安装时部署）。
                // 它是程序组件不是用户数据，卸载必须带走，否则机器上留下
                // 一份孤儿 GPL 二进制；重装时 Setup 会重新铺一份。
                string binDir = Path.Combine(data, "bin");
                if (Directory.Exists(binDir))
                {
                    try { Directory.Delete(binDir, recursive: true); }
                    catch (Exception ex) { Warn("删除备份引擎目录 " + binDir + " 失败：" + ex.Message); }
                }

                // 卸载完毕写一个 .done 标记，便于排查“卸载是否真的跑过”
                string doneFile = Path.Combine(data, "uninstall.done.log");
                try { File.AppendAllText(doneFile, _log.ToString(), new UTF8Encoding(false)); }
                catch { }
            });

            // Step 7：把 KarlsLightAccess.exe 所在目录（安装目录）整体加入
            // 重启后删除批处理。exe 自己被进程占用不能直接删；所以只删子文件
            // + 在 %TEMP% 写 RunOnce：重启时 cmd /c rmdir。
            Try("清理安装目录（文件、快捷方式所在目录）", CleanInstallDirectory);
        }

        private static void Try(string caption, Action a)
        {
            Log("[" + caption + "] 开始…");
            try { a(); Log("[" + caption + "] 完成。"); }
            catch (Exception ex)
            {
                string msg = caption + " 失败：" + ex.GetType().Name + ": " + ex.Message;
                Warn(msg);
                Log("  FAIL: " + msg);
            }
        }

        // ── Step1：固件 ──────────────────────────────────────────
        private static void RestoreBootOrderFromBackup()
        {
            ushort[] backup;
            try { backup = Firmware.LoadBootOrderBackup(); }
            catch (FileNotFoundException)
            {
                Warn("没有发现 bootorder.bak（可能从未在这台机器部署过）。将只删除 KLA 引导项而不主动改 BootOrder。");
                return;
            }
            catch (Exception ex)
            {
                Warn("读取 bootorder.bak 失败：" + ex.Message);
                return;
            }
            if (backup == null || backup.Length == 0)
            {
                Warn("备份 BootOrder 是空数组，拒绝写回（可能导致机器无项可启）。");
                return;
            }

            byte[] buf = new byte[backup.Length * 2];
            for (int i = 0; i < backup.Length; i++)
                BitConverter.GetBytes(backup[i]).CopyTo(buf, i * 2);
            int err;
            if (!Firmware.Write("BootOrder", buf, out err))
                throw new InvalidOperationException("写回 BootOrder 失败：错误码 " + err.ToString());
            Log("  BootOrder 已还原为备份（" + backup.Length.ToString() + " 项）。");
        }

        private static void DeleteAccessBootEntries()
        {
            // 不依赖 record.boot.slot：部署多次可能留好几个，按名字/路径整批清。
            // 读当前 BootOrder，把匹配 KLA 的项都删；BootOrder 里这些项在 RestoreBootOrderFromBackup
            // 已被替换为备份，但项本身还存在 UEFI BootXXXX 变量，得单独清。
            List<BootEntry> all = new List<BootEntry>();
            ushort[] order = Firmware.GetBootOrder();
            foreach (ushort n in order)
            {
                BootEntry e = Firmware.GetBootEntry(n);
                if (e != null) all.Add(e);
            }
            // 另外从 0000..00FF 扫一遍，BootOrder 不包含但遗留的 BootXXXX 也要清（避免脏槽）。
            for (int n = 0; n <= 0x00FF; n++)
            {
                bool already = false;
                foreach (BootEntry x in all)
                    if (x.Number == n) { already = true; break; }
                if (already) continue;
                BootEntry e = Firmware.GetBootEntry(n);
                if (e == null) continue;
                all.Add(e);
            }

            int deleted = 0;
            foreach (BootEntry e in all)
            {
                if (!IsOurEntry(e)) continue;
                try
                {
                    DeleteBootVar(e.Number);
                    deleted++;
                    Log("  已删除 Boot" + e.Number.ToString("X4") +
                        "（" + (e.Description ?? "") + "）。");
                }
                catch (Exception ex)
                {
                    Warn("删除 Boot" + e.Number.ToString("X4") + " 失败：" + ex.Message);
                }
            }
            if (deleted == 0)
                Log("  未找到 " + Branding.ShortName + " 的引导项（可能之前已经删过）。");
            else
                Log("  合计删除 " + deleted.ToString() + " 个 " + Branding.ShortName + " 引导项。");
        }

        private static bool IsOurEntry(BootEntry e)
        {
            if (e == null) return false;
            string desc = (e.Description ?? "").ToLowerInvariant();
            string path = (e.FilePath ?? "").ToLowerInvariant();
            if (desc.IndexOf(Branding.ProductName.ToLowerInvariant()) >= 0) return true;
            if (desc.IndexOf("KARL'S LIGHT".ToLowerInvariant()) >= 0) return true;
            if (path.IndexOf("\\efi\\kla\\") >= 0) return true;
            return false;
        }

        /// <summary>
        /// 通过 SetFirmwareEnvironmentVariableW 写入 0 长度删除 UEFI BootXXXX。
        /// 这是规范定义的删除语义，但 Firmware.Write 为避免误删专门拦住了空写，
        /// 所以这里直接走底层 P/Invoke 手动删。
        /// </summary>
        private static void DeleteBootVar(int number)
        {
            int err;
            Firmware.EnablePrivilege(out err);
            string name = "Boot" + number.ToString("X4");
            bool ok = SetFirmwareEnvDel(name);
            if (!ok)
            {
                int e = Marshal.GetLastWin32Error();
                // 2=ERROR_FILE_NOT_FOUND 可以接受（项不存在）。
                if (e != 2)
                    throw new InvalidOperationException("删除 UEFI 变量 " + name +
                        " 失败，错误码 " + e.ToString());
            }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetFirmwareEnvironmentVariableW(
            string lpName, string lpGuid, byte[] pValue, uint nSize);

        private static bool SetFirmwareEnvDel(string name)
        {
            return SetFirmwareEnvironmentVariableW(name,
                "{8be4df61-93ca-11d2-aa0d-00e098032b8c}", null, 0u);
        }

        // ── Step2：ESP 清理 ──────────────────────────────────────
        private static void CleanEsp()
        {
            // 我们把每个盘的 ESP 都可能扫一遍，但是 Windows 只有启动盘有 EFI。
            List<uint> disks = new List<uint>();
            foreach (DiskInfo d in Storage.ListDisks())
            {
                disks.Add(d.Number);
            }

            int cleaned = 0;
            foreach (uint dn in disks)
            {
                EspInfo2 esp = FindEsp2(dn);
                if (esp == null) continue;
                string letter = null;
                try
                {
                    letter = MountEsp2(esp);
                    WaitForVolumeReady2(letter + @":\");
                    string root = letter + @":\";
                    int rm = 0;
                    SafeRmDir(Path.Combine(root, @"EFI\KLA\"), ref rm, "EFI\\KLA");
                    SafeRmDir(Path.Combine(root, @"boot\grub\"), ref rm, "boot\\grub");
                    // EFI\Boot\bootx64.efi：我们覆写过 removable fallback 就删；
                    // 否则保留微软原版（否则某些品牌机 fallback 启不动）。
                    string fbPath = Path.Combine(root, @"EFI\Boot\bootx64.efi");
                    if (File.Exists(fbPath))
                    {
                        try
                        {
                            long len = new FileInfo(fbPath).Length;
                            // 我们的 grubx64.efi 约 2.3MB；微软 bootx64.efi 约 1.2MB。
                            if (len >= BootxEfiMinOurSize)
                            {
                                try { File.SetAttributes(fbPath, FileAttributes.Normal); } catch { }
                                File.Delete(fbPath);
                                Log("  删除 EFI\\Boot\\bootx64.efi（大小 " +
                                    len.ToString() + " B，判断为 " + Branding.ShortName + " fallback）。");
                                rm++;
                            }
                            else
                                Log("  保留 EFI\\Boot\\bootx64.efi（大小 " +
                                    len.ToString() + " B，判断为系统原版）。");
                        }
                        catch (Exception ex) { Warn("检查 removable fallback 失败：" + ex.Message); }
                    }
                    if (rm > 0)
                    {
                        cleaned++;
                        Log("  盘" + dn.ToString() + " ESP 清理完毕（移除 " + rm.ToString() + " 组目录/文件）。");
                    }
                }
                finally
                {
                    if (letter != null)
                    {
                        try { UnmountEsp2(esp, letter); }
                        catch (Exception ex) { Log("  卸载 ESP 盘符（可忽略）：" + ex.Message); }
                    }
                }
            }
            if (cleaned == 0)
                Log("  （所有磁盘 ESP 未发现 KLA 目录，跳过）。");
        }

        /// <summary>递归删除目录（只删我们自己知道的目录名，避免硬编码盘符误删）。</summary>
        private static void SafeRmDir(string dir, ref int counter, string tag)
        {
            if (!Directory.Exists(dir)) return;
            try
            {
                Directory.Delete(dir, recursive: true);
                Log("    已删除目录 " + tag);
                counter++;
            }
            catch (Exception ex)
            {
                // FAT32 上有些临时文件可能被系统占用，单文件逐个删一遍再删根
                try
                {
                    foreach (string f in Directory.GetFiles(dir))
                    {
                        try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                        try { File.Delete(f); } catch { }
                    }
                    foreach (string s in Directory.GetDirectories(dir))
                    {
                        try { Directory.Delete(s, recursive: true); } catch { }
                    }
                    if (Directory.GetFiles(dir).Length == 0 &&
                        Directory.GetDirectories(dir).Length == 0)
                    {
                        Directory.Delete(dir, false);
                        Log("    已删除目录 " + tag + "（fallback 逐文件删除后成功）。");
                        counter++;
                        return;
                    }
                }
                catch { }
                Warn("删除 " + dir + " 失败：" + ex.Message);
            }
        }

        // ESP/Mount/Unmount 的本地拷贝（与 Deployment 保持同样的 SELECT + AddAccessPath 逻辑）
        // 因为 Deployment 这些方法是 private，同程序集也得反射才能碰；直接再写一份更小更安全。
        private class EspInfo2
        {
            public uint DiskNumber;
            public uint PartitionNumber;
        }

        private static EspInfo2 FindEsp2(uint diskNumber)
        {
            ManagementScope scope = new ManagementScope(Ns);
            scope.Connect();
            foreach (ManagementObject p in new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT * FROM MSFT_Partition WHERE DiskNumber=" + diskNumber)).Get())
            {
                string type = p["GptType"] == null ? "" : p["GptType"].ToString();
                if (type.IndexOf("c12a7328-f81f-11d2-ba4b-00a0c93ec93b",
                        StringComparison.OrdinalIgnoreCase) < 0) continue;
                EspInfo2 e = new EspInfo2();
                e.DiskNumber = diskNumber;
                e.PartitionNumber = Convert.ToUInt32(p["PartitionNumber"], CultureInfo.InvariantCulture);
                return e;
            }
            return null;
        }

        private static string MountEsp2(EspInfo2 esp)
        {
            char letter = FreeDriveLetter2();
            ManagementScope scope = new ManagementScope(Ns);
            scope.Connect();
            ObjectQuery q = new ObjectQuery(
                "SELECT * FROM MSFT_Partition WHERE DiskNumber=" + esp.DiskNumber +
                " AND PartitionNumber=" + esp.PartitionNumber);
            using (ManagementObjectSearcher s = new ManagementObjectSearcher(scope, q))
            {
                foreach (ManagementObject mo in s.Get())
                {
                    ManagementBaseObject inp = mo.GetMethodParameters("AddAccessPath");
                    inp["AccessPath"] = letter + ":\\";
                    ManagementBaseObject outp = mo.InvokeMethod("AddAccessPath", inp, null);
                    uint rc = Convert.ToUInt32(outp["ReturnValue"], CultureInfo.InvariantCulture);
                    if (rc != 0)
                        throw new InvalidOperationException("挂载 ESP 失败，返回码 " + rc.ToString());
                    return letter.ToString();
                }
            }
            throw new InvalidOperationException("找不到 ESP 分区 " + esp.PartitionNumber + "。");
        }

        private static void UnmountEsp2(EspInfo2 esp, string letter)
        {
            ManagementScope scope = new ManagementScope(Ns);
            scope.Connect();
            ObjectQuery q = new ObjectQuery(
                "SELECT * FROM MSFT_Partition WHERE DiskNumber=" + esp.DiskNumber +
                " AND PartitionNumber=" + esp.PartitionNumber);
            using (ManagementObjectSearcher s = new ManagementObjectSearcher(scope, q))
            {
                foreach (ManagementObject mo in s.Get())
                {
                    ManagementBaseObject inp = mo.GetMethodParameters("RemoveAccessPath");
                    inp["AccessPath"] = letter + ":\\";
                    mo.InvokeMethod("RemoveAccessPath", inp, null);
                    return;
                }
            }
        }

        private static char FreeDriveLetter2()
        {
            HashSet<char> used = new HashSet<char>();
            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                    used.Add(char.ToUpperInvariant(d.Name[0]));
            }
            catch { }
            for (char c = 'Z'; c >= 'E'; c--)
                if (!used.Contains(c)) return c;
            throw new InvalidOperationException("没有空闲盘符可用于临时挂载 ESP。");
        }

        private static void WaitForVolumeReady2(string root)
        {
            int stable = 0;
            for (int i = 0; i < 100; i++)
            {
                try { if (Directory.Exists(root)) stable++; else stable = 0; }
                catch { stable = 0; }
                if (stable >= 5) return;
                Thread.Sleep(50);
            }
            throw new InvalidOperationException("ESP 盘符 " + root + " 5 秒内未就绪。");
        }

        // ── Step3：删 Rescue 分区 + 扩相邻 D 盘 ──────────────────
        private static PartInfo FindRescueAny(JsonValue record)
        {
            // 优先用 deploy.json 的记录（准确），否则全磁盘扫像我们建的
            if (record != null)
            {
                JsonValue rp = record["rescuePartition"];
                if (rp != null)
                {
                    uint disk = (uint)rp["diskNumber"].AsLong(0xFFFFFFFF);
                    uint part = (uint)rp["partitionNumber"].AsLong(0xFFFFFFFF);
                    if (disk != 0xFFFFFFFFu)
                    {
                        PartInfo p = FindByDiskPart(disk, part);
                        if (p != null)
                        {
                            if (LooksLikeOurRescue(p)) return p;
                            Log("  deploy.json 记录的分区 #" + part + " 不符合救援分区特征，改全局扫。");
                        }
                    }
                }
            }
            foreach (DiskInfo d in Storage.ListDisks())
            {
                foreach (PartInfo p in d.Partitions)
                {
                    if (LooksLikeOurRescue(p)) return p;
                }
            }
            return null;
        }

        private static bool LooksLikeOurRescue(PartInfo p)
        {
            if (p.Size < Storage.RescueBytes / 2) return false;
            if (string.Equals(p.GptType ?? "", RecoveryGptType, StringComparison.OrdinalIgnoreCase))
            {
                if (p.Size >= Storage.RescueBytes && p.Size <= Storage.RescueBytes * 2)
                    return true;
            }
            if (!p.HasLetter && p.IsHidden && p.Size == Storage.RescueBytes) return true;
            return false;
        }

        private static PartInfo FindByDiskPart(uint disk, uint part)
        {
            foreach (DiskInfo d in Storage.ListDisks())
            {
                if (d.Number != disk) continue;
                foreach (PartInfo p in d.Partitions)
                {
                    if (p.PartitionNumber == part) return p;
                }
            }
            return null;
        }

        private static PartInfo FindAdjacentVolumeToGrow(PartInfo rescue, JsonValue record)
        {
            // 按 deploy.json 记录找"从哪个分区缩的"——如果它还在就是首选。
            string shrunkLetter = null;
            if (record != null)
            {
                JsonValue s = record["shrunkFrom"];
                if (s != null)
                    shrunkLetter = (s["driveLetter"] == null) ? null : s["driveLetter"].AsString(null);
            }

            // 按物理偏移前后查，找相邻且是 NTFS/有盘符的分区（在 rescue 前面还是后面，
            // 取决于用户盘布局；Windows 扩展分区时只能往后吃未分配，所以找 rescue
            // 前一号分区：我们 CreateRescuePartition 建在 D: 后面，通常 rescue.Offset > D.Offset）
            DiskInfo disk = null;
            int idx = -1;
            foreach (DiskInfo d in Storage.ListDisks())
            {
                if (d.Number != rescue.DiskNumber) continue;
                // 按 Offset 排序
                List<PartInfo> ps = new List<PartInfo>(d.Partitions);
                ps.Sort(delegate(PartInfo a, PartInfo b)
                {
                    return a.Offset.CompareTo(b.Offset);
                });
                for (int i = 0; i < ps.Count; i++)
                {
                    if (ps[i].PartitionNumber == rescue.PartitionNumber)
                    {
                        idx = i; disk = d; break;
                    }
                }
                if (disk != null)
                {
                    // 前置候选：idx-1 必须在 rescue 前面，且 Offset+Size == rescue.Offset（紧邻）
                    if (idx > 0)
                    {
                        PartInfo prev = ps[idx - 1];
                        ulong prevEnd = checked(prev.Offset + prev.Size);
                        if (prevEnd == rescue.Offset)
                        {
                            if (CanGrowInto(prev))
                            {
                                if (string.IsNullOrEmpty(shrunkLetter) ||
                                    prev.DriveLetter == shrunkLetter)
                                {
                                    Log("  确定待扩展分区：" + prev.Display + "（紧邻 rescue 之前）。");
                                    return prev;
                                }
                            }
                        }
                    }
                    // 后置候选（极少，但保险）
                    if (idx + 1 < ps.Count)
                    {
                        PartInfo next = ps[idx + 1];
                        ulong rescueEnd = checked(rescue.Offset + rescue.Size);
                        if (rescueEnd == next.Offset && CanGrowInto(next))
                            return next;
                    }
                    // 如果没有严格相邻，按 shrunkLetter 兜底（用户磁盘已有别的工具加了小分区）
                    foreach (PartInfo p in ps)
                    {
                        if (!string.IsNullOrEmpty(shrunkLetter) && p.DriveLetter == shrunkLetter &&
                            CanGrowInto(p))
                        {
                            Warn("未找到与 rescue 严格相邻的卷，按部署记录使用 " +
                                shrunkLetter + ": 进行扩展（可能有一小段未分配留在中间）。");
                            return p;
                        }
                    }
                    break;
                }
            }
            return null;
        }

        private static bool CanGrowInto(PartInfo p)
        {
            if (p == null) return false;
            if (!p.HasLetter) return false;
            if (string.IsNullOrEmpty(p.FileSystem))
            {
                // Storage 里的 FileSystem 是 Volume 配对后赋的，可能为空；
                // 退化为按字母判断：只能 NTFS。
                try
                {
                    DriveInfo di = new DriveInfo(p.DriveLetter);
                    if (!string.Equals(di.DriveFormat, "NTFS",
                        StringComparison.OrdinalIgnoreCase)) return false;
                }
                catch { return false; }
            }
            else if (!string.Equals(p.FileSystem, "NTFS",
                         StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return true;
        }

        private static void DeletePartition(PartInfo p)
        {
            ManagementScope scope = new ManagementScope(Ns);
            scope.Connect();
            ObjectQuery q = new ObjectQuery(
                "SELECT * FROM MSFT_Partition WHERE DiskNumber=" + p.DiskNumber +
                " AND PartitionNumber=" + p.PartitionNumber);
            using (ManagementObjectSearcher s = new ManagementObjectSearcher(scope, q))
            {
                foreach (ManagementObject mo in s.Get())
                {
                    ManagementBaseObject outp = mo.InvokeMethod("DeleteObject", null, null);
                    uint rc = Convert.ToUInt32(outp["ReturnValue"], CultureInfo.InvariantCulture);
                    if (rc != 0)
                    {
                        string ext = "";
                        try { ext = (outp["ExtendedStatus"] ?? "").ToString(); } catch { }
                        throw new InvalidOperationException("MSFT_Partition.DeleteObject 返回码 " +
                            rc.ToString() + (string.IsNullOrEmpty(ext) ? "" : "：" + ext));
                    }
                    Log("  已删除隐藏救援分区：盘" + p.DiskNumber.ToString() + " 分区" +
                        p.PartitionNumber.ToString() + "（大小 " + Storage.Fmt(p.Size) + "）。");
                    return;
                }
            }
            throw new InvalidOperationException("WMI 未能定位要删除的分区。");
        }

        private static void ExtendPartition(PartInfo p, ulong addBytes)
        {
            // GetSupportedSize：SizeMax = 这个分区当前能扩到的最大 Size（如果后面正好
            // 是未分配空间，SizeMax 就等于 CurrentSize + 未分配大小；否则不会把
            // 后面 NTFS 的空间吃过去，符合预期）。
            ulong sizeMin, sizeMax;
            if (!Storage.GetSupportedSize(p, out sizeMin, out sizeMax))
                throw new InvalidOperationException("查询分区可扩展上限失败。");

            ulong want = checked(p.Size + addBytes);
            ulong actual = Math.Min(want, sizeMax);
            if (actual <= p.Size)
            {
                Warn("相邻分区没有可扩展空间（GetSupportedSize.SizeMax = " +
                     Storage.Fmt(sizeMax) + "），将保留为未分配空间。");
                return;
            }

            ManagementScope scope = new ManagementScope(Ns);
            scope.Connect();
            ObjectQuery q = new ObjectQuery(
                "SELECT * FROM MSFT_Partition WHERE DiskNumber=" + p.DiskNumber +
                " AND PartitionNumber=" + p.PartitionNumber);
            using (ManagementObjectSearcher s = new ManagementObjectSearcher(scope, q))
            {
                foreach (ManagementObject mo in s.Get())
                {
                    ManagementBaseObject inp = mo.GetMethodParameters("Resize");
                    inp["Size"] = actual;
                    ManagementBaseObject outp = mo.InvokeMethod("Resize", inp, null);
                    uint rc = Convert.ToUInt32(outp["ReturnValue"], CultureInfo.InvariantCulture);
                    if (rc != 0)
                    {
                        string ext = "";
                        try { ext = (outp["ExtendedStatus"] ?? "").ToString(); } catch { }
                        throw new InvalidOperationException("扩展相邻分区失败，返回码 " +
                            rc.ToString() + (string.IsNullOrEmpty(ext) ? "" : "：" + ext));
                    }
                    Log("  已把 " + p.Display + " 从 " + Storage.Fmt(p.Size) + " 扩展到 " +
                        Storage.Fmt(actual) + "（+ " + Storage.Fmt(actual - p.Size) + "）。");
                    return;
                }
            }
            throw new InvalidOperationException("WMI 未能定位要扩展的分区。");
        }

        // ── Step4：清理备份文件 ────────────────────────────────────
        private static void CleanBackups()
        {
            string dataDir = Firmware.DataDir();
            // D:\KLA 备份目录默认在 Wizard 里选的就是它；也可能用户改到别的盘。
            // 为了不误删用户其它目录，只删我们约定的根目录（可改），并且
            // 只在目录里存在 .kla marker 或 "backups" / "snapshots" 子目录时才真删。
            string[] candidateBackupRoots = new string[]
            {
                @"D:\KLA",
                @"E:\KLA",
                Path.Combine(dataDir, "backups"),
            };
            foreach (string r in candidateBackupRoots)
            {
                if (!Directory.Exists(r)) continue;
                if (!LooksLikeOurBackupRoot(r))
                {
                    Warn("可疑备份目录：" + r + " 不包含 " + Branding.ShortName +
                         " 的标记文件/目录，为安全起见未自动删除（请手动检查）。");
                    continue;
                }
                try
                {
                    Directory.Delete(r, recursive: true);
                    Log("  已删除备份目录：" + r);
                }
                catch (Exception ex) { Warn("删除备份目录 " + r + " 失败：" + ex.Message); }
            }

            // bootorder.bak/bootorder.json/deploy.log/app.log/settings.json（保留隐私？不，用户点了"是清所有备份"）
            // · settings.json 必须一起清：里面存 licenseAgreed / onboardingDone，
            //   残留会导致再装主程序后跳过 LegalWindow + Wizard，用户体验像「引导页没了」。
            string[] filesToKill = new string[]
            {
                "bootorder.bak",
                "bootorder.json",
                "deploy.log",
                "app.log",
                "shots.log",
                "settings.json",
            };
            foreach (string fn in filesToKill)
            {
                string p = Path.Combine(dataDir, fn);
                try
                {
                    if (File.Exists(p))
                    {
                        File.Delete(p);
                        Log("  已删除数据文件：" + fn);
                    }
                }
                catch (Exception ex) { Warn("删除 " + fn + " 失败：" + ex.Message); }
            }
        }

        private static bool LooksLikeOurBackupRoot(string dir)
        {
            try
            {
                // Wizard 备份的目标目录至少包含一个名为 backups/snapshots 或 .kla 的痕迹
                foreach (string sub in Directory.GetDirectories(dir))
                {
                    string name = Path.GetFileName(sub);
                    if (string.Equals(name, "backups", StringComparison.OrdinalIgnoreCase)) return true;
                    if (string.Equals(name, "snapshots", StringComparison.OrdinalIgnoreCase)) return true;
                }
                foreach (string f in Directory.GetFiles(dir))
                {
                    string name = Path.GetFileName(f);
                    if (string.Equals(name, ".kla", StringComparison.OrdinalIgnoreCase)) return true;
                    if (name.StartsWith("KLA-", StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { return false; }
            return false;
        }

        // ── Step5：注册表 + 快捷方式 ────────────────────────────
        private static void CleanUninstallRegistry()
        {
            string keyPath64 = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" +
                "KARLSLIGHTACCESS_" + Branding.Version.Replace('.', '_');
            string keyPath32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\" +
                "KARLSLIGHTACCESS_" + Branding.Version.Replace('.', '_');
            int removed = 0;
            using (Microsoft.Win32.RegistryKey hklm =
                Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine,
                    Microsoft.Win32.RegistryView.Registry64))
            {
                try { hklm.DeleteSubKeyTree(keyPath64, false); removed++; }
                catch (ArgumentException) { /* 不存在 */ }
                catch (Exception ex) { Warn("删除卸载注册表 (64) 失败：" + ex.Message); }
                try { hklm.DeleteSubKeyTree(keyPath32, false); removed++; }
                catch (ArgumentException) { }
                catch (Exception ex) { Warn("删除卸载注册表 (WOW64) 失败：" + ex.Message); }
            }
            Log("  清理卸载注册表项（已删除 " + removed.ToString() + " 个分支）。");
        }

        private static void CleanShortcuts()
        {
            string lnkName = Branding.ProductName + ".lnk";
            string[] locations = new string[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            };
            foreach (string d in locations)
            {
                string lnk = Path.Combine(d, lnkName);
                try
                {
                    if (File.Exists(lnk)) { File.Delete(lnk); Log("  已删快捷方式：" + lnk); }
                }
                catch (Exception ex) { Warn("删除快捷方式 " + lnk + " 失败：" + ex.Message); }
            }
        }

        // ── Step7：清理安装目录 ────────────────────────────────────
        private static void CleanInstallDirectory()
        {
            // 找自身 exe 所在目录。它应当就是 InstallLocation。
            string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrEmpty(exePath)) return;
            string installDir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrEmpty(installDir)) return;

            // 1) 能直接删的子文件/子目录先删
            foreach (string f in Directory.GetFiles(installDir))
            {
                string name = Path.GetFileName(f);
                if (string.Equals(name, Path.GetFileName(exePath),
                        StringComparison.OrdinalIgnoreCase)) continue;   // exe 自己，留给 RunOnce
                if (name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(f); }
                catch (Exception ex) { Log("  删除文件（可忽略）：" + name + " → " + ex.Message); }
            }
            foreach (string d in Directory.GetDirectories(installDir))
            {
                try { Directory.Delete(d, recursive: true); }
                catch (Exception ex) { Log("  删除目录（可忽略）：" + d + " → " + ex.Message); }
            }

            // 2) 写 RunOnce：下次登录自动 cmd /c rmdir /s /q 整个安装目录；
            //    KarlsLightAccess.exe 在卸载返回后进程退出，再下一登录就能清。
            //    时序炸弹防御：卸载时在目录里放一个标记文件，bat 先验证标记再删。
            //    否则「卸载→没重启→重装回同一目录→重启」会把新装的程序整目录
            //    删掉——用户一觉醒来发现主程序凭空消失，就是这条路。
            //    Setup 安装时也会删 RunOnce 键 + 标记文件，两道闸。
            try
            {
                string flagPath = Path.Combine(installDir, "_kla_uninstall.flag");
                try { File.WriteAllText(flagPath, "uninstalled", Encoding.ASCII); }
                catch { /* exe 被占也写不了就只剩 RunOnce 删键那道闸 */ }

                using (Microsoft.Win32.RegistryKey hklm =
                    Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine,
                        Microsoft.Win32.RegistryView.Registry64))
                using (Microsoft.Win32.RegistryKey run =
                    hklm.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", true))
                {
                    string batPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp",
                        "_kla_uninstall_" + Guid.NewGuid().ToString("N") + ".cmd");
                    string bat =
                        "@echo off\r\n" +
                        "ping 127.0.0.1 -n 3 > nul\r\n" +
                        "if not exist \"" + flagPath + "\" goto :end\r\n" +
                        "rmdir /s /q \"" + installDir + "\" 2>nul\r\n" +
                        ":end\r\n" +
                        "del /f /q \"" + batPath + "\"\r\n";
                    File.WriteAllText(batPath, bat, Encoding.ASCII);
                    run.SetValue("KARLSLIGHTACCESS-RM-INSTALL",
                        "\"" + Environment.GetFolderPath(Environment.SpecialFolder.System) +
                        "\\cmd.exe\" /c \"\"" + batPath + "\"\"");
                    Log("  已写入 RunOnce 清理安装目录：下次登录将自动删除 " + installDir);
                }
            }
            catch (Exception ex) { Warn("写 RunOnce 清理脚本失败（安装目录要手动删）：" + ex.Message); }
        }

        // ── 通用 ────────────────────────────────────────────────
        private static bool IsElevated()
        {
            try
            {
                WindowsIdentity id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        private static void Log(string msg)
        {
            if (_log == null) return;
            _log.AppendLine(msg);
        }

        private static void Warn(string msg)
        {
            if (_warnings == null) _warnings = new List<string>();
            _warnings.Add(msg);
            Log("WARN: " + msg);
        }

        private static string LogFilePath()
        {
            return Path.Combine(Firmware.DataDir(), "uninstall.log");
        }

        private static void FlushLog()
        {
            try
            {
                string dir = Firmware.DataDir();
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string path = LogFilePath();
                string hdr = "\r\n\r\n---------------------------------------\r\n" +
                             "卸载会话：" + DateTime.Now.ToString("s") + "\r\n";
                File.AppendAllText(path, hdr + _log.ToString(), new UTF8Encoding(false));
                // 同步写一份到 %TEMP%，防 ProgramData 被保护时至少还有痕迹
                try
                {
                    string tp = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Temp\\KLA-uninstall.log");
                    File.AppendAllText(tp, hdr + _log.ToString(), new UTF8Encoding(false));
                }
                catch { }
            }
            catch { }
        }
    }
}
