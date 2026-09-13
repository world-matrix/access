// KARL'S LIGHT ACCESS 安装引导。
//
// 设计目标：
//   · 5 步向导：欢迎 → 协议 → 安装路径 → 进度 → 完成
//   · 和主程序 KarlsLightAccess.exe 用同一套品牌色、字体、窗口风格（直接复用
//     Branding / Theme / DarkWindow / Ui / Legal / MarkdownView 类）。
//   · 欢迎页 slogan 轮播，给安装过程增加一点品牌感。
//   · 协议页和主程序启动弹窗完全一致：EULA + 隐私政策两勾选，未勾全不允许点同意。
//
// 注意：本程序需要 **管理员权限**（写 Program Files、写 HKLM 卸载注册表、
// 创建桌面/开始菜单快捷方式）。win32manifest 里已配 requireAdministrator。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using KarlsLight.Access;
using Path = System.IO.Path;

namespace KarlsLight.Setup
{
    internal static class SetupProgram
    {
        // 标语：4 条中英文，欢迎页每 3s 换一条，淡入淡出。
        private static readonly string[] Slogans = new string[] {
            "每次开机先见 KLA 启动菜单，3 秒不操作自动回 Windows。",
            "救援环境装在本机隐藏分区 —— 不用 U 盘、不用另一台电脑。",
            "备份 / 还原 / 出厂重置，全流程图形化向导。",
            "Designed by KARL'S LIGHT in Guiyang · © KARL'S LIGHT CO., LTD."
        };

        // 允许 winexe 下的 --selftest 把输出打到父进程（powershell/cmd）控制台。
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);
        private const int ATTACH_PARENT_PROCESS = -1;

