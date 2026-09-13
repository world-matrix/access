// 程序入口。
//
// 这里有个 --selftest 开关，是产品的一部分而不是临时调试代码：
// 主题样式表是 XAML 字符串在运行时解析的（这台机器没有 MSBuild，编译不了
// .xaml），也就是说 XAML 里的错**编译期发现不了**，只会在启动时炸。
// --selftest 把「解析主题 + 构造每个窗口 + 构造每一步向导页」跑一遍然后退出，
// 打包脚本每次构建后都调它。没有这道检查，一个拼错的属性名要等用户双击图标
// 才会暴露。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Markup;

namespace KarlsLight.Access
{
    internal static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            bool selfTest = false;
            bool forceWizard = false;
            bool headlessDeploy = false;
            bool uninstall = false;
            bool quietUninstall = false;
            string shotsDir = null;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "--selftest") selfTest = true;
                if (a == "--wizard") forceWizard = true;   // 便于重看引导，也便于测试
                if (a == "--deploy") headlessDeploy = true; // 临时入口：绕 UI 直接跑 Deployment.Run()，验证全链路
                if (a == "--uninstall") uninstall = true;
                if (a == "--quiet") quietUninstall = true;   // 静默卸载，配合 --uninstall 使用
                if (a == "--shots" && i + 1 < args.Length) shotsDir = args[i + 1];
            }

            // 卸载路径先走：它要自己删 ProgramData/注册表、恢复 BootOrder、删救援分区，
            // 再退出。这一步不碰主题资源，也不弹法律协议。
            if (uninstall)
            {
                int rc = Uninstaller.Run(quietUninstall);
                try { Environment.ExitCode = rc; } catch { }
                return rc;
            }

            Application app = new Application();
            // 协议窗是程序里第一个实例化的窗口，WPF 会把它隐式设成 MainWindow；
            // 若此刻就是 OnMainWindowClose，用户点「同意」关窗的瞬间 Application
            // 开始 Shutdown，后面 WizardWindow.Show() + app.Run() 直接被掐死——
            // 表现为「同意协议后程序退出，要再开一次」。对策与安装器
            // PreparingWindow 同款：协议阶段显式关，MainWindow 就位后再切回。
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            try
            {
                app.Resources.MergedDictionaries.Add(Theme.Build());
            }
            catch (Exception ex)
            {
                // 主题挂了整个界面就没法看了，但这时连样式都没有，
                // 只能用系统原生 MessageBox 报错——它不依赖我们的资源。
                MessageBox.Show(
                    "主题资源解析失败，程序无法启动。\n\n" + ex.Message,
                    Branding.ShortName, MessageBoxButton.OK, MessageBoxImage.Error);
                return 2;
            }

            // 中文界面要显式设 Language，否则 WPF 按 en-US 做断行，
            // 中文长句会在标点前不当换行。
            FrameworkElement.LanguageProperty.OverrideMetadata(
                typeof(FrameworkElement),
                new FrameworkPropertyMetadata(
                    XmlLanguage.GetLanguage(CultureInfo.CurrentUICulture.IetfLanguageTag)));

            // 单文件绿色版支持：4 个小 ESP 文件嵌在 ManifestResource 里，
            // 启动时如果 exe 同级没有 image\ 就释放出来。协议/关于/备份
            // 等 90% 的功能完全不需要这些文件，但早点释放能避免用户点
            // 「部署 KLA」的时候再提示"文件缺失"的额外一步。
            try { Deployment.EnsureAssets(); } catch { }

            if (selfTest) return SelfTest();
            if (headlessDeploy) return HeadlessDeploy();
            if (shotsDir != null) return Shots(shotsDir);

            // ── 法律协议同意：在任何 UI 界面之前弹 ─────────────────────
            // 位置：
            //   安装引导（installer.exe / .msi）也会单独有「同意协议」页，
            //   这里是**运行时**的硬约束——用户把 exe 拷过去直接双击、或者安装
            //   时手快跳过了，那运行时这里再拦一次，保证 explicit opt-in。
            // 顺序：先协议，后 wizard/MainWindow，顺序不能反——
            //   wizard 的下一步按钮在「同意协议」之前出现等于合规硬约束失效。
            if (!Settings.LicenseAgreed)
            {
                LegalWindow lw = new LegalWindow();
                bool? r = null;
                try { r = lw.ShowDialog(); }
                catch (Exception ex)
                {
                    // Legal.cs 的 MD → FlowDocument 解析挂了、主题资源坏了等
                    // 异常，这里兜底给 MessageBox 报错——但不能让用户不看协议
                    // 就进主界面，所以仍然退出（返回非 0）。
                    Program.LogError(ex);
                    MessageBox.Show(
                        "用户协议组件加载失败，程序无法启动。\n\n" + ex.Message,
                        Branding.ShortName, MessageBoxButton.OK, MessageBoxImage.Error);
                    return 3;
                }
                if (!r.GetValueOrDefault() || !lw.Agreed)
                {
                    // 用户点了「拒绝并退出」或关窗。
                    // 返回 0 而不是错误码：这不是程序崩了，是用户主动拒绝。
                    return 0;
                }
            }

            // 未捕获异常兜底。默认行为是弹一个英文的 .NET 崩溃框，
            // 对普通用户等于什么都没说。
            app.DispatcherUnhandledException +=
                delegate(object s, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
                {
                    LogError(e.Exception);
                    MessageBox.Show(
                        "程序遇到了一个未预料的问题：\n\n" + e.Exception.Message +
                        "\n\n详细信息已记录到\n" + LogPath(),
                        Branding.ShortName, MessageBoxButton.OK, MessageBoxImage.Error);
                    e.Handled = true;
                };

            Window start;
            if (forceWizard || !Settings.OnboardingDone)
                start = new WizardWindow();
            else
                start = new MainWindow();

            // MainWindow 就位后才恢复 OnMainWindowClose：主窗关闭即退出，
            // 协议窗/向导窗的关闭不再牵连整个应用。
            app.MainWindow = start;
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            start.Show();
            return app.Run();
        }

        /// <summary>
        /// 临时入口：绕过 UI 向导，直接对 D: 跑 Deployment.Run()。
        /// 跑完一次后会从源码删除，不进产品。用于在 UI 按钮触发逻辑还没接好
        /// 之前先把整条链路跑通一遍。会先弹 OKCancel 让用户最后确认一次。
        /// </summary>
        private static int HeadlessDeploy()
        {
            AttachParentConsole();
            StringBuilder rep = new StringBuilder();
            rep.AppendLine("=== KLA Headless Deploy ===");

            try
            {
                rep.AppendLine("[1] 找 D: 分区");
                PartInfo chosen = null;
                foreach (DiskInfo d in Storage.ListDisks())
                {
                    foreach (PartInfo p in d.Partitions)
                    {
                        if (p.DriveLetter == "D") { chosen = p; break; }
                    }
                    if (chosen != null) break;
                }
                if (chosen == null)
                {
                    string msg = "找不到 D: 分区。";
                    rep.AppendLine("    " + msg);
                    MessageBox.Show(msg, Branding.ShortName,
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    WriteReport(rep);
                    return 1;
                }
                rep.AppendLine("    选中 " + chosen.Display);

                rep.AppendLine("[2] 算压缩计划");
                ShrinkPlan plan = Storage.Evaluate(chosen, Storage.RescueBytes);
                rep.AppendLine("    Feasible=" + plan.Feasible +
                               "  ActualTake=" + Storage.Fmt(plan.ActualTake) +
                               "  ResultingSize=" + Storage.Fmt(plan.ResultingSize));
                if (!plan.Feasible)
                {
                    string msg = "压缩计划不可行：" + plan.Reason;
                    rep.AppendLine("    " + msg);
                    MessageBox.Show(msg, Branding.ShortName,
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    WriteReport(rep);
                    return 1;
                }

                rep.AppendLine("[3] 找镜像");
                string img = Deployment.FindImage();
                if (img == null)
                {
                    string msg = "找不到镜像：" + Deployment.ExpectedImagePath();
                    rep.AppendLine("    " + msg);
                    MessageBox.Show(msg, Branding.ShortName,
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    WriteReport(rep);
                    return 1;
                }
                rep.AppendLine("    镜像：" + img + "  (" +
                    new FileInfo(img).Length + " bytes)");

                rep.AppendLine("[4] 等用户确认");
                MessageBoxResult r = MessageBox.Show(
                    "即将对 D: 部署 " + Branding.ShortName + "：\n\n" +
                    "  · 把 D: 压缩 " + Storage.Fmt(plan.ActualTake) + "\n" +
                    "  · 在盘末建立 1 GB 隐藏分区\n" +
                    "  · 写 access.img 进新分区\n" +
                    "  · 在 ESP\\EFI\\KLA\\ 装 grubx64.efi + grub.cfg + wallpaper.png + memtest.efi\n" +
                    "  · 新建 Boot#### 启动项（不动现有项），插到 BootOrder 首位\n\n" +
                    "半途失败会留下一个缩了 1 GB 但没建完分区的盘。\n" +
                    "BootOrder 已先备份到 %ProgramData%\\KLA\\，出事用 Restore-BootOrder.ps1。\n\n" +
                    "OK 继续，Cancel 中止。",
                    Branding.ProductName, MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (r != MessageBoxResult.OK)
                {
                    rep.AppendLine("    用户取消");
                    WriteReport(rep);
                    return 2;
                }

                rep.AppendLine("[5] 调 Deployment.Run()");
                Deployment.Run(plan, img);
                rep.AppendLine("    部署成功！");
                MessageBox.Show(
                    "部署成功完成。\n\n" +
                    "下次开机会进 GRUB 菜单：\n" +
                    "  · 3 秒不按键 → 进 Windows（默认项）\n" +
                    "  · 按 F4 → 进 " + Branding.ShortName + " 救援环境\n" +
                    "  · 按 F5 → 进 MemTest86+\n\n" +
                    "想立刻试一次？重启后开机时按 F4 / F5 即可。",
                    Branding.ProductName, MessageBoxButton.OK, MessageBoxImage.Information);
                WriteReport(rep);
                return 0;
            }
            catch (Exception ex)
            {
                LogError(ex);
                rep.AppendLine("[FAIL] " + ex.GetType().Name + ": " + ex.Message);
                rep.AppendLine(ex.StackTrace);
                MessageBox.Show(
                    "部署失败：\n\n" + ex.Message +
                    "\n\n详细信息已记录到\n" + LogPath() + "\n" + Deployment.LogPath(),
                    Branding.ShortName, MessageBoxButton.OK, MessageBoxImage.Error);
                WriteReport(rep);
                return 1;
            }
        }

        private static void WriteReport(StringBuilder rep)
        {
            try
            {
                Console.OutputEncoding = System.Text.Encoding.UTF8;
                Console.Write(rep.ToString());
            }
            catch { }
            try
            {
                string exeDir = Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                File.WriteAllText(Path.Combine(exeDir, "deploy-headless.log"),
                    rep.ToString(), new System.Text.UTF8Encoding(true));
            }
            catch { }
        }

        /// <summary>
        /// 不弹窗地把所有界面构造一遍。构造过程会触发样式查找和 XAML 解析，
        /// 拼错的资源键、非法的属性值都会在这里抛出来。
        ///
        /// 报告同时写三个地方：父进程的控制台、exe 旁边的 selftest.log、
        /// 以及进程退出码。这个程序编出来是 winexe（不带控制台子系统），
        /// 从 PowerShell 里跑的话 Console.Write 默认哪儿都不去——
        /// 所以要先 AttachConsole 挂到父进程的控制台上；挂不上也无所谓，
        /// 构建脚本读的是那个 .log 和退出码，不依赖控制台。
        /// </summary>
        private static int SelfTest()
        {
            AttachParentConsole();

            System.Text.StringBuilder rep = new System.Text.StringBuilder();
            int bad = 0;

            bad += Check(rep, "主题资源字典", delegate
            {
                ResourceDictionary d = Theme.Build();
                if (d.Count == 0) throw new Exception("资源字典是空的");
            });

            bad += Check(rep, "主窗口", delegate { MainWindow w = new MainWindow(); w.Close(); });

            bad += Check(rep, "关于窗口", delegate
            {
                AboutWindow a = new AboutWindow(); a.Close();
            });
            bad += Check(rep, "文档查看器", delegate
            {
                DocViewerWindow d = new DocViewerWindow("自测",
                    "# 标题\n\n正文 **粗**\n\n- 项目一\n- 项目二");
                d.Close();
            });

            bad += Check(rep, "法律协议弹窗", delegate
            {
                // 只验证构造不抛、MD 解析不崩；不按同意/拒绝。
                LegalWindow w = new LegalWindow();
                w.Close();
            });

            bad += Check(rep, "法律资源嵌入", delegate
            {
                // EULA 和隐私政策两个资源都要在。csc /resource: 拼错会出现在这里。
                if (string.IsNullOrEmpty(Legal.EulaText()))
                    throw new Exception("EULA 资源为空或未嵌入");
                if (string.IsNullOrEmpty(Legal.PrivacyText()))
                    throw new Exception("隐私政策资源为空或未嵌入");
                if (string.IsNullOrEmpty(Legal.CombinedText()))
                    throw new Exception("组合文本为空");
            });

            bad += Check(rep, "ESP 文件资源嵌入（单文件绿色版）", delegate
            {
                // 4 个 ESP 小文件嵌在 ManifestResource 里，单文件绿色版用。
                // 检查它们全都能开流、大小 > 0。
                System.Reflection.Assembly a =
                    System.Reflection.Assembly.GetExecutingAssembly();
                string[] names = new string[] { "grubx64.efi", "grub.cfg",
                    "wallpaper.png", "memtest.efi" };
                foreach (string n in names)
                {
                    string rid = "KarlsLight.Access.esp." + n;
                    using (System.IO.Stream s = a.GetManifestResourceStream(rid))
                    {
                        if (s == null) throw new Exception("缺少资源：" + rid);
                        if (s.Length == 0) throw new Exception("资源空：" + rid);
                    }
                }
            });

            bad += Check(rep, "引导向导", delegate
            {
                WizardWindow w = new WizardWindow();
                // 逐步构造每一页：只 new 一个向导只会构造第一步，
                // 后面几步的错要等用户点到那一步才暴露，那就失去意义了。
                w.BuildAllStepsForTest();
                w.Close();
            });

            rep.AppendLine(bad == 0 ? "自检通过" : "自检失败：" + bad + " 项");

            try
            {
                Console.OutputEncoding = System.Text.Encoding.UTF8;
                Console.Write(rep.ToString());
            }
            catch { }

            try
            {
                string exeDir = Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                File.WriteAllText(Path.Combine(exeDir, "selftest.log"),
                    rep.ToString(), new System.Text.UTF8Encoding(true));
            }
            catch { }

            return bad == 0 ? 0 : 1;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);

        private static void AttachParentConsole()
        {
            // -1 = ATTACH_PARENT_PROCESS。没有父控制台时返回 false，
            // 这不是错误——双击运行本来就没有控制台。
            try { AttachConsole(-1); } catch { }
        }

        /// <summary>
        /// 把每个界面渲染成 PNG 存到指定目录，然后退出。
        ///
        /// --selftest 只保证界面「构造得出来」，保证不了「看着对」。
        /// 版式压没压住、暗色配色在真实渲染下够不够对比、中文有没有在
        /// 奇怪的地方断行——这些只有看图才知道。开发时每改一次界面跑一遍，
        /// 比截屏靠谱：窗口开在屏幕外，不受当前分辨率和缩放影响，
        /// 每次出的图尺寸都一样，能直接前后对比。
        /// </summary>
        private static int Shots(string dir)
        {
            AttachParentConsole();
            System.Text.StringBuilder rep = new System.Text.StringBuilder();
            try
            {
                Directory.CreateDirectory(dir);

                WizardWindow wiz = new WizardWindow();
                OffScreen(wiz);
                for (int i = 0; i < WizardWindow.StepCountForTest; i++)
                {
                    wiz.GoForTest(i);
                    Capture(wiz, Path.Combine(dir,
                        "wizard-" + (i + 1).ToString("00") + ".png"), rep);
                }
                wiz.Close();

                MainWindow main = new MainWindow();
                OffScreen(main);
                for (int i = 0; i < main.NavCountForTest; i++)
                {
                    main.SelectForTest(i);
                    Capture(main, Path.Combine(dir,
                        "main-" + (i + 1).ToString("00") + ".png"), rep);
                }
                main.Close();
            }
            catch (Exception ex)
            {
                rep.AppendLine("截图失败：" + ex.GetType().Name + ": " + ex.Message);
                rep.AppendLine(ex.StackTrace);
                try { Console.Write(rep.ToString()); } catch { }
                return 1;
            }
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; Console.Write(rep.ToString()); }
            catch { }
            return 0;
        }

        private static void OffScreen(Window w)
        {
            // 开在屏幕外而不是隐藏：WPF 对没显示过的窗口不做完整布局，
            // Visibility.Hidden 的窗口渲染出来是一张空图。
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = -20000;
            w.Top = -20000;
            w.ShowInTaskbar = false;
            w.Show();
        }

        private static void Capture(Window w, string path, System.Text.StringBuilder rep)
        {
            Pump();
            w.UpdateLayout();
            Pump();

            int pw = (int)Math.Round(w.ActualWidth);
            int ph = (int)Math.Round(w.ActualHeight);
            if (pw <= 0 || ph <= 0) throw new Exception("窗口尺寸是 " + pw + "x" + ph);

            System.Windows.Media.Imaging.RenderTargetBitmap rtb =
                new System.Windows.Media.Imaging.RenderTargetBitmap(
                    pw, ph, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(w);

            System.Windows.Media.Imaging.PngBitmapEncoder enc =
                new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                enc.Save(fs);

            rep.AppendLine("  " + Path.GetFileName(path) + "  " + pw + "x" + ph);
        }

        /// <summary>把消息队列跑空，让布局、字体测量和图片解码都落定。</summary>
        private static void Pump()
        {
            for (int i = 0; i < 3; i++)
            {
                System.Windows.Threading.DispatcherFrame frame =
                    new System.Windows.Threading.DispatcherFrame();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.ContextIdle,
                    new Action(delegate { frame.Continue = false; }));
                System.Windows.Threading.Dispatcher.PushFrame(frame);
            }
        }

        private static int Check(System.Text.StringBuilder rep, string name, Action body)
        {
            try
            {
                body();
                rep.AppendLine("  [OK]   " + name);
                return 0;
            }
            catch (Exception ex)
            {
                rep.AppendLine("  [FAIL] " + name + " — " + ex.GetType().Name + ": " + ex.Message);
                if (ex.InnerException != null)
                    rep.AppendLine("         内层：" + ex.InnerException.Message);
                rep.AppendLine(ex.StackTrace);
                return 1;
            }
        }

        public static string LogPath()
        {
            return Path.Combine(Firmware.DataDir(), "app.log");
        }

        public static void LogError(Exception ex)
        {
            try
            {
                string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " +
                              ex.GetType().FullName + ": " + ex.Message + "\r\n" +
                              ex.StackTrace + "\r\n\r\n";
                File.AppendAllText(LogPath(), line, System.Text.Encoding.UTF8);
            }
            catch
            {
                // 记日志失败不能再抛——那会变成异常处理里又抛异常，
                // 用户看到的就成了完全无关的错误。
            }
        }
    }


    /// <summary>
    /// 程序自己的配置。存 %ProgramData%\KLA\settings.json。
    ///
    /// 注意不放 HKCU 或 %AppData%：部署状态是整机级别的（隐藏分区、引导项都是
    /// 全机器的），换个用户登录不该看到「还没安装过」。
    /// </summary>
    internal static class Settings
    {
        // ── 法律协议版本 ────────────────────────────────────────────
        // EULA/Privacy 升级时把这个数 bump 一下，所有机器下次启动都会再弹。
        // 见 EULA 第十三条 / 隐私政策第十四条「协议修改」。
        // v2 = 2026-08-25：隐私政策 V1.1 重写（写实了实际权限与本地处理范围）。
        public const int CurrentLicenseVersion = 2;

        private static string FilePath()
        {
            return Path.Combine(Firmware.DataDir(), "settings.json");
        }

        private static JsonValue Load()
        {
            try
            {
                string p = FilePath();
                if (!File.Exists(p)) return new JsonValue();
                return JsonValue.Parse(File.ReadAllText(p, System.Text.Encoding.UTF8));
            }
            catch
            {
                // 配置文件坏了就当没有——它只影响「要不要再看一次引导/协议」，
                // 不值得因此让程序起不来。
                return new JsonValue();
            }
        }

        private static void SaveFull(
            bool onboardingDone, string installedTo,
            bool licenseAgreed, int licenseVersion)
        {
            JsonWriter j = new JsonWriter();
            j.BeginObject();
            j.Prop("schemaVersion", 1);
            j.Prop("onboardingDone", onboardingDone);
            j.Prop("installedTo", installedTo == null ? "" : installedTo);
            j.Prop("licenseAgreed", licenseAgreed);
            j.Prop("licenseVersion", licenseVersion);
            j.Prop("updatedAt", DateTime.Now.ToString("s"));
            j.EndObject();

            // 原子写：直接覆盖的话，写到一半断电会留下半个 JSON，
            // 下次启动解析失败。先写临时文件再替换。
            string p = FilePath();
            string tmp = p + ".tmp";
            File.WriteAllText(tmp, j.ToString(), new System.Text.UTF8Encoding(false));
            if (File.Exists(p)) File.Delete(p);
            File.Move(tmp, p);
        }

        public static bool OnboardingDone
        {
            get { return Load()["onboardingDone"].AsBool(false); }
        }

        public static string InstalledTo
        {
            get { return Load()["installedTo"].AsString(""); }
        }

        /// <summary>
        /// 当前法律协议版本是否已被用户同意。
        /// 老配置里没有 licenseVersion 字段时按 0 算，肯定小于 CurrentLicenseVersion，
        /// 于是会再弹一次——这是我们想要的（升级协议版本后重新同意）。
        /// </summary>
        public static bool LicenseAgreed
        {
            get
            {
                JsonValue v = Load();
                return v["licenseAgreed"].AsBool(false) &&
                       v["licenseVersion"].AsLong(0) >= CurrentLicenseVersion;
            }
        }

        public static void MarkOnboardingDone(string installedTo)
        {
            try
            {
                JsonValue cur = Load();
                SaveFull(
                    true,
                    installedTo,
                    cur["licenseAgreed"].AsBool(false),
                    (int)cur["licenseVersion"].AsLong(0));
            }
            catch (Exception ex) { Program.LogError(ex); }
        }

        public static void MarkLicenseAgreed()
        {
            JsonValue cur = Load();
            SaveFull(
                cur["onboardingDone"].AsBool(false),
                cur["installedTo"].AsString(""),
                true,
                CurrentLicenseVersion);
        }
    }
}
