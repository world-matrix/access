// 备份引擎。
//
// 这个文件实现的是 backup-design.md §4 定的那份 index.json —— 它是 Windows
// 主程序和 ACCESS 救援端之间**唯一的**接口。字段名、目录名、时间格式都是硬契约，
// 改这里就必须同步改 server.py，否则 Windows 里做的备份在 ACCESS 里读不出来，
// 整个方案就废了。
//
// 几条不能动的：
//   · 存储根目录必须叫 KLA。救援端不认盘符，它遍历所有 NTFS 卷找根下的 KLA\
//     （server.py 的 find_kla_store）。
//   · 时间一律 UTC，ISO 8601 带 Z。本地时间在跨时区/夏令时的机器上会让
//     增量链的先后顺序错乱。
//   · index.json 原子写。断电写坏索引 = 所有备份都还在但一个都找不到。
//   · schemaVersion 不认识就只读不改——宁可这个版本用不了，也不能把
//     新版本写的索引降级破坏掉。
//
// 捕获必须带 --snapshot（VSS）。不走卷影副本，注册表 hive 和正在写入的文件
// 会捕获失败或产生不一致的快照，还原出来的系统可能起不来。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace KarlsLight.Access
{
    /// <summary>备份范围。语义见 backup-design.md §1，代号两侧共用。</summary>
    internal enum BackupScope
    {
        L0,   // 出厂母盘
        L1,   // 完整系统（含用户数据）
        L2,   // 系统与程序（排除用户 profile 数据区）
        L3,   // 用户数据
        L4    // 纯系统：Windows+驱动+系统服务，不含应用软件和个人文件
    }

    internal class BackupOptions
    {
        public BackupScope Scope = BackupScope.L2;
        public string StoreRoot;          // ...\KLA
        public bool SetAsFactoryBaseline;
    }

    /// <summary>index.json 里 entries[] 的一条。字段名和 JSON 一一对应。</summary>
    internal class BackupEntry
    {
        public string Id;
        public string Type;               // "full" | "incremental"
        public string File;               // 相对 StoreRoot 的路径
        public string Parent;             // 上一条的 Id，全量为 null
        public long SizeBytes;
        public string CreatedUtc;
        public string OsCaption;
        public string SourceVolumeGuid;
        public bool IsFactoryBaseline;
        public string Sha256;

        // 运行时补的，不写进 entries[]
        public string StoreRoot;
        public string ChainId;
        public string Scope;
        public string ScopeLabel;

        public bool IsFull { get { return Type == "full"; } }

        public string FullPath { get { return Path.Combine(StoreRoot, File.Replace('/', '\\')); } }

        /// <summary>把 UTC 的 ISO 串转成本地时间给人看。转不了就原样显示。</summary>
        public string LocalTime
        {
            get
            {
                DateTime d;
                if (DateTime.TryParse(CreatedUtc, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out d))
                    return d.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                return CreatedUtc;
            }
        }
    }

    internal class BackupChain
    {
        public string ChainId;
        public string Scope;
        public string ScopeLabel;
        public string CreatedUtc;
        public List<BackupEntry> Entries = new List<BackupEntry>();
    }

    internal static class Backup
    {
        public const int SchemaVersion = 1;
        public const string StoreDirName = "KLA";     // 硬契约，见文件头

        // ── 工具定位 ─────────────────────────────────────────────────
        public static string ExpectedToolPath()
        {
            return Path.Combine(Path.Combine(Firmware.DataDir(), "bin"), "wimlib-imagex.exe");
        }

        /// <summary>
        /// 找 wimlib-imagex.exe。先看 %ProgramData%\KLA\bin，再看程序目录。
        /// 找不到返回 null——调用方必须据此拒绝开始备份，而不是退化成别的工具。
        /// DISM 不能替代：它没有 VSS 开关，也没有 --delta-from，
        /// 用它产出的东西救援端读不了。
        /// </summary>
        public static string FindTool()
        {
            try
            {
                string p = ExpectedToolPath();
                if (System.IO.File.Exists(p)) return p;

                string dir = Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                p = Path.Combine(Path.Combine(dir, "bin"), "wimlib-imagex.exe");
                if (System.IO.File.Exists(p)) return p;
            }
            catch { }
            return null;
        }

        // ── 存储位置 ─────────────────────────────────────────────────
        /// <summary>
        /// 挑存放备份的卷：不是系统盘、剩余空间最多的固定磁盘。
        /// 放系统盘上是错的——恢复出厂会把 C: 整个覆盖掉，备份跟着一起没。
        /// </summary>
        public static string DefaultStoreRoot()
        {
            try
            {
                string sysRoot = Path.GetPathRoot(Environment.GetFolderPath(
                    Environment.SpecialFolder.Windows));
                DriveInfo best = null;
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    if (string.Equals(d.Name, sysRoot, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.Equals(d.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase)) continue;
                    if (best == null || d.AvailableFreeSpace > best.AvailableFreeSpace) best = d;
                }
                if (best != null) return Path.Combine(best.Name, StoreDirName);
            }
            catch { }
            return null;
        }

        public static string IndexPath(string storeRoot)
        {
            return Path.Combine(storeRoot, "index.json");
        }

        // ── 读索引 ───────────────────────────────────────────────────
        public static List<BackupChain> LoadChains(string storeRoot)
        {
            List<BackupChain> chains = new List<BackupChain>();
            if (string.IsNullOrEmpty(storeRoot)) return chains;

            string p = IndexPath(storeRoot);
            if (!System.IO.File.Exists(p)) return chains;

            JsonValue root = JsonValue.Parse(
                System.IO.File.ReadAllText(p, Encoding.UTF8));

            long ver = root["schemaVersion"].AsLong(0);
            if (ver > SchemaVersion)
                throw new InvalidOperationException(
                    "这份备份索引是更新版本的程序写的（schemaVersion " + ver +
                    "，本程序支持到 " + SchemaVersion + "）。\n" +
                    "为避免破坏它，本程序不会修改它。请升级 " + Branding.ShortName + "。");

            foreach (JsonValue c in root["chains"].AsArray())
            {
                BackupChain ch = new BackupChain();
                ch.ChainId = c["chainId"].AsString("");
                ch.Scope = c["scope"].AsString("");
                ch.ScopeLabel = c["scopeLabel"].AsString("");
                ch.CreatedUtc = c["createdUtc"].AsString("");

                foreach (JsonValue e in c["entries"].AsArray())
                {
                    BackupEntry en = new BackupEntry();
                    en.Id = e["id"].AsString("");
                    en.Type = e["type"].AsString("full");
                    en.File = e["file"].AsString("");
                    en.Parent = e["parent"].Exists ? e["parent"].AsString(null) : null;
                    en.SizeBytes = e["sizeBytes"].AsLong(0);
                    en.CreatedUtc = e["createdUtc"].AsString("");
                    en.OsCaption = e["osCaption"].AsString("");
                    en.SourceVolumeGuid = e["sourceVolumeGuid"].AsString("");
                    en.IsFactoryBaseline = e["isFactoryBaseline"].AsBool(false);
                    en.Sha256 = e["sha256"].AsString("");
                    en.StoreRoot = storeRoot;
                    en.ChainId = ch.ChainId;
                    en.Scope = ch.Scope;
                    en.ScopeLabel = ch.ScopeLabel;
                    ch.Entries.Add(en);
                }
                chains.Add(ch);
            }
            return chains;
        }

        /// <summary>所有还原点拉平成一条时间线，新的在前。管理界面用。</summary>
        public static List<BackupEntry> ListAll(string storeRoot)
        {
            List<BackupEntry> all = new List<BackupEntry>();
            foreach (BackupChain c in LoadChains(storeRoot))
                all.AddRange(c.Entries);
            all.Sort(delegate(BackupEntry a, BackupEntry b)
            {
                return string.CompareOrdinal(b.CreatedUtc, a.CreatedUtc);
            });
            return all;
        }

        // ── 写索引 ───────────────────────────────────────────────────
        private static void SaveChains(string storeRoot, List<BackupChain> chains)
        {
            JsonWriter j = new JsonWriter();
            j.BeginObject();
            j.Prop("schemaVersion", SchemaVersion);
            j.Prop("machineId", MachineId());
            j.Prop("updatedUtc", UtcNow());

            j.Key("chains");
            j.BeginArray();
            foreach (BackupChain c in chains)
            {
                j.BeginObject();
                j.Prop("chainId", c.ChainId);
                j.Prop("scope", c.Scope);
                j.Prop("scopeLabel", c.ScopeLabel);
                j.Prop("createdUtc", c.CreatedUtc);

                j.Key("entries");
                j.BeginArray();
                foreach (BackupEntry e in c.Entries)
                {
                    j.BeginObject();
                    j.Prop("id", e.Id);
                    j.Prop("type", e.Type);
                    j.Prop("file", e.File);
                    j.Prop("parent", e.Parent);          // null 会写成 JSON null
                    j.Prop("sizeBytes", e.SizeBytes);
                    j.Prop("createdUtc", e.CreatedUtc);
                    j.Prop("osCaption", e.OsCaption);
                    j.Prop("sourceVolumeGuid", e.SourceVolumeGuid);
                    j.Prop("isFactoryBaseline", e.IsFactoryBaseline);
                    j.Prop("sha256", e.Sha256);
                    j.EndObject();
                }
                j.EndArray();
                j.EndObject();
            }
            j.EndArray();
            j.EndObject();

            // 原子写。断电留下半个 index.json 的后果是所有备份文件都还在，
            // 但两侧都找不到它们——比丢一个备份严重得多。
            Directory.CreateDirectory(storeRoot);
            string p = IndexPath(storeRoot);
            string tmp = p + ".tmp";
            System.IO.File.WriteAllText(tmp, j.ToString(), new UTF8Encoding(false));
            if (System.IO.File.Exists(p)) System.IO.File.Delete(p);
            System.IO.File.Move(tmp, p);
        }

        // ── 机器与系统信息 ───────────────────────────────────────────
        private static string UtcNow()
        {
            return DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'",
                CultureInfo.InvariantCulture);
        }

        public static string MachineId()
        {
            try
            {
                foreach (ManagementObject o in new ManagementObjectSearcher(
                    "SELECT UUID FROM Win32_ComputerSystemProduct").Get())
                {
                    object v = o["UUID"];
                    if (v != null) return v.ToString();
                }
            }
            catch { }
            return "";
        }

        private static string OsCaption()
        {
            try
            {
                foreach (ManagementObject o in new ManagementObjectSearcher(
                    "SELECT Caption, Version FROM Win32_OperatingSystem").Get())
                {
                    string cap = o["Caption"] == null ? "" : o["Caption"].ToString().Trim();
                    string ver = o["Version"] == null ? "" : o["Version"].ToString().Trim();
                    return string.IsNullOrEmpty(ver) ? cap : cap + " (" + ver + ")";
                }
            }
            catch { }
            return "";
        }

        private static string SystemVolumeGuid()
        {
            try
            {
                string sysRoot = Path.GetPathRoot(Environment.GetFolderPath(
                    Environment.SpecialFolder.Windows));            // "C:\"
                string letter = sysRoot.Substring(0, 1);
                foreach (ManagementObject o in new ManagementObjectSearcher(
                    "SELECT DeviceID FROM Win32_Volume WHERE DriveLetter='" +
                    letter + ":'").Get())
                {
                    object v = o["DeviceID"];
                    if (v != null) return v.ToString();
                }
            }
            catch { }
            return "";
        }

        // ── 范围 ─────────────────────────────────────────────────────
        public static string ScopeCode(BackupScope s)
        {
            switch (s)
            {
                case BackupScope.L0: return "L0";
                case BackupScope.L1: return "L1";
                case BackupScope.L3: return "L3";
                case BackupScope.L4: return "L4";
                default: return "L2";
            }
        }

        public static string ScopeLabel(BackupScope s)
        {
            switch (s)
            {
                case BackupScope.L0: return "出厂母盘";
                case BackupScope.L1: return "完整系统";
                case BackupScope.L3: return "用户数据";
                case BackupScope.L4: return "纯系统（不含软件）";
                default: return "系统与程序";
            }
        }

        /// <summary>
        /// 生成 wimlib 的 --config 文件。格式和 DISM 的 WimScript.ini 一样。
        /// 排除项来自 backup-design.md §1「默认排除规则」，两侧必须一致。
        /// </summary>
        private static string WriteExcludeConfig(BackupScope scope)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[ExclusionList]");
            sb.AppendLine(@"\pagefile.sys");
            sb.AppendLine(@"\hiberfil.sys");
            sb.AppendLine(@"\swapfile.sys");
            sb.AppendLine(@"\System Volume Information");
            sb.AppendLine(@"\$Recycle.Bin");
            sb.AppendLine(@"\Windows\Temp");
            sb.AppendLine(@"\Users\*\AppData\Local\Temp");
            sb.AppendLine(@"\Users\*\AppData\Local\Microsoft\Windows\INetCache");
            // 我们自己的备份目录不能进备份——否则第二次备份会把第一次的
            // 几十 GB WIM 一起打进去，体积翻倍且毫无意义。
            sb.AppendLine(@"\" + StoreDirName);

            if (scope == BackupScope.L2)
            {
                sb.AppendLine(@"\Users\*\Desktop");
                sb.AppendLine(@"\Users\*\Documents");
                sb.AppendLine(@"\Users\*\Pictures");
                sb.AppendLine(@"\Users\*\Videos");
                sb.AppendLine(@"\Users\*\Music");
                sb.AppendLine(@"\Users\*\Downloads");
            }

            if (scope == BackupScope.L4)
            {
                // 纯系统：Windows 本体（含 DriverStore 驱动库、系统服务二进制、
                // 注册表 hive）+ ProgramData 的服务状态。应用软件和个人文件全排除。
                // ProgramData 保留：Windows Defender、计划任务、服务状态都住在
                // 这里，剔了它还原出来的系统服务是半残的。
                sb.AppendLine(@"\Program Files");
                sb.AppendLine(@"\Program Files (x86)");
                sb.AppendLine(@"\Program Files\WindowsApps");
                sb.AppendLine(@"\Users");
                sb.AppendLine(@"\PerfLogs");
                sb.AppendLine(@"\inetpub");
                // 应用残留的开始菜单/桌面快捷方式：还原后指向不存在的程序，
                // 留着一堆死图标不如不带。
                sb.AppendLine(@"\ProgramData\Microsoft\Windows\Start Menu\Programs");
                sb.AppendLine(@"\Users\*\AppData\Roaming\Microsoft\Windows\Start Menu");
            }

            sb.AppendLine();
            sb.AppendLine("[CompressionExclusionList]");
            sb.AppendLine("*.mp3");
            sb.AppendLine("*.zip");
            sb.AppendLine("*.cab");
            sb.AppendLine(@"\WINDOWS\inf\*.pnf");

            string p = Path.Combine(Path.GetTempPath(),
                "kla-exclude-" + Guid.NewGuid().ToString("N") + ".ini");
            // wimlib 读这个文件按 UTF-8，带 BOM 更保险（路径里有中文时）
            System.IO.File.WriteAllText(p, sb.ToString(), new UTF8Encoding(true));
            return p;
        }

        // ── 执行备份 ─────────────────────────────────────────────────
        /// <summary>
        /// 开始一次备份。阻塞直到完成或失败，进度显示在模态窗口里。
        /// 失败一律抛异常，调用方负责报给用户。
        /// </summary>
        public static void Start(BackupOptions o, Window owner)
        {
            string tool = FindTool();
            if (tool == null)
                throw new InvalidOperationException("找不到备份引擎 wimlib-imagex.exe。");

            if (string.IsNullOrEmpty(o.StoreRoot))
                throw new InvalidOperationException("没有指定备份存放位置。");

            Directory.CreateDirectory(o.StoreRoot);

            List<BackupChain> chains = LoadChains(o.StoreRoot);

            // 找当前月、同范围的链；没有就新起一条。
            // 每月一条新全量是保留策略的基础：整条删除是安全的，
            // 删链中间的某一条则会连累它后面所有增量。
            string code = ScopeCode(o.Scope);
            string period = DateTime.UtcNow.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            string chainId = "c-" + period + "-" + code.ToLowerInvariant();

            BackupChain chain = null;
            foreach (BackupChain c in chains)
                if (c.ChainId == chainId) { chain = c; break; }

            bool newChain = (chain == null);
            if (newChain)
            {
                chain = new BackupChain();
                chain.ChainId = chainId;
                chain.Scope = code;
                chain.ScopeLabel = ScopeLabel(o.Scope);
                chain.CreatedUtc = UtcNow();
                chains.Add(chain);
            }

            string chainDir = "chain-" + period + "-" + code.ToLowerInvariant();
            Directory.CreateDirectory(Path.Combine(o.StoreRoot, chainDir));

            bool isFull = (chain.Entries.Count == 0);
            BackupEntry parent = isFull ? null : chain.Entries[chain.Entries.Count - 1];

            string fileRel = isFull
                ? chainDir + "/base.wim"
                : chainDir + "/d" + chain.Entries.Count.ToString("000") + ".wim";
            string fileAbs = Path.Combine(o.StoreRoot, fileRel.Replace('/', '\\'));

            if (System.IO.File.Exists(fileAbs))
                throw new InvalidOperationException(
                    "目标文件已存在但不在索引里：" + fileAbs +
                    "\n这通常意味着上一次备份中断了。请先手动删除它。");

            string source = Path.GetPathRoot(Environment.GetFolderPath(
                Environment.SpecialFolder.Windows));
            if (o.Scope == BackupScope.L3)
                source = Path.Combine(source, "Users");

            string cfg = WriteExcludeConfig(o.Scope);
            string label = ScopeLabel(o.Scope) + " " +
                           DateTime.Now.ToString("yyyy-MM-dd HH:mm");

            StringBuilder args = new StringBuilder();
            args.Append("capture ");
            args.Append(Q(source)).Append(' ');
            args.Append(Q(fileAbs)).Append(' ');
            args.Append(Q(label)).Append(' ');
            args.Append("--snapshot ");                       // VSS，不可省，见文件头
            args.Append("--compress=LZX ");
            args.Append("--config=").Append(Q(cfg)).Append(' ');
            args.Append("--check");
            if (!isFull)
            {
                string parentAbs = Path.Combine(o.StoreRoot,
                    parent.File.Replace('/', '\\'));
                args.Append(" --delta-from=").Append(Q(parentAbs));
            }

            ProgressWindow pw = new ProgressWindow(
                isFull ? "正在创建完整备份" : "正在创建增量备份",
                "备份过程中可以继续使用电脑。请不要关机或断电。");
            if (owner != null && owner.IsVisible) pw.Owner = owner;

            Exception failure = null;
            Thread worker = new Thread(delegate()
            {
                try
                {
                    RunTool(tool, args.ToString(), pw);

                    if (!System.IO.File.Exists(fileAbs))
                        throw new IOException("备份引擎报告成功，但没有产出文件：" + fileAbs);

                    pw.Say("正在校验完整性…");
                    BackupEntry e = new BackupEntry();
                    e.Id = "e-" + (chain.Entries.Count + 1).ToString("0000");
                    e.Type = isFull ? "full" : "incremental";
                    e.File = fileRel;
                    e.Parent = isFull ? null : parent.Id;
                    e.SizeBytes = new FileInfo(fileAbs).Length;
                    e.CreatedUtc = UtcNow();
                    e.OsCaption = OsCaption();
                    e.SourceVolumeGuid = SystemVolumeGuid();
                    e.IsFactoryBaseline = false;
                    e.Sha256 = Sha256File(fileAbs, pw);

                    chain.Entries.Add(e);

                    // 出厂基准全局至多一个（backup-design.md §4 写入规则）。
                    // 设新的之前先把旧的清掉，不然救援端的「恢复出厂」不知道认哪个。
                    if (o.SetAsFactoryBaseline)
                    {
                        foreach (BackupChain c in chains)
                            foreach (BackupEntry x in c.Entries)
                                x.IsFactoryBaseline = false;
                        e.IsFactoryBaseline = true;
                    }

                    pw.Say("正在写入索引…");
                    SaveChains(o.StoreRoot, chains);
                    HardenStore(o.StoreRoot);
                }
                catch (Exception ex)
                {
                    failure = ex;
                    // 半个 WIM 留在盘上会让下一次备份撞上「文件已存在」，
                    // 而且它本身是不可用的。收拾干净。
                    try { if (System.IO.File.Exists(fileAbs)) System.IO.File.Delete(fileAbs); }
                    catch { }
                }
                finally
                {
                    try { if (System.IO.File.Exists(cfg)) System.IO.File.Delete(cfg); }
                    catch { }
                    pw.Finish();
                }
            });
            worker.IsBackground = true;
            worker.SetApartmentState(ApartmentState.MTA);

            // 等窗口真的显示出来再开工。反过来的话，一个很快就失败的任务
            // 会在 ShowDialog 之前就调 Close()，然后 ShowDialog 对着一个
            // 已关闭的窗口抛异常——真正的失败原因被这个次生异常盖掉。
            pw.Loaded += delegate { worker.Start(); };
            pw.ShowDialog();

            if (failure != null) throw failure;
        }

        private static string Q(string s)
        {
            return "\"" + s.TrimEnd('\\') + "\"";
        }

        /// <summary>
        /// 跑 wimlib 并把输出喂给进度窗口。
        ///
        /// 按字符读而不是按行：wimlib 的百分比进度是用 \r 原地刷新的，
        /// ReadLine 要等到整个阶段结束才返回一次，进度条会一动不动几十分钟，
        /// 用户会以为卡死了。
        /// </summary>
        private static void RunTool(string exe, string args, ProgressWindow pw)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;

            StringBuilder tail = new StringBuilder();

            using (Process p = Process.Start(psi))
            {
                Thread errReader = new Thread(delegate()
                {
                    try
                    {
                        string line;
                        while ((line = p.StandardError.ReadLine()) != null)
                        {
                            lock (tail) { tail.AppendLine(line); }
                            Log(line);
                        }
                    }
                    catch { }
                });
                errReader.IsBackground = true;
                errReader.Start();

                StringBuilder cur = new StringBuilder();
                int ch;
                while ((ch = p.StandardOutput.Read()) >= 0)
                {
                    if (ch == '\r' || ch == '\n')
                    {
                        string s = cur.ToString().Trim();
                        cur.Length = 0;
                        if (s.Length > 0) { pw.Say(s); Log(s); }
                    }
                    else cur.Append((char)ch);
                }

                p.WaitForExit();
                errReader.Join(2000);

                if (p.ExitCode != 0)
                {
                    string err;
                    lock (tail) { err = tail.ToString().Trim(); }
                    throw new InvalidOperationException(
                        "备份引擎退出码 " + p.ExitCode +
                        (err.Length == 0 ? "" : "：\n\n" + Trim(err, 800)));
                }
            }
        }

        private static string Trim(string s, int max)
        {
            if (s.Length <= max) return s;
            return s.Substring(s.Length - max);
        }

        private static string Sha256File(string path, ProgressWindow pw)
        {
            using (System.Security.Cryptography.SHA256 h =
                       System.Security.Cryptography.SHA256.Create())
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                       FileShare.Read, 1 << 20))
            {
                byte[] buf = new byte[1 << 20];
                long done = 0, total = fs.Length;
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    h.TransformBlock(buf, 0, n, null, 0);
                    done += n;
                    if (pw != null && total > 0)
                        pw.Say("正在校验完整性… " + (done * 100 / total) + "%");
                }
                h.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(h.Hash).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>
        /// 给备份目录加隐藏+系统属性（backup-design.md §目录保护）。
        /// 防呆不防拆——挡的是「在资源管理器里手滑删掉」，不是有意破坏。
        /// 失败不影响备份本身，所以整个吞掉。
        /// </summary>
        private static void HardenStore(string storeRoot)
        {
            try
            {
                DirectoryInfo di = new DirectoryInfo(storeRoot);
                di.Attributes |= FileAttributes.Hidden | FileAttributes.System;
            }
            catch { }
        }

        // ── 删除 ─────────────────────────────────────────────────────
        /// <summary>这条还原点后面有几条依赖它。UI 删除前必须问一遍。</summary>
        public static int CountDependents(string storeRoot, BackupEntry e)
        {
            foreach (BackupChain c in LoadChains(storeRoot))
            {
                if (c.ChainId != e.ChainId) continue;
                for (int i = 0; i < c.Entries.Count; i++)
                    if (c.Entries[i].Id == e.Id)
                        return c.Entries.Count - i - 1;
            }
            return 0;
        }

        /// <summary>
        /// 删一个还原点，连带删掉依赖它的所有后继。
        ///
        /// 只删文件不更新索引，或者只更新索引不删文件，两种半吊子状态都比
        /// 全删干净糟糕：前者让空间要不回来，后者让索引指向不存在的文件。
        /// 顺序是先删文件再写索引——反过来的话，删文件失败会留下
        /// 「索引说没有、文件还占着几十 GB」的孤儿。
        /// </summary>
        public static void Delete(BackupEntry e)
        {
            List<BackupChain> chains = LoadChains(e.StoreRoot);
            BackupChain chain = null;
            int idx = -1;
            foreach (BackupChain c in chains)
            {
                if (c.ChainId != e.ChainId) continue;
                for (int i = 0; i < c.Entries.Count; i++)
                    if (c.Entries[i].Id == e.Id) { chain = c; idx = i; break; }
                if (chain != null) break;
            }
            if (chain == null)
                throw new InvalidOperationException("索引里找不到这个还原点，可能已被删除。");

            List<BackupEntry> doomed = chain.Entries.GetRange(idx, chain.Entries.Count - idx);

            List<string> failed = new List<string>();
            foreach (BackupEntry x in doomed)
            {
                string p = Path.Combine(e.StoreRoot, x.File.Replace('/', '\\'));
                try { if (System.IO.File.Exists(p)) System.IO.File.Delete(p); }
                catch (Exception ex) { failed.Add(x.File + "（" + ex.Message + "）"); }
            }

            chain.Entries.RemoveRange(idx, chain.Entries.Count - idx);
            if (chain.Entries.Count == 0)
            {
                chains.Remove(chain);
                try
                {
                    string dir = Path.Combine(e.StoreRoot,
                        "chain-" + chain.ChainId.Substring(2));
                    if (Directory.Exists(dir) && Directory.GetFiles(dir).Length == 0)
                        Directory.Delete(dir);
                }
                catch { }
            }
            SaveChains(e.StoreRoot, chains);

            if (failed.Count > 0)
                throw new IOException(
                    "索引已更新，但有文件没能删掉，需要手动清理：\n\n  " +
                    string.Join("\n  ", failed.ToArray()));
        }

        // ── 出厂基准 ─────────────────────────────────────────────────
        public static void SetFactoryBaseline(BackupEntry e)
        {
            List<BackupChain> chains = LoadChains(e.StoreRoot);
            bool found = false;
            foreach (BackupChain c in chains)
                foreach (BackupEntry x in c.Entries)
                {
                    x.IsFactoryBaseline = (x.Id == e.Id && c.ChainId == e.ChainId);
                    if (x.IsFactoryBaseline) found = true;
                }
            if (!found)
                throw new InvalidOperationException("索引里找不到这个还原点。");
            SaveChains(e.StoreRoot, chains);
        }

        // ── 请求还原 ─────────────────────────────────────────────────
        /// <summary>
        /// 请求还原系统卷。
        ///
        /// Windows 运行中还原不了自己的系统卷，所以这里只做两件事
        /// （backup-design.md §5「还原」）：写 pending-restore.json，设 BootNext。
        /// 真正的还原动作由 ACCESS 开机后读到这个文件来执行。
        /// </summary>
        public static void RequestRestore(BackupEntry e)
        {
            JsonWriter j = new JsonWriter();
            j.BeginObject();
            j.Prop("schemaVersion", SchemaVersion);
            j.Prop("requestedUtc", UtcNow());
            j.Prop("machineId", MachineId());
            j.Prop("chainId", e.ChainId);
            j.Prop("entryId", e.Id);
            j.Prop("file", e.File);
            j.Prop("scope", e.Scope);
            j.EndObject();

            string p = Path.Combine(e.StoreRoot, "pending-restore.json");
            string tmp = p + ".tmp";
            System.IO.File.WriteAllText(tmp, j.ToString(), new UTF8Encoding(false));
            if (System.IO.File.Exists(p)) System.IO.File.Delete(p);
            System.IO.File.Move(tmp, p);

            JsonValue rec = Deployment.ReadRecord();
            if (rec == null)
                throw new InvalidOperationException(
                    Branding.ShortName + " 尚未部署到这台电脑，无法进入还原环境。");

            int slot = (int)rec["boot"]["slot"].AsLong(-1);
            if (slot < 0)
                throw new InvalidOperationException("部署记录里没有启动项编号。");

            Firmware.SetBootNext(slot);
        }

        public static void CancelRestore(string storeRoot)
        {
            try
            {
                string p = Path.Combine(storeRoot, "pending-restore.json");
                if (System.IO.File.Exists(p)) System.IO.File.Delete(p);
            }
            catch { }
        }

        private static void Log(string msg)
        {
            try
            {
                System.IO.File.AppendAllText(
                    Path.Combine(Firmware.DataDir(), "backup.log"),
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + msg + "\r\n",
                    new UTF8Encoding(true));
            }
            catch { }
        }
    }


    /// <summary>
    /// 备份进度窗口。不显示百分比进度条——wimlib 的输出里没有可靠的总量，
    /// 编一个会走到 99% 然后停十分钟的进度条比不给进度条更让人焦虑。
    /// 显示的是它当前在做什么，加上已用时间。
    /// </summary>
    internal class ProgressWindow : DarkWindow
    {
        private readonly TextBlock _status;
        private readonly TextBlock _elapsed;
        private readonly DateTime _start = DateTime.Now;
        private readonly System.Windows.Threading.DispatcherTimer _timer;
        private bool _done;

        public ProgressWindow(string title, string note)
            : base(title, 560, 260)
        {
            SetCaption(title);
            ResizeMode = ResizeMode.NoResize;
            MinWidth = 560; MinHeight = 260;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            StackPanel s = new StackPanel();
            s.Margin = new Thickness(34, 28, 34, 28);

            TextBlock h = Ui.H2(title);
            s.Children.Add(h);

            TextBlock n = Ui.Dim(note);
            n.Margin = new Thickness(0, 10, 0, 0);
            s.Children.Add(n);

            Marquee bar = new Marquee();
            bar.Margin = new Thickness(0, 24, 0, 0);
            s.Children.Add(bar);

            _status = Ui.Dim("正在准备…");
            _status.Margin = new Thickness(0, 16, 0, 0);
            _status.TextWrapping = TextWrapping.NoWrap;
            _status.TextTrimming = TextTrimming.CharacterEllipsis;
            s.Children.Add(_status);

            _elapsed = Ui.Dim("");
            _elapsed.FontSize = 11;
            _elapsed.Margin = new Thickness(0, 8, 0, 0);
            s.Children.Add(_elapsed);

            Host.Content = s;

            _timer = new System.Windows.Threading.DispatcherTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += delegate
            {
                TimeSpan t = DateTime.Now - _start;
                _elapsed.Text = "已用时 " + (int)t.TotalMinutes + " 分 " + t.Seconds + " 秒";
            };
            _timer.Start();

            // 备份跑到一半被关窗，wimlib 还在后台跑，产出的是个半截 WIM。
            // 只有 Finish() 之后才允许关。
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e)
            {
                if (!_done) e.Cancel = true;
            };
        }

        public void Say(string msg)
        {
            Dispatcher.BeginInvoke((Action)delegate { _status.Text = msg; });
        }

        public void Finish()
        {
            Dispatcher.BeginInvoke((Action)delegate
            {
                _done = true;
                _timer.Stop();
                Close();
            });
        }
    }
}
