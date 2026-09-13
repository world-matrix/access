// 主窗口。左侧导航 + 右侧内容，照 IBM Access Predesktop Area 的分栏结构。
//
// 六个页面就是用户列的六项：备份当前系统、管理备份记录、关于软件、
// 官网、版权声明、免责条款。后三项内容短，但单独成页而不是塞进「关于」的
// 折叠区——法律文本被折叠起来等于没写。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace KarlsLight.Access
{
    internal class MainWindow : DarkWindow
    {
        private class NavItem
        {
            public string Label;
            public Func<UIElement> Build;
            public Border Chrome;
            public TextBlock Caption;   // 直接存着，不从 Chrome.Child 强转回来
        }

        private readonly List<NavItem> _nav = new List<NavItem>();
        private readonly StackPanel _navPanel = new StackPanel();
        private readonly ContentControl _body = new ContentControl();
        private int _current = -1;

        public MainWindow()
            : base(Branding.ProductName, 1000, 700)
        {
            SetCaption(Branding.ProductName);

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(212) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Border side = new Border();
            side.Background = Theme.Brush(Theme.NavyDark);
            side.BorderBrush = Theme.Brush(Theme.Border);
            side.BorderThickness = new Thickness(0, 0, 1, 0);

            Grid sideGrid = new Grid();
            sideGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            sideGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            sideGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            sideGrid.Children.Add(BuildBrandBlock());

            _navPanel.Margin = new Thickness(0, 8, 0, 0);
            Grid.SetRow(_navPanel, 1);
            sideGrid.Children.Add(_navPanel);

            // 左下栏：版本号是个可点的"关于"按钮。
            // 鼠标悬停变手型、背景亮一格——用户一眼就看得出来"这东西能点"。
            Border aboutBtn = new Border();
            aboutBtn.Margin = new Thickness(0, 0, 0, 10);
            aboutBtn.Padding = new Thickness(18, 8, 18, 6);
            aboutBtn.Cursor = System.Windows.Input.Cursors.Hand;
            aboutBtn.Background = Brushes.Transparent;
            aboutBtn.MouseEnter += delegate { aboutBtn.Background = Theme.Brush(Theme.Navy); };
            aboutBtn.MouseLeave += delegate { aboutBtn.Background = Brushes.Transparent; };
            aboutBtn.MouseLeftButtonUp += delegate
            {
                try
                {
                    AboutWindow aw = new AboutWindow();
                    aw.Owner = this;
                    aw.ShowDialog();
                }
                catch (Exception ex) { Program.LogError(ex); }
            };

            StackPanel aboutStack = new StackPanel();
            TextBlock ver = Ui.Dim("版本 " + Branding.Version);
            ver.FontSize = 11;
            aboutStack.Children.Add(ver);
            TextBlock hint = Ui.Dim("点击查看关于信息");
            hint.FontSize = 10;
            hint.Opacity = 0.6;
            hint.Margin = new Thickness(0, 2, 0, 0);
            aboutStack.Children.Add(hint);
            aboutBtn.Child = aboutStack;
            Grid.SetRow(aboutBtn, 2);
            sideGrid.Children.Add(aboutBtn);

            side.Child = sideGrid;
            Grid.SetColumn(side, 0);
            g.Children.Add(side);

            ScrollViewer sv = new ScrollViewer();
            sv.Content = _body;
            sv.Padding = new Thickness(40, 34, 40, 34);
            Grid.SetColumn(sv, 1);
            g.Children.Add(sv);

            Host.Content = g;

            Add("备份当前系统", PageBackup);
            Add("管理备份记录", PageManage);
            Add("关于软件", PageAbout);
            Add("访问官网", PageWebsite);
            // （说明）关于本程序、安装向导、首次启动协议三处都已经展示完整的
            // 版权声明与免责条款/EULA/隐私政策。侧边栏就不再重复挂两份了，
            // 避免用户一眼扫过去觉得"又是协议"。相应 PageCopyright/PageDisclaimer
            // 方法保留着但不注册到导航，其它代码也没引用。

            Select(0);
        }

        // 侧边栏顶上的品牌块。
        //
        // 原来这里放的是缩到 34px 高的 logo.png，效果不能用：那张图是细线星芒
        // 加点阵字标，缩到 34px 之后星芒是一层灰、字标是一排糊点。
        // 改成矢量星芒 + 排版出来的文字，任何缩放倍率下都是清楚的。
        // 完整锁定图形仍然用在欢迎页（Wizard，96px 高），那个尺寸它是站得住的。
        private Border BuildBrandBlock()
        {
            StackPanel words = new StackPanel();
            words.VerticalAlignment = VerticalAlignment.Center;

            TextBlock name = Ui.Text("KARL'S LIGHT");
            name.FontSize = 13;
            words.Children.Add(name);

            // 字间空格是故意的：美术源文件里 ACCESS 字标就是拉开字距的，
            // WPF 的 TextBlock 没有 letter-spacing，插空格是最省事也最稳的做法。
            TextBlock sub = Ui.Dim("A C C E S S");
            sub.FontSize = 11;
            sub.Margin = new Thickness(0, 2, 0, 0);
            words.Children.Add(sub);

            StackPanel s = new StackPanel();
            s.Orientation = Orientation.Horizontal;
            s.Margin = new Thickness(18, 20, 18, 18);

            FrameworkElement star = Ui.Starburst(30, Theme.Brush(Theme.Text));
            star.VerticalAlignment = VerticalAlignment.Center;
            star.Margin = new Thickness(0, 0, 11, 0);
            s.Children.Add(star);
            s.Children.Add(words);

            Border b = new Border();
            b.BorderBrush = Theme.Brush(Theme.Border);
            b.BorderThickness = new Thickness(0, 0, 0, 1);
            b.Child = s;
            return b;
        }

        // 导航是纯文字的。本来打算用 Segoe MDL2 的图标字体，放弃了：
        // 那些码位在 Unicode 私有使用区，源文件被任何一个编辑器按 ANSI 存过一次
        // 就全成问号，而且这种损坏编译不报错，要等界面画出来才看得见。
        // 选中态靠左侧那条品牌红的竖线表达，六项也不多，认字比认图标快。
        private void Add(string label, Func<UIElement> build)
        {
            NavItem it = new NavItem();
            it.Label = label;
            it.Build = build;

            TextBlock tx = new TextBlock();
            tx.Text = label;
            tx.FontFamily = Theme.UiFont;
            tx.FontSize = 13;
            tx.Foreground = Theme.Brush(Theme.TextDim);
            tx.VerticalAlignment = VerticalAlignment.Center;

            Border chrome = new Border();
            chrome.Padding = new Thickness(15, 11, 12, 11);
            chrome.BorderThickness = new Thickness(3, 0, 0, 0);
            chrome.BorderBrush = Brushes.Transparent;
            chrome.Background = Brushes.Transparent;
            chrome.Cursor = System.Windows.Input.Cursors.Hand;
            chrome.Child = tx;

            int index = _nav.Count;
            chrome.MouseLeftButtonUp += delegate { Select(index); };
            chrome.MouseEnter += delegate { if (_current != index) chrome.Background = Theme.Brush(Theme.Navy); };
            chrome.MouseLeave += delegate { if (_current != index) chrome.Background = Brushes.Transparent; };

            it.Chrome = chrome;
            it.Caption = tx;
            _nav.Add(it);
            _navPanel.Children.Add(chrome);
        }

        private void Select(int index)
        {
            if (index < 0 || index >= _nav.Count) return;
            _current = index;

            for (int i = 0; i < _nav.Count; i++)
            {
                bool on = (i == index);
                NavItem it = _nav[i];
                it.Chrome.Background = on ? Theme.Brush(Theme.Navy) : Brushes.Transparent;
                it.Chrome.BorderBrush = on ? Theme.Brush(Theme.Red) : Brushes.Transparent;
                it.Caption.Foreground = on ? Theme.Brush(Theme.Text) : Theme.Brush(Theme.TextDim);
            }

            try
            {
                _body.Content = _nav[index].Build();
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
                _body.Content = Callout("这个页面打不开：" + ex.Message);
            }
        }

        /// <summary>--shots 用：切到第 index 个页面。</summary>
        public void SelectForTest(int index) { Select(index); }
        public int NavCountForTest { get { return _nav.Count; } }
        public string NavLabelForTest(int i) { return _nav[i].Label; }

        // ── 备份当前系统 ─────────────────────────────────────────────
        private RadioButton _rL2, _rL1, _rL3, _rL4;
        private CheckBox _asBaseline;
        private TextBox _dest;
        private TextBlock _space;

        private UIElement PageBackup()
        {
            StackPanel col = new StackPanel();
            col.Children.Add(Ui.H1("备份当前系统"));
            col.Children.Add(Lead(
                "把现在这个能正常工作的系统保存下来。备份在系统运行中进行，" +
                "不需要重启，你可以继续用电脑。"));

            if (!Deployment.IsInstalled())
                col.Children.Add(Callout(
                    Branding.ShortName + " 还没有部署到这台电脑上。现在仍然可以备份，" +
                    "但还原系统需要从 " + Branding.ShortName + " 启动才能进行——" +
                    "正在运行的 Windows 没法覆盖它自己。建议先完成部署。"));

            // ── 范围 ──
            StackPanel scope = new StackPanel();
            scope.Children.Add(Ui.H2("备份哪些内容"));

            _rL2 = Radio("系统与程序",
                "Windows、驱动、已安装的软件和设置。不含桌面、文档、图片这些个人文件。" +
                "适合个人文件本来就放在别的盘上的情况——体积小得多，也是最常用的一档。", true);
            _rL1 = Radio("完整系统",
                "上面这些，加上所有个人文件。整机灾难恢复用，还原后和备份那一刻完全一样。" +
                "体积最大。", false);
            _rL3 = Radio("仅个人文件",
                "只备份桌面、文档、图片、视频、下载。不含系统，" +
                "所以不能用来修复起不来的 Windows，但可以随时取回误删的文件。", false);
            _rL4 = Radio("纯系统（不含软件）",
                "只备份 Windows 本体、全部驱动和系统服务设置，不含任何应用软件和个人文件。" +
                "系统被搞坏但不想重装、又嫌「系统与程序」体积大时用这档——" +
                "还原后系统、驱动、服务都能正常起来，软件需要重装。", false);

            scope.Children.Add(_rL2);
            scope.Children.Add(_rL1);
            scope.Children.Add(_rL3);
            scope.Children.Add(_rL4);

            TextBlock ex = Ui.Dim(
                "所有档位都会跳过页面文件、休眠文件、回收站和系统临时目录。" +
                "这些还原后会自动重建，备份它们只是白占空间。");
            ex.Margin = new Thickness(0, 14, 0, 0);
            scope.Children.Add(ex);

            Border scopeCard = Card620(scope);
            scopeCard.Margin = new Thickness(0, 24, 0, 0);
            col.Children.Add(scopeCard);

            // ── 位置 ──
            StackPanel dst = new StackPanel();
            dst.Children.Add(Ui.H2("存放位置"));
            TextBlock dstHint = Ui.Dim(
                "必须放在系统盘以外的磁盘上——恢复出厂会把系统盘整个覆盖，" +
                "备份放在那里会跟着一起没。最好是另一块物理硬盘：" +
                "同一块盘上的备份挡不住硬盘本身故障。");
            dstHint.Margin = new Thickness(0, 8, 0, 0);
            dst.Children.Add(dstHint);

            string def = Backup.DefaultStoreRoot();
            _dest = new TextBox();
            _dest.Text = (def == null) ? "" : def;
            _dest.Margin = new Thickness(0, 12, 0, 0);
            _dest.Padding = new Thickness(10, 7, 10, 7);
            _dest.Background = Theme.Brush(Theme.Bg);
            _dest.Foreground = Theme.Brush(Theme.Text);
            _dest.BorderBrush = Theme.Brush(Theme.Border);
            _dest.CaretBrush = Theme.Brush(Theme.Text);
            _dest.FontFamily = Theme.UiFont;
            _dest.FontSize = 12;
            _dest.TextChanged += delegate { RefreshSpace(); };
            dst.Children.Add(_dest);

            _space = Ui.Dim("");
            _space.Margin = new Thickness(0, 10, 0, 0);
            dst.Children.Add(_space);

            if (def == null)
                dst.Children.Add(Inline(
                    "这台电脑上只找到一个 NTFS 卷（系统盘）。请接一块移动硬盘，" +
                    "或在部署时让程序划分一个数据分区。"));

            Border dstCard = Card620(dst);
            dstCard.Margin = new Thickness(0, 18, 0, 0);
            col.Children.Add(dstCard);

            // ── 出厂基准 ──
            _asBaseline = new CheckBox();
            _asBaseline.Content = "把这次备份设为「出厂基准」";
            _asBaseline.Margin = new Thickness(0, 20, 0, 0);
            _asBaseline.MaxWidth = 640;
            _asBaseline.HorizontalAlignment = HorizontalAlignment.Left;
            col.Children.Add(_asBaseline);

            TextBlock bh = Ui.Dim(
                "出厂基准是「恢复出厂」按钮的目标。全局只能有一个，" +
                "勾选会取代现有的那个。系统刚装好、驱动和常用软件都装齐、" +
                "还没堆积垃圾的时候，最适合设为基准。");
            bh.Margin = new Thickness(26, 6, 0, 0);
            bh.MaxWidth = 614;
            bh.HorizontalAlignment = HorizontalAlignment.Left;
            col.Children.Add(bh);

            Button go = Ui.Primary("开始备份", delegate { StartBackup(); });
            go.Margin = new Thickness(0, 24, 0, 0);
            go.HorizontalAlignment = HorizontalAlignment.Left;
            col.Children.Add(go);

            RefreshSpace();
            return col;
        }

        private RadioButton Radio(string title, string body, bool on)
        {
            StackPanel s = new StackPanel();
            TextBlock t = Ui.Text(title);
            t.FontSize = 13;
            s.Children.Add(t);
            TextBlock d = Ui.Dim(body);
            d.Margin = new Thickness(0, 4, 0, 0);
            d.MaxWidth = 540;
            s.Children.Add(d);

            RadioButton r = new RadioButton();
            r.Content = s;
            r.GroupName = "scope";
            r.IsChecked = on;
            r.Margin = new Thickness(0, 14, 0, 0);
            return r;
        }

        private void RefreshSpace()
        {
            if (_space == null) return;
            try
            {
                string root = Path.GetPathRoot(_dest.Text);
                if (string.IsNullOrEmpty(root)) { _space.Text = ""; return; }
                DriveInfo d = new DriveInfo(root);
                if (!d.IsReady) { _space.Text = root + " 当前不可用。"; return; }
                _space.Text = root + " 剩余 " + Storage.Fmt((ulong)d.AvailableFreeSpace) +
                              " / 共 " + Storage.Fmt((ulong)d.TotalSize);
            }
            catch { _space.Text = ""; }
        }

        private void StartBackup()
        {
            if (string.IsNullOrEmpty(_dest.Text))
            {
                MessageBox.Show("请先指定备份存放位置。", Branding.ShortName,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 放系统盘上等于没备份——恢复出厂会把它一起覆盖掉。
            try
            {
                string sysRoot = Path.GetPathRoot(Environment.GetFolderPath(
                    Environment.SpecialFolder.Windows));
                if (string.Equals(Path.GetPathRoot(_dest.Text), sysRoot,
                        StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(
                        "不能把备份放在系统盘（" + sysRoot + "）上。\n\n" +
                        "还原系统时这个盘会被整个覆盖，放在这里的备份会连同问题一起消失。",
                        Branding.ShortName, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
            catch { }

            string tool = Backup.FindTool();
            if (tool == null)
            {
                MessageBox.Show(
                    "备份引擎尚未就位，无法开始备份。\n\n" +
                    "程序不会退而求其次用别的工具凑合——格式不对的备份在 " +
                    Branding.ShortName + " 里读不出来，而那正是你需要它的时候。\n\n" +
                    "期望位置：\n" + Backup.ExpectedToolPath(),
                    Branding.ShortName, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            BackupOptions o = new BackupOptions();
            o.StoreRoot = _dest.Text;
            o.SetAsFactoryBaseline = (_asBaseline.IsChecked == true);
            if (_rL1.IsChecked == true) o.Scope = BackupScope.L1;
            else if (_rL3.IsChecked == true) o.Scope = BackupScope.L3;
            else if (_rL4.IsChecked == true) o.Scope = BackupScope.L4;
            else o.Scope = BackupScope.L2;

            try
            {
                Backup.Start(o, this);
                MessageBox.Show("备份完成。", Branding.ShortName,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Select(1);
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
                MessageBox.Show("备份失败：\n\n" + ex.Message,
                    Branding.ShortName, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── 管理备份记录 ─────────────────────────────────────────────
        private UIElement PageManage()
        {
            StackPanel col = new StackPanel();
            col.Children.Add(Ui.H1("管理备份记录"));
            col.Children.Add(Lead(
                "每条链的第一个是完整备份，后面的只记录相对上一次的变化，" +
                "所以又快又省空间。"));

            string store = Backup.DefaultStoreRoot();
            List<BackupChain> chains;
            try
            {
                chains = (store == null)
                    ? new List<BackupChain>()
                    : Backup.LoadChains(store);
            }
            catch (Exception ex)
            {
                col.Children.Add(Callout("读取备份索引失败：" + ex.Message));
                return col;
            }

            int total = 0;
            foreach (BackupChain c in chains) total += c.Entries.Count;

            if (total == 0)
            {
                StackPanel empty = new StackPanel();
                empty.Children.Add(Ui.H2("还没有备份"));
                TextBlock t = Ui.Dim(
                    "到「备份当前系统」做第一次备份。\n\n" +
                    "如果系统刚装好不久，现在正是做基准备份的好时候——" +
                    "越干净的系统，越值得留下来。");
                t.Margin = new Thickness(0, 10, 0, 0);
                empty.Children.Add(t);
                Border c2 = Card620(empty);
                c2.Margin = new Thickness(0, 24, 0, 0);
                col.Children.Add(c2);
                return col;
            }

            foreach (BackupChain ch in chains)
            {
                TextBlock head = Ui.H2(ch.ScopeLabel + "  ·  " + ch.Entries.Count + " 个还原点");
                head.Margin = new Thickness(0, 26, 0, 0);
                col.Children.Add(head);

                TextBlock sub = Ui.Dim("链 " + ch.ChainId + "，越靠下越新，每一条都依赖它上面的全部。");
                sub.Margin = new Thickness(0, 6, 0, 10);
                col.Children.Add(sub);

                for (int i = 0; i < ch.Entries.Count; i++)
                    col.Children.Add(EntryRow(ch, i));
            }

            col.Children.Add(Callout(
                "增量备份首尾相连。删除中间的某一个，排在它后面的还原点都会失效——" +
                "程序会在删除前明确告诉你会连带影响哪几个。\n\n" +
                "要整体清理，删除某条链的第一个（完整备份）即可连带删除整条链，" +
                "这不会影响其他链。"));
            return col;
        }

        private Border EntryRow(BackupChain ch, int index)
        {
            BackupEntry e = ch.Entries[index];

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel left = new StackPanel();

            TextBlock title = Ui.Text(e.LocalTime + "  ·  " + (e.IsFull ? "完整备份" : "增量备份"));
            title.FontSize = 14;
            left.Children.Add(title);

            string sub = Storage.Fmt((ulong)Math.Max(0, e.SizeBytes));
            if (!string.IsNullOrEmpty(e.OsCaption)) sub += "  ·  " + e.OsCaption;
            TextBlock s = Ui.Dim(sub);
            s.Margin = new Thickness(0, 4, 0, 0);
            left.Children.Add(s);

            if (e.IsFactoryBaseline)
            {
                TextBlock b = Ui.Text("出厂基准");
                b.FontSize = 11;
                b.Foreground = Theme.Brush(Theme.RedLite);
                b.Margin = new Thickness(0, 6, 0, 0);
                left.Children.Add(b);
            }

            Grid.SetColumn(left, 0);
            g.Children.Add(left);

            StackPanel btns = new StackPanel();
            btns.Orientation = Orientation.Horizontal;
            btns.VerticalAlignment = VerticalAlignment.Center;

            if (e.Scope != "L3")
            {
                Button restore = Ui.Btn("还原", delegate { ConfirmRestore(e); });
                btns.Children.Add(restore);

                if (!e.IsFactoryBaseline)
                {
                    Button mark = Ui.Btn("设为基准", delegate { SetBaseline(e); });
                    mark.Margin = new Thickness(8, 0, 0, 0);
                    btns.Children.Add(mark);
                }
            }

            Button del = Ui.Btn("删除", delegate { ConfirmDelete(e); });
            del.Margin = new Thickness(8, 0, 0, 0);
            btns.Children.Add(del);

            Grid.SetColumn(btns, 1);
            g.Children.Add(btns);

            Border card = Ui.Card(g);
            card.Margin = new Thickness(0, 0, 0, 10);
            card.MaxWidth = 640;
            card.HorizontalAlignment = HorizontalAlignment.Left;
            card.Padding = new Thickness(16, 14, 16, 14);
            if (e.IsFactoryBaseline) card.BorderBrush = Theme.Brush(Theme.Red);
            return card;
        }

        private void SetBaseline(BackupEntry e)
        {
            try
            {
                Backup.SetFactoryBaseline(e);
                Select(1);
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
                MessageBox.Show("设置失败：" + ex.Message, Branding.ShortName,
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ConfirmRestore(BackupEntry e)
        {
            if (!Deployment.IsInstalled())
            {
                MessageBox.Show(
                    Branding.ShortName + " 还没有部署到这台电脑上，无法进行系统还原。\n\n" +
                    "正在运行的 Windows 没法覆盖它自己，还原必须从 " +
                    Branding.ShortName + " 启动后进行。",
                    Branding.ShortName, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            MessageBoxResult r = MessageBox.Show(
                "将把系统还原到 " + e.LocalTime + " 这个还原点。\n\n" +
                "点击确定后电脑会重启进入 " + Branding.ShortName + "，" +
                "在那里再次确认后才会真正开始还原。\n\n" +
                "还原会覆盖系统盘的全部内容——这个还原点之后新增或修改的文件都会丢失。\n\n" +
                "另请注意：还原功能目前仍在验证阶段。「还原完的 Windows 能不能正常开机」" +
                "尚未在真实机器上走完整套流程，请确保重要数据另有一份不依赖本软件的备份。\n\n" +
                "请先保存所有正在编辑的文件。确定继续吗？",
                Branding.ProductName, MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (r != MessageBoxResult.OK) return;

            try
            {
                Backup.RequestRestore(e);
                MessageBox.Show(
                    "已安排下次开机进入 " + Branding.ShortName + "。\n\n" +
                    "现在可以手动重启电脑。还原会在那边再问你一次，" +
                    "在确认之前随时可以退出。",
                    Branding.ShortName, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
                Backup.CancelRestore(e.StoreRoot);
                MessageBox.Show("安排还原失败：\n\n" + ex.Message,
                    Branding.ShortName, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ConfirmDelete(BackupEntry e)
        {
            int affected;
            try { affected = Backup.CountDependents(e.StoreRoot, e); }
            catch (Exception ex)
            {
                MessageBox.Show("读取索引失败：" + ex.Message, Branding.ShortName,
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string msg = "确定删除 " + e.LocalTime + " 这个还原点吗？";
            if (affected > 0)
                msg += "\n\n它后面还有 " + affected + " 个增量备份依赖它，" +
                       "会被一并删除——增量只记录变化，失去起点之后它们无法独立还原。";
            if (e.IsFactoryBaseline)
                msg += "\n\n这是当前的出厂基准。删除后「恢复出厂」将没有目标，" +
                       "需要重新指定一个。";
            msg += "\n\n此操作不可撤销。";

            if (MessageBox.Show(msg, Branding.ShortName,
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                return;

            try
            {
                Backup.Delete(e);
                Select(1);
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
                Select(1);
                MessageBox.Show("删除时出了问题：\n\n" + ex.Message,
                    Branding.ShortName, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── 关于软件 ─────────────────────────────────────────────────
        private UIElement PageAbout()
        {
            StackPanel col = new StackPanel();
            col.Children.Add(Ui.H1("关于 " + Branding.ProductName));

            // 产品概要卡片：版本号 + 版权两行 + 三链接（严格按用户指定）
            StackPanel head = new StackPanel();
            head.Children.Add(Ui.Text(Branding.ProductName));
            TextBlock pv = Ui.Text("版本 " + Branding.Version);
            pv.FontSize = 20;
            pv.FontWeight = FontWeights.Light;
            pv.Foreground = Theme.Brush(Theme.RedLite);
            pv.Margin = new Thickness(0, 6, 0, 14);
            head.Children.Add(pv);

            TextBlock c1 = Ui.Text("© KARL'S LIGHT CO., LTD. All Rights Reserved.");
            c1.FontSize = 14;
            head.Children.Add(c1);
            TextBlock c2 = Ui.Dim("Designed by KARL'S LIGHT in Guiyang");
            c2.FontSize = 13;
            c2.Margin = new Thickness(0, 3, 0, 0);
            head.Children.Add(c2);

            Grid g1 = new Grid();
            g1.Margin = new Thickness(0, 16, 0, 0);
            g1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            g1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            TextBlock l1 = Ui.Dim("官方网站"); Grid.SetColumn(l1, 0); g1.Children.Add(l1);
            TextBlock v1 = Ui.Link(Branding.Website, Branding.WebsiteUrl);
            Grid.SetColumn(v1, 1); g1.Children.Add(v1);
            head.Children.Add(g1);

            Grid g2 = new Grid();
            g2.Margin = new Thickness(0, 6, 0, 0);
            g2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            g2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            TextBlock l2 = Ui.Dim("许可协议"); Grid.SetColumn(l2, 0); g2.Children.Add(l2);
            TextBlock v2 = MakeDocLink("查看《软件最终用户许可协议》全文",
                "最终用户许可协议", Legal.EulaText());
            Grid.SetColumn(v2, 1); g2.Children.Add(v2);
            head.Children.Add(g2);

            Grid g3 = new Grid();
            g3.Margin = new Thickness(0, 6, 0, 0);
            g3.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            g3.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            TextBlock l3 = Ui.Dim("隐私政策"); Grid.SetColumn(l3, 0); g3.Children.Add(l3);
            TextBlock v3 = MakeDocLink("查看《软件隐私政策》全文",
                "隐私政策", Legal.PrivacyText());
            Grid.SetColumn(v3, 1); g3.Children.Add(v3);
            head.Children.Add(g3);

            Border headCard = Card620(head);
            headCard.Margin = new Thickness(0, 20, 0, 0);
            col.Children.Add(headCard);

            StackPanel s = new StackPanel();
            s.Children.Add(Ui.H2("这是什么"));
            s.Children.Add(Ui.Dim(Branding.AboutBlurb));
            Border blurb = Card620(s);
            blurb.Margin = new Thickness(0, 18, 0, 0);
            col.Children.Add(blurb);

            StackPanel info = new StackPanel();
            info.Children.Add(Ui.H2("部署信息"));
            info.Children.Add(Kv("产品名称", Branding.ProductName));
            info.Children.Add(Kv("版本", Branding.Version));
            info.Children.Add(Kv("开发", Branding.Company));
            info.Children.Add(Kv("官方网站", Branding.Website));

            JsonValue rec = Deployment.ReadRecord();
            if (rec != null)
            {
                info.Children.Add(Kv("部署时间", LocalOf(rec["deployedAt"].AsString(""))));
                info.Children.Add(Kv("救援分区",
                    "磁盘 " + rec["rescuePartition"]["diskNumber"].AsLong(0) +
                    " 分区 " + rec["rescuePartition"]["partitionNumber"].AsLong(0) +
                    "（" + Storage.Fmt((ulong)Math.Max(0,
                        rec["rescuePartition"]["size"].AsLong(0))) + "）"));
                info.Children.Add(Kv("启动项", rec["boot"]["variable"].AsString("—")));
            }
            else
            {
                info.Children.Add(Kv("部署状态", "尚未部署到磁盘"));
            }

            Border infoCard = Card620(info);
            infoCard.Margin = new Thickness(0, 18, 0, 0);
            col.Children.Add(infoCard);

            StackPanel diag = new StackPanel();
            diag.Children.Add(Ui.H2("诊断"));
            TextBlock dt = Ui.Dim(
                "程序运行日志保存在下面这个位置。反馈问题时把它一起带上，" +
                "能省掉大量来回确认。");
            dt.Margin = new Thickness(0, 8, 0, 12);
            diag.Children.Add(dt);

            TextBlock path = Ui.Dim(Firmware.DataDir());
            path.FontFamily = new FontFamily("Consolas, Courier New");
            path.FontSize = 12;
            diag.Children.Add(path);

            Button open = Ui.Btn("打开日志目录", delegate
            {
                try
                {
                    Directory.CreateDirectory(Firmware.DataDir());
                    System.Diagnostics.Process.Start("explorer.exe", Firmware.DataDir());
                }
                catch (Exception ex)
                {
                    MessageBox.Show("打不开：" + ex.Message, Branding.ShortName);
                }
            });
            open.Margin = new Thickness(0, 14, 0, 0);
            open.HorizontalAlignment = HorizontalAlignment.Left;
            diag.Children.Add(open);

            Border diagCard = Card620(diag);
            diagCard.Margin = new Thickness(0, 18, 0, 0);
            col.Children.Add(diagCard);

            Button rerun = Ui.Btn("重新查看初次设置向导", delegate
            {
                WizardWindow w = new WizardWindow();
                Application.Current.MainWindow = w;
                w.Show();
                Close();
            });
            rerun.Margin = new Thickness(0, 18, 0, 0);
            rerun.HorizontalAlignment = HorizontalAlignment.Left;
            col.Children.Add(rerun);

            return col;
        }

        // 许可协议/隐私链接：红色超链接，点击弹出只读 DocViewer。
        private TextBlock MakeDocLink(string label, string title, string md)
        {
            Hyperlink h = new Hyperlink(new Run(label));
            h.Foreground = Theme.Brush(Theme.RedLite);
            h.TextDecorations = null;
            h.Cursor = System.Windows.Input.Cursors.Hand;
            h.Click += delegate
            {
                try { DocViewerWindow.Show(null, title, md); }
                catch (Exception ex) { Program.LogError(ex); }
            };
            h.MouseEnter += delegate { h.TextDecorations = TextDecorations.Underline; };
            h.MouseLeave += delegate { h.TextDecorations = null; };
            TextBlock t = new TextBlock(h);
            t.FontFamily = Theme.UiFont;
            t.FontSize = 13;
            return t;
        }

        private static string LocalOf(string iso)
        {
            DateTime d;
            if (DateTime.TryParse(iso, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out d))
                return d.ToString("yyyy-MM-dd HH:mm");
            return string.IsNullOrEmpty(iso) ? "—" : iso;
        }

        // ── 官网 ─────────────────────────────────────────────────────
        private UIElement PageWebsite()
        {
            StackPanel col = new StackPanel();
            col.Children.Add(Ui.H1("KARL'S LIGHT 官方网站"));
            col.Children.Add(Lead("产品信息、使用文档与技术支持。"));

            StackPanel s = new StackPanel();
            TextBlock url = Ui.Text(Branding.Website);
            url.FontSize = 20;
            url.FontWeight = FontWeights.Light;
            url.Foreground = Theme.Brush(Theme.RedLite);
            s.Children.Add(url);

            Button open = Ui.Primary("在浏览器中打开",
                delegate { Ui.OpenUrl(Branding.WebsiteUrl); });
            open.Margin = new Thickness(0, 18, 0, 0);
            open.HorizontalAlignment = HorizontalAlignment.Left;
            s.Children.Add(open);

            Border c = Card620(s);
            c.Margin = new Thickness(0, 22, 0, 0);
            col.Children.Add(c);
            return col;
        }

        // ── 版权声明 ─────────────────────────────────────────────────
        private UIElement PageCopyright()
        {
            StackPanel col = new StackPanel();
            col.Children.Add(Ui.H1("版权声明"));

            StackPanel s = new StackPanel();
            TextBlock t = Ui.Text(Branding.Copyright);
            t.FontSize = 14;
            t.LineHeight = 26;
            s.Children.Add(t);

            Border c = Card620(s);
            c.Margin = new Thickness(0, 22, 0, 0);
            col.Children.Add(c);

            StackPanel s2 = new StackPanel();
            s2.Children.Add(Ui.H2("第三方组件"));
            TextBlock t2 = Ui.Dim(
                Branding.ShortName + " 的救援环境基于 Linux 及一系列开源软件构建，" +
                "这些组件各自遵循其原始许可证（GPL、LGPL、MIT 等）发布，" +
                "其版权归各自作者所有。\n\n" +
                "完整的组件清单与许可证文本随救援环境一同分发，" +
                "也可通过官网索取对应的源代码。");
            t2.Margin = new Thickness(0, 10, 0, 0);
            s2.Children.Add(t2);

            Border c2 = Card620(s2);
            c2.Margin = new Thickness(0, 18, 0, 0);
            col.Children.Add(c2);

            return col;
        }

        // ── 免责条款 ─────────────────────────────────────────────────
        private UIElement PageDisclaimer()
        {
            StackPanel col = new StackPanel();
            col.Children.Add(Ui.H1("免责条款"));
            col.Children.Add(Lead("使用本软件前请完整阅读。"));

            StackPanel s = new StackPanel();
            TextBlock t = Ui.Dim(Branding.Disclaimer);
            t.LineHeight = 22;
            s.Children.Add(t);

            Border c = Card620(s);
            c.Margin = new Thickness(0, 22, 0, 0);
            col.Children.Add(c);
            return col;
        }

        // ── 小构件 ───────────────────────────────────────────────────
        private static TextBlock Lead(string s)
        {
            TextBlock t = Ui.Dim(s);
            t.Margin = new Thickness(0, 12, 0, 0);
            t.MaxWidth = 640;
            t.HorizontalAlignment = HorizontalAlignment.Left;
            return t;
        }

        private static Border Card620(UIElement child)
        {
            Border b = Ui.Card(child);
            b.MaxWidth = 640;
            b.HorizontalAlignment = HorizontalAlignment.Left;
            return b;
        }

        private static Border Callout(string s)
        {
            Border b = Inline(s);
            b.Margin = new Thickness(0, 20, 0, 0);
            return b;
        }

        private static Border Inline(string s)
        {
            TextBlock t = Ui.Dim(s);
            Border b = new Border();
            b.Background = Theme.Brush(Theme.Surface);
            b.BorderBrush = Theme.Brush(Theme.Red);
            b.BorderThickness = new Thickness(2, 0, 0, 0);
            b.Padding = new Thickness(16, 12, 16, 12);
            b.Margin = new Thickness(0, 12, 0, 0);
            b.MaxWidth = 640;
            b.HorizontalAlignment = HorizontalAlignment.Left;
            b.Child = t;
            return b;
        }

        private static Grid Kv(string k, string v)
        {
            Grid g = new Grid();
            g.Margin = new Thickness(0, 9, 0, 0);
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            TextBlock a = Ui.Dim(k);
            Grid.SetColumn(a, 0);
            g.Children.Add(a);

            TextBlock b = Ui.Text(v);
            b.FontSize = 13;
            Grid.SetColumn(b, 1);
            g.Children.Add(b);
            return g;
        }
    }
}