        // ══════════════════════════════════════════════════════════
        // #region debug-point setup-boot
        // 启动追踪：双击闪退时，exe 同级 setup-boot.log 会记录每一步的进程 + 异常堆栈。
        // 不依赖 WPF / Console，不改动原有业务流程。
        private static readonly object _bootLogLock = new object();
        private static string _bootLogPath = null;
        internal static string BootLogPath()
        {
            if (_bootLogPath != null) return _bootLogPath;
            try
            {
                string exe = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(exe))
                {
                    string dir = Path.GetDirectoryName(exe);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        string probe = Path.Combine(dir, "setup-boot.log");
                        // 测试能不能写：Create → 不抛就算 OK
                        using (new FileStream(probe, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite)) { }
                        _bootLogPath = probe;
                        return _bootLogPath;
                    }
                }
            }
            catch { }
            _bootLogPath = Path.Combine(Path.GetTempPath(), "KLA-setup-boot.log");
            return _bootLogPath;
        }

        internal static void BootLog(string m)
        {
            try
            {
                string line = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + m + "\r\n";
                lock (_bootLogLock) { File.AppendAllText(BootLogPath(), line, System.Text.Encoding.UTF8); }
            }
            catch { /* 写追踪日志失败时绝对不能反作用于主流程 */ }
        }
        private static void HookGlobalExceptions(Application app)
        {
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                Exception ex = e.ExceptionObject as Exception;
                BootLog("[FATAL] AppDomain UnhandledException  isTerminating=" + e.IsTerminating +
                        "  type=" + (ex == null ? "null" : ex.GetType().FullName) +
                        "  msg=" + (ex == null ? "" : ex.Message) + "\r\n" + (ex == null ? "" : ex.StackTrace));
            };
            app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
            {
                BootLog("[FATAL] DispatcherUnhandledException: " + e.Exception.GetType().FullName +
                        "  msg=" + e.Exception.Message + "\r\n" + e.Exception.StackTrace);
                try
                {
                    MessageBox.Show("安装程序发生未处理异常：\r\n\r\n" +
                        e.Exception.GetType().FullName + ": " + e.Exception.Message + "\r\n\r\n" +
                        e.Exception.StackTrace,
                        Branding.ShortName, MessageBoxButton.OK, MessageBoxImage.Stop);
                }
                catch { }
                // 不吞异常，保留崩溃语义但能留日志
                e.Handled = false;
            };
            app.Exit += delegate(object sender, ExitEventArgs e)
            {
                BootLog("[APP EXIT] Application.Exit  fired with code=" + e.ApplicationExitCode +
                        "  MainWindow=" +
                        (app.MainWindow == null ? "(null)" : app.MainWindow.GetType().Name) +
                        "  ShutdownMode=" + app.ShutdownMode);
            };
        }
        // #endregion

        [STAThread]
        public static int Main(string[] args)
        {
            BootLog("======= Setup Main 入口  PID=" + Process.GetCurrentProcess().Id +
                    "  args=" + (args == null ? "null" : string.Join(" | ", args)));
            try
            {
                BootLog("  cmdline=[" + Environment.CommandLine + "]");
                BootLog("  EntryAssembly.Location=[" + Assembly.GetExecutingAssembly().Location + "]");
                BootLog("  CurrentDirectory=[" + Environment.CurrentDirectory + "]");
                BootLog("  UserInteractive=" + Environment.UserInteractive +
                        "  Is64BitProcess=" + Environment.Is64BitProcess);
            }
            catch (Exception ex) { BootLog("  (env dump err: " + ex.Message + ")"); }

            // ══════════════════════════════════════════════════════════
            // 构建期子命令：--build-single <srcSetup> <payloadDir> <outExe>
            // 纯 IO（不创建 Application / WPF），把 payload 尾部嵌到 Setup.exe 生成单 EXE。
            // 这里必须放在最前面：否则 Application+admin manifest 组合在非管理员环境会弹 UAC。
            // ══════════════════════════════════════════════════════════
            if (args != null && args.Length >= 4 &&
                args[0].Equals("--build-single", StringComparison.OrdinalIgnoreCase))
            {
                try { AttachConsole(ATTACH_PARENT_PROCESS); } catch { }
                try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
                string srcExe = args[1];
                string payDir = args[2];
                string outExe = args[3];
                int last = -1;
                try
                {
                    Installer.BuildSingleExe(srcExe, payDir, outExe, delegate(int p)
                    {
                        if (p != last)
                        {
                            last = p;
                            Console.Write("\r      [构造单 EXE] {0,3}%   ({1})", p,
                                (p < 3) ? "复制 Setup.exe 本体" :
                                (p < 92) ? "写入 payload 数据段" :
                                (p < 98) ? "写索引" : "写 FOOTER 签名");
                        }
                    });
                    Console.WriteLine();
                    FileInfo fi = new FileInfo(outExe);
                    double gb = fi.Length / (1024.0 * 1024.0 * 1024.0);
                    Console.WriteLine("      [构造单 EXE] 完成 → {0}   ({1:N2} GB / {2:N0} 字节)",
                        Path.GetFileName(outExe), gb, fi.Length);
                    // 顺便验证 HasEmbeddedBlob=true
                    if (!Installer.HasEmbeddedBlob(outExe))
                        throw new Exception("生成的单 EXE 未通过 HasEmbeddedBlob 检测。");
                    Console.WriteLine("      [构造单 EXE] 尾部签名验证：PASS (magic=KLAP)");
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("\n[FATAL] 构造单 EXE 失败：" + ex.Message);
                    return 11;
                }
            }

            bool selfTest = false;
            for (int i = 0; i < (args == null ? 0 : args.Length); i++)
                if (args[i].Equals("--selftest", StringComparison.OrdinalIgnoreCase))
                    selfTest = true;

            BootLog(selfTest ? "  --selftest flag detected." : "  --selftest flag NOT detected; GUI path.");

            BootLog("  [step] new Application() ...");
            Application app = new Application();
            // NOTE: 不能用 OnMainWindowClose。单 EXE 构建下，PreparingWindow（正在准备安装文件
            // 进度对话框）是第一个 new 出来的 Window，WPF 会把 app.MainWindow 自动赋成它。
            // 等后台线程释放完 payload、pw.Close()，OnMainWindowClose 会让整个 App
            // 在 SetupWindow 创建之前就 Shutdown —— 用户看到的就是「打开秒闪退，向导没出来」。
            // 改为 OnExplicitShutdown：每个 return 分支手动 app.Shutdown(code)。
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            BootLog("  [step] Application created OK. ShutdownMode=OnExplicitShutdown. Hook global exceptions.");
            HookGlobalExceptions(app);

            try
            {
                BootLog("  [step] Theme.Build() + Merge ...");
                app.Resources.MergedDictionaries.Add(Theme.Build());
                BootLog("  [step] Theme installed OK.  Count=" + app.Resources.MergedDictionaries.Count);
            }
            catch (Exception ex)
            {
                BootLog("  [FAIL] Theme.Build exception: " + ex.GetType().FullName + ": " + ex.Message + "\r\n" + ex.StackTrace);
                if (selfTest) { Console.Error.WriteLine("主题资源解析失败：" + ex.Message); try { app.Shutdown(1); } catch { } return 1; }
                MessageBox.Show("主题资源解析失败：" + ex.Message,
                    Branding.ShortName, MessageBoxButton.OK, MessageBoxImage.Error);
                try { app.Shutdown(1); } catch { }
                return 1;
            }

            if (selfTest)
            {
                BootLog("  [step] Run SelfTest().");
                int rc = SelfTest();
                BootLog("  [step] SelfTest() returned " + rc + "  => app.Shutdown(" + rc + ") then return.");
                try { app.Shutdown(rc); } catch { }
                return rc;
            }

            // ── 单 EXE：运行前检查是否带尾部 payload 嵌入 → 先自解压到 %TEMP% ──
            try
            {
                string me = Assembly.GetExecutingAssembly().Location;
                BootLog("  [step] HasEmbeddedBlob check on my EXE: " + me);
                bool blob = Installer.HasEmbeddedBlob(me);
                BootLog("  [step] HasEmbeddedBlob = " + blob);
                if (blob)
                {
                    BootLog("  [step] new PreparingWindow();  Current MainWindow=" +
                            (app.MainWindow == null ? "(null)" : app.MainWindow.GetType().Name));
                    PreparingWindow pw = new PreparingWindow();
                    BootLog("  [step] PreparingWindow created. Now MainWindow=" +
                            (app.MainWindow == null ? "(null)" : app.MainWindow.GetType().Name) +
                            "  (if it is now PreparingWindow, then OnMainWindowClose will shut down the app right after pw.Close!)");
                    Exception err = null;
                    string extractedDir = null;

                    System.Threading.Thread th = new System.Threading.Thread(delegate()
                    {
                        try
                        {
                            BootLog("  [bg-thread] ExtractEmbeddedPayload start.");
                            extractedDir = Installer.ExtractEmbeddedPayload(me,
                                delegate(int p) { try { pw.Update(p, string.Format("正在释放安装文件… {0}%", p)); } catch { } });
                            BootLog("  [bg-thread] ExtractEmbeddedPayload OK. dir=[" + (extractedDir ?? "(null)") + "]");
                            Installer.SetRuntimePayloadDir(extractedDir);
                            BootLog("  [bg-thread] SetRuntimePayloadDir OK. Will Dispatcher.BeginInvoke(pw.Close).");
                        }
                        catch (Exception ex)
                        {
                            err = ex;
                            BootLog("  [bg-thread] ExtractEmbeddedPayload EXCEPTION: " + ex.GetType().FullName + ": " + ex.Message + "\r\n" + ex.StackTrace);
                        }
                        if (!pw.Dispatcher.HasShutdownStarted)
                            pw.Dispatcher.BeginInvoke((Action)delegate
                            {
                                BootLog("  [ui-thread] pw.Close() via Dispatcher.BeginInvoke");
                                try { pw.Close(); } catch (Exception e2) { BootLog("  [ui-thread] pw.Close threw: " + e2.Message); }
                            });
                        else
                            BootLog("  [bg-thread] Dispatcher already HasShutdownStarted=true — skip pw.Close()");
                    });
                    th.IsBackground = true;
                    BootLog("  [step] Starting extract thread + pw.ShowDialog()");
                    th.Start();
                    pw.ShowDialog();   // 阻塞直到释放完成或失败
                    BootLog("  [step] pw.ShowDialog() RETURNED. After dialog, MainWindow=" +
                            (app.MainWindow == null ? "(null)" : app.MainWindow.GetType().Name) +
                            "  app IsDefined=" + (Application.Current != null));
                    // OnExplicitShutdown + 清空 MainWindow：确保接下来 new SetupWindow() 时它成为真正的主窗口，
                    // 并防止后续任何 pw 相关回调误认为 App 要关了。
                    try { app.MainWindow = null; } catch { }

                    if (err != null)
                    {
                        BootLog("  [FAIL] Extract err: " + err.GetType().FullName + ": " + err.Message);
                        MessageBox.Show("准备安装文件失败：\n\n" + err.Message,
                            Branding.ShortName, MessageBoxButton.OK, MessageBoxImage.Error);
                        try { app.Shutdown(3); } catch { }
                        return 3;
                    }
                }
                else
                {
                    BootLog("  [step] No embedded blob. Expecting sibling payload\\ folder. FindPayloadDir ...");
                    string fnd = Installer.FindPayloadDir();
                    BootLog("  [step] FindPayloadDir returned: [" + (fnd ?? "(null - will fail later, but let's try SetupWindow anyway)") + "]");
                }
            }
            catch (Exception ex)
            {
                BootLog("  [FAIL] Prepare wrapper: " + ex.GetType().FullName + ": " + ex.Message + "\r\n" + ex.StackTrace);
                MessageBox.Show("准备安装文件失败：\n\n" + ex.ToString(),
                    Branding.ShortName, MessageBoxButton.OK, MessageBoxImage.Error);
                try { app.Shutdown(3); } catch { }
                return 3;
            }

            try
            {
                BootLog("  [step] new SetupWindow(); MainWindow=" +
                        (app.MainWindow == null ? "(null)" : app.MainWindow.GetType().Name));
                SetupWindow w = new SetupWindow(Slogans);
                BootLog("  [step] SetupWindow created; MainWindow now=" +
                        (app.MainWindow == null ? "(null)" : app.MainWindow.GetType().Name));
                // 显式把 SetupWindow 设为主窗口（OnExplicitShutdown 下这一步不是必须的，但对 Alt+F4 关闭主窗口 => 关 App 语义有帮助）
                try { if (app.MainWindow == null) app.MainWindow = w; } catch { }
                BootLog("  [step] w.ShowDialog() — entering installer wizard (user interaction follows)");
                w.ShowDialog();
                BootLog("  [step] SetupWindow.ShowDialog() returned. End of Main, app.Shutdown(0) then return 0.");
            }
            catch (Exception ex)
            {
                BootLog("  [FAIL] SetupWindow phase: " + ex.GetType().FullName + ": " + ex.Message + "\r\n" + ex.StackTrace);
                MessageBox.Show("安装程序出错：\n\n" + ex.ToString(),
                    Branding.ShortName, MessageBoxButton.OK, MessageBoxImage.Error);
                try { app.Shutdown(2); } catch { }
                return 2;
            }
            BootLog("======= Main returning 0 normally =======");
            try { app.Shutdown(0); } catch { }
            return 0;
        }

        private static int SelfTest()
        {
            int bad = 0;
            System.Text.StringBuilder rep = new System.Text.StringBuilder();
            Action<string, Action> chk = delegate(string name, Action fn)
            {
                try { fn(); rep.AppendLine("  [OK]  " + name); }
                catch (Exception ex) { bad++; rep.AppendLine("  [FAIL] " + name + " — " + ex.ToString().Replace("\r\n", "\n        ").Replace("\n", "\n        ")); }
            };

            chk("主题资源字典", delegate
            {
                if (Theme.Build().Count == 0) throw new Exception("空字典");
            });
            chk("法律资源", delegate
            {
                if (string.IsNullOrEmpty(Legal.EulaText())) throw new Exception("EULA 空");
                if (string.IsNullOrEmpty(Legal.PrivacyText())) throw new Exception("隐私空");
            });
            chk("安装窗口（Welcome→Legal→Dir→Progress→Done 逐页构造）", delegate
            {
                SetupWindow w = new SetupWindow(new string[] { "自测标语" });
                try { w.BuildAllPages(); } finally { w.Close(); }
            });
            chk("payload 目录发现性（exe 同目录下 payload\\KarlsLightAccess.exe）", delegate
            {
                string p = Installer.FindPayloadDir();
                if (p == null) throw new Exception(
                    "找不到 payload 目录（Setup.exe 旁边要有 payload\\KarlsLightAccess.exe + image\\）。\n" +
                    "（构建阶段本项允许 FAIL，仅提醒；正式发布时必须 PASS）");
            });
            chk("备份引擎 wimlib（payload\\bin\\wimlib-imagex.exe + libwim-15.dll）", delegate
            {
                string p = Installer.FindPayloadDir();
                if (p == null) throw new Exception(
                    "找不到 payload 目录，无法检查 wimlib。");
                if (!File.Exists(Path.Combine(p, "bin", "wimlib-imagex.exe")) ||
                    !File.Exists(Path.Combine(p, "bin", "libwim-15.dll")))
                    throw new Exception(
                        "payload\\bin\\ 缺 wimlib-imagex.exe / libwim-15.dll。\n" +
                        "装出去的主程序点备份会弹「备份引擎尚未就位」。");
            });
            chk("自解压格式 round-trip（BuildSingleExe → HasEmbeddedBlob → ExtractEmbeddedPayload → 字节一致）",
                delegate { Installer.SelfTestRoundTrip(); });

            string tail = (bad == 0) ? "安装器自检通过" : ("安装器自检 " + bad + " 项失败");
            rep.AppendLine(tail);
            string all = rep.ToString();

            // ① 能 AttachConsole 到父进程就打控制台
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; Console.Write(all); } catch { }

            // ② 无论如何写一份到 exe 同级 setup-selftest.log（Build.ps1 读它）
            try
            {
                string me = Assembly.GetExecutingAssembly().Location;
                string log = Path.Combine(Path.GetDirectoryName(me), "setup-selftest.log");
                File.WriteAllText(log, all, System.Text.Encoding.UTF8);
            }
            catch { }

            return (bad == 0) ? 0 : 1;
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // 安装引导主窗口。900×620，左侧品牌条 + 右侧页面内容 + 底部导航。
    // ════════════════════════════════════════════════════════════════════
    internal class SetupWindow : DarkWindow
    {
        private const int StepWelcome  = 0;
        private const int StepLegal    = 1;
        private const int StepDir      = 2;
        private const int StepProgress = 3;
        private const int StepDone     = 4;
        private static readonly string[] StepNames = new string[] {
            "欢迎", "协议", "安装位置", "正在安装", "完成"
        };

        private readonly string[] _slogans;
        private int _step;
        private FrameworkElement _currentPage;
        private readonly Border _contentHolder;
        private readonly TextBlock[] _stepLabels;
        private readonly Ellipse[] _stepDots;

        private Button _btnBack;
        private Button _btnNext;
        private Button _btnCancel;

        // Legal 页状态
        private CheckBox _ckEula;
        private CheckBox _ckPrivacy;

        // Dir 页状态
        private TextBox _dirBox;
        private TextBlock _spaceHint;

        // Progress 页状态
        private ProgressBar _pbar;
        private TextBlock _status;

        // Done 页状态
        private CheckBox _launchBox;

        public SetupWindow(string[] slogans)
            : base("安装 " + Branding.ProductName, 900, 620)
        {
            SetCaption("安装向导 — " + Branding.ProductName);
            MinWidth = 860; MinHeight = 580;
            ResizeMode = ResizeMode.CanMinimize;

            _slogans = slogans ?? new string[0];
            _step = 0;

            // 步骤缓存必须先分配：BuildBrandBar() 里会写入 _stepLabels/_stepDots 元素。
            _stepLabels = new TextBlock[StepNames.Length];
            _stepDots   = new Ellipse[StepNames.Length];

            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // ── 中间主体：左品牌栏 / 右内容页 ──────────────────────────
            Grid body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(body, 0);
            root.Children.Add(body);

            body.Children.Add(BuildBrandBar());   // column 0
            _contentHolder = BuildContentPane();
            Grid.SetColumn(_contentHolder, 1);
            body.Children.Add(_contentHolder);

            // ── 底部导航栏 ──────────────────────────────────────────
            Border foot = new Border();
            foot.Background = Theme.Brush(Theme.Surface);
            foot.BorderBrush = Theme.Brush(Theme.Border);
            foot.BorderThickness = new Thickness(0, 1, 0, 0);
            foot.Padding = new Thickness(28, 14, 28, 18);
            Grid.SetRow(foot, 1);

            Grid nav = new Grid();
            nav.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            nav.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            nav.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            nav.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _btnBack   = Ui.Btn("上一步", delegate { OnBack(); });
            _btnNext   = Ui.Primary("下一步", delegate { OnNext(); });
            _btnCancel = Ui.Btn("取消", delegate { OnCancel(); });
            Grid.SetColumn(_btnBack,   1);
            Grid.SetColumn(_btnNext,   2);
            Grid.SetColumn(_btnCancel, 3);
            _btnBack.Margin   = new Thickness(0, 0, 12, 0);
            _btnNext.Margin   = new Thickness(0, 0, 12, 0);
            nav.Children.Add(_btnBack);
            nav.Children.Add(_btnNext);
            nav.Children.Add(_btnCancel);
            foot.Child = nav;
            root.Children.Add(foot);

            Host.Content = root;
            GoTo(0);
        }

        // ───────────────────────────────────────────────────────────
        // 步骤 0：左侧品牌条（260px 藏青渐变 + 星芒 + 字标 + 步骤列表 + 版权）
        // ───────────────────────────────────────────────────────────
        private FrameworkElement BuildBrandBar()
        {
            Border bar = new Border();
            LinearGradientBrush bg = new LinearGradientBrush();
            bg.StartPoint = new Point(0, 0);
            bg.EndPoint   = new Point(1, 1);
            bg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(Theme.NavyDark), 0));
            bg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(Theme.Navy),     0.55));
            bg.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(Theme.NavyLite), 1));
            bar.Background = bg;

            Grid g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.Margin = new Thickness(28, 30, 20, 26);

            // ── 品牌头：星芒 + 字标 ──────────────────────────────
            StackPanel head = new StackPanel();
            head.Orientation = Orientation.Horizontal;
            FrameworkElement star = Ui.Starburst(46, Theme.Brush("#E8E6EA"));
            star.VerticalAlignment = VerticalAlignment.Center;
            star.Margin = new Thickness(0, 0, 12, 0);
            head.Children.Add(star);

            StackPanel w = new StackPanel();
            TextBlock n = new TextBlock(); n.Text = "KARL'S LIGHT";
            n.Foreground = new SolidColorBrush(Colors.White); n.FontSize = 18;
            n.FontFamily = Theme.UiFont;
            w.Children.Add(n);
            TextBlock s = new TextBlock(); s.Text = "A C C E S S   S E T U P";
            s.Foreground = Theme.Brush("#C8C0D0"); s.FontSize = 11; s.Margin = new Thickness(1, 2, 0, 0);
            s.FontFamily = Theme.UiFont; s.FontWeight = FontWeights.Light;
            w.Children.Add(s);
            head.Children.Add(w);
            Grid.SetRow(head, 0);
            g.Children.Add(head);

            // ── 步骤列表（5 步，当前高亮）──────────────────────
            StackPanel steps = new StackPanel();
            steps.VerticalAlignment = VerticalAlignment.Center;
            steps.Margin = new Thickness(0, 34, 0, 0);
            for (int i = 0; i < StepNames.Length; i++)
            {
                Grid row = new Grid();
                row.Margin = new Thickness(0, 10, 0, 10);
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                Ellipse dot = new Ellipse();
                dot.Width = 16; dot.Height = 16;
                dot.StrokeThickness = 2;
                dot.Stroke = new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF));
                dot.Fill = Brushes.Transparent;
                dot.VerticalAlignment = VerticalAlignment.Center;
                dot.HorizontalAlignment = HorizontalAlignment.Center;
                Grid.SetColumn(dot, 0);
                _stepDots[i] = dot;

                TextBlock t = new TextBlock();
                t.Text = (i + 1) + ".  " + StepNames[i];
                t.Foreground = new SolidColorBrush(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF));
                t.FontFamily = Theme.UiFont; t.FontSize = 13;
                t.VerticalAlignment = VerticalAlignment.Center;
                t.Margin = new Thickness(14, 0, 0, 0);
                Grid.SetColumn(t, 1);
                _stepLabels[i] = t;

                row.Children.Add(dot);
                row.Children.Add(t);
                steps.Children.Add(row);
            }
            Grid.SetRow(steps, 1);
            g.Children.Add(steps);

            // ── 底部版权两行（和 exe 关于页严格一致）──────────
            StackPanel footCopy = new StackPanel();
            TextBlock c1 = new TextBlock();
            c1.Text = "© KARL'S LIGHT CO., LTD. All Rights Reserved.";
            c1.Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xE8, 0xE6, 0xEA));
            c1.FontFamily = Theme.UiFont; c1.FontSize = 11;
            footCopy.Children.Add(c1);
            TextBlock c2 = new TextBlock();
            c2.Text = "Designed by KARL'S LIGHT in Guiyang";
            c2.Foreground = new SolidColorBrush(Color.FromArgb(0x9A, 0xE8, 0xE6, 0xEA));
            c2.FontFamily = Theme.UiFont; c2.FontSize = 10;
            c2.Margin = new Thickness(0, 3, 0, 0);
            footCopy.Children.Add(c2);
            Grid.SetRow(footCopy, 2);
            g.Children.Add(footCopy);

            bar.Child = g;
            return bar;
        }

        private Border BuildContentPane()
        {
            Border b = new Border();
            b.Background = Theme.Brush(Theme.Bg);
            b.Padding = new Thickness(36, 30, 30, 14);
            return b;
        }

        // ───────────────────────────────────────────────────────────
        // 步骤切换
        // ───────────────────────────────────────────────────────────
        private void GoTo(int step)
        {
            _step = step;
            // 刷新步骤指示
            for (int i = 0; i < StepNames.Length; i++)
            {
                if (i == _step)
                {
                    _stepDots[i].Fill = Theme.Brush(Theme.RedLite);
                    _stepDots[i].Stroke = Theme.Brush(Theme.RedLite);
                    _stepLabels[i].FontWeight = FontWeights.Bold;
                    _stepLabels[i].Foreground = new SolidColorBrush(Colors.White);
                }
                else if (i < _step)
                {
                    _stepDots[i].Fill = new SolidColorBrush(Colors.White);
                    _stepDots[i].Stroke = new SolidColorBrush(Colors.White);
                    _stepLabels[i].Foreground = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF));
                    _stepLabels[i].FontWeight = FontWeights.Normal;
                }
                else
                {
                    _stepDots[i].Fill = Brushes.Transparent;
                    _stepDots[i].Stroke = new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF));
                    _stepLabels[i].Foreground = new SolidColorBrush(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF));
                    _stepLabels[i].FontWeight = FontWeights.Normal;
                }
            }

            // 按钮可见/文案
            _btnBack.IsEnabled = (_step > StepWelcome && _step != StepProgress);
            _btnBack.Visibility = (_step == StepDone) ? Visibility.Collapsed : Visibility.Visible;
            _btnCancel.Visibility = (_step == StepDone) ? Visibility.Collapsed : Visibility.Visible;

            switch (_step)
            {
                case StepWelcome:   ShowPage(PageWelcome());   _btnNext.Content = "下一步"; break;
                case StepLegal:     ShowPage(PageLegal());     _btnNext.Content = "同意并继续"; UpdateLegalGate(); break;
                case StepDir:       ShowPage(PageDir());       _btnNext.Content = "开始安装"; UpdateSpaceHint(); break;
                case StepProgress:  ShowPage(PageProgress());  _btnNext.Content = "下一步"; _btnNext.IsEnabled = false; BeginInstall(); break;
                case StepDone:      ShowPage(PageDone());      _btnNext.Content = "完成"; break;
            }
        }

        private void ShowPage(FrameworkElement page)
        {
            _currentPage = page;
            _contentHolder.Child = page;
        }

        public void BuildAllPages()
        {
            // 用于 --selftest：逐页构造，确保每一页都不抛异常
            for (int i = 0; i <= 4; i++)
            {
                switch (i)
                {
                    case 0: PageWelcome();    break;
                    case 1: PageLegal();      break;
                    case 2: PageDir();        break;
                    case 3: /* Progress: no UI to build standalone; Skip run installer */ break;
                    case 4: PageDone();       break;
                }
            }
        }

        private void OnBack()   { if (_step > StepWelcome && _step != StepProgress) GoTo(_step - 1); }
        private void OnCancel()
        {
            if (_step == StepProgress) { /* 正在安装不允许取消 */ return; }
            MessageBoxResult r = MessageBox.Show(this,
                "确定要退出安装吗？", Branding.ProductName,
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (r == MessageBoxResult.Yes) Close();
        }

        private void OnNext()
        {
            switch (_step)
            {
                case StepWelcome: GoTo(StepLegal); break;
                case StepLegal:
                    if (!_ckEula.IsChecked.GetValueOrDefault() || !_ckPrivacy.IsChecked.GetValueOrDefault())
                        return;
                    GoTo(StepDir);
                    break;
                case StepDir:
                    if (!Directory.Exists(Path.GetPathRoot(_dirBox.Text)))
                    {
                        MessageBox.Show(this, "安装路径的盘符不存在：" + _dirBox.Text,
                            Branding.ShortName, MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    GoTo(StepProgress);
                    break;
                case StepDone:
                    try
                    {
                        if (_launchBox.IsChecked.GetValueOrDefault())
                        {
                            string exe = Path.Combine(_dirBox.Text, "KarlsLightAccess.exe");
                            if (File.Exists(exe)) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                        }
                    }
                    catch { /* 启动失败不影响安装收尾 */ }
                    Close();
                    break;
            }
        }

        // ══════════════════════════════════════════════════════════
        // 页面 1：欢迎（slogan 轮播 3s/fade + 4 功能要点）
        // ══════════════════════════════════════════════════════════
        private FrameworkElement PageWelcome()
        {
            StackPanel col = new StackPanel();

            TextBlock h = Ui.H1("欢迎使用 " + Branding.ProductName);
            col.Children.Add(h);

            TextBlock sub = Ui.Dim("安装前请关闭其它正在运行的程序。安装大约需要 3-10 分钟，视磁盘速度而定。");
            sub.Margin = new Thickness(0, 6, 0, 22);
            sub.TextWrapping = TextWrapping.Wrap;
            col.Children.Add(sub);

            // Slogan 轮播容器（用高一点的边框，给淡入动画留呼吸空间）
            Border sloganBox = new Border();
            sloganBox.Background = Theme.Brush(Theme.Surface);
            sloganBox.BorderBrush = Theme.Brush(Theme.Border);
            sloganBox.BorderThickness = new Thickness(1);
            sloganBox.Padding = new Thickness(24, 22, 22, 22);
            sloganBox.CornerRadius = new CornerRadius(6);
            sloganBox.Margin = new Thickness(0, 0, 0, 24);

            TextBlock slogan = new TextBlock();
            slogan.Foreground = Theme.Brush(Theme.RedLite);
            slogan.FontFamily = Theme.UiFont;
            slogan.FontSize = 18;
            slogan.FontWeight = FontWeights.Light;
            slogan.TextWrapping = TextWrapping.Wrap;
            slogan.Text = (_slogans.Length > 0) ? _slogans[0] : Branding.ProductName;
            slogan.Name = "SloganText";
            sloganBox.Child = slogan;
            col.Children.Add(sloganBox);

            if (_slogans.Length > 1)
            {
                int idx = 0;
                DispatcherTimer t = new DispatcherTimer(DispatcherPriority.Background);
                t.Interval = TimeSpan.FromSeconds(3);
                t.Tick += delegate
                {
                    idx = (idx + 1) % _slogans.Length;
                    // 先淡到透明 150ms → 换字 → 淡回来 250ms
                    DoubleAnimation a1 = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
                    a1.Completed += delegate
                    {
                        slogan.Text = _slogans[idx];
                        DoubleAnimation a2 = new DoubleAnimation(1, TimeSpan.FromMilliseconds(250));
                        slogan.BeginAnimation(UIElement.OpacityProperty, a2);
                    };
                    slogan.BeginAnimation(UIElement.OpacityProperty, a1);
                };
                t.Start();
                // 窗口关闭时停 timer（避免 leak）
                this.Closed += delegate { try { t.Stop(); } catch { } };
            }

            // 4 个功能要点（小卡片）
            TextBlock featTitle = Ui.Text("产品亮点");
            featTitle.FontSize = 13; featTitle.FontWeight = FontWeights.Bold;
            featTitle.Margin = new Thickness(0, 0, 0, 10);
            col.Children.Add(featTitle);

            string[][] feats = new string[][]
            {
                new string[] { "开机救援",       "每次开机都进 KLA 菜单，3 秒不操作回 Windows，期间按 F4 启动 KLA 救援。" },
                new string[] { "一键还原",       "还原点备份、差量快照、出厂基准备份一键完成。" },
                new string[] { "不依赖 U 盘",   "救援系统装在本机隐藏分区，断电也不丢。" },
                new string[] { "官方支持",       Branding.Website + " · Designed by KARL'S LIGHT in Guiyang" },
            };
            foreach (string[] f in feats)
            {
                Grid row = new Grid();
                row.Margin = new Thickness(0, 4, 0, 4);
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                TextBlock k = Ui.Text(f[0]);
                k.FontWeight = FontWeights.Bold; k.Foreground = Theme.Brush(Theme.RedLite);
                Grid.SetColumn(k, 0); row.Children.Add(k);
                TextBlock v = Ui.Dim(f[1]);
                v.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(v, 1); row.Children.Add(v);
                col.Children.Add(row);
            }

            ScrollViewer sv = new ScrollViewer();
            sv.Content = col;
            sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            sv.Padding = new Thickness(0);
            return sv;
        }

        // ══════════════════════════════════════════════════════════
        // 页面 2：协议（和主程序 LegalWindow 同款，双勾选）
        // ══════════════════════════════════════════════════════════
        private FrameworkElement PageLegal()
        {
            Grid g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock title = Ui.H1("用户协议与隐私政策");
            title.Margin = new Thickness(0, 0, 0, 12);

            FlowDocumentScrollViewer fsv = new FlowDocumentScrollViewer();
            fsv.Document = MarkdownView.Build(Legal.CombinedText(), 13);
            fsv.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            fsv.IsToolBarVisible = false;
            fsv.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
            fsv.FontFamily = Theme.UiFont;
            fsv.Foreground = Theme.Brush(Theme.Text);
            fsv.Padding = new Thickness(0);
            fsv.Background = Theme.Brush(Theme.Bg);

            ScrollViewer sv = new ScrollViewer();
            sv.Content = new StackPanel { Children = { title, fsv } };
            sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            Grid.SetRow(sv, 0);
            g.Children.Add(sv);

            // 勾选 + 拒绝提示
            StackPanel ck = new StackPanel();
            ck.Margin = new Thickness(0, 14, 0, 0);
            // 两个勾选框的文字里《协议名》是 Hyperlink，点一下弹 DocViewerWindow 全文。
            // 合规提示：PIPL 第十七条要求"便捷的撤回方式"——勾选框本身就是撤回开关。
            _ckEula = new CheckBox();
            _ckEula.Foreground = Theme.Brush(Theme.Text);
            _ckEula.Margin = new Thickness(0, 0, 0, 8);
            TextBlock eb = new TextBlock();
            eb.Inlines.Add(new Run("我已阅读并同意"));
            Hyperlink hlEula = new Hyperlink(new Run("《最终用户许可协议》"));
            hlEula.Foreground = Theme.Brush(Theme.RedLite);
            hlEula.Cursor = System.Windows.Input.Cursors.Hand;
            hlEula.TextDecorations = TextDecorations.Underline;
            hlEula.Click += delegate
            {
                try { DocViewerWindow.Show(this, "最终用户许可协议", Legal.EulaText()); }
                catch (Exception ex) { SetupProgram.BootLog("[Legal Page] open EULA viewer err: " + ex.Message); }
            };
            eb.Inlines.Add(hlEula);
            _ckEula.Content = eb;
            _ckEula.Checked   += delegate { UpdateLegalGate(); };
            _ckEula.Unchecked += delegate { UpdateLegalGate(); };
            ck.Children.Add(_ckEula);

            _ckPrivacy = new CheckBox();
            _ckPrivacy.Foreground = Theme.Brush(Theme.Text);
            _ckPrivacy.Margin = new Thickness(0, 0, 0, 8);
            TextBlock pb = new TextBlock();
            pb.Inlines.Add(new Run("我已阅读并同意"));
            Hyperlink hlPriv = new Hyperlink(new Run("《隐私政策》"));
            hlPriv.Foreground = Theme.Brush(Theme.RedLite);
            hlPriv.Cursor = System.Windows.Input.Cursors.Hand;
            hlPriv.TextDecorations = TextDecorations.Underline;
            hlPriv.Click += delegate
            {
                try { DocViewerWindow.Show(this, "隐私政策", Legal.PrivacyText()); }
                catch (Exception ex) { SetupProgram.BootLog("[Legal Page] open Privacy viewer err: " + ex.Message); }
            };
            pb.Inlines.Add(hlPriv);
            _ckPrivacy.Content = pb;
            _ckPrivacy.Checked   += delegate { UpdateLegalGate(); };
            _ckPrivacy.Unchecked += delegate { UpdateLegalGate(); };
            ck.Children.Add(_ckPrivacy);

            TextBlock tipLinks = Ui.Dim("点击上方《最终用户许可协议》《隐私政策》可单独查看全文。");
            tipLinks.Margin = new Thickness(0, 0, 0, 6);
            ck.Children.Add(tipLinks);
            TextBlock warn = Ui.Dim("如您不同意以上任一协议，请点击「取消」退出安装。");
            ck.Children.Add(warn);
            Grid.SetRow(ck, 1);
            g.Children.Add(ck);
            return g;
        }

        private void UpdateLegalGate()
        {
            if (_btnNext == null || _step != StepLegal) return;
            _btnNext.IsEnabled = _ckEula.IsChecked.GetValueOrDefault() &&
                                 _ckPrivacy.IsChecked.GetValueOrDefault();
        }

        // ══════════════════════════════════════════════════════════
        // 页面 3：选择安装路径
        // ══════════════════════════════════════════════════════════
        private FrameworkElement PageDir()
        {
            StackPanel col = new StackPanel();

            TextBlock h = Ui.H1("选择安装位置");
            col.Children.Add(h);

            TextBlock sub = Ui.Dim(
                "建议安装到系统盘以外的磁盘。如果系统盘需要还原，安装目录里的" +
                "还原点不会和系统一起被覆盖。");
            sub.Margin = new Thickness(0, 6, 0, 22);
            sub.TextWrapping = TextWrapping.Wrap;
            col.Children.Add(sub);

            // 路径输入 + 浏览
            Grid row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _dirBox = new TextBox();
            _dirBox.Text = Installer.DefaultInstallDir();
            _dirBox.Padding = new Thickness(10, 8, 10, 8);
            _dirBox.FontFamily = Theme.UiFont; _dirBox.FontSize = 13;
            _dirBox.Background = Theme.Brush(Theme.Surface);
            _dirBox.Foreground = Theme.Brush(Theme.Text);
            _dirBox.BorderBrush = Theme.Brush(Theme.Border);
            _dirBox.BorderThickness = new Thickness(1);
            _dirBox.VerticalContentAlignment = VerticalAlignment.Center;
            _dirBox.TextChanged += delegate { UpdateSpaceHint(); };
            Grid.SetColumn(_dirBox, 0); row.Children.Add(_dirBox);

            Button browse = Ui.Btn("浏览…", delegate
            {
                // Win7/8 没有 FolderBrowserEx 的 Modern UI；用 Win32 的 FolderBrowserDialog
                // （System.Windows.Forms 不能直接引用，框架子集没引）。
                // 简化：弹 InputBox 让用户改路径，或者干脆写死弹窗。
                try
                {
                    // 用系统自带的 Shell 文件夹选择对话框（无需额外引用）
                    Type shellDlg = Type.GetTypeFromProgID("Shell.Application");
                    object shell = Activator.CreateInstance(shellDlg);
                    object folder = shellDlg.InvokeMember("BrowseForFolder",
                        BindingFlags.InvokeMethod, null, shell,
                        new object[] { 0, "请选择安装目录", 0, null });
                    if (folder != null)
                    {
                        object self = folder.GetType().InvokeMember("Self",
                            BindingFlags.GetProperty, null, folder, null);
                        object path = self.GetType().InvokeMember("Path",
                            BindingFlags.GetProperty, null, self, null);
                        string p = (path ?? "").ToString();
                        if (!string.IsNullOrEmpty(p))
                        {
                            if (Directory.Exists(p))
                                _dirBox.Text = Path.Combine(p, Branding.ProductName);
                            else
                                _dirBox.Text = p;
                        }
                    }
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message); }
            });
            browse.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(browse, 1); row.Children.Add(browse);
            col.Children.Add(row);

            // 空间提示
            _spaceHint = Ui.Dim("");
            _spaceHint.Margin = new Thickness(0, 14, 0, 0);
            col.Children.Add(_spaceHint);
            UpdateSpaceHint();

            return col;
        }

        private void UpdateSpaceHint()
        {
            if (_spaceHint == null || _dirBox == null) return;
            try
            {
                string root = Path.GetPathRoot(_dirBox.Text);
                long needed = Installer.PayloadBytes();
                DriveInfo di = new DriveInfo(root);
                double availGB = di.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
                double needGB  = needed / (1024.0 * 1024.0 * 1024.0);
                if (availGB >= needGB)
                    _spaceHint.Text = string.Format(
                        "程序与资源共需约 {0:F1} GB · 当前磁盘剩余 {1:F1} GB（空间充足）。",
                        needGB, availGB);
                else
                    _spaceHint.Text = string.Format(
                        "⚠ 空间不足：需要 {0:F1} GB，磁盘仅剩 {1:F1} GB。请选择其它磁盘。",
                        needGB, availGB);
            }
            catch (Exception ex)
            {
                _spaceHint.Text = "无法读取磁盘空间：" + ex.Message;
            }
        }

        // ══════════════════════════════════════════════════════════
        // 页面 4：安装进度
        // ══════════════════════════════════════════════════════════
        private FrameworkElement PageProgress()
        {
            StackPanel col = new StackPanel();

            TextBlock h = Ui.H1("正在安装 " + Branding.ProductName);
            h.Margin = new Thickness(0, 0, 0, 22);
            col.Children.Add(h);

            _pbar = new ProgressBar();
            _pbar.Minimum = 0; _pbar.Maximum = 100;
            _pbar.Height = 14; _pbar.Value = 0;
            _pbar.Foreground = Theme.Brush(Theme.RedLite);
            _pbar.Background = Theme.Brush(Theme.Surface2);
            _pbar.BorderBrush = Theme.Brush(Theme.Border);
            col.Children.Add(_pbar);

            _status = Ui.Dim("准备中…");
            _status.Margin = new Thickness(0, 14, 0, 0);
            _status.TextWrapping = TextWrapping.Wrap;
            col.Children.Add(_status);

            Border box = new Border();
            box.Background = Theme.Brush(Theme.Surface);
            box.BorderBrush = Theme.Brush(Theme.Border);
            box.BorderThickness = new Thickness(1);
            box.Padding = new Thickness(18, 16, 18, 16);
            box.CornerRadius = new CornerRadius(4);
            box.Margin = new Thickness(0, 24, 0, 0);
            TextBlock tip = Ui.Dim(
                "提示：安装过程中请勿拔出存储设备或强制关机。\n" +
                "主程序与 EFI 引导资产约 600KB，\n" +
                "救援分区镜像约 1 GB（复制它占 90% 时间）。");
            tip.TextWrapping = TextWrapping.Wrap;
            box.Child = tip;
            col.Children.Add(box);

            return col;
        }

        private void BeginInstall()
        {
            // ── 覆盖更新提示：检测到旧版先问一句（BeginInstall 在 UI 线程）────
            try
            {
                if (Installer.DetectExistingInstall())
                {
                    MessageBoxResult r = System.Windows.MessageBox.Show(
                        "检测到这台电脑上已经安装了 KARL'S LIGHT ACCESS。\n\n" +
                        "继续安装将执行覆盖更新：\n" +
                        "  · 程序文件升级为新版本；\n" +
                        "  · 首次启动重新走一遍协议与部署向导；\n" +
                        "  · 已部署的急救系统分区会被复用/重建，不会重复占空间。\n\n" +
                        "是否继续覆盖更新？",
                        "覆盖更新确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (r != MessageBoxResult.Yes)
                    {
                        SetupProgram.BootLog("[BeginInstall] 用户取消覆盖更新");
                        return;
                    }
                    Installer.UpdateDetected = true;
                }
            }
            catch (Exception ex) { SetupProgram.BootLog("[BeginInstall] 覆盖检测异常（忽略）: " + ex.Message); }

            // ── UI 线程采集一次所有要用到的参数（WPF 对象属性不能跨线程读）────────────
            string installDir;
            try { installDir = _dirBox.Text; }
            catch { installDir = Installer.DefaultInstallDir(); }
            if (string.IsNullOrWhiteSpace(installDir)) installDir = Installer.DefaultInstallDir();
            SetupProgram.BootLog("[BeginInstall] installDir snapshot=[" + installDir + "]");

            // 不用 .NET 4.5 的 IProgress<T>/Progress<T>（本机 csc 4.0.30319），
            // 自己写一个 UI 线程包装的 Action<int> 进度回调。
            Action<int> report = delegate(int v)
            {
                try
                {
                    if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                    Dispatcher.BeginInvoke((Action)delegate
                    {
                        try
                        {
                            _pbar.Value = v;
                            if (v == 100) { _status.Text = "安装完成。"; _btnNext.IsEnabled = true; GoTo(StepDone); }
                            else
                            {
                                // 注意：LastStatus 由后台线程写；这里只是读 string，不用加锁。
                                string s = Installer.LastStatus;
                                if (!string.IsNullOrEmpty(s)) _status.Text = s;
                            }
                        }
                        catch (Exception uiEx)
                        {
                            SetupProgram.BootLog("[BeginInstall] report UI apply err: " + uiEx.GetType().Name + ": " + uiEx.Message);
                        }
                    });
                }
                catch (Exception repEx)
                {
                    SetupProgram.BootLog("[BeginInstall] report dispatch err: " + repEx.GetType().Name + ": " + repEx.Message);
                }
            };

            System.Threading.Thread th = new System.Threading.Thread(delegate()
            {
                try
                {
                    SetupProgram.BootLog("[BeginInstall] bg-thread start Installer.Run");
                    Installer.Run(installDir, report);
                    SetupProgram.BootLog("[BeginInstall] bg-thread Installer.Run OK");
                }
                catch (Exception ex)
                {
                    SetupProgram.BootLog("[BeginInstall] bg-thread Installer.Run EXCEPTION: " + ex.GetType().FullName + ": " + ex.Message + "\r\n" + ex.StackTrace);
                    try
                    {
                        if (Dispatcher.HasShutdownStarted) { return; }
                        Dispatcher.BeginInvoke((Action)delegate
                        {
                            try
                            {
                                MessageBox.Show(this, "安装失败：\n\n" + ex.ToString(),
                                    Branding.ShortName, MessageBoxButton.OK, MessageBoxImage.Error);
                                _btnNext.IsEnabled = true;
                                _btnNext.Content = "取消";
                            }
                            catch (Exception uiEx)
                            {
                                SetupProgram.BootLog("[BeginInstall] failure MessageBox UI err: " + uiEx.Message);
                            }
                        });
                    }
                    catch (Exception disEx)
                    {
                        SetupProgram.BootLog("[BeginInstall] failure dispatch err: " + disEx.GetType().Name + ": " + disEx.Message);
                    }
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        // ══════════════════════════════════════════════════════════
        // 页面 5：完成
        // ══════════════════════════════════════════════════════════
        private FrameworkElement PageDone()
        {
            StackPanel col = new StackPanel();

            // 完成图标（圆勾）
            Grid ok = new Grid();
            ok.Width = 72; ok.Height = 72;
            ok.HorizontalAlignment = HorizontalAlignment.Left;
            ok.Margin = new Thickness(0, 6, 0, 20);
            Ellipse e = new Ellipse();
            e.Fill = Theme.Brush(Theme.RedLite);
            ok.Children.Add(e);
            TextBlock check = new TextBlock();
            check.Text = "\u2713";   // ✓
            check.Foreground = new SolidColorBrush(Colors.White);
            check.FontSize = 42;
            check.HorizontalAlignment = HorizontalAlignment.Center;
            check.VerticalAlignment = VerticalAlignment.Center;
            check.FontFamily = new FontFamily("Segoe UI Symbol, Segoe UI");
            check.FontWeight = FontWeights.Bold;
            ok.Children.Add(check);
            col.Children.Add(ok);

            TextBlock h = Ui.H1("安装完成");
            col.Children.Add(h);

            TextBlock tip = Ui.Text("" + Branding.ProductName + " 已成功安装到本机。");
            tip.Margin = new Thickness(0, 6, 0, 14);
            tip.FontSize = 14;
            col.Children.Add(tip);

            TextBlock dim = Ui.Dim(
                "· 桌面与开始菜单已创建快捷方式\n" +
                "· 第一次启动时会弹出协议确认与引导向导\n" +
                "· 删除「" + Branding.ProductName + "」请使用控制面板，不要直接删安装目录");
            dim.TextWrapping = TextWrapping.Wrap;
            col.Children.Add(dim);

            _launchBox = new CheckBox();
            _launchBox.Content = "点击「完成」后立即启动 " + Branding.ProductName;
            _launchBox.Foreground = Theme.Brush(Theme.Text);
            _launchBox.Margin = new Thickness(0, 22, 0, 0);
            _launchBox.IsChecked = true;
            col.Children.Add(_launchBox);

            return col;
        }
    }

    // ══════════════════════════════════════════════════════════════
    // 安装实际动作：发现 payload → 写文件 → 写注册表 → 建快捷方式。
    // 在后台线程跑，通过 Action<int> 回调推 0-100%。
    //
    // 单 EXE 自解压：
    //   Setup.exe 尾部追加完整 payload 二进制 + 12 字节 footer（KLAP magic）。
    //   启动时如果 HasEmbeddedBlob=true，就释放到 %TEMP%\KLA-Payload-{GUID}，
    //   用 SetRuntimePayloadDir 覆盖，后续 FindPayloadDir 直接用临时目录。
    // ══════════════════════════════════════════════════════════════
    internal static class Installer
    {
        public static string LastStatus = "";

        private const uint KLAP_MAGIC = 0x4B4C4150;   // "KLAP"
        private const int  FOOTER_BYTES = 12;         // blobSize(uint32) + indexOff(uint32) + magic(uint32)
        private static string _runtimeOverridePayloadDir;
        public static void SetRuntimePayloadDir(string d) { _runtimeOverridePayloadDir = d; }

        public static string DefaultInstallDir()
        {
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            return Path.Combine(pf, Branding.Company, Branding.ProductName);
        }

        /// <summary>Setup.exe 旁边的 payload 目录，里面放主程序 + image\。</summary>
        public static string FindPayloadDir()
        {
            // 0) 单 EXE 自解压：运行前已释放到临时目录
            if (!string.IsNullOrEmpty(_runtimeOverridePayloadDir) &&
                Directory.Exists(_runtimeOverridePayloadDir) &&
                File.Exists(Path.Combine(_runtimeOverridePayloadDir, "KarlsLightAccess.exe")))
                return _runtimeOverridePayloadDir;

            string me = Assembly.GetExecutingAssembly().Location;
            string myDir = Path.GetDirectoryName(me);

            // 1) 开发期/构建产物：Setup.exe 所在目录下 payload\
            string p = Path.Combine(myDir, "payload");
            if (Directory.Exists(p) && File.Exists(Path.Combine(p, "KarlsLightAccess.exe"))) return p;

            // 2) 构建期：Setup.exe 在 installer/bin/，主程序在 app/bin/
            string try2 = Path.Combine(
                Path.GetFullPath(Path.Combine(myDir, "..", "..", "..", "app", "bin")));
            if (Directory.Exists(try2) && File.Exists(Path.Combine(try2, "KarlsLightAccess.exe")))
                return try2;

            return null;
        }

        /// <summary>payload 目录总字节数（用于安装前提示空间）。找不到返回 0。</summary>
        public static long PayloadBytes()
        {
            try
            {
                string p = FindPayloadDir();
                if (p == null) return 1L << 30;    // 保守按 1 GB 估
                long total = 0;
                foreach (string f in Directory.GetFiles(p, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(f).Length; }
                    catch { }
                }
                return Math.Max(total, 1L << 30);
            }
            catch { return 1L << 30; }
        }

        /// <summary>本次安装是否检测到旧版本（覆盖更新场景）。</summary>
        public static bool UpdateDetected;

        /// <summary>
        /// 检测本机是否已装过 KLA：卸载注册表项带版本号（KARLSLIGHTACCESS_1_0_0），
        /// 所以按前缀遍历；另外兜底看默认安装目录里有没有主程序。
        /// </summary>
        public static bool DetectExistingInstall()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey uninstall =
                    Microsoft.Win32.RegistryKey.OpenBaseKey(
                        Microsoft.Win32.RegistryHive.LocalMachine,
                        Microsoft.Win32.RegistryView.Registry64)
                    .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                {
                    if (uninstall != null)
                    {
                        foreach (string name in uninstall.GetSubKeyNames())
                        {
                            if (name.StartsWith("KARLSLIGHTACCESS_", StringComparison.OrdinalIgnoreCase))
                                return true;
                        }
                    }
                }
            }
            catch { }
            try
            {
                string guess = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "KARL'S LIGHT ACCESS");
                return File.Exists(Path.Combine(guess, "KarlsLightAccess.exe"));
            }
            catch { return false; }
        }

        public static void Run(string installDir, Action<int> progress)
        {
            LastStatus = "查找安装源文件…";
            progress(2);

            // ── 0) 覆盖更新检测：已装过（注册表卸载项在）→ UI 层弹提示。
            //    这里只置标志，弹窗由调用方（进度页开始前）读取展示——安装
            //    本身继续走覆盖（文件覆盖复制 + settings 清理 + 部署时复用分区）。
            UpdateDetected = DetectExistingInstall();

            // ── 0.5) 拆 RunOnce 定时炸弹 ─────────────────────────────
            // 卸载器会在 RunOnce 写「下次登录 rmdir 安装目录」。如果用户
            // 卸载后没重启就重装回同一目录，重启时那个 rmdir 会把刚装好的
            // 程序整目录删光（主程序凭空消失）。安装时立刻删掉这个键和
            // 卸载标记文件，两道闸一起拆。
            try
            {
                using (Microsoft.Win32.RegistryKey hklm =
                    Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine,
                        Microsoft.Win32.RegistryView.Registry64))
                using (Microsoft.Win32.RegistryKey run =
                    hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", true))
                {
                    if (run != null && run.GetValue("KARLSLIGHTACCESS-RM-INSTALL") != null)
                    {
                        run.DeleteValue("KARLSLIGHTACCESS-RM-INSTALL");
                        LastStatus = "已取消待执行的旧卸载清理（防止误删新安装）";
                    }
                }
                string flag = Path.Combine(installDir, "_kla_uninstall.flag");
                if (File.Exists(flag)) File.Delete(flag);
            }
            catch { /* 非管理员或键不存在：忽略 */ }

            string payload = FindPayloadDir();
            if (payload == null)
                throw new DirectoryNotFoundException(
                    "找不到 payload 目录（Setup.exe 旁边应有 payload\\KarlsLightAccess.exe 与 payload\\image\\）。\n" +
                    "请重新下载安装包。");
            progress(5);

            string srcExe   = Path.Combine(payload, "KarlsLightAccess.exe");
            string srcImage = Path.Combine(payload, "image");
            if (!File.Exists(srcExe))   throw new FileNotFoundException("缺少主程序：" + srcExe);
            if (!Directory.Exists(srcImage)) throw new DirectoryNotFoundException("缺少 image/ 目录：" + srcImage);

            // ── 1) 创建安装目录（5% → 10%）──────────────────────
            LastStatus = "创建安装目录 " + installDir + " …";
            Directory.CreateDirectory(installDir);
            progress(10);

            // ── 2) 复制 KarlsLightAccess.exe（10% → 15%）───────
            LastStatus = "复制主程序 KarlsLightAccess.exe …";
            SafeCopyFile(srcExe, Path.Combine(installDir, "KarlsLightAccess.exe"), overwrite: true);
            progress(15);

            // ── 2.5) 部署备份引擎 wimlib → %ProgramData%\KLA\bin ──────
            // Backup.FindTool() 的首选位置（ExpectedToolPath）。没有它，
            // 主程序「备份当前系统」会弹「备份引擎尚未就位」。以前只在
            // 开发机上手工跑 kla\scripts\Install-Wimlib.ps1 部署，装机包
            // 用户永远缺这个 → 装完即坏。这里随安装补上（覆盖更新也重铺）。
            string srcBin = Path.Combine(payload, "bin");
            if (Directory.Exists(srcBin))
            {
                LastStatus = "部署备份引擎 wimlib（%ProgramData%\\KLA\\bin）…";
                string dstBin = Path.Combine(Firmware.DataDir(), "bin");
                Directory.CreateDirectory(dstBin);
                foreach (string f in Directory.GetFiles(srcBin))
                {
                    // 覆盖更新时旧 exe 可能正被一次未收尾的备份占用，重试几轮
                    CopyWithRetry(f, Path.Combine(dstBin, Path.GetFileName(f)), 3);
                }
            }
            else
            {
                // 不拦安装（老 payload 兼容），但记下来便于排查
                LastStatus = "警告：payload 里没有 bin\\wimlib，备份功能将不可用";
            }

            // ── 3) 复制 image/：4 个小文件 15%→18%，image\grub 整树 18%→22%，access.img 22%→90%
            LastStatus = "复制 EFI 引导资产（grub / 壁纸）…";
            string dstImage = Path.Combine(installDir, "image");
            Directory.CreateDirectory(dstImage);
            foreach (string small in new string[] { "grubx64.efi", "grub.cfg", "wallpaper.png", "memtest.efi" })
            {
                string s = Path.Combine(srcImage, small);
                if (File.Exists(s)) SafeCopyFile(s, Path.Combine(dstImage, small), true);
            }
            progress(18);

            // image\grub：GRUB 模块树（x86_64-efi/*.mod、unicode.pf2、splash、theme、
            //   wallpaper、memtest、grub.cfg）。部署 InstallBoot 阶段会整树拷到
            //   ESP 的 \boot\grub\，是 grubx64.efi（$prefix 硬编码=/boot/grub）
            //   真正加载 insmod png/background_image 的目录。缺失则降级 text mode
            //   菜单（壁纸不显示、字体方块），所以这里一定得搬过去。
            LastStatus = "复制 GRUB 模块与字体（约 8 MB，250+ 文件）…";
            string srcGrubTree = Path.Combine(srcImage, "grub");
            if (Directory.Exists(srcGrubTree))
            {
                string dstGrubTree = Path.Combine(dstImage, "grub");
                CopyDirectoryTree(srcGrubTree, dstGrubTree);
            }
            progress(22);

            string imgSrc = Path.Combine(srcImage, "access.img");
            if (File.Exists(imgSrc))
            {
                string imgDst = Path.Combine(dstImage, "access.img");
                LastStatus = "复制救援分区镜像（1 GB），大约 1-5 分钟…";
                CopyStreamWithProgress(imgSrc, imgDst, 20, 90, progress);
            }
            progress(90);

            // ── 4) 写卸载注册表（需要管理员）90% → 95% ──────────
            try
            {
                LastStatus = "写入卸载信息…";
                WriteUninstallRegistry(installDir);
            }
            catch { /* 非管理员运行会拒写，不拦流程；用户手动删也行 */ }
            progress(95);

            // ── 4.5) 清理旧版 settings.json ──────────────────────────
            // 卸载时如果用户点了「否(N)保留备份」，settings.json 会残留
            // licenseAgreed=true / onboardingDone=true，新装主程序启动后
            // 直接进 MainWindow 跳过 LegalWindow + Wizard，用户感觉
            // 「引导页面没了」+「同意协议后没反应」。安装时强制清掉，
            // 让主程序首次启动永远是干净状态：先弹协议，再进部署向导。
            try
            {
                string dataDir = Firmware.DataDir();
                string settings = Path.Combine(dataDir, "settings.json");
                if (File.Exists(settings))
                {
                    File.Delete(settings);
                }
            }
            catch { /* 文件被占用/无权限时跳过，主程序会再判断 LicenseAgreed */ }

            // ── 5) 快捷方式：桌面 + 开始菜单 95% → 99%
            try
            {
                LastStatus = "创建桌面快捷方式…";
                CreateShortcut(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    installDir);
                LastStatus = "创建开始菜单快捷方式…";
                string sm = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
                string programs = Path.Combine(sm, "Programs");
                if (Directory.Exists(programs)) sm = programs;
                Directory.CreateDirectory(sm);
                CreateShortcut(sm, installDir);
            }
            catch { /* 快捷方式写不出来不致命 */ }
            progress(99);

            LastStatus = "完成，正在切换页面…";
            progress(100);
        }

        private static void SafeCopyFile(string src, string dst, bool overwrite)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            File.Copy(src, dst, overwrite);
        }

        /// <summary>
        /// 带重试的覆盖复制：目标被占用（覆盖更新时旧 wimlib 还在跑）先等再试，
        /// 三轮都失败才抛。%ProgramData% 下的引擎文件值得多试几次——
        /// 没它备份功能整个是摆设。
        /// 注意别用 C#6 的 catch...when：这套源码用 .NET 4 自带 csc（C#5）编译。
        /// </summary>
        private static void CopyWithRetry(string src, string dst, int rounds)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            for (int i = 1; i <= rounds; i++)
            {
                try { File.Copy(src, dst, true); return; }
                catch (IOException)
                {
                    if (i >= rounds) throw;
                    System.Threading.Thread.Sleep(500);
                }
            }
        }

        private static void CopyStreamWithProgress(string src, string dst,
            int pStart, int pEnd, Action<int> progress)
        {
            using (FileStream fi = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (FileStream fo = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                long total = fi.Length;
                long sofar = 0;
                byte[] buf = new byte[1024 * 1024]; // 1 MB 块
                int n;
                int lastReport = pStart;
                while ((n = fi.Read(buf, 0, buf.Length)) > 0)
                {
                    fo.Write(buf, 0, n);
                    sofar += n;
                    int cur = pStart + (int)((pEnd - pStart) * sofar / Math.Max(1, total));
                    if (cur > lastReport)
                    {
                        lastReport = cur;
                        progress(cur);
                    }
                }
            }
        }

        /// <summary>递归复制目录树（用于 image\grub\ 等资产目录）。
        /// 源目录不存在时直接返回空，不抛。</summary>
        private static void CopyDirectoryTree(string src, string dst)
        {
            if (!Directory.Exists(src)) return;
            Directory.CreateDirectory(dst);
            foreach (string file in Directory.GetFiles(src))
            {
                string target = Path.Combine(dst, Path.GetFileName(file));
                SafeCopyFile(file, target, true);
            }
            foreach (string sub in Directory.GetDirectories(src))
            {
                string name = Path.GetFileName(sub);
                CopyDirectoryTree(sub, Path.Combine(dst, name));
            }
        }

        private static void WriteUninstallRegistry(string installDir)
        {
            string exe = Path.Combine(installDir, "KarlsLightAccess.exe");
            string keyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\"
                + "KARLSLIGHTACCESS_" + Branding.Version.Replace('.', '_');
            using (Microsoft.Win32.RegistryKey hklm =
                Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine,
                    Microsoft.Win32.RegistryView.Registry64))
            {
                // 卸载键带版本号（KARLSLIGHTACCESS_1_2_0）。版本升级覆盖安装时
                // 会写出新键，旧版本的键若不清掉就会变成孤儿：控制面板出现
                // 两个卸载项，DetectExistingInstall 也永远误报「已安装」。
                // 所以写键之前，把所有不同版本的 KARLSLIGHTACCESS_* 键全删。
                try
                {
                    using (Microsoft.Win32.RegistryKey uninstall =
                        hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", true))
                    {
                        if (uninstall != null)
                        {
                            foreach (string name in uninstall.GetSubKeyNames())
                            {
                                if (name.StartsWith("KARLSLIGHTACCESS_", StringComparison.OrdinalIgnoreCase) &&
                                    name != "KARLSLIGHTACCESS_" + Branding.Version.Replace('.', '_'))
                                {
                                    try { uninstall.DeleteSubKeyTree(name); } catch { }
                                }
                            }
                        }
                    }
                }
                catch { }

            using (Microsoft.Win32.RegistryKey k = hklm.CreateSubKey(keyPath, true))
            {
                k.SetValue("DisplayName",    Branding.ProductName + " " + Branding.Version);
                k.SetValue("Publisher",      Branding.Company);
                k.SetValue("DisplayVersion", Branding.Version);
                k.SetValue("DisplayIcon",    exe + ",0");
                k.SetValue("InstallLocation", installDir);
                try { k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd")); } catch { }
                try
                {
                    long sz = 0;
                    foreach (string f in Directory.GetFiles(installDir, "*", SearchOption.AllDirectories))
                        try { sz += new FileInfo(f).Length; } catch { }
                    k.SetValue("EstimatedSize", (int)(sz / 1024),
                        Microsoft.Win32.RegistryValueKind.DWord);
                }
                catch { }
                // 卸载入口：直接拉起 KarlsLightAccess.exe --uninstall
                // 它会：(1) 询问是否清除所有备份文件/记录
                //       (2) 删除 Rescue 分区并把空间合并回相邻 NTFS 卷
                //       (3) 恢复原始 BootOrder + 清除 KLA BootXXXX
                //       (4) 清理 ESP 上 EFI\KLA\、\boot\grub\、EFI\Boot\bootx64.efi
                //       (5) 清理 ProgramData\KLA 日志/备份文件 + D:\KLA 备份目录
                // 主程序自身卸载完文件、快捷方式后会自动退出。
                k.SetValue("UninstallString", "\"" + exe + "\" --uninstall");
                k.SetValue("QuietUninstallString", "\"" + exe + "\" --uninstall --quiet");
                k.SetValue("URLInfoAbout", Branding.WebsiteUrl);
                k.SetValue("HelpLink",     Branding.WebsiteUrl);
                k.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
            }
            }
        }

        // 用 IShellLink 创建 .lnk 快捷方式（不引 Shell32 Interop，反射 COM 调）
        private static void CreateShortcut(string whereDir, string installDir)
        {
            string exe = Path.Combine(installDir, "KarlsLightAccess.exe");
            string lnkPath = Path.Combine(whereDir, Branding.ProductName + ".lnk");
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return;
            object shell = Activator.CreateInstance(t);
            try
            {
                object link = t.InvokeMember("CreateShortcut",
                    BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                Type lt = link.GetType();
                lt.InvokeMember("TargetPath",
                    BindingFlags.SetProperty, null, link, new object[] { exe });
                lt.InvokeMember("WorkingDirectory",
                    BindingFlags.SetProperty, null, link, new object[] { installDir });
                lt.InvokeMember("Description",
                    BindingFlags.SetProperty, null, link, new object[] { "启动 " + Branding.ProductName });
                lt.InvokeMember("IconLocation",
                    BindingFlags.SetProperty, null, link, new object[] { exe + ",0" });
                lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            }
            finally
            {
                try { Marshal.ReleaseComObject(shell); } catch { }
            }
        }

        // ════════════════════════════════════════════════════════════════
        // 单 EXE：自解压 Payload 尾部嵌入（PE Overlay Blob）
        //   格式：[SETUP.EXE 原始内容] [FILE DATA 0..N-1] [INDEX] [FOOTER(12B)]
        //   FOOTER(12B) = blobPayloadSize(4, uint) | indexOffset(4, uint) | KLAP_MAGIC(4)
        // ════════════════════════════════════════════════════════════════

        public static bool HasEmbeddedBlob(string exePath)
        {
            try
            {
                FileInfo fi = new FileInfo(exePath);
                if (fi.Length < FOOTER_BYTES + 64L) return false;
                using (FileStream fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    fs.Seek(-FOOTER_BYTES, SeekOrigin.End);
                    byte[] raw = new byte[FOOTER_BYTES];
                    int r = 0; while (r < FOOTER_BYTES) { int n = fs.Read(raw, r, FOOTER_BYTES - r); if (n <= 0) break; r += n; }
                    return BitConverter.ToUInt32(raw, 8) == KLAP_MAGIC;
                }
            }
            catch { return false; }
        }

        private static List<string> ScanFiles(string payloadDir)
        {
            List<string> files = new List<string>();
            foreach (string f in Directory.GetFiles(payloadDir, "*", SearchOption.AllDirectories))
            {
                string rel = f.Substring(payloadDir.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                files.Add(rel);
            }
            files.Sort(StringComparer.Ordinal);
            return files;
        }

        /// <summary>构造单 EXE：setupSrcExe(原始) + payloadDir(整目录) → outExe(带末尾嵌入)</summary>
        public static void BuildSingleExe(string setupSrcExe, string payloadDir, string outExe, Action<int> progress)
        {
            if (!File.Exists(setupSrcExe)) throw new FileNotFoundException(setupSrcExe);
            if (!Directory.Exists(payloadDir)) throw new DirectoryNotFoundException(payloadDir);
            List<string> rels = ScanFiles(payloadDir);
            if (rels.Count == 0) throw new Exception("Payload 目录为空：" + payloadDir);

            // 1) 复制 Setup.exe 本体 → outExe（进度 0% → 3%）
            File.Copy(setupSrcExe, outExe, true);
            if (progress != null) progress(3);

            // 读取每个文件信息：绝对路径 / 长度
            List<object[]> info = new List<object[]>();
            long totalData = 0;
            foreach (string rel in rels)
            {
                string full = Path.Combine(payloadDir, rel);
                long len = new FileInfo(full).Length;
                info.Add(new object[] { rel, full, len });
                totalData += len;
            }

            // 2) 以 Append 打开 outExe：写入 FILE DATA 段（3% → 90%）
            long[] offsets = new long[info.Count];
            byte[] buf = new byte[1024 * 256];   // 256KB 块，1GB 文件 ≈ 4K 次循环 / 不占大对象堆
            long written = 0;
            using (FileStream fo = new FileStream(outExe, FileMode.Append, FileAccess.Write, FileShare.None))
            {
                long dataStart = fo.Position;

                for (int i = 0; i < info.Count; i++)
                {
                    offsets[i] = fo.Position - dataStart;    // blob 内偏移
                    string full = (string)info[i][1];
                    using (FileStream fi = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        int n;
                        while ((n = fi.Read(buf, 0, buf.Length)) > 0)
                        {
                            fo.Write(buf, 0, n);
                            written += n;
                            if (progress != null)
                            {
                                int p = 3 + (int)(87L * written / Math.Max(1L, totalData));
                                progress(p);
                            }
                        }
                    }
                }

                // 3) 写 INDEX 段（90% → 98%）
                long indexOff = fo.Position - dataStart;
                if (progress != null) progress(92);
                using (BinaryWriter bw = new BinaryWriter(fo, System.Text.Encoding.UTF8, true))
                {
                    bw.Write((uint)info.Count);
                    for (int i = 0; i < info.Count; i++)
                    {
                        string name = (string)info[i][0];
                        long   len  = (long)info[i][2];
                        byte[] nb = System.Text.Encoding.UTF8.GetBytes(name);
                        bw.Write((ushort)nb.Length);
                        bw.Write(nb, 0, nb.Length);
                        bw.Write((ulong)offsets[i]);
                        bw.Write((ulong)len);
                    }
                    bw.Flush();
                }
                if (progress != null) progress(98);

                // 4) 写 FOOTER(12B) → blobSize / indexOffset / MAGIC
                long blobPayloadSize = fo.Position - dataStart;
                if (blobPayloadSize >= 0xFFFFF000L)
                    throw new Exception("Payload 超过 4294963200 B (≈4GB)，格式不支持。");
                using (BinaryWriter bw = new BinaryWriter(fo, System.Text.Encoding.UTF8, true))
                {
                    bw.Write((uint)blobPayloadSize);
                    bw.Write((uint)indexOff);
                    bw.Write((uint)KLAP_MAGIC);
                    bw.Flush();
                }
            }
            if (progress != null) progress(100);
        }

        /// <summary>运行期自解压：自身 exe 尾部 payload → %TEMP%\KLA-Payload-{guid}\ 返回临时目录</summary>
        public static string ExtractEmbeddedPayload(string exePath, Action<int> progress)
        {
            using (FileStream fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                // 读 FOOTER
                fs.Seek(-FOOTER_BYTES, SeekOrigin.End);
                byte[] footer = new byte[FOOTER_BYTES];
                int rr = 0; while (rr < FOOTER_BYTES) { int n = fs.Read(footer, rr, FOOTER_BYTES - rr); if (n <= 0) break; rr += n; }
                uint blobSize = BitConverter.ToUInt32(footer, 0);
                uint indexOff = BitConverter.ToUInt32(footer, 4);
                uint magic    = BitConverter.ToUInt32(footer, 8);
                if (magic != KLAP_MAGIC) throw new Exception("不是自解压格式（magic mismatch）。");
                long fileLen   = fs.Length;
                long blobStart = fileLen - FOOTER_BYTES - blobSize;

                // 读 INDEX
                fs.Seek(blobStart + indexOff, SeekOrigin.Begin);
                List<object[]> entries = new List<object[]>();
                long totalBytes = 0;
                using (BinaryReader br = new BinaryReader(fs, System.Text.Encoding.UTF8, true))
                {
                    uint num = br.ReadUInt32();
                    for (uint i = 0; i < num; i++)
                    {
                        ushort nl = br.ReadUInt16();
                        byte[] nb = br.ReadBytes(nl);
                        string name = System.Text.Encoding.UTF8.GetString(nb);
                        ulong off = br.ReadUInt64();
                        ulong len = br.ReadUInt64();
                        entries.Add(new object[] { name, (long)off, (long)len });
                        totalBytes += (long)len;
                    }
                }

                // 建临时目录：%TEMP%\KLA-Payload-{guid短}
                string dir = Path.Combine(Path.GetTempPath(),
                    "KLA-Payload-" + Guid.NewGuid().ToString("N").Substring(0, 12));
                Directory.CreateDirectory(dir);

                // 释放每个文件（进度 0→100 按字节数推进）
                long extracted = 0;
                byte[] buf = new byte[1024 * 256];
                for (int i = 0; i < entries.Count; i++)
                {
                    string name = (string)entries[i][0];
                    long off    = (long)entries[i][1];
                    long len    = (long)entries[i][2];
                    string dst  = Path.Combine(dir, name);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    fs.Seek(blobStart + off, SeekOrigin.Begin);
                    long left = len;
                    using (FileStream fo = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        while (left > 0)
                        {
                            int want = (int)Math.Min(buf.Length, left);
                            int n = fs.Read(buf, 0, want);
                            if (n <= 0) break;
                            fo.Write(buf, 0, n);
                            left -= n;
                            extracted += n;
                            if (progress != null)
                                progress((int)(100L * extracted / Math.Max(1L, totalBytes)));
                        }
                    }
                }
                return dir;
            }
        }

        /// <summary>自测 4/4+1 新增：构造小 payload → BuildSingleExe → HasEmbeddedBlob → ExtractEmbeddedPayload → 字节级对比</summary>
        public static void SelfTestRoundTrip()
        {
            string tag = Guid.NewGuid().ToString("N").Substring(0, 10);
            string a = Path.Combine(Path.GetTempPath(), "KLA-RT-A-" + tag);
            string fakeSetup = Path.Combine(Path.GetTempPath(), "KLA-fake-" + tag + ".exe");
            string combined  = Path.Combine(Path.GetTempPath(), "KLA-combined-" + tag + ".exe");
            string extractedDir = null;
            try
            {
                Directory.CreateDirectory(a);
                File.WriteAllText(Path.Combine(a, "KarlsLightAccess.exe"), "DUMMY-EXE-CONTENT-" + tag);
                Directory.CreateDirectory(Path.Combine(a, "image"));
                File.WriteAllText(Path.Combine(a, "image", "grub.cfg"),
                    "# test config for roundtrip " + tag);
                byte[] big = new byte[256 * 1024 + 13]; new Random(12345).NextBytes(big);
                File.WriteAllBytes(Path.Combine(a, "image", "access.img"), big);

                byte[] fakeHeader = new byte[5000 + (tag.Length % 7)];
                new Random(tag.GetHashCode()).NextBytes(fakeHeader);
                File.WriteAllBytes(fakeSetup, fakeHeader);

                BuildSingleExe(fakeSetup, a, combined, null);

                if (!HasEmbeddedBlob(combined))
                    throw new Exception("HasEmbeddedBlob=false after BuildSingleExe。");

                extractedDir = ExtractEmbeddedPayload(combined, null);

                // 逐文件对比（A → extractedDir）
                foreach (string orig in Directory.GetFiles(a, "*", SearchOption.AllDirectories))
                {
                    string rel = orig.Substring(a.Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    string yPath = Path.Combine(extractedDir, rel);
                    if (!File.Exists(yPath)) throw new Exception("释放后缺少：" + rel);
                    byte[] x = File.ReadAllBytes(orig);
                    byte[] y = File.ReadAllBytes(yPath);
                    if (x.Length != y.Length) throw new Exception("长度不符：" + rel + " (" + x.Length + " vs " + y.Length + ")");
                    for (int i = 0; i < x.Length; i++)
                        if (x[i] != y[i]) throw new Exception("字节不一致 @ " + rel + " 偏移 " + i);
                }
            }
            finally
            {
                try { Directory.Delete(a, true); } catch { }
                try { File.Delete(fakeSetup); } catch { }
                try { File.Delete(combined); } catch { }
                try { if (!string.IsNullOrEmpty(extractedDir)) Directory.Delete(extractedDir, true); } catch { }
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // 单 EXE 自解压进度弹窗（出现真正的 SetupWindow 5 页向导之前）
    // ════════════════════════════════════════════════════════════════════
    internal class PreparingWindow : DarkWindow
    {
        private readonly ProgressBar _pb;
        private readonly TextBlock _st;

        public PreparingWindow() : base(Branding.ProductName + " 正在准备安装", 520, 220)
        {
            MinWidth = 480; MinHeight = 200;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;

            Grid g = new Grid();
            g.Margin = new Thickness(34, 26, 34, 26);
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            TextBlock h = Ui.H1("正在准备安装文件");
            h.Margin = new Thickness(0, 0, 0, 18);
            Grid.SetRow(h, 0); g.Children.Add(h);

            _pb = new ProgressBar();
            _pb.Height = 16;
            _pb.Minimum = 0; _pb.Maximum = 100; _pb.Value = 0;
            _pb.Foreground = Theme.Brush(Theme.RedLite);
            _pb.Background = Theme.Brush(Theme.Surface2);
            _pb.BorderBrush = Theme.Brush(Theme.Border);
            Grid.SetRow(_pb, 1); g.Children.Add(_pb);

            _st = Ui.Dim("请稍候，正在释放安装文件到临时目录…");
            _st.Margin = new Thickness(0, 14, 0, 0);
            Grid.SetRow(_st, 2); g.Children.Add(_st);

            Host.Content = g;
        }

        public void Update(int pct, string status)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke((Action)delegate { Update(pct, status); });
                return;
            }
            try { _pb.Value = Math.Max(0, Math.Min(100, pct)); } catch { }
            if (!string.IsNullOrEmpty(status))
                try { _st.Text = status; } catch { }
        }
    }
}
