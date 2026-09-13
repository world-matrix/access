// 品牌与法律文本的唯一来源。
//
// 这些字符串会同时出现在界面、安装程序、exe 的版本资源、代码签名证书的
// 主题名里。散落在各处写四遍，改一次就会漏三处——版权声明写错在法律上
// 不是小事，所以集中在这里，别处一律引用。
//
// 注意：整个工程按 C# 5 写（系统自带的 csc.exe 是 Roslyn 之前的版本），
// 不能用字符串内插、?.、表达式体成员、nameof。

namespace KarlsLight.Access
{
    internal static class Branding
    {
        public const string ProductName = "KARL'S LIGHT ACCESS";
        public const string ShortName   = "ACCESS";
        public const string Company     = "KARL'S LIGHT(China)";
        // Version 只放纯数字：exe 版本资源（AssemblyVersion）、卸载注册表键名
        // 都从这里派生，掺字母会坏。给人看的版本一律用 DisplayVersion。
        public const string Version     = "1.2.0";
        public const string DisplayVersion = "1.2 Public Beta";
        public const string Website     = "www.kezdx.com";
        public const string WebsiteUrl  = "https://www.kezdx.com";

        // 用户给的原文。两处 KARL'S LIGHT 统一用直角撇号 '：原文第二处是全角
        // 左单引号（输入法顺手打出来的），一句版权声明里混用两种撇号不像是
        // 有意的，统一成和公司名一致的那一种。
        public const string Copyright =
            "©Copyright KARL'S LIGHT CO.LTD All rights reserved. " +
            "Designed by KARL'S LIGHT in Guiyang";

        // 代码签名证书的主题。城市取自版权声明里的 Guiyang。
        public const string CertSubject =
            "CN=KARL'S LIGHT CO.,LTD, O=KARL'S LIGHT CO.,LTD, L=Guiyang, C=CN";

        // 免责条款。写得具体一点——泛泛的「概不负责」既不可信也没用，
        // 用户真正需要知道的是「哪些情况下会丢数据」。
        public const string Disclaimer =
@"一、本软件按「现状」提供，不附带任何形式的明示或默示担保，包括但不限于对适销性、特定用途适用性的担保。

二、本软件涉及磁盘分区、系统镜像捕获与还原等底层操作。这类操作在以下情况下可能导致数据丢失，且丢失通常不可恢复：

    · 操作过程中断电、强制关机或拔出存储设备
    · 源磁盘存在物理故障（坏道、接口不稳、临近失效的固态硬盘）
    · 用户选错了目标分区
    · 与其他磁盘工具、磁盘加密软件或杀毒软件同时运行产生冲突

三、请在执行任何还原、格式化或分区调整之前，将重要数据另行备份到与本机物理分离的存储介质上。同一块硬盘上的备份挡不住硬盘本身故障。

四、在法律允许的最大范围内，" + Company + @" 不对使用或无法使用本软件所引起的任何直接、间接、偶然、特殊或后果性损害承担责任，包括但不限于数据丢失、业务中断、利润损失。

五、本软件不修改计算机固件（BIOS/UEFI）程序本身。它仅按 UEFI 规范写入标准的启动项与 BootNext 变量——这是操作系统安装程序同样会做的事。

六、继续使用本软件，即表示您已阅读并同意上述条款。";

        // 关于页里说明「这软件是干什么的」那段。
        public const string AboutBlurb =
@"KARL'S LIGHT ACCESS 是一套装在本机硬盘隐藏分区里的救援与恢复环境。

它复刻了 IBM ThinkPad 上那颗蓝色 Access 按键的思路：救援系统不依赖任何外部介质，开机进 KLA 菜单后按 F4 就进得去。系统坏到进不了桌面的时候，你不需要先去找一根 U 盘，也不需要另一台还能用的电脑。

Windows 这一侧的程序负责三件事：把当前系统打包成还原点、管理这些还原点、以及在你需要的时候把机器直接引导进 ACCESS。";
    }
}
