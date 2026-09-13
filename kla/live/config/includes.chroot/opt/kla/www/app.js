/* ═══════════════════════════════════════════════════════════════
   KARL'S LIGHT ACCESS —— 前端逻辑

   分类结构忠实复刻 Access IBM 预桌面区域的四大类：
     Rescue and Recovery / Configure / Communicate / Troubleshoot
   点进去的交互也照原版：顶部步骤条一路「下一步」到底，
   动作按钮永远钉在同一个位置，不让用户在页面里找。

   阶段 2：文件救援已接通后端（/api/browse + copyout 作业）。
   其余功能仍是占位，标签上如实标注，不做"看起来能点"的假象。
   ═══════════════════════════════════════════════════════════════ */

const CATEGORIES = [
  {
    // 系统概况：不是任务分类，是特殊渲染页（special 字段走 renderOverview）。
    // 放第一个：用户从 splash 的硬件检查进来，第一眼看到的就是检查结果。
    id: 'overview',
    name: '系统概况',
    icon: 'dashboard',
    desc: '这台电脑的品牌、型号与硬件配置一览。',
    special: 'overview',
    tasks: []
  },
  {
    id: 'rescue',
    name: '救援与恢复',
    icon: 'rescue',
    desc: '系统无法启动或数据受损时，从这里恢复。',
    tasks: [
      {
        id: 'factory', icon: 'factory', title: '恢复出厂',
        desc: '用出厂母盘覆盖系统分区，回到部署时的干净状态',
        needs: ['wimlib'],
        winOnly: true,
        ready: true,
        open: () => openFactory(),
        help: {
          title: '恢复出厂',
          body: '把系统分区完整覆盖为部署 KLA 时捕获的出厂母盘。所有在那之后安装的程序、修改的设置都会消失。',
          warn: '系统分区上的数据将被全部覆盖。执行前请先用「文件救援」把要保留的数据导出。'
        }
      },
      {
        id: 'restore', icon: 'clock', title: '从备份还原',
        desc: '在备份时间线上选择一个还原点回滚系统',
        needs: ['wimlib'],
        winOnly: true,
        ready: true,
        open: () => openRestore(),
        help: {
          title: '从备份还原',
          body: '列出 Windows 侧 KLA 主程序创建的所有还原点，按时间线选择其一还原。增量还原会自动按链依次应用。',
          note: '还原完成后会自动重建引导。若还原后仍无法启动，请回到本菜单执行「修复启动」。'
        }
      },
      {
        id: 'files', icon: 'files', title: '文件救援',
        desc: '把数据导出到 U 盘、移动硬盘或另一个分区',
        needs: ['ntfs3g'],
        ready: true,
        open: () => openRescueFiles(),
        help: {
          title: '文件救援',
          body: '不修改任何原有数据，只读挂载源分区，浏览并复制文件到别的卷。系统进不去但硬盘还好的情况下，优先用这个。',
          note: '源分区全程只读挂载，救援过程不会对它写入任何东西。'
        }
      }
    ]
  },
  {
    id: 'configure',
    name: '配置',
    icon: 'sliders',
    desc: '调整磁盘、账户与启动设置。',
    tasks: [
      {
        id: 'partition', icon: 'disk', title: '分区管理',
        desc: '查看与调整磁盘分区结构（GParted）',
        needs: ['gparted'],
        ready: true,
        open: () => openPartition(),
        help: {
          title: '分区管理',
          body: '图形化的分区工具，可以创建、删除、调整、格式化分区。',
          warn: '误操作会导致数据永久丢失。除非明确知道在做什么，否则不要动系统分区和救援分区。'
        }
      },
      {
        id: 'winacct', icon: 'key', title: 'Windows 账户',
        desc: '重置本地账户密码、解锁被禁用的账户',
        needs: ['chntpw'],
        winOnly: true,
        ready: true,
        open: () => openWinacct(),
        help: {
          title: 'Windows 账户',
          body: '直接编辑 Windows 的 SAM 数据库，清空本地账户密码或启用被禁用的账户。',
          note: '只对本地账户有效。Microsoft 在线账户的密码需要在 account.microsoft.com 重置。'
        }
      },
      {
        id: 'bootfix', icon: 'wrench', title: '修复启动',
        desc: '重建 Windows 引导记录与启动项',
        needs: [],
        winOnly: true,
        ready: true,
        open: () => openBootfix(),
        help: {
          title: '修复启动',
          body: '重新生成 ESP 上的引导文件和 BCD 启动配置，解决「找不到操作系统」「0xc000000f」这类启动失败。',
          note: '此操作不会动你的数据，只重写引导区，可以放心尝试。'
        }
      }
    ]
  },
  {
    id: 'communicate',
    name: '通信',
    icon: 'satellite',
    desc: '连接网络，访问互联网与网络共享。',
    tasks: [
      {
        id: 'network', icon: 'wifi', title: '网络设置',
        desc: '连接有线或无线网络',
        needs: ['nmcli'],
        ready: true,
        open: () => openWifi(),
        help: {
          title: '网络设置',
          body: '扫描并连接 WiFi；有线网络一般自动获取 IP，直接可用。救援环境已内置常见网卡的固件。',
          note: '如果这里看不到任何网卡，说明本机网卡的驱动未包含在救援系统中。'
        }
      },
      {
        id: 'browser', icon: 'browser', title: '打开浏览器',
        desc: '上网查找解决方案或下载文件',
        needs: ['chromium'],
        ready: true,
        open: () => openBrowser(),
        help: {
          title: '打开浏览器',
          body: '打开一个独立的浏览器窗口。系统坏了正好可以用它查报错信息、下载驱动或工具。',
          note: '下载的文件会存到内存盘，重启即消失。需要保留请存到 U 盘。'
        }
      },
      {
        id: 'share', icon: 'share', title: '网络位置',
        desc: '挂载 SMB 共享，用于导出备份或读取镜像',
        needs: ['cifs'],
        ready: true,
        open: () => openShare(),
        help: {
          title: '网络位置',
          body: '挂载局域网内的 SMB/CIFS 共享目录。可以把救援出来的文件直接写到 NAS，也可以从网络位置读取备份镜像做还原。'
        }
      },
      {
        id: 'proxy', icon: 'proxy', title: '网络代理',
        desc: '设置应急代理，访问 GitHub 等被卡站点',
        needs: [],
        ready: true,
        open: () => openProxy(),
        help: {
          title: '网络代理',
          body: '给救援环境设置 HTTP 代理（路由器上已跑代理服务的场景）。设置后浏览器、终端里的 curl/wget、apt 都走代理。',
          note: '代理设置保存在内存里，重启救援环境后自动清除。'
        }
      }
    ]
  },
  {
    id: 'troubleshoot',
    name: '诊断',
    icon: 'scan',
    desc: '判断是软件问题还是硬件真的坏了。',
    tasks: [
      {
        id: 'hwdiag', icon: 'stethoscope', title: '硬件诊断',
        desc: '硬盘 SMART、温度、CPU 与内存压力测试',
        needs: ['smartctl'],
        ready: true,
        open: () => openHwdiag(),
        help: {
          title: '硬件诊断',
          body: '读取硬盘 SMART 健康数据、传感器温度，并可运行 CPU/内存压力测试。系统频繁崩溃时先跑这个，排除硬件故障。',
          note: 'SMART 出现「重新分配扇区计数」或「待定扇区」非零，说明硬盘正在坏，尽快备份换盘。'
        }
      },
      {
        id: 'memtest', icon: 'chip', title: '内存测试',
        desc: '完整的内存颗粒检测（MemTest86+）',
        needs: ['memtester'],
        ready: true,
        open: () => openMemtest(),
        help: {
          title: '内存测试',
          body: '重启进入独立的 MemTest86+ 环境做完整内存检测。这是判断随机蓝屏、程序莫名崩溃最有效的手段。',
          note: '完整跑一遍需要数小时。建议睡前开始，第二天看结果。按 Esc 可随时中止并重启。'
        }
      },
      {
        id: 'gpudiag', icon: 'monitor', title: '显卡诊断',
        desc: '显卡型号、显存、驱动状态与内核报错',
        needs: ['smartctl'],
        ready: true,
        open: () => openGpudiag(),
        help: {
          title: '显卡诊断',
          body: '读取显卡型号、显存容量、当前绑定的驱动、每个视频接口的连接状态，并抓取内核里 GPU 相关的报错。花屏、黑屏、驱动崩溃先看这里。',
          note: '「驱动」一栏显示（未绑定驱动）说明内核没认出这张卡，通常是显卡太新或救援系统缺它的固件。'
        }
      },
      {
        id: 'datarec', icon: 'search', title: '数据恢复',
        desc: '找回误删的分区与文件（TestDisk / PhotoRec）',
        needs: ['testdisk'],
        ready: true,
        open: () => openDatarec(),
        help: {
          title: '数据恢复',
          body: 'TestDisk 恢复误删的分区表，PhotoRec 按文件特征扫描恢复已删除文件。',
          warn: '发现数据丢失后应立即停止向该磁盘写入任何内容，否则会覆盖待恢复的数据。'
        }
      }
    ]
  },
  {
    id: 'advanced',
    name: '高级功能',
    icon: 'advanced',
    desc: '终端、安装系统、SSH 与远程协助。给知道自己要做什么的人。',
    tasks: [
      {
        id: 'terminal', icon: 'terminal', title: 'Linux 终端',
        desc: '打开命令行终端，直接操作救援系统',
        needs: ['lxterminal'],
        ready: true,
        open: () => openTerminal(),
        help: {
          title: 'Linux 终端',
          body: '打开一个完整的 bash 终端窗口。救援系统是 Debian Linux，常用命令（mount / fsck / dd / vim）都可以直接用。',
          note: '救援系统运行在内存里，终端里做的修改重启即消失；对硬盘分区做的修改是永久的。'
        }
      },
      {
        id: 'osinstall', icon: 'disc', title: '安装全新系统',
        desc: '用你提供的 ISO 制作可启动的系统安装盘',
        needs: ['wimlib'],
        ready: true,
        open: () => openOsinstall(),
        help: {
          title: '安装全新系统',
          body: '选择一个 Windows 或 Linux 的 ISO 镜像，把它制作成可启动的安装盘。重启后从那块盘启动就是标准的系统安装程序。',
          warn: '制作过程会清空目标盘上的所有数据。选盘前请三思，重要资料先拷出来。'
        }
      },
      {
        id: 'ssh', icon: 'lock', title: 'SSH 设置',
        desc: '开启 SSH 服务、设置登录密码',
        needs: [],
        ready: true,
        open: () => openSsh(),
        help: {
          title: 'SSH 设置',
          body: '启动救援系统的 SSH 服务并设置 root 密码。开启后局域网里的其他电脑就能用 ssh 命令连进来操作。',
          note: '救援系统是内存盘，密码和配置重启即失效，不用记得改回去。'
        }
      },
      {
        id: 'remote', icon: 'assist', title: '远程协助',
        desc: '让技术人员通过网络连接到这台机器',
        needs: [],
        ready: true,
        open: () => openRemote(),
        help: {
          title: '远程协助',
          body: '自己看不懂界面、需要别人帮忙时用这个：开启 SSH 后把连接信息告诉同一网络里的技术人员，对方就能远程操作这台机器的救援系统。',
          note: '双方必须在同一个局域网里。这个功能不提供互联网远程控制。'
        }
      },
      {
        id: 'filemgr', icon: 'drawer', title: '文件管理器',
        desc: '图形化浏览与管理本机文件（PCManFM）',
        needs: ['pcmanfm'],
        ready: true,
        open: () => openFilemgr(),
        help: {
          title: '文件管理器',
          body: '打开 PCManFM 文件管理器。可以浏览已挂载的分区、U 盘、SMB 共享里的文件，做复制、删除、重命名。',
          warn: '在文件管理器里删掉的文件不进回收站，直接没了。'
        }
      }
    ]
  },
  {
    id: 'linuxtools',
    name: 'Linux 工具',
    icon: 'penguin',
    desc: '修复装在这台电脑上的 Linux 系统的常见适配问题。',
    tasks: [
      {
        id: 'dpkg-fix', icon: 'pkg', title: '更新中断修复',
        desc: '升级时断电/死机后 apt 报错、装不了软件',
        needs: [], ready: true, open: () => openLinuxfix('dpkg-fix'),
        help: {
          title: '更新中断修复',
          body: '最常见的问题：系统升级或装软件时中断，之后所有 apt/dpkg 操作报错。修复流程：dpkg --configure -a → apt -f install → apt update。',
          note: '修复本身不需要联网，但完成后建议联网跑一次完整更新。'
        }
      },
      {
        id: 'wifi-firmware', icon: 'signal', title: 'WiFi/蓝牙修复',
        desc: '无线网卡识别了但搜不到网/蓝牙失效（固件缺失）',
        needs: [], ready: true, open: () => openLinuxfix('wifi-firmware'),
        help: {
          title: 'WiFi/蓝牙修复',
          body: '重装 linux-firmware 无线固件包并解除 rfkill 硬阻断。适用于「网卡识别了但没有无线网络/蓝牙开关是灰的」。',
          warn: '需要在救援环境里先连网（插网线或用可用网络）。'
        }
      },
      {
        id: 'nomodeset', icon: 'nomsg', title: '开机黑屏修复',
        desc: '启动后黑屏/花屏（显卡驱动崩溃）',
        needs: [], ready: true, open: () => openLinuxfix('nomodeset'),
        help: {
          title: '开机黑屏修复',
          body: '给内核加 nomodeset 参数：放弃 KMS 显卡加速，用固件 VESA 模式显示。这是「先让系统能亮起来」的急救方案，进去之后再慢慢查显卡驱动。',
          note: '画面会变糊、没有分辨率选择，这是正常代价。'
        }
      },
      {
        id: 'sound', icon: 'sound', title: '无声修复',
        desc: '系统正常但完全没有声音（音频栈损坏）',
        needs: [], ready: true, open: () => openLinuxfix('sound'),
        help: {
          title: '无声修复',
          body: '重装 alsa-utils + pipewire + wireplumber 并清空音频服务的状态目录。适用于「音量图标在、设备也在，但就是没声」。',
          warn: '需要在救援环境里先连网。'
        }
      },
      {
        id: 'nvidia-clean', icon: 'gpu-fan', title: 'NVIDIA 驱动回退',
        desc: '装了 N 卡驱动后频繁死机/黑屏，回退开源驱动',
        needs: [], ready: true, open: () => openLinuxfix('nvidia-clean'),
        help: {
          title: 'NVIDIA 驱动回退',
          body: '卸载全部 NVIDIA 专有驱动包，回退到开源 nouveau。专有驱动装挂了导致死机时用这个先稳住系统。',
          warn: '需要在救援环境里先连网。'
        }
      },
      {
        id: 'grub-rebuild', icon: 'grub', title: '重建 Linux 引导',
        desc: '重装 Windows/动过分区后 Linux 启动项没了',
        needs: [], ready: true, open: () => openLinuxfix('grub-rebuild'),
        help: {
          title: '重建 Linux 引导',
          body: '在目标 Linux 系统里重跑 grub-install + update-grub。适用于「Linux 还在硬盘上，但开机直接进 Windows 了」。',
          note: '要求目标系统的 ESP 挂载在 /boot/efi（绝大多数发行版默认如此）。'
        }
      },
      {
        id: 'passwd-root', icon: 'lock', title: '重置 Linux 密码',
        desc: '忘了 root 或管理员密码',
        needs: [], ready: true, open: () => openLinuxfix('passwd-root'),
        help: {
          title: '重置 Linux 密码',
          body: '直接改目标系统 root 账户的密码。设好后重启进那个系统，用新密码登录（桌面系统可以再用 sudo 提权改自己的密码）。'
        }
      },
      {
        id: 'fsck-repair', icon: 'fsck', title: '文件系统修复',
        desc: '异常断电后报 file system error / 只读模式',
        needs: [], ready: true, open: () => openLinuxfix('fsck-repair'),
        help: {
          title: '文件系统修复',
          body: '卸载状态对目标分区跑 fsck -y，修复文件系统不一致。异常断电后系统强制进 readonly 模式时先跑这个。',
          warn: '硬盘正在坏（SMART 有坏道计数）时先抢救数据再修复。'
        }
      }
    ]
  },
  {
    id: 'toolbox',
    name: '实用工具箱',
    icon: 'toolbox',
    desc: 'CPU-Z、AIDA64、硬盘/内存读速测试——快速验机、看参数、测性能。',
    tasks: [
      {
        id: 'cpu-z', icon: 'cpu-z', title: 'CPU-Z（处理器详情）',
        desc: '查看 CPU 型号、核心/线程、频率、缓存、主板与内存规格',
        needs: ['lscpu', 'dmidecode'],
        ready: true,
        open: () => openToolbox('cpu-z'),
        help: {
          title: 'CPU-Z（处理器详情）',
          body: '读取 /proc/cpuinfo + lscpu + dmidecode，展示 CPU 型号、核心/线程、基础/睿频、一/二/三级缓存、主板品牌型号、内存通道、SPD 时序等关键参数。',
          note: '读取 SMBIOS/DMI 信息需要 root 权限，救援系统默认已以 root 运行。'
        }
      },
      {
        id: 'aida64', icon: 'aida', title: 'AIDA64（整机传感器）',
        desc: '汇总整机温度、电压、风扇转速、功耗与硬件列表',
        needs: ['sensors'],
        ready: true,
        open: () => openToolbox('aida64'),
        help: {
          title: 'AIDA64（整机传感器）',
          body: '汇总 lm-sensors（CPU/GPU/主板温度、电压、风扇转速）、硬盘 SMART 温度、电池信息、lspci 设备列表，相当于 AIDA64 的「计算机→传感器」页。',
          note: '部分笔记本的 EC 传感器需要额外内核模块，读不到是正常现象。'
        }
      },
      {
        id: 'hdd-speed', icon: 'hdd-speed', title: '硬盘读速测试',
        desc: '用 hdparm -tT + dd 实测每块盘缓存与顺序读取速度',
        needs: ['hdparm'],
        ready: true,
        open: () => openToolbox('hdd-speed'),
        help: {
          title: '硬盘读速测试',
          body: '分两步：① hdparm -t（缓存读，测盘控制器与总线带宽）；② dd if=/dev/sdX of=/dev/null bs=1M count=2048（顺序大文件读，接近真实连续读）。按磁盘逐个展示结果，SATA SSD 一般 500MB/s、NVMe 一般 1500~7000MB/s。',
          warn: '全程只读读取，不会对磁盘写入任何内容。'
        }
      },
      {
        id: 'ram-speed', icon: 'ram-speed', title: '内存读速测试',
        desc: '用 mbw/memtester 实测内存复制带宽（MB/s）',
        needs: ['mbw'],
        ready: true,
        open: () => openToolbox('ram-speed'),
        help: {
          title: '内存读速测试',
          body: '运行 mbw（Memory BandWidth）对 128MB 缓冲区做 3 组拷贝测试，输出 AVG 平均带宽。DDR4 笔记本一般 15~25GB/s、DDR5 一般 30~60GB/s。',
          note: '救援系统运行在内存里，已用容量会从总内存扣除，所以缓冲区大小自动做了下限保护。'
        }
      }
    ]
  }
];

// ── 工具 ────────────────────────────────────────────────────────
const $ = sel => document.querySelector(sel);

const fmtSize = b => {
  if (!b) return '—';
  const u = ['B', 'KB', 'MB', 'GB', 'TB'];
  let i = 0, n = b;
  while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; }
  return n.toFixed(n < 10 && i > 0 ? 1 : 0) + ' ' + u[i];
};

// 速率（B/s）格式化：磁盘 IO、网络吞吐用。1 KB/s = 1000 B/s（硬盘厂商惯例，对用户更直观）。
const fmtBps = b => {
  if (b == null || isNaN(b)) return '—';
  b = Number(b);
  if (b < 0) b = 0;
  if (b < 1e3) return b.toFixed(0) + ' B/s';
  if (b < 1e6) return (b / 1e3).toFixed(1) + ' KB/s';
  if (b < 1e9) return (b / 1e6).toFixed(2) + ' MB/s';
  return (b / 1e9).toFixed(2) + ' GB/s';
};

// 百分号：一位小数，裁剪到 0-100
const fmtPct = p => {
  if (p == null || isNaN(p)) return '—';
  let v = Number(p);
  if (v < 0) v = 0;
  if (v > 100) v = 100;
  return v.toFixed(1) + ' %';
};

// sparkline：N 点折线 + 底部面积。140×34 画布，末尾 60 点环形截断。
// color = cpu/mem/gpu/disk/net（对应 5 种主题色）。
const sparkline = (points, color) => {
  const W = 140, H = 34, PAD = 2;
  let data = (points || []).slice(-60);
  while (data.length < 60) data.unshift(null);
  let max = 1;
  data.forEach(v => { if (v != null && !isNaN(v) && v > max) max = v; });
  const colors = { cpu: '#4ea1ff', mem: '#ff9f43', gpu: '#8b5cf6', disk: '#24c08b', net: '#e45858' };
  const stroke = colors[color] || '#4ea1ff';
  const fill = stroke + '33';
  const maxY = H - PAD * 2;
  const stepX = (W - PAD * 2) / (data.length - 1 || 1);
  let d = '';
  let area = '';
  let firstX = null, firstY = null, lastX = null, lastY = null;
  let penActive = false;
  data.forEach((v, i) => {
    const x = PAD + i * stepX;
    if (v == null || isNaN(v)) { penActive = false; return; }
    const y = H - PAD - maxY * (v / max);
    if (!penActive) {
      d += ` L ${x.toFixed(1)} ${y.toFixed(1)}`;   // 前面会先 M 起点，第一次也能直接拼 L（首段无 M 就前置一个 M）
    } else {
      d += ` L ${x.toFixed(1)} ${y.toFixed(1)}`;
    }
    if (firstX == null) { firstX = x; firstY = y; }
    lastX = x; lastY = y;
    penActive = true;
  });
  if (d.startsWith(' L ')) d = 'M' + d.slice(2);  // 把首段补成 move
  if (lastX != null && firstX != null) {
    area = d + ` L ${lastX.toFixed(1)} ${(H - PAD).toFixed(1)} L ${firstX.toFixed(1)} ${(H - PAD).toFixed(1)} Z`;
  }
  const empty = firstX == null;
  return (
    `<svg class="sparkline" viewBox="0 0 ${W} ${H}" preserveAspectRatio="none">` +
    (area ? `<path d="${area}" fill="${fill}" stroke="none" />` : '') +
    (!empty ? `<path d="${d}" fill="none" stroke="${stroke}" stroke-width="1.3" stroke-linejoin="round" stroke-linecap="round"/>`
            : `<path d="M 4 ${H - PAD} L ${W - 4} ${H - PAD}" fill="none" stroke="#c8cdd5" stroke-dasharray="3 2" stroke-width="1"/>`) +
    `</svg>`
  );
};

// 概览页活计时器：时钟(1s) + metrics(2s)；离开页面时双清，避免 interval 挂空 DOM。
let ovClock = null;
let ovMetricsTimer = null;
let ovMetricBuf = { cpu: [], mem: [], gpu: [], disk: [], net: [] };
const METRIC_BUF_MAX = 60;

function stopClock() {
  if (ovClock)       { clearInterval(ovClock);       ovClock = null; }
  if (ovMetricsTimer){ clearInterval(ovMetricsTimer); ovMetricsTimer = null; }
}

function ovNow() {
  const d = new Date();
  const p = n => String(n).padStart(2, '0');
  const wd = ['日', '一', '二', '三', '四', '五', '六'][d.getDay()];
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}` +
         ` 星期${wd} ${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
}

// 1 张「任务管理器」风格图谱卡：标题/图标/百分值/sparkline/底部进度条/辅助行
function ovMetricCard(kind, title, iconName, pct, sparkBuf, extraRow) {
  const pctNum = (pct == null || isNaN(pct)) ? null : Math.max(0, Math.min(100, Number(pct)));
  const pctCss = pctNum == null ? 0 : pctNum;
  const pctDisplay = pctNum == null ? '—' : pctNum.toFixed(1) + ' %';
  return `<div class="ov-metric-card" data-ovm="${kind}">
    <div class="ov-metric-head">
      ${icon(iconName, 'ico20')}
      <span class="ov-metric-title">${esc(title)}</span>
      <span class="ov-metric-pct">${pctDisplay}</span>
    </div>
    <div class="ov-metric-spark">${sparkline(sparkBuf, kind)}</div>
    <div class="ov-metric-bar"><div class="ov-metric-barfill" style="width:${pctCss}%"></div></div>
    <div class="ov-metric-extra">${extraRow || ''}</div>
  </div>`;
}

const fmtTime = s => {
  if (s == null) return '—';
  const m = Math.floor(s / 60), r = Math.floor(s % 60);
  return m > 0 ? `${m} 分 ${r} 秒` : `${r} 秒`;
};

const icon = (name, cls) =>
  `<svg class="${cls}"><use href="assets/icons.svg#${name}"></use></svg>`;

const esc = s => String(s == null ? '' : s).replace(/[&<>"]/g,
  c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));

const get = async p => {
  try { const r = await fetch(p); return r.ok ? await r.json() : null; }
  catch { return null; }
};

// GET 里那个 null 会把"后端 500 了"和"后端说 ok:false"混成一件事。
// 浏览和作业提交都需要区分这两者，所以单开一个带状态码的。
const call = async (p, method, body) => {
  try {
    const r = await fetch(p, {
      method: method || 'GET',
      headers: { 'Content-Type': 'application/json' },
      body: body === undefined ? null : JSON.stringify(body)
    });
    let data = {};
    try { data = await r.json(); } catch { /* 空响应体 */ }
    return { status: r.status, data };
  } catch {
    return { status: 0, data: { reason: '无法连接到后端服务' } };
  }
};

// ── 路径 ────────────────────────────────────────────────────────
// 前端只处理卷内的相对路径（永远以 / 开头），挂载点由后端拼。
const joinPath = (dir, name) =>
  (dir === '/' || !dir) ? '/' + name : dir.replace(/\/+$/, '') + '/' + name;

const parentPath = p => {
  if (!p || p === '/') return '/';
  const i = p.replace(/\/+$/, '').lastIndexOf('/');
  return i <= 0 ? '/' : p.slice(0, i);
};

// 某个路径是否已被某个选中的祖先目录覆盖。
// parentPath('/') 恒为 '/'，所以必须先判 picked 再判是否到根，否则死循环。
function coveredByAncestor(p, picked) {
  let cur = parentPath(p);
  for (;;) {
    if (picked.has(cur)) return cur;
    if (cur === '/') return null;
    cur = parentPath(cur);
  }
}

// NTFS 不接受这几个字符；开头的点在 Linux 下是隐藏文件，用户多半不是这个意思
const cleanSubdir = s =>
  (s || '').replace(/[\/\\:*?"<>|]/g, '').replace(/^\.+/, '').trim();

// ── 状态 ────────────────────────────────────────────────────────
const state = { tools: {}, disks: [], current: 'overview', wiz: null, mode: null };

// ── 救援目标模式：Windows / Linux ───────────────────────────────
// 同一套救援环境服务两类系统。任务用 winOnly 标记 Windows 专属
// （恢复出厂/备份还原走 wimlib，只认 NTFS + Windows 卷；chntpw 只认
// Windows 的 SAM）。Linux 工具整类只对 Linux 用户显示。首次进入
// 弹选择，之后右上角随时切换。选择存 localStorage（救援系统在内存
// 里跑，重启即回到选择页——这正好，每次救援先想清楚救的是谁）。
const MODE_KEY = 'kla-mode';

function loadMode() {
  try { state.mode = localStorage.getItem(MODE_KEY) || null; }
  catch { state.mode = null; }
}

function setMode(m) {
  state.mode = m;
  try { localStorage.setItem(MODE_KEY, m); } catch {}
  updateModeLabel();
  // 换模式后当前分类可能不可见（比如 Linux 模式下的 linuxtools），
  // 回到第一个可见分类。
  const cats = visibleCategories();
  if (!cats.find(c => c.id === state.current) && cats.length) {
    state.current = cats[0].id;
  }
  if (state.wiz) { stopPolling(); state.wiz = null; }
  renderHome();
}

function updateModeLabel() {
  // 保证在 DOM 解析完成后再读写，避免 boot() 首屏读不到元素
  // 导致"一直显示 target 图标"的假阳性。
  const tick = () => {
    const lbl = document.getElementById('modeLabel');
    if (lbl) lbl.textContent = '目标：' + (state.mode === 'linux' ? 'Linux' : 'Windows');
    const btn = document.getElementById('modeBtn');
    if (btn) {
      const use = btn.querySelector('use');
      if (use) use.setAttribute('href',
        'assets/icons.svg#' + (state.mode === 'linux' ? 'penguin' : 'windows'));
    }
  };
  if (typeof requestAnimationFrame === 'function') {
    try { requestAnimationFrame(tick); requestAnimationFrame(() => setTimeout(tick, 0)); return; }
    catch (_) {}
  }
  setTimeout(tick, 0);
}

// 横幅上 Wi-Fi 按钮的实时状态：
// - 有连接 → 绿色图标 + 显示"Wi-Fi: SSID (X%)"
// - 无连接但有驱动 → 灰色图标 + "Wi-Fi: 未连接"
// - 驱动不存在 → 灰色图标 + "Wi-Fi: 无网卡"
let wifiBannerTimer = null;
async function updateWifiBanner() {
  const btn = document.getElementById('wifiBtn');
  const lbl = document.getElementById('wifiLabel');
  if (!btn || !lbl) return;
  const use = btn.querySelector('use');
  let net = null;
  try { net = (await call('/api/network')).data; } catch (_) {}
  let label = 'Wi-Fi';
  let iconName = 'wifi-0';
  let color = '';
  if (net && net.ok === false && net.reason && /无|设备|驱动|firmware/i.test(net.reason)) {
    label = 'Wi-Fi: 无网卡';
    iconName = 'wifi-0';
  } else if (net && Array.isArray(net.active) && net.active.length) {
    const a = net.active[0];
    const sig = typeof a.signal === 'number' ? a.signal : null;
    const bars = (sig == null) ? 0 : Math.max(0, Math.min(3, Math.floor(sig / 30)));
    iconName = 'wifi-' + (bars || 0);
    if (a.type === 'wifi' || a.ssid) {
      const name = a.ssid || a.name || '已连接';
      const pct = (sig == null) ? '' : ' (' + String(sig) + '%)';
      label = 'Wi-Fi: ' + name + pct;
    } else if (a.type === 'ethernet' || /eth|enp|有线/i.test(a.name || a.type || '')) {
      label = '有线: ' + (a.name || '已连接');
      iconName = 'satellite';
    } else {
      label = '连接: ' + (a.name || String(a.type || '已连接'));
    }
    color = '#4ade80'; // 绿色=已连接
  } else {
    label = 'Wi-Fi: 未连接';
    iconName = 'wifi-0';
  }
  try { btn.style.color = color || ''; } catch (_) {}
  if (use) use.setAttribute('href', 'assets/icons.svg#' + iconName);
  lbl.textContent = label;
}
function startWifiBannerPolling() {
  if (wifiBannerTimer) return;
  updateWifiBanner();
  wifiBannerTimer = setInterval(updateWifiBanner, 5000);
}

function visibleTasks(cat) {
  if (state.mode === 'linux') return cat.tasks.filter(t => !t.winOnly);
  return cat.tasks;
}

function visibleCategories() {
  if (state.mode === 'linux') {
    // Linux 模式：linuxtools 显示；其余分类过滤 winOnly 任务，
    // 过滤后一个任务都不剩的分类整个隐藏——但系统概况（special）没有
    // 任务也永远显示，它是环境信息不是工具。
    return CATEGORIES.filter(c => {
      if (c.id === 'linuxtools') return true;
      if (c.special) return true;
      return visibleTasks(c).length > 0;
    });
  }
  // Windows 模式：隐藏 Linux 专属分类，其余全显示。
  return CATEGORIES.filter(c => c.id !== 'linuxtools');
}

function showModePick() {
  const el = document.getElementById('modePick');
  if (!el) return;
  el.hidden = false;
  el.querySelectorAll('.mode-card').forEach(b => {
    b.onclick = () => { el.hidden = true; setMode(b.dataset.mode); };
  });
}

const ESP_GUID = 'c12a7328-f81f-11d2-ba4b-00a0c93ec93b';

// 把「磁盘 → 分区」摊平，同时保留分区属于哪块盘——
// 「别把数据救到同一块正在坏的盘上」这条提示需要这个信息。
function allVolumes() {
  const out = [];
  state.disks.forEach(d => (d.partitions || []).forEach(p => {
    out.push(Object.assign({}, p, { diskName: d.name, diskModel: d.model }));
  }));
  return out;
}

const volLabel = v => v.label || (v.fstype ? v.fstype.toUpperCase() + ' 卷' : '未格式化');

// ── 渲染：主页 ──────────────────────────────────────────────────
function missingDeps(task) {
  return (task.needs || []).filter(n => !state.tools[n]);
}

function renderNav() {
  $('#nav').innerHTML = visibleCategories().map(c => {
    const avail = visibleTasks(c).filter(t => missingDeps(t).length === 0).length;
    const total = visibleTasks(c).length;
    const badge = c.special ? '' : `<span class="count">${avail}/${total}</span>`;
    return `<li class="nav-item ${c.id === state.current ? 'active' : ''}" data-cat="${c.id}">
      ${icon(c.icon, 'ico24')}
      <span>${esc(c.name)}</span>
      ${badge}
    </li>`;
  }).join('');

  $('#nav').querySelectorAll('.nav-item').forEach(el => {
    el.addEventListener('click', () => {
      if (!leaveWizard()) return;
      state.current = el.dataset.cat;
      renderHome();
    });
  });
}

function renderHome() {
  state.wiz = null;
  stopClock();
  $('#backBtn').hidden = true;

  const cats = visibleCategories();
  let cat = cats.find(c => c.id === state.current);
  if (!cat && cats.length) { cat = cats[0]; state.current = cat.id; }
  $('#catTitle').textContent = cat.name;
  $('#catDesc').textContent = cat.desc;

  // 系统概况不是任务列表，走专用渲染（每次进来重新拉一遍，信息是活的）
  if (cat.special === 'overview') {
    renderOverview();
    renderNav();
    return;
  }

  $('#contentBody').innerHTML = '<ul class="tasks" id="tasks"></ul>';
  $('#tasks').innerHTML = visibleTasks(cat).map(t => {
    const missing = missingDeps(t);
    const off = missing.length > 0;
    let tag, cls = '';
    if (off) { tag = '缺少 ' + missing.join(', '); }
    else if (t.ready) { tag = '可用'; cls = 'ready'; }
    else { tag = '尚未实现'; }
    return `<li class="task ${off ? 'disabled' : ''}" data-task="${t.id}">
      ${icon(t.icon, 'ico32')}
      <div class="task-text">
        <div class="task-title">${esc(t.title)}</div>
        <div class="task-desc">${esc(t.desc)}</div>
      </div>
      <span class="task-tag ${cls}">${esc(tag)}</span>
    </li>`;
  }).join('');

  $('#tasks').querySelectorAll('.task').forEach(el => {
    const task = visibleTasks(cat).find(t => t.id === el.dataset.task);
    el.addEventListener('mouseenter', () => showHelp(task));
    el.addEventListener('click', () => {
      if (el.classList.contains('disabled')) return;
      if (task.open) { task.open(); return; }
      showHelp(task, '这个功能还没接上实现，点了不会有任何动作。');
    });
  });

  renderNav();
}

/* ═══════════════════════════════════════════════════════════════
   系统概况页
   ═══════════════════════════════════════════════════════════════ */

function renderOverview() {
  const body = $('#contentBody');
  body.innerHTML = '<div class="ov-loading">正在读取硬件信息…</div>';
  get('/api/sysinfo').then(info => {
    if (!info || !info.ok) {
      body.innerHTML = '<div class="ov-loading">硬件信息读取失败。点左侧其他分类再回来可重试。</div>';
      return;
    }
    stopClock();
    ovMetricBuf = { cpu: [], mem: [], rom: [], gpu: [] };

    const ramGb = (info.ramBytes / (1024 * 1024 * 1024)).toFixed(1).replace(/\.0$/, '');
    const ramUsedGb = info.ramUsedBytes
      ? (info.ramUsedBytes / (1024 * 1024 * 1024)).toFixed(2) : null;
    const ramPct = (info.ramBytes && info.ramUsedBytes)
      ? (100.0 * info.ramUsedBytes / info.ramBytes) : null;
    const cpu = info.cpu || {};
    const coresStr = [
      cpu.physicalCpus ? cpu.physicalCpus + ' 路' : null,
      cpu.cores ? cpu.cores + ' 核' : null,
      cpu.threads ? cpu.threads + ' 线程' : null,
      cpu.hyperthreading ? '超线程' + cpu.hyperthreading : null,
    ].filter(Boolean).join(' · ') || '—';
    const freqs = [
      cpu.baseFreq ? '基础 ' + cpu.baseFreq : null,
      cpu.boostFreq ? '睿频 ' + cpu.boostFreq : null,
    ].filter(Boolean).join(' · ') || '—';
    const isa = cpu.isa || '—';
    const cache = [
      cpu.l1Cache ? 'L1 ' + cpu.l1Cache : null,
      cpu.l2Cache ? 'L2 ' + cpu.l2Cache : null,
      cpu.l3Cache ? 'L3 ' + cpu.l3Cache : null,
    ].filter(Boolean).join(' · ') || '—';
    const cpuDetail = cpu.vendor || cpu.family || cpu.modelNo || cpu.stepping
      ? `${cpu.vendor || ''}${cpu.family ? ' Family ' + cpu.family : ''}${cpu.modelNo ? ' Model ' + cpu.modelNo : ''}${cpu.stepping ? ' Stepping ' + cpu.stepping : ''}`.replace(/\s+/g, ' ').trim()
      : '—';

    const slots = info.memorySlots || [];
    const slotHtml = slots.length
      ? slots.map(sl => `
          <div class="ov-line">
            <span class="ov-k">${esc(sl.slot || '插槽')}</span>
            <b>${esc(sl.size || '—')} · ${esc(sl.type || '—')} · ${esc(sl.speed || '—')}</b>
            ${sl.manufacturer || sl.part ? `
              <div class="ov-sub">
                ${sl.manufacturer ? esc(sl.manufacturer) : ''}
                ${sl.part ? ' · P/N ' + esc(sl.part) : ''}
              </div>` : ''}
          </div>`).join('')
      : `<div class="ov-line"><span class="ov-k">SPD</span>SMBIOS 未暴露内存模组信息（常见于未解锁 BIOS 的笔记本）。总容量 ${ramGb} GB。</div>`;

    const diskRows = (info.disks || []).map(d => {
      const usePct = (d.usePercent != null) ? d.usePercent : null;
      const used = d.usedBytes ? fmtSize(d.usedBytes) : null;
      const sub = (used && d.size)
        ? `已用 ${used} / ${esc(d.size)}${usePct != null ? ' · ' + String(usePct) + ' %' : ''}`
        : `容量 ${esc(d.size)}`;
      const useCss = usePct != null ? Math.max(0, Math.min(100, usePct)) : 0;
      return `<div class="ov-disk">
        <span class="ov-disk-name">${esc(d.name)}</span>
        <span class="ov-disk-model">${esc(d.model)}</span>
        <span class="ov-disk-kind">${esc(d.kind)}</span>
        <span class="ov-disk-size">${esc(d.size)}</span>
        ${usePct != null ? `
          <div class="ov-usebar">
            <div class="ov-usebar-fill" style="width:${useCss}%"></div>
            <span class="ov-usebar-text">${String(usePct)} %</span>
          </div>` : ''}
        <div class="ov-disk-sub">${esc(sub)}</div>
      </div>`;
    }).join('') || '<div class="ov-disk">未检测到磁盘</div>';

    const gpuList = (info.gpus || []).map(g => `<div class="ov-line">${esc(g)}</div>`).join('');

    const board = info.board || {};
    const boardRows = [
      ['厂商', board.boardVendor],
      ['型号', board.boardModel],
      ['版本', board.boardVersion],
      ['芯片组', board.chipset],
      ['机箱类型', info.chassis],
    ].filter(r => r[1]);

    const biosRows = [
      ['厂商', info.biosVendor],
      ['版本', info.biosVersion],
      ['日期', info.biosDate],
      ['固件', info.firmware],
      ['安全启动', info.secureBoot],
    ].filter(r => r[1]);

    const adapters = (info.networkAdapters || []).map(a => `<div class="ov-line">${esc(a)}</div>`).join('')
      || '<div class="ov-line">—</div>';
    const audioHtml = (info.audio || []).map(a => `<div class="ov-line">${esc(a)}</div>`).join('')
      || '<div class="ov-line">—</div>';

    const bat = info.battery;
    const batHtml = bat
      ? `<div class="kv" style="margin-top:8px">
          <div><span>型号</span><b>${esc(bat.model || bat.name || '—')}</b></div>
          <div><span>当前电量</span><b>${bat.currentPercent != null ? String(bat.currentPercent) + ' %' : '—'}${bat.status ? '（' + esc(bat.status) + '）' : ''}</b></div>
          <div><span>设计 / 当前容量</span><b>${bat.designWh != null ? String(bat.designWh) + ' Wh' : '—'}${bat.currentWh != null ? ' / ' + String(bat.currentWh) + ' Wh' : ''}</b></div>
          <div><span>健康度 / 循环</span><b>${esc(bat.health || '—')}${bat.cycles != null ? ' · ' + String(bat.cycles) + ' 次' : ''}</b></div>
          <div><span>技术</span><b>${esc(bat.tech || '—')}</b></div>
        </div>`
      : `<div class="ov-line"><span class="ov-k">电池</span>未检测到电池（台式机 / 白牌机正常）。</div>`;

    // ROM 卡：主盘（总字节最大的那块）用量百分比 + 辅助行显示容量信息
    let romPct = null, romSub = '未检测到磁盘';
    const allDisks = (info.disks || []).slice().sort((a, b) => {
      const ab = (a.totalBytes || 0) - (b.totalBytes || 0); return ab > 0 ? -1 : ab < 0 ? 1 : 0;
    });
    if (allDisks.length) {
      const d0 = allDisks[0];
      if (d0.usePercent != null) romPct = d0.usePercent;
      else if (d0.totalBytes && d0.usedBytes) romPct = (100 * d0.usedBytes / d0.totalBytes);
      const used = d0.usedBytes ? fmtSize(d0.usedBytes) : null;
      romSub = (used && d0.size)
        ? `${used} / ${d0.size} · ${(romPct != null ? String(romPct).slice(0, 6) + ' %' : '')}`
        : `容量 ${d0.size || '—'}`;
    }

    body.innerHTML = `
      <!-- ① 顶部 4 张监测图：CPU / RAM / ROM / GPU — 2×2 网格布局 -->
      <div class="ov-metrics-row" id="ovMetricsRow" style="grid-template-columns:1fr 1fr">
        ${ovMetricCard('cpu', 'CPU 使用率', 'cpu-z',     null, ovMetricBuf.cpu, '核心运行状态')}
        ${ovMetricCard('mem', '内存 使用率', 'ram-speed', null, ovMetricBuf.mem,
            `${ramUsedGb != null ? ramUsedGb + ' / ' + ramGb + ' GB' : ramGb + ' GB 总容量'}`)}
        ${ovMetricCard('rom', 'ROM 磁盘用量', 'disk',    null, ovMetricBuf.rom, romSub)}
        ${ovMetricCard('gpu', 'GPU 占用',    'gpu-fan',   null, ovMetricBuf.gpu, '核心 / 显存 / 温度')}
      </div>

      <!-- ② 硬件信息块：品牌 / CPU / 内存 / 显卡 / 磁盘 SMART / 主板 BIOS / 网络音频电池 / 环境 -->
      <div class="ov-grid">
        <div class="ov-card ov-head-card">
          <div class="ov-brand">${esc(info.vendor)}</div>
          <div class="ov-model">${esc(info.model)}</div>
          <div class="ov-serial">序列号　<span class="ov-serial-val">${esc(info.serial)}</span></div>
          ${info.chassis ? `<div class="ov-sub-row">机型　${esc(info.chassis)}</div>` : ''}
        </div>

        <div class="ov-card">
          <div class="ov-title">处理器（CPU）</div>
          <div class="ov-line"><span class="ov-k">型号</span>${esc(cpu.model)}</div>
          <div class="ov-line"><span class="ov-k">规格</span>${esc(coresStr)}</div>
          <div class="ov-line"><span class="ov-k">频率</span>${esc(freqs)}</div>
          <div class="ov-line"><span class="ov-k">缓存</span>${esc(cache)}</div>
          <div class="ov-line"><span class="ov-k">ISA</span>${esc(isa)}</div>
          <div class="ov-line"><span class="ov-k">家族</span>${esc(cpuDetail)}</div>
        </div>

        <div class="ov-card">
          <div class="ov-title">内存（RAM）· 合计 ${ramGb} GB${
            ramPct != null ? ' · 当前占用 ' + String(ramPct).slice(0, 5) + ' %' : ''}</div>
          ${slotHtml}
        </div>

        <div class="ov-card">
          <div class="ov-title">显卡（GPU）</div>
          ${gpuList || '<div class="ov-line">—</div>'}
        </div>

        <div class="ov-card">
          <div class="ov-title">存储设备</div>
          ${diskRows}
        </div>

        <div class="ov-card">
          <div class="ov-title">主板</div>
          <div class="kv" style="margin-top:6px">
            ${boardRows.length
              ? boardRows.map(r => `<div><span>${esc(r[0])}</span><b>${esc(String(r[1]))}</b></div>`).join('')
              : '<div><span>—</span><b>未识别</b></div>'}
          </div>
          <div class="ov-title" style="margin-top:16px">BIOS / UEFI</div>
          <div class="kv" style="margin-top:6px">
            ${biosRows.length
              ? biosRows.map(r => `<div><span>${esc(r[0])}</span><b>${esc(String(r[1]))}</b></div>`).join('')
              : '<div><span>—</span><b>未识别</b></div>'}
          </div>
        </div>

        <div class="ov-card">
          <div class="ov-title">网络适配器</div>
          ${adapters}
          <div class="ov-title" style="margin-top:16px">音频控制器</div>
          ${audioHtml}
          <div class="ov-title" style="margin-top:16px">电池</div>
          ${batHtml}
        </div>

        <div class="ov-card">
          <div class="ov-title">当前环境</div>
          <div class="ov-line"><span class="ov-k">急救系统</span>KARL'S LIGHT ACCESS ${esc(info.version)}</div>
          <div class="ov-line"><span class="ov-k">内核</span>${esc(info.kernel || '—')}</div>
          <div class="ov-line"><span class="ov-k">时间</span><span id="ovClock">${ovNow()}</span></div>
          <div class="ov-line"><span class="ov-k">救援目标</span>${state.mode === 'linux' ? 'Linux' : 'Windows'}</div>
        </div>
      </div>`;

    const clk = $('#ovClock');
    if (clk) ovClock = setInterval(() => { clk.textContent = ovNow(); }, 1000);

    refreshMetrics(true);
    ovMetricsTimer = setInterval(() => refreshMetrics(false), 2000);
  }).catch(() => {
    stopClock();
    body.innerHTML = '<div class="ov-loading">硬件信息读取失败。点左侧其他分类再回来可重试。</div>';
  });
}

// 概览页轮询 /api/metrics：60 点环形 buffer + 局部替换 4 张卡（CPU/RAM/ROM/GPU），避免整页闪烁。
async function refreshMetrics(firstRun) {
  const m = await get('/api/metrics');
  if (!m) return;

  ovMetricBuf.cpu.push(m.cpuPct);
  if (ovMetricBuf.cpu.length > METRIC_BUF_MAX) ovMetricBuf.cpu.shift();

  const memPct = m.mem ? m.mem.pct : null;
  ovMetricBuf.mem.push(memPct);
  if (ovMetricBuf.mem.length > METRIC_BUF_MAX) ovMetricBuf.mem.shift();

  let gpuPct = null; let gpuSub = '核心 / 显存 / 温度';
  if (m.gpus && m.gpus.length) {
    const g0 = m.gpus[0];
    gpuPct = g0.corePct;
    const parts = [];
    if (g0.corePct != null) parts.push('核心 ' + String(g0.corePct) + ' %');
    if (g0.memPct  != null) parts.push('显存 ' + String(g0.memPct) + ' %');
    if (g0.tempC   != null) parts.push(String(g0.tempC) + ' °C');
    if (parts.length) gpuSub = parts.join(' · ');
  }
  ovMetricBuf.gpu.push(gpuPct);
  if (ovMetricBuf.gpu.length > METRIC_BUF_MAX) ovMetricBuf.gpu.shift();

  // ROM 磁盘用量：所有物理盘取字节总量最大的那一块，显示 used/total
  let romPct = null, romSub = '—';
  const disksSorted = (m.disks || []).slice().sort((a, b) => {
    const ab = (a.totalBytes || 0) - (b.totalBytes || 0); return ab > 0 ? -1 : ab < 0 ? 1 : 0;
  });
  if (disksSorted.length) {
    const d0 = disksSorted[0];
    if (d0.usePercent != null) romPct = d0.usePercent;
    else if (d0.totalBytes && d0.usedBytes) romPct = (100 * d0.usedBytes / d0.totalBytes);
    const used = d0.usedBytes ? fmtSize(d0.usedBytes) : null;
    romSub = (used && d0.totalBytes)
      ? `${used} / ${fmtSize(d0.totalBytes)} · ${(romPct != null ? String(romPct).slice(0, 6) + ' %' : '')}`
      : `容量 ${d0.totalBytes ? fmtSize(d0.totalBytes) : '—'}`;
  }
  ovMetricBuf.rom.push(romPct);
  if (ovMetricBuf.rom.length > METRIC_BUF_MAX) ovMetricBuf.rom.shift();

  const row = $('#ovMetricsRow');
  if (!row) return;

  const memUsedGb  = m.mem && m.mem.used  ? (m.mem.used  / (1024 ** 3)).toFixed(2) : null;
  const memTotalGb = m.mem && m.mem.total ? (m.mem.total / (1024 ** 3)).toFixed(1).replace(/\.0$/, '') : null;

  const cards = {
    cpu: ovMetricCard('cpu', 'CPU 使用率',  'cpu-z',     m.cpuPct, ovMetricBuf.cpu,
                       fmtPct(m.cpuPct)),
    mem: ovMetricCard('mem', '内存 使用率', 'ram-speed', memPct,   ovMetricBuf.mem,
                       (memUsedGb != null && memTotalGb ? memUsedGb + ' / ' + memTotalGb + ' GB' : '—')),
    rom: ovMetricCard('rom', 'ROM 磁盘用量', 'disk',    romPct,   ovMetricBuf.rom, romSub),
    gpu: ovMetricCard('gpu', 'GPU 占用',    'gpu-fan',   gpuPct,   ovMetricBuf.gpu, gpuSub),
  };
  Object.keys(cards).forEach(k => {
    const el = row.querySelector(`.ov-metric-card[data-ovm="${k}"]`);
    if (!el) return;
    el.outerHTML = cards[k];
  });
}

function showHelp(task, extra) {
  const h = task.help || {};
  $('#helpBody').innerHTML = `
    <span class="ht">${esc(h.title || task.title)}</span>
    ${esc(h.body || task.desc)}
    ${h.note ? `<div class="hnote">${esc(h.note)}</div>` : ''}
    ${h.warn ? `<div class="hwarn">${esc(h.warn)}</div>` : ''}
    ${extra ? `<div class="hnote">${esc(extra)}</div>` : ''}`;
}

function showHelpRaw(title, body, kind) {
  $('#helpBody').innerHTML =
    `<span class="ht">${esc(title)}</span><div class="${kind || 'hnote'}">${esc(body)}</div>`;
}

/* ═══════════════════════════════════════════════════════════════
   文件救援向导
   ═══════════════════════════════════════════════════════════════ */

const WIZ_STEPS = ['选择源分区', '挑选文件', '选择保存位置', '拷贝'];

function openRescueFiles() {
  state.wiz = {
    step: 1,
    srcDev: null,
    cwd: '/',
    entries: [],
    browseErr: null,
    loading: false,
    picked: new Set(),
    dstDev: null,
    subdir: 'KLA-救援文件',
    jobId: null,
    job: null,
    poll: null,
    submitErr: null
  };
  $('#backBtn').hidden = false;
  renderWizard();
}

// 离开向导前的守门。作业还在跑的时候不能一走了之——
// 后端是全局单作业，人走了作业还占着，回来会看到"已有作业在运行"却不知道是什么。
function leaveWizard() {
  const w = state.wiz;
  if (!w) return true;
  if (w.job && ['pending', 'scanning', 'running'].includes(w.job.state)) {
    showHelpRaw('拷贝正在进行', '请先等它结束，或按「取消拷贝」中止后再离开。', 'hwarn');
    return false;
  }
  stopPolling();
  return true;
}

function stopPolling() {
  if (state.wiz && state.wiz.poll) {
    clearInterval(state.wiz.poll);
    state.wiz.poll = null;
  }
}

function renderWizard() {
  const w = state.wiz;
  // 分发器：每种向导走自己的渲染器，文件救援走原流程（无 kind）。
  const WIZ_RENDERERS = {
    factory: renderRestoreWizard,
    restore: renderRestoreWizard,
    launch: renderLaunchWizard,
    datarec: renderDatarecWizard,
    winacct: renderWinacctWizard,
    bootfix: renderBootfixWizard,
    hwdiag: renderHwdiagWizard,
    memtest: renderMemtestWizard,
    share: renderShareWizard,
    gpudiag: renderGpudiagWizard,
    ssh: renderSshWizard,
    remote: renderRemoteWizard,
    osinstall: renderOsinstallWizard,
    proxy: renderProxyWizard,
    linuxfix: renderLinuxfixWizard,
    toolbox: renderToolboxWizard,
  };
  if (w && w.kind && WIZ_RENDERERS[w.kind]) {
    WIZ_RENDERERS[w.kind]();
    return;
  }
  $('#catTitle').textContent = '文件救援';
  $('#catDesc').textContent = '源分区全程只读挂载，不会对它写入任何东西。';

  const steps = WIZ_STEPS.map((s, i) => {
    const n = i + 1;
    const cls = n === w.step ? 'active' : (n < w.step ? 'done' : '');
    return `<div class="step ${cls}"><span class="num">${n < w.step ? '✓' : n}</span>${esc(s)}</div>`;
  }).join('');

  $('#contentBody').innerHTML =
    `<div class="wizard">
       <div class="steps">${steps}</div>
       <div class="wiz-body" id="wizBody"></div>
       <div class="wiz-foot" id="wizFoot"></div>
     </div>`;

  ({ 1: stepSource, 2: stepBrowse, 3: stepDest, 4: stepRun })[w.step]();
}

function foot(html) { $('#wizFoot').innerHTML = html; }

function goStep(n) {
  state.wiz.step = n;
  state.wiz.submitErr = null;
  renderWizard();
}

// ── 步骤 1：选源分区 ────────────────────────────────────────────
function stepSource() {
  const w = state.wiz;
  const vols = allVolumes();

  if (!vols.length) {
    $('#wizBody').innerHTML =
      `<div class="errbox">没有检测到任何分区。如果硬盘确实装着，说明救援系统缺少这块硬盘控制器的驱动。</div>`;
    foot(`<div class="spacer"></div><button class="btn" id="wCancel">返回</button>`);
    $('#wCancel').onclick = renderHome;
    return;
  }

  $('#wizBody').innerHTML =
    `<div class="wiz-hint">选择要从中取出文件的分区。通常是装着 Windows 的那个 NTFS 卷。</div>
     ${renderVolList(vols, v => {
       if (!v.fstype) return { disabled: true, tag: '未格式化', tagKind: 'err' };
       if (v.isKlaRescue) return { tag: '救援分区', tagKind: 'warn' };
       if (v.parttype === ESP_GUID) return { tag: 'EFI 系统分区', tagKind: 'warn' };
       if (v.isWindows) return { tag: 'Windows 数据' };
       return {};
     }, w.srcDev, 'src')}`;

  bindVolPick('src', name => { w.srcDev = name; w.picked.clear(); w.cwd = '/'; stepSource(); });

  foot(`<div class="summary">${w.srcDev ? '已选：' + esc(w.srcDev) : '尚未选择'}</div>
        <div class="spacer"></div>
        <button class="btn primary" id="wNext" ${w.srcDev ? '' : 'disabled'}>下一步</button>`);
  const nx = $('#wNext');
  if (nx) nx.onclick = () => { goStep(2); loadDir('/'); };
}

function renderVolList(vols, decorate, selected, kind) {
  // 按物理盘分组：救援时"这两个分区在同一块盘上"是关键信息——
  // 盘要是正在坏，拷到同一块盘上等于没救。
  const byDisk = {};
  vols.forEach(v => { (byDisk[v.diskName] = byDisk[v.diskName] || []).push(v); });

  return '<div class="vol-list">' + Object.keys(byDisk).map(dn => {
    const head = `<div class="vol-disk">${esc(dn)} · ${esc(byDisk[dn][0].diskModel)}</div>`;
    const rows = byDisk[dn].map(v => {
      const d = decorate(v) || {};
      const on = selected === v.name;
      return `<div class="vol ${d.disabled ? 'disabled' : ''} ${on ? 'sel' : ''}"
                   data-kind="${kind}" data-vol="${esc(v.name)}"
                   ${d.disabled ? 'data-off="1"' : ''}
                   ${d.reason ? `title="${esc(d.reason)}"` : ''}>
        ${icon('disk', 'ico32')}
        <div>
          <div class="vol-name">${esc(volLabel(v))} <span style="font-weight:normal;opacity:.6">(${esc(v.name)})</span></div>
          <div class="vol-meta">${esc(v.fstype || '无文件系统')} · ${fmtSize(v.sizeBytes)}${
            d.reason ? ' · ' + esc(d.reason) : ''}</div>
        </div>
        ${d.tag ? `<span class="vol-tag ${d.tagKind || ''}">${esc(d.tag)}</span>` : ''}
      </div>`;
    }).join('');
    return head + rows;
  }).join('') + '</div>';
}

function bindVolPick(kind, onPick) {
  document.querySelectorAll(`.vol[data-kind="${kind}"]`).forEach(el => {
    if (el.dataset.off) return;
    el.onclick = () => onPick(el.dataset.vol);
  });
}

// ── 步骤 2：浏览 + 多选 ─────────────────────────────────────────
async function loadDir(path) {
  const w = state.wiz;
  w.loading = true; w.browseErr = null;
  renderBrowse();

  const url = `/api/browse?dev=${encodeURIComponent(w.srcDev)}&path=${encodeURIComponent(path)}`;
  const { status, data } = await call(url);
  // 用户可能在请求飞行途中又点了别处，回来的是过期响应，丢掉
  if (!state.wiz || state.wiz !== w || w.step !== 2) return;

  w.loading = false;
  if (status === 200 && data.ok) {
    w.cwd = path;
    w.entries = data.entries || [];
  } else {
    w.browseErr = data.reason || `读取失败（HTTP ${status}）`;
    w.entries = [];
  }
  renderBrowse();
}

function stepBrowse() { renderBrowse(); }

function renderBrowse() {
  const w = state.wiz;

  // 面包屑：根那一节显示分区名，比一个光秃秃的 / 好认
  const segs = w.cwd.split('/').filter(Boolean);
  let acc = '';
  const crumbs = [`<span class="crumb" data-go="/">${esc(w.srcDev)}</span>`].concat(
    segs.map((s, i) => {
      acc = joinPath(acc || '/', s);
      const last = i === segs.length - 1;
      return `<span class="sep">›</span><span class="crumb ${last ? 'cur' : ''}"
                data-go="${esc(acc)}">${esc(s)}</span>`;
    })
  ).join('');

  let body;
  if (w.loading) {
    body = `<div class="empty">正在读取…</div>`;
  } else if (w.browseErr) {
    body = `<div class="errbox"><b>无法读取这个目录</b><br>${esc(w.browseErr)}</div>`;
  } else if (!w.entries.length) {
    body = `<div class="empty">这个目录是空的</div>`;
  } else {
    body = w.entries.map(e => {
      const p = joinPath(w.cwd, e.name);
      const anc = coveredByAncestor(p, w.picked);
      const on = w.picked.has(p) || !!anc;
      return `<div class="frow ${e.isDir ? 'dir' : ''} ${on ? 'picked' : ''}" data-p="${esc(p)}">
        <input type="checkbox" ${on ? 'checked' : ''} ${anc ? 'disabled' : ''}
               ${anc ? `title="已随上级目录 ${esc(anc)} 一起选中"` : ''}>
        ${icon(e.isDir ? 'folder' : 'doc', 'ico16')}
        <span class="fname" ${e.isDir ? 'data-cd="1"' : ''}>${esc(e.name)}</span>
        <span class="fsize">${e.isDir ? '' : fmtSize(e.sizeBytes)}</span>
      </div>`;
    }).join('');
  }

  const picked = [...w.picked];
  const pickedBar = picked.length ? `
    <div class="picked-bar">
      <div class="picked-head">已选 <b>${picked.length}</b> 项
        <span class="clear" id="wClear">全部清除</span></div>
      <div class="picked-items">${picked.map(p =>
        `<div class="picked-item"><span class="x" data-un="${esc(p)}">✕</span>
         <span class="p">${esc(p)}</span></div>`).join('')}</div>
    </div>` : '';

  $('#wizBody').innerHTML =
    `<div class="brow">
       <div class="crumbs">${crumbs}</div>
       <div class="flist">${body}</div>
       ${pickedBar}
     </div>`;

  // 面包屑跳转
  $('#wizBody').querySelectorAll('.crumb[data-go]').forEach(el => {
    if (el.classList.contains('cur')) return;
    el.onclick = () => loadDir(el.dataset.go);
  });
  // 双列点击：勾选框管选中，文件夹名管进入。两者互不干扰，
  // 否则"想勾选却进了目录"是这类界面最常见的挫败点。
  $('#wizBody').querySelectorAll('.frow').forEach(el => {
    const p = el.dataset.p;
    const cb = el.querySelector('input');
    if (cb && !cb.disabled) cb.onchange = () => { togglePick(p, cb.checked); renderBrowse(); };
    const nm = el.querySelector('[data-cd]');
    if (nm) nm.onclick = () => loadDir(p);
  });
  $('#wizBody').querySelectorAll('[data-un]').forEach(el => {
    el.onclick = () => { w.picked.delete(el.dataset.un); renderBrowse(); };
  });
  const clr = $('#wClear');
  if (clr) clr.onclick = () => { w.picked.clear(); renderBrowse(); };

  foot(`<button class="btn" id="wBack">上一步</button>
        <div class="summary">${picked.length ? `已选 ${picked.length} 项` : '勾选要救出来的文件或文件夹'}</div>
        <div class="spacer"></div>
        <button class="btn primary" id="wNext" ${picked.length ? '' : 'disabled'}>下一步</button>`);
  $('#wBack').onclick = () => goStep(1);
  const nx = $('#wNext');
  if (nx) nx.onclick = () => goStep(3);
}

function togglePick(p, on) {
  const s = state.wiz.picked;
  if (!on) { s.delete(p); return; }
  // 选中一个目录时，把它下面已经单独选中的项去掉。
  // 留着不会拷错文件，但后端会把同一份数据 walk 两遍，
  // 进度条的分母因此虚高，用户看到的"总大小"是假的。
  for (const q of [...s]) {
    if (q !== p && (q + '/').startsWith(p + '/')) s.delete(q);
  }
  s.add(p);
}

// ── 步骤 3：选目标 ──────────────────────────────────────────────
function stepDest() {
  const w = state.wiz;
  const src = allVolumes().find(v => v.name === w.srcDev) || {};
  const vols = allVolumes();

  $('#wizBody').innerHTML =
    `<div class="wiz-hint">选择把文件保存到哪里。建议选一块<b>外接</b>的 U 盘或移动硬盘。</div>
     ${renderVolList(vols, v => {
       if (v.name === w.srcDev)   return { disabled: true, tag: '源分区', tagKind: 'err', reason: '不能拷到自己身上' };
       if (!v.fstype)             return { disabled: true, tag: '未格式化', tagKind: 'err', reason: '没有文件系统，无法写入' };
       if (v.parttype === ESP_GUID) return { disabled: true, tag: 'EFI 系统分区', tagKind: 'err', reason: '写这里会破坏引导' };
       if (v.isKlaRescue)         return { disabled: true, tag: '救援分区', tagKind: 'err', reason: '写满会让救援环境本身失效' };
       // 同一块物理盘不禁止——盘没坏的时候这是完全合理的操作——但必须说清楚
       if (v.diskName === src.diskName) return { tag: '同一块硬盘', tagKind: 'warn', reason: '源盘若有物理故障，拷到这里等于没救' };
       return {};
     }, w.dstDev, 'dst')}
     <div style="margin-top:18px">
       <div class="wiz-hint" style="margin-bottom:6px">保存到目标卷的哪个文件夹：</div>
       <input id="wSub" value="${esc(w.subdir)}" spellcheck="false"
              style="font:inherit;font-size:13px;padding:7px 10px;width:320px;
                     border:1px solid var(--line);border-radius:2px">
       <div class="wiz-hint" style="margin-top:5px">
         文件夹不存在会自动创建。选中项的目录名本身会保留在里面。</div>
     </div>`;

  bindVolPick('dst', name => { w.dstDev = name; stepDest(); });
  $('#wSub').oninput = e => { w.subdir = e.target.value; };

  const ready = !!w.dstDev;
  foot(`<button class="btn" id="wBack">上一步</button>
        <div class="summary">${esc(w.srcDev)} → ${w.dstDev ? esc(w.dstDev) : '？'} · ${w.picked.size} 项</div>
        <div class="spacer"></div>
        <button class="btn primary" id="wGo" ${ready ? '' : 'disabled'}>开始拷贝</button>`);
  $('#wBack').onclick = () => goStep(2);
  const go = $('#wGo');
  if (go) go.onclick = submitCopy;
}

// ── 步骤 4：执行 ────────────────────────────────────────────────
async function submitCopy() {
  const w = state.wiz;
  const sub = cleanSubdir(w.subdir) || 'KLA-救援文件';
  w.subdir = sub;
  goStep(4);

  const { status, data } = await call('/api/jobs', 'POST', {
    type: 'copyout',
    args: {
      src_dev: w.srcDev,
      src_paths: [...w.picked],
      dst_dev: w.dstDev,
      dst_subdir: sub
    }
  });

  if (status !== 200 || !data.ok) {
    w.submitErr = data.reason || `提交失败（HTTP ${status}）`;
    renderWizard();
    return;
  }
  w.jobId = data.job.id;
  w.job = data.job;
  renderRun();
  // 500ms：肉眼觉得连续，又不会把一个只有 128 MB 内存的救援环境
  // 拿去伺候轮询。作业结束立刻停表。
  w.poll = setInterval(pollJob, 500);
}

async function pollJob() {
  const w = state.wiz;
  if (!w || !w.jobId) { stopPolling(); return; }
  const { status, data } = await call('/api/jobs/' + encodeURIComponent(w.jobId));
  if (!state.wiz || state.wiz !== w) return;
  if (status === 200) {
    w.job = data;
    if (['done', 'failed', 'cancelled'].includes(data.state)) stopPolling();
  }
  // 文件救援最后一步是 4，新向导最后一步是 w.steps.length。
  // 不论哪种，调 renderWizard() 让 dispatcher 决定渲染谁——
  // 文件救援走原流程（→stepRun→renderRun），factory/restore 走 stepRunGeneric。
  const lastStep = (w.steps && w.steps.length) || 4;
  if (w.step === lastStep) renderWizard();
}

function stepRun() { renderRun(); }

function renderRun() {
  const w = state.wiz;

  if (w.submitErr) {
    $('#wizBody').innerHTML = `<div class="errbox"><b>没能开始拷贝</b><br>${esc(w.submitErr)}</div>`;
    foot(`<button class="btn" id="wBack">上一步</button><div class="spacer"></div>
          <button class="btn" id="wHome">回到菜单</button>`);
    $('#wBack').onclick = () => goStep(3);
    $('#wHome').onclick = renderHome;
    return;
  }

  const j = w.job;
  if (!j) {
    $('#wizBody').innerHTML = `<div class="empty">正在提交作业…</div>`;
    foot('');
    return;
  }

  const running = ['pending', 'scanning', 'running'].includes(j.state);
  // 扫描阶段还不知道总量，此时给确定的百分比等于撒谎，用不确定态的滚动条
  const scanning = j.state === 'scanning' || (running && !j.totalBytes);
  const pct = j.totalBytes ? Math.min(100, 100 * j.doneBytes / j.totalBytes) : 0;

  let barCls = '';
  if (scanning) barCls = 'indet';
  else if (j.state === 'done') barCls = j.errorCount > 0 ? 'err' : 'ok';
  else if (j.state === 'failed' || j.state === 'cancelled') barCls = 'err';

  const title = {
    pending: '正在准备…', scanning: '正在统计数据量…', running: '正在拷贝',
    done: j.errorCount > 0 ? '拷贝结束，但有文件没能读出来' : '拷贝完成',
    failed: '拷贝失败', cancelled: '已取消'
  }[j.state] || j.state;

  const errs = (j.errors || []).length ? `
    <div class="errlist">
      <div class="eh">${j.errorCount} 个文件没能拷出来${
        j.errorCount > j.errors.length ? `（只列出前 ${j.errors.length} 个）` : ''}</div>
      ${j.errors.map(e =>
        `<div class="er"><div class="ep">${esc(e.path)}</div><div class="em">${esc(e.error)}</div></div>`
      ).join('')}
    </div>` : '';

  $('#wizBody').innerHTML = `
    <div class="prog-wrap">
      <div class="prog-title">${esc(title)}</div>
      <div class="prog-cur">${esc(j.current || j.message || '')}</div>
      <div class="bar ${barCls}"><i style="width:${scanning ? 35 : pct}%"></i></div>
      <div class="prog-nums">
        <div>已拷贝<b>${fmtSize(j.doneBytes)}</b>${
          j.totalBytes ? `<span style="font-size:11px"> / ${fmtSize(j.totalBytes)}</span>` : ''}</div>
        <div>文件<b>${j.doneFiles || 0}${j.totalFiles ? ' / ' + j.totalFiles : ''}</b></div>
        <div>用时<b>${fmtTime(j.elapsedSeconds)}</b></div>
        ${j.errorCount ? `<div>失败<b style="color:var(--ksl-red)">${j.errorCount}</b></div>` : ''}
      </div>
      ${(j.state === 'failed') ? `<div class="errbox" style="margin-top:16px">${esc(j.message)}</div>` : ''}
      ${(j.state === 'done') ? `<div class="wiz-hint" style="margin-top:14px">
          已保存到目标卷的 <b>${esc(w.subdir)}</b> 文件夹。拔盘前请先点「重启」或「关机」，
          让系统把缓存真正写进去。</div>` : ''}
      ${errs}
    </div>`;

  if (running) {
    foot(`<div class="summary">${esc(w.srcDev)} → ${esc(w.dstDev)}</div>
          <div class="spacer"></div>
          <button class="btn" id="wCancelJob">取消拷贝</button>`);
    $('#wCancelJob').onclick = async () => {
      $('#wCancelJob').disabled = true;
      $('#wCancelJob').textContent = '正在停止…';
      await call('/api/jobs/' + encodeURIComponent(w.jobId) + '/cancel', 'POST');
    };
  } else {
    foot(`<button class="btn" id="wAgain">再救一批</button>
          <button class="btn ghost" id="wVerify">浏览目标卷</button>
          <div class="spacer"></div>
          <button class="btn primary" id="wHome">完成</button>`);
    $('#wAgain').onclick = () => {
      w.picked.clear(); w.job = null; w.jobId = null; w.cwd = '/';
      goStep(2); loadDir('/');
    };
    // 拷完能当场核对，比让用户拔了盘回 Windows 里才发现少东西强得多
    $('#wVerify').onclick = () => {
      w.srcDev = w.dstDev; w.picked.clear(); w.job = null; w.jobId = null;
      goStep(2); loadDir('/' + w.subdir);
    };
    $('#wHome').onclick = renderHome;
  }
}

/* ═══════════════════════════════════════════════════════════════
   恢复出厂 / 从备份还原 向导

   与文件救援共用 leaveWizard / stopPolling / pollJob / foot / goStep 这些
   通用件，但状态对象 state.wiz 的字段不同——单开一组 step* 函数，避免和
   文件救援的 stepSource/stepBrowse/stepDest 撞名。

   设计要点：
   - factory 走 3 步：选目标 → 确认 → 执行
   - restore 走 4 步：读 index → 选还原点 → 选目标 → 确认（restoreStepConfirm）
     实际渲染里 step 2 「选还原点」复用了 step 1 的 UI——step 1 是时间线选择，
     选完点下一步直接跳 step 3。step 2 在向导条上保留是为了让用户看到「选还原点」
     这一项被画上勾。
   - pending-restore.json：Windows 侧「还原」按钮写下的交接文件。读到就自动
     预选那个 entry，并把用户直接推进到「确认覆盖」页。
   ═══════════════════════════════════════════════════════════════ */

const WIZ_FACTORY_STEPS = ['选择目标分区', '确认覆盖', '执行还原'];
const WIZ_RESTORE_STEPS = ['读取备份索引', '选择还原点', '选择目标分区', '确认覆盖'];

function openFactory() {
  state.wiz = {
    kind: 'factory',
    step: 1,
    steps: WIZ_FACTORY_STEPS,
    targetDev: null,
    jobId: null,
    job: null,
    poll: null,
    submitErr: null,
    confirmed: false,
  };
  $('#backBtn').hidden = false;
  renderWizard();
}

function openRestore() {
  state.wiz = {
    kind: 'restore',
    step: 1,
    steps: WIZ_RESTORE_STEPS,
    indexData: null,
    indexErr: null,
    entryId: null,
    targetDev: null,
    jobId: null,
    job: null,
    poll: null,
    submitErr: null,
    confirmed: false,
  };
  $('#backBtn').hidden = false;
  renderWizard();
  loadBackupIndex();
}

async function loadBackupIndex() {
  const w = state.wiz;
  if (!w || w.kind !== 'restore') return;
  const { status, data } = await call('/api/backups');
  if (!state.wiz || state.wiz !== w || w.kind !== 'restore') return;
  if (status === 200 && data.found) {
    w.indexData = data;
    // pending-restore：Windows 写下的交接文件，存在则自动预选 entry
    const pr = data.pendingRestore;
    if (pr && pr.entryId) {
      w.entryId = pr.entryId;
    }
  } else {
    w.indexErr = data.reason || `读取失败（HTTP ${status}）`;
  }
  if (w.step === 1 || w.step === 2) renderWizard();
}

function renderRestoreWizard() {
  const w = state.wiz;
  if (!w) return;
  $('#catTitle').textContent = w.kind === 'factory' ? '恢复出厂' : '从备份还原';
  $('#catDesc').textContent = w.kind === 'factory'
    ? '用出厂母盘覆盖系统分区，回到部署时的干净状态。'
    : '在备份时间线上选择一个还原点回滚系统。';

  const steps = w.steps.map((s, i) => {
    const n = i + 1;
    const cls = n === w.step ? 'active' : (n < w.step ? 'done' : '');
    return `<div class="step ${cls}"><span class="num">${n < w.step ? '✓' : n}</span>${esc(s)}</div>`;
  }).join('');

  $('#contentBody').innerHTML =
    `<div class="wizard">
       <div class="steps">${steps}</div>
       <div class="wiz-body" id="wizBody"></div>
       <div class="wiz-foot" id="wizFoot"></div>
     </div>`;

  const fns = {
    factory: { 1: factoryStepTarget, 2: factoryStepConfirm, 3: stepRunGeneric },
    restore: { 1: restoreStepIndex, 2: restoreStepPick, 3: restoreStepTarget, 4: restoreStepConfirm },
  }[w.kind];
  fns[w.step]();
}

// ── 恢复出厂 步骤 ────────────────────────────────────────────
function factoryStepTarget() {
  const w = state.wiz;
  const vols = allVolumes();

  if (!vols.length) {
    $('#wizBody').innerHTML =
      `<div class="errbox">没有检测到任何分区。如果硬盘确实装着，说明救援系统缺少这块硬盘控制器的驱动。</div>`;
    foot(`<div class="spacer"></div><button class="btn" id="wCancel">返回</button>`);
    $('#wCancel').onclick = renderHome;
    return;
  }

  $('#wizBody').innerHTML =
    `<div class="wiz-hint">选择要被覆盖的<b>系统分区</b>。通常是装着 Windows 的那个 NTFS 卷。
     <span class="hwarn" style="display:block;margin-top:8px">
       目标分区会被完全清空重写。如果上面有要保留的数据，请先点「返回」改用「文件救援」救出来。</span></div>
     ${renderVolList(vols, v => {
       if (!v.fstype) return { disabled: true, tag: '未格式化', tagKind: 'err' };
       if (v.isKlaRescue) return { disabled: true, tag: '救援分区', tagKind: 'err', reason: '救援系统自己住的分区，不能覆盖' };
       if (v.parttype === ESP_GUID) return { disabled: true, tag: 'EFI 系统分区', tagKind: 'err', reason: 'ESP 不在还原范围内（会被单独重建）' };
       if (v.isWindows) return { tag: 'Windows 系统卷' };
       return { tag: '非系统卷', tagKind: 'warn', reason: '选这里会把目标当前内容当作非系统卷覆盖' };
     }, w.targetDev, 'target')}`;

  bindVolPick('target', name => { w.targetDev = name; factoryStepTarget(); });

  foot(`<button class="btn" id="wBack">返回</button>
        <div class="summary">${w.targetDev ? '已选：' + esc(w.targetDev) : '尚未选择'}</div>
        <div class="spacer"></div>
        <button class="btn primary" id="wNext" ${w.targetDev ? '' : 'disabled'}>下一步</button>`);
  $('#wBack').onclick = renderHome;
  $('#wNext').onclick = () => goStep(2);
}

function factoryStepConfirm() {
  const w = state.wiz;
  const vol = allVolumes().find(v => v.name === w.targetDev) || {};
  $('#wizBody').innerHTML =
    `<div class="wiz-hint">请确认下面的操作：</div>
     <div class="confirm-box">
       <div class="conf-row"><span>操作</span><b>恢复出厂</b></div>
       <div class="conf-row"><span>目标分区</span><b>${esc(volLabel(vol))} (${esc(w.targetDev)})</b></div>
       <div class="conf-row"><span>目标大小</span><b>${fmtSize(vol.sizeBytes)}</b></div>
       <div class="conf-row"><span>来源</span><b>D:\\KLA\\factory.wim</b></div>
       <div class="conf-row"><span>系统分区现状</span><b style="color:var(--ksl-red)">将被完全清空</b></div>
       <div class="conf-row"><span>引导分区</span><b>EFI\\Microsoft\\ 重建，EFI\\KLA\\ 保留</b></div>
     </div>
     <div class="hwarn" style="margin-top:14px">
       <label style="display:flex;align-items:center;gap:8px;cursor:pointer">
         <input type="checkbox" id="wAck" style="width:auto">
         <span>我已了解目标分区上的所有数据将被永久删除，且已用「文件救援」导出需要保留的内容</span>
       </label>
     </div>`;

  foot(`<button class="btn" id="wBack">上一步</button>
        <div class="spacer"></div>
        <button class="btn primary" id="wGo" disabled>开始恢复出厂</button>`);
  $('#wBack').onclick = () => goStep(1);
  const ack = $('#wAck');
  const go = $('#wGo');
  ack.onchange = () => { go.disabled = !ack.checked; w.confirmed = ack.checked; };
  go.onclick = submitFactory;
}

// ── 从备份还原 步骤 ────────────────────────────────────────────
function restoreStepIndex() {
  const w = state.wiz;
  if (w.indexErr) {
    $('#wizBody').innerHTML =
      `<div class="errbox"><b>读取备份索引失败</b><br>${esc(w.indexErr)}
       <br><br>已扫描全部硬盘的所有 NTFS 卷（C: 到 Z: 都会自动查找 KLA\\ 目录）。
       确认 Windows 主程序已在某个数据卷的 KLA\\ 下创建过备份。如果是移动硬盘/移动硬盘盒，
       请确认它已接好并且没被 BitLocker 加密（加密卷救援系统读不了）。</div>`;
    foot(`<button class="btn" id="wCancel">返回</button>`);
    $('#wCancel').onclick = renderHome;
    return;
  }
  if (!w.indexData) {
    $('#wizBody').innerHTML = `<div class="empty">正在扫描所有硬盘上的 KLA 备份目录…</div>`;
    foot('');
    return;
  }

  const chains = (w.indexData.index && w.indexData.index.chains) || [];
  if (!chains.length) {
    const sp = esc(w.indexData.storePath || '?');
    const dev = esc(w.indexData.device || '?');
    const tried = (w.indexData.tried && w.indexData.tried.length)
      ? `<br>已尝试的卷：${esc(w.indexData.tried.join('；'))}` : '';
    $('#wizBody').innerHTML =
      `<div class="errbox">所有已挂载硬盘的 KLA\\index.json 里都没有备份链。
       <br>当前选中的存储位置：${dev} 上的 ${sp}
       ${tried}
       <br><br>请先在 Windows 主程序里创建一次备份；如果备份在移动硬盘上，
       请接好后点「重新扫描」。</div>`;
    foot(`<button class="btn" id="wCancel">返回</button>
          <button class="btn" id="wRescan">重新扫描</button>`);
    $('#wCancel').onclick = renderHome;
    $('#wRescan').onclick = () => { w.indexErr = null; w.indexData = null; renderWizard(); loadBackupIndex(); };
    return;
  }

  // pending-restore 已预选 entry：直接推进到 step 3（选目标）
  if (w.entryId) {
    goStep(3);
    return;
  }

  const lines = [];
  if (w.indexData.mergedStores && w.indexData.mergedStores.length) {
    lines.push(`<div class="wiz-hint">已合并以下磁盘上的备份链：${esc(w.indexData.mergedStores.join('、'))}</div>`);
  }
  chains.forEach(c => {
    lines.push(`<div class="chain-head">${esc(c.scopeLabel || c.scope || '备份')} · 链 ${esc(c.chainId)}</div>`);
    (c.entries || []).forEach(e => {
      const on = w.entryId === e.id;
      const dt = e.createdUtc ? new Date(e.createdUtc).toLocaleString() : '?';
      lines.push(`<div class="timeline-row ${on ? 'sel' : ''}" data-eid="${esc(e.id)}">
        ${icon(e.type === 'full' ? 'disk' : 'clock', 'ico16')}
        <div>
          <div class="tl-name">${esc(dt)} ${e.isFactoryBaseline ? '· 出厂基准' : ''}</div>
          <div class="tl-meta">${esc(e.type || '?')} · ${fmtSize(e.sizeBytes)}${e.osCaption ? ' · ' + esc(e.osCaption) : ''}</div>
        </div>
      </div>`);
    });
  });

  $('#wizBody').innerHTML = `<div class="wiz-hint">选择一个还原点。增量还原会自动按链依次应用其前的所有镜像。</div>
     <div class="timeline">${lines.join('')}</div>`;

  $('#wizBody').querySelectorAll('.timeline-row').forEach(el => {
    el.onclick = () => { w.entryId = el.dataset.eid; restoreStepIndex(); };
  });

  foot(`<button class="btn" id="wBack">返回</button>
        <div class="summary">${w.entryId ? '已选：' + esc(w.entryId) : '尚未选择'}</div>
        <div class="spacer"></div>
        <button class="btn primary" id="wNext" ${w.entryId ? '' : 'disabled'}>下一步</button>`);
  $('#wBack').onclick = renderHome;
  $('#wNext').onclick = () => goStep(3);
}

function restoreStepPick() {
  // 向导条上的过渡步：还原点已在 step 1 选定，这里只是把用户引到「选目标」
  const w = state.wiz;
  if (!w.entryId) { goStep(1); return; }
  goStep(3);
}

function restoreStepTarget() {
  const w = state.wiz;
  const vols = allVolumes();

  if (!vols.length) {
    $('#wizBody').innerHTML =
      `<div class="errbox">没有检测到任何分区。</div>`;
    foot(`<div class="spacer"></div><button class="btn" id="wCancel">返回</button>`);
    $('#wCancel').onclick = renderHome;
    return;
  }

  $('#wizBody').innerHTML =
    `<div class="wiz-hint">选择要被覆盖的<b>系统分区</b>。
     <span class="hwarn" style="display:block;margin-top:8px">
       目标分区会被完全清空重写。请确认选中的就是当初做备份的源分区。</span></div>
     ${renderVolList(vols, v => {
       if (!v.fstype) return { disabled: true, tag: '未格式化', tagKind: 'err' };
       if (v.isKlaRescue) return { disabled: true, tag: '救援分区', tagKind: 'err', reason: '救援系统自己住的分区' };
       if (v.parttype === ESP_GUID) return { disabled: true, tag: 'EFI 系统分区', tagKind: 'err' };
       if (v.isWindows) return { tag: 'Windows 系统卷' };
       return { tag: '非系统卷', tagKind: 'warn' };
     }, w.targetDev, 'target')}`;

  bindVolPick('target', name => { w.targetDev = name; restoreStepTarget(); });

  foot(`<button class="btn" id="wBack">上一步</button>
        <div class="summary">${w.targetDev ? '已选：' + esc(w.targetDev) : '尚未选择'}</div>
        <div class="spacer"></div>
        <button class="btn primary" id="wNext" ${w.targetDev ? '' : 'disabled'}>下一步</button>`);
  $('#wBack').onclick = () => goStep(1);
  $('#wNext').onclick = () => goStep(4);
}

function restoreStepConfirm() {
  const w = state.wiz;
  const vol = allVolumes().find(v => v.name === w.targetDev) || {};
  const chains = (w.indexData && w.indexData.index && w.indexData.index.chains) || [];
  let picked = null;
  for (const c of chains) {
    for (const e of (c.entries || [])) {
      if (e.id === w.entryId) { picked = e; break; }
    }
    if (picked) break;
  }

  $('#wizBody').innerHTML =
    `<div class="wiz-hint">请确认下面的还原操作：</div>
     <div class="confirm-box">
       <div class="conf-row"><span>操作</span><b>从备份还原</b></div>
       <div class="conf-row"><span>还原点</span><b>${esc(picked ? picked.id : w.entryId)}${picked && picked.isFactoryBaseline ? ' (出厂基准)' : ''}</b></div>
       <div class="conf-row"><span>目标分区</span><b>${esc(volLabel(vol))} (${esc(w.targetDev)})</b></div>
       <div class="conf-row"><span>目标大小</span><b>${fmtSize(vol.sizeBytes)}</b></div>
       <div class="conf-row"><span>系统分区现状</span><b style="color:var(--ksl-red)">将被完全清空</b></div>
       <div class="conf-row"><span>引导分区</span><b>EFI\\Microsoft\\ 重建</b></div>
     </div>
     <div class="hwarn" style="margin-top:14px">
       <label style="display:flex;align-items:center;gap:8px;cursor:pointer">
         <input type="checkbox" id="wAck" style="width:auto">
         <span>我已了解目标分区上的所有数据将被永久删除，且已用「文件救援」导出需要保留的内容</span>
       </label>
     </div>`;

  foot(`<button class="btn" id="wBack">上一步</button>
        <div class="spacer"></div>
        <button class="btn primary" id="wGo" disabled>开始还原</button>`);
  $('#wBack').onclick = () => goStep(3);
  const ack = $('#wAck');
  const go = $('#wGo');
  ack.onchange = () => { go.disabled = !ack.checked; w.confirmed = ack.checked; };
  go.onclick = submitRestore;
}

// ── 提交作业 ──────────────────────────────────────────────────
async function submitFactory() {
  const w = state.wiz;
  goStep(3);  // factory 共 3 步，第 3 步是执行
  const { status, data } = await call('/api/jobs', 'POST', {
    type: 'factory',
    args: { target_dev: w.targetDev }
  });

  if (status !== 200 || !data.ok) {
    w.submitErr = data.reason || `提交失败（HTTP ${status}）`;
    renderWizard();
    return;
  }
  w.jobId = data.job.id;
  w.job = data.job;
  renderWizard();
  w.poll = setInterval(pollJob, 500);
}

async function submitRestore() {
  const w = state.wiz;
  goStep(4);  // restore 共 4 步，第 4 步是执行
  const { status, data } = await call('/api/jobs', 'POST', {
    type: 'restore',
    args: { entry_id: w.entryId, target_dev: w.targetDev }
  });

  if (status !== 200 || !data.ok) {
    w.submitErr = data.reason || `提交失败（HTTP ${status}）`;
    renderWizard();
    return;
  }
  w.jobId = data.job.id;
  w.job = data.job;
  renderWizard();
  w.poll = setInterval(pollJob, 500);
}

// ── 通用步骤：执行（factory / restore / bootfix / stress / memtester 共用）
function stepRunGeneric() {
  const w = state.wiz;
  if (w.submitErr) {
    $('#wizBody').innerHTML = `<div class="errbox"><b>没能开始作业</b><br>${esc(w.submitErr)}</div>`;
    foot(`<button class="btn" id="wBack">上一步</button><div class="spacer"></div>
          <button class="btn" id="wHome">回到菜单</button>`);
    $('#wBack').onclick = () => goStep(w.steps.length - 1);
    $('#wHome').onclick = renderHome;
    return;
  }

  const j = w.job;
  if (!j) {
    $('#wizBody').innerHTML = `<div class="empty">正在提交作业…</div>`;
    foot('');
    return;
  }

  const running = ['pending', 'scanning', 'running'].includes(j.state);
  const scanning = j.state === 'scanning' || (running && !j.totalBytes);
  const pct = j.totalBytes ? Math.min(100, 100 * j.doneBytes / j.totalBytes) : 0;

  let barCls = '';
  if (scanning) barCls = 'indet';
  else if (j.state === 'done') barCls = j.errorCount > 0 ? 'err' : 'ok';
  else if (j.state === 'failed' || j.state === 'cancelled') barCls = 'err';

  // 各作业类型在「运行中」「完成」时显示的文案
  const JOB_LABELS = {
    factory:   { running: '正在恢复出厂', done: '恢复出厂完成', failed: '恢复出厂失败' },
    restore:   { running: '正在还原',     done: '还原完成',     failed: '还原失败' },
    bootfix:   { running: '正在修复引导', done: '引导修复完成', failed: '引导修复失败' },
    stress:    { running: '正在压力测试', done: '压力测试完成', failed: '压力测试失败' },
    memtester: { running: '正在测试内存', done: '内存测试完成', failed: '内存测试失败' },
    install:   { running: '正在制作安装盘', done: '安装盘制作完成', failed: '安装盘制作失败' },
    linuxfix:  { running: '正在修复', done: '修复完成', failed: '修复失败' },
    hdd_speed: { running: '正在测试硬盘读速', done: '硬盘读速测试完成', failed: '硬盘读速测试失败' },
    ram_speed: { running: '正在测试内存读速', done: '内存读速测试完成', failed: '内存读速测试失败' },
  };
  const labels = JOB_LABELS[j.type] || JOB_LABELS[w.kind] ||
                 { running: '正在执行', done: '完成', failed: '失败' };

  const title = {
    pending: '正在准备…', scanning: '正在统计数据量…',
    running: labels.running,
    done: j.errorCount > 0 ? '完成，但有告警' : labels.done,
    failed: labels.failed, cancelled: '已取消'
  }[j.state] || j.state;

  const errs = (j.errors || []).length ? `
    <div class="errlist">
      <div class="eh">${j.errorCount} 项告警/失败${j.errorCount > j.errors.length ? `（只列出前 ${j.errors.length} 个）` : ''}</div>
      ${j.errors.map(e =>
        `<div class="er"><div class="ep">${esc(e.path)}</div><div class="em">${esc(e.error)}</div></div>`
      ).join('')}
    </div>` : '';

  // 完成时的提示文案：factory/restore 提示重启进 Windows，bootfix 提示重启验证，其余不额外提示
  let doneHint = '';
  if (j.state === 'done') {
    if (j.type === 'factory' || j.type === 'restore') {
      doneHint = '还原完成。重启进入 Windows 验证。若进不去系统，请回到本菜单执行「修复启动」。';
    } else if (j.type === 'bootfix') {
      doneHint = '引导修复完成。请重启验证能否正常进入 Windows。';
    } else if (j.type === 'memtester') {
      doneHint = j.result && j.result.passed === false
        ? '内存测试发现错误！建议重启进入 MemTest86+ 做完整检测。'
        : '内存测试通过。如仍有疑虑，建议重启进入 MemTest86+ 做完整检测。';
    } else if (j.type === 'install') {
      doneHint = '制作完成。重启电脑，在开机时按启动菜单键（常见 F12）选择这块盘，'
               + '即进入标准系统安装程序。安装会覆盖所选系统盘，注意别选错盘。';
    }
  }

  // 运行时的摘要行：factory/restore 显示 目标←源，stress/memtester 显示模式
  let summary = '';
  if (j.type === 'factory') summary = `${esc(w.targetDev)} ← factory.wim`;
  else if (j.type === 'restore') summary = `${esc(w.targetDev)} ← ${esc(w.entryId)}`;
  else if (j.type === 'bootfix') summary = '修复 ESP + UEFI 启动项';
  else if (j.type === 'stress') summary = `${esc(w.stressMode)} · ${w.stressDuration}秒`;
  else if (j.type === 'memtester') summary = `memtester · ${w.rounds || 1} 轮`;
  else if (j.type === 'install') summary = `${esc(w.isoPath || '').split('/').pop()} → ${esc(w.targetDisk || '')}（清空）`;
  else if (j.type === 'linuxfix') summary = `${esc(w.fixTitle || '')} · ${esc(w.targetDev || '')}`;
  else summary = j.title || '';

  $('#wizBody').innerHTML = `
    <div class="prog-wrap">
      <div class="prog-title">${esc(title)}</div>
      <div class="prog-cur">${esc(j.current || j.message || '')}</div>
      <div class="bar ${barCls}"><i style="width:${scanning ? 35 : pct}%"></i></div>
      <div class="prog-nums">
        <div>已展开<b>${fmtSize(j.doneBytes)}</b>${
          j.totalBytes ? `<span style="font-size:11px"> / ${fmtSize(j.totalBytes)}</span>` : ''}</div>
        <div>已处理<b>${j.doneFiles || 0}</b></div>
        <div>用时<b>${fmtTime(j.elapsedSeconds)}</b></div>
        ${j.errorCount ? `<div>告警<b style="color:var(--ksl-red)">${j.errorCount}</b></div>` : ''}
      </div>
      ${(j.state === 'failed') ? `<div class="errbox" style="margin-top:16px">${esc(j.message)}</div>` : ''}
      ${doneHint ? `<div class="wiz-hint" style="margin-top:14px">${esc(doneHint)}</div>` : ''}
      ${errs}
    </div>`;

  if (running) {
    foot(`<div class="summary">${esc(summary)}</div>
          <div class="spacer"></div>
          <button class="btn" id="wCancelJob">取消</button>`);
    $('#wCancelJob').onclick = async () => {
      $('#wCancelJob').disabled = true;
      $('#wCancelJob').textContent = '正在停止…';
      await call('/api/jobs/' + encodeURIComponent(w.jobId) + '/cancel', 'POST');
    };
  } else {
    foot(`<button class="btn ghost" id="wReboot">重启</button>
          <div class="spacer"></div>
          <button class="btn primary" id="wHome">完成</button>`);
    $('#wReboot').onclick = async () => {
      $('#wReboot').disabled = true;
      $('#wReboot').textContent = '正在重启…';
      await call('/api/power', 'POST', { action: 'reboot' });
      // 给系统一点时间执行，3秒后提示
      setTimeout(() => {
        showHelpRaw('正在重启', '如果机器没有自动重启，请手动按电源按钮关机再开机。', 'hnote');
      }, 3000);
    };
    $('#wHome').onclick = renderHome;
  }
}

// ── 状态条 ──────────────────────────────────────────────────────
function renderStatus({ sys, net, backups }) {
  const list = state.disks;
  const disk0 = list[0];
  const hasRescue = list.some(d => (d.partitions || []).some(p => p.isKlaRescue));

  const items = [
    { dot: 'ok', label: '固件',
      value: `${(sys && sys.firmware) || '?'} · SB ${(sys && sys.secureBoot) || '?'}` },
    { dot: 'ok', label: '内存', value: fmtSize(sys && sys.memoryBytes) },
    { dot: disk0 ? 'ok' : 'err', label: '磁盘',
      value: disk0 ? `${disk0.model} ${fmtSize(disk0.sizeBytes)}` : '未检测到' },
    { dot: (net && net.online) ? 'ok' : 'warn', label: '网络',
      value: (net && net.online) ? '已连接' : '未连接' },
    { dot: hasRescue ? 'ok' : 'err', label: '救援分区',
      value: hasRescue ? '已挂载' : '未找到' },
    { dot: (backups && backups.found) ? 'ok' : 'warn', label: '备份',
      value: (backups && backups.found)
        ? `${((backups.index && backups.index.chains) || []).length} 条链` : '无' }
  ];

  $('#statusbar').innerHTML = items.map(i =>
    `<span class="st-item"><i class="dot ${i.dot}"></i>${i.label} <b>${esc(i.value)}</b></span>`
  ).join('');

  $('#machineName').textContent = (sys && sys.machine) || '未知机型';
  $('#machineName').title = (sys && sys.machine) || '';
}

/* ═══════════════════════════════════════════════════════════════
   其余功能向导：分区管理 / Windows 账户 / 修复启动 / 网络设置 /
   打开浏览器 / 网络位置 / 硬件诊断 / 内存测试 / 数据恢复

   分两类：
   - 「启动程序」类（partition/network/browser/datarec/winacct）：
     POST /api/launch 让后端 fork 一个图形进程，前端只显示「已启动」。
   - 「作业」类（bootfix/hwdiag 压力/memtester）：走 /api/jobs 提交+轮询，
     复用 stepRunGeneric 的进度渲染。
   ═══════════════════════════════════════════════════════════════ */

// ── 通用：启动程序向导（partition/network/browser 用） ──────────
const WIZ_LAUNCH_STEPS = ['确认', '已启动'];

function openLaunch(kind, opts) {
  state.wiz = {
    kind: 'launch',
    step: 1,
    steps: WIZ_LAUNCH_STEPS,
    launchKind: kind,
    launchOpts: opts || {},
    launched: false,
    launchErr: null,
  };
  $('#backBtn').hidden = false;
  renderWizard();
}

function renderLaunchWizard() {
  const w = state.wiz;
  const opts = w.launchOpts || {};
  $('#catTitle').textContent = w.launchKind;
  $('#catDesc').textContent = opts.desc || '';

  const steps = renderStepsBar(w);
  $('#contentBody').innerHTML = wizardShell(steps);

  if (w.step === 1) {
    const warn = opts.warn
      ? `<div class="hwarn" style="margin-top:12px">${esc(opts.warn)}</div>` : '';
    const note = opts.note
      ? `<div class="hnote" style="margin-top:8px">${esc(opts.note)}</div>` : '';
    $('#wizBody').innerHTML =
      `<div class="wiz-hint">${esc(opts.hint || `即将打开 ${w.launchKind}。`)}</div>${warn}${note}`;
    foot(`<button class="btn" id="wBack">返回</button>
          <div class="spacer"></div>
          <button class="btn primary" id="wGo">打开</button>`);
    $('#wBack').onclick = renderHome;
    $('#wGo').onclick = async () => {
      $('#wGo').disabled = true;
      $('#wGo').textContent = '正在启动…';
      const { status, data } = await call('/api/launch', 'POST', opts.launchArgs || {});
      if (status === 200 && data.ok) { w.launched = true; }
      else { w.launchErr = (data && data.reason) || `启动失败（HTTP ${status}）`; }
      goStep(2);
    };
  } else {
    if (w.launchErr) {
      $('#wizBody').innerHTML = `<div class="errbox"><b>启动失败</b><br>${esc(w.launchErr)}</div>`;
    } else {
      const label = w.launchOpts.launchLabel || w.launchKind;
      $('#wizBody').innerHTML =
        `<div class="wiz-hint"><b>${esc(label)}</b> 已在独立窗口中打开。</div>
         <div class="hnote" style="margin-top:10px">程序关闭后本界面会自动恢复前台。</div>`;
    }
    foot(`<button class="btn" id="wAgain">再开一次</button>
          <div class="spacer"></div>
          <button class="btn primary" id="wHome">完成</button>`);
    $('#wAgain').onclick = () => { w.launched = false; w.launchErr = null; goStep(1); };
    $('#wHome').onclick = renderHome;
  }
}

function openPartition() {
  openLaunch('分区管理（GParted）', {
    desc: '图形化分区工具，可创建、删除、调整、格式化分区。',
    hint: '即将打开 GParted。它运行在独立的图形窗口中。',
    warn: '误操作会导致数据永久丢失。不要动系统分区和救援分区。',
    launchArgs: { app: 'gparted' },
    launchLabel: 'GParted',
  });
}

/* ══ 网络连接态前置检查 ══════════════════════════════════════════
   用户投诉：「通讯菜单里的网络链接无法使用」——根因是没连 WiFi/有线的情况下
   点"打开浏览器"直接启动 Chromium 白屏、点"网络位置"挂载永远超时、
   点"网络代理"设置了也连不上，用户体感就是"坏了"。
   修复：在打开这三个需要网络的功能入口前，先查 /api/network 接口：
   · 已联网（online=true）→ 正常继续；
   · 未联网 → 弹 YesNo 提示引导去「通信 → 网络设置」先连网。
   ══════════════════════════════════════════════════════════════ */
async function ensureNetwork(featureName) {
  try {
    const r = await call('/api/network');
    if (r.status === 200 && r.data && r.data.online) return true;
  } catch (_) {}
  // 未联网 → 弹模态提示
  const go = confirm(
    (featureName || '此功能') + '需要联网才能正常使用。\n\n' +
    '当前未检测到可用网络连接（WiFi 或有线）。\n' +
    '是否现在跳转到「通信 → 网络设置」连接网络？\n\n' +
    '（连接好网络后，重新点击本功能即可使用）'
  );
  if (go) openNetwork();
  return false;
}

function openNetwork() {
  // 不再依赖外部 nm-connection-editor（图形化 NM 不一定装）。
  // 直接用我们自己的 WiFi 面板，有线网络一般自动获取 IP，顺手给出提示。
  openWifi();
}

async function openBrowser() {
  if (!(await ensureNetwork('打开浏览器'))) return;
  openLaunch('浏览器', {
    desc: '上网查找解决方案或下载文件。',
    hint: '即将打开 Chromium 浏览器。',
    note: '下载的文件会存到内存盘，重启即消失。需要保留请存到 U 盘。',
    launchArgs: { app: 'chromium' },
    launchLabel: 'Chromium',
  });
}

// ── 数据恢复（选 TestDisk 还是 PhotoRec） ───────────────────────
function openDatarec() {
  state.wiz = {
    kind: 'datarec', step: 1, steps: ['选择工具', '已启动'],
    launched: false, launchErr: null, tool: null,
  };
  $('#backBtn').hidden = false;
  renderWizard();
}

function renderDatarecWizard() {
  const w = state.wiz;
  $('#catTitle').textContent = '数据恢复';
  $('#catDesc').textContent = '找回误删的分区与文件。';
  $('#contentBody').innerHTML = wizardShell(renderStepsBar(w));

  if (w.step === 1) {
    $('#wizBody').innerHTML =
      `<div class="wiz-hint">选择恢复工具：</div>
       <div class="tool-pick">
         <div class="tool-opt" data-tool="testdisk">
           <div class="tool-title">TestDisk</div>
           <div class="tool-desc">恢复误删的分区表，修复 MBR/GPT</div>
         </div>
         <div class="tool-opt" data-tool="photorec">
           <div class="tool-title">PhotoRec</div>
           <div class="tool-desc">按文件特征扫描恢复已删除文件</div>
         </div>
       </div>
       <div class="hwarn" style="margin-top:12px">
         发现数据丢失后应立即停止向该磁盘写入任何内容。</div>`;
    document.querySelectorAll('.tool-opt').forEach(el => {
      el.onclick = async () => {
        w.tool = el.dataset.tool; goStep(2);
        const { status, data } = await call('/api/launch', 'POST', { app: w.tool });
        if (status !== 200 || !data.ok)
          w.launchErr = (data && data.reason) || `启动失败（HTTP ${status}）`;
        renderWizard();
      };
    });
    foot(`<button class="btn" id="wBack">返回</button><div class="spacer"></div>`);
    $('#wBack').onclick = renderHome;
  } else {
    if (w.launchErr) {
      $('#wizBody').innerHTML = `<div class="errbox"><b>启动失败</b><br>${esc(w.launchErr)}</div>`;
    } else {
      const label = w.tool === 'testdisk' ? 'TestDisk' : 'PhotoRec';
      $('#wizBody').innerHTML = `<div class="wiz-hint"><b>${esc(label)}</b> 已在终端窗口中打开。</div>`;
    }
    foot(`<button class="btn" id="wAgain">换个工具</button>
          <div class="spacer"></div>
          <button class="btn primary" id="wHome">完成</button>`);
    $('#wAgain').onclick = () => { w.tool = null; w.launchErr = null; goStep(1); };
    $('#wHome').onclick = renderHome;
  }
}

// ── Windows 账户（chntpw） ──────────────────────────────────────
function openWinacct() {
  state.wiz = {
    kind: 'winacct', step: 1, steps: ['选择分区', '确认', '已启动'],
    targetDev: null, username: '', launched: false, launchErr: null,
  };
  $('#backBtn').hidden = false;
  renderWizard();
}

function renderWinacctWizard() {
  const w = state.wiz;
  $('#catTitle').textContent = 'Windows 账户';
  $('#catDesc').textContent = '重置本地账户密码、解锁被禁用的账户。';
  $('#contentBody').innerHTML = wizardShell(renderStepsBar(w));
  ({ 1: winacctStepSelect, 2: winacctStepConfirm, 3: winacctStepDone })[w.step]();
}

function winacctStepSelect() {
  const w = state.wiz;
  const vols = allVolumes();
  $('#wizBody').innerHTML =
    `<div class="wiz-hint">选择装着 Windows 的分区（通常是 C:）：</div>
     ${renderVolList(vols, v => {
       if (!v.isWindows) return { disabled: true, tag: '非 Windows 卷', tagKind: 'err', reason: '不是 NTFS 或不是 Windows 系统卷' };
       return { tag: 'Windows 系统卷' };
     }, w.targetDev, 'winacct-target')}`;
  bindVolPick('winacct-target', name => { w.targetDev = name; winacctStepSelect(); });
  foot(`<button class="btn" id="wBack">返回</button>
        <div class="summary">${w.targetDev ? '已选：' + esc(w.targetDev) : '尚未选择'}</div>
        <div class="spacer"></div>
        <button class="btn primary" id="wNext" ${w.targetDev ? '' : 'disabled'}>下一步</button>`);
  $('#wBack').onclick = renderHome;
  $('#wNext').onclick = () => goStep(2);
}

function winacctStepConfirm() {
  const w = state.wiz;
  $('#wizBody').innerHTML =
    `<div class="wiz-hint">可选：指定要重置密码的用户名。留空则列出所有用户由你选。</div>
     <div style="margin-top:10px">
       <input id="wUser" value="${esc(w.username)}" placeholder="如 Administrator（可留空）"
              style="font:inherit;font-size:13px;padding:7px 10px;width:320px;
                     border:1px solid var(--line);border-radius:2px">
     </div>
     <div class="hnote" style="margin-top:8px">
       点击「打开 chntpw」后，终端窗口会打开 chntpw 的交互菜单。
       按提示操作：1=清空密码、2=解锁账户、3=提升为管理员、q=退出。</div>`;
  const inp = $('#wUser');
  if (inp) inp.oninput = e => { w.username = e.target.value; };
  foot(`<button class="btn" id="wBack">上一步</button>
        <div class="spacer"></div>
        <button class="btn primary" id="wGo">打开 chntpw</button>`);
  $('#wBack').onclick = () => goStep(1);
  $('#wGo').onclick = async () => {
    $('#wGo').disabled = true;
    $('#wGo').textContent = '正在启动…';
    const { status, data } = await call('/api/launch', 'POST',
      { app: 'chntpw', dev: w.targetDev, user: w.username });
    if (status === 200 && data.ok) { w.launched = true; }
    else { w.launchErr = (data && data.reason) || `启动失败（HTTP ${status}）`; }
    goStep(3);
  };
}

function winacctStepDone() {
  const w = state.wiz;
  if (w.launchErr) {
    $('#wizBody').innerHTML = `<div class="errbox"><b>启动失败</b><br>${esc(w.launchErr)}</div>`;
  } else {
    $('#wizBody').innerHTML =
      `<div class="wiz-hint"><b>chntpw</b> 已在终端窗口中打开。</div>
       <div class="hnote" style="margin-top:8px">在终端里按提示操作，完成后关闭终端即可。</div>`;
  }
  foot(`<button class="btn" id="wAgain">再开一次</button>
        <div class="spacer"></div>
        <button class="btn primary" id="wHome">完成</button>`);
  $('#wAgain').onclick = () => { w.launchErr = null; goStep(2); };
  $('#wHome').onclick = renderHome;
}

// ── 修复启动（作业） ────────────────────────────────────────────
function openBootfix() {
  state.wiz = {
    kind: 'bootfix', step: 1, steps: ['确认', '执行'],
    jobId: null, job: null, poll: null, submitErr: null,
  };
  $('#backBtn').hidden = false;
  renderWizard();
}

function renderBootfixWizard() {
  const w = state.wiz;
  $('#catTitle').textContent = '修复启动';
  $('#catDesc').textContent = '重建 Windows 引导记录与启动项。';
  $('#contentBody').innerHTML = wizardShell(renderStepsBar(w));
  if (w.step === 1) {
    $('#wizBody').innerHTML =
      `<div class="wiz-hint">将执行以下操作：</div>
       <div class="confirm-box">
         <div class="conf-row"><span>1</span><b>优先从 esp-factory.tar 重建 ESP 的 EFI/Microsoft/</b></div>
         <div class="conf-row"><span>2</span><b>若无快照，检查引导文件并用 efibootmgr 重建启动项</b></div>
       </div>
       <div class="hnote" style="margin-top:10px">此操作不会动你的数据，只重写引导区。</div>`;
    foot(`<button class="btn" id="wBack">返回</button>
          <div class="spacer"></div>
          <button class="btn primary" id="wGo">开始修复</button>`);
    $('#wBack').onclick = renderHome;
    $('#wGo').onclick = submitBootfix;
  } else {
    stepRunGeneric();
  }
}

async function submitBootfix() {
  const w = state.wiz;
  goStep(2);
  const { status, data } = await call('/api/jobs', 'POST', { type: 'bootfix', args: {} });
  if (status !== 200 || !data.ok) {
    w.submitErr = data.reason || `提交失败（HTTP ${status}）`;
    renderWizard(); return;
  }
  w.jobId = data.job.id; w.job = data.job;
  renderWizard();
  w.poll = setInterval(pollJob, 500);
}

// ── 硬件诊断（SMART + 传感器 + 压力测试） ───────────────────────
function openHwdiag() {
  state.wiz = {
    kind: 'hwdiag', step: 1, steps: ['选择磁盘', '诊断数据', '压力测试'],
    targetDev: null, smartData: null, smartErr: null, sensorsData: null,
    jobId: null, job: null, poll: null, submitErr: null,
    stressMode: 'cpu', stressDuration: 60,
  };
  $('#backBtn').hidden = false;
  renderWizard();
}

function renderHwdiagWizard() {
  const w = state.wiz;
  $('#catTitle').textContent = '硬件诊断';
  $('#catDesc').textContent = '硬盘 SMART、温度、CPU 与内存压力测试。';
  $('#contentBody').innerHTML = wizardShell(renderStepsBar(w));
  ({ 1: hwdiagStepSelect, 2: hwdiagStepData, 3: hwdiagStepStress })[w.step]();
}

function hwdiagStepSelect() {
  const w = state.wiz;
  const disks = state.disks || [];
  if (!disks.length) {
    $('#wizBody').innerHTML = `<div class="errbox">没有检测到任何磁盘。</div>`;
    foot(`<div class="spacer"></div><button class="btn" id="wBack">返回</button>`);
    $('#wBack').onclick = renderHome; return;
  }
  $('#wizBody').innerHTML =
    `<div class="wiz-hint">选择要查看 SMART 数据的磁盘：</div>
     <div class="vol-list">${disks.map(d => {
       const on = w.targetDev === d.name;
       return `<div class="vol ${on ? 'sel' : ''}" data-kind="hwdisk" data-vol="${esc(d.name)}">
         ${icon('disk', 'ico32')}
         <div>
           <div class="vol-name">${esc(d.model)} <span style="font-weight:normal;opacity:.6">(${esc(d.name)})</span></div>
           <div class="vol-meta">${fmtSize(d.sizeBytes)}</div>
         </div>
       </div>`;
     }).join('')}</div>`;
  document.querySelectorAll('.vol[data-kind="hwdisk"]').forEach(el => {
    el.onclick = () => { w.targetDev = el.dataset.vol; goStep(2); loadHwdiagData(); };
  });
  foot(`<button class="btn" id="wBack">返回</button>
        <div class="summary">${w.targetDev ? '已选：' + esc(w.targetDev) : '尚未选择'}</div>
        <div class="spacer"></div>
        <button class="btn primary" id="wNext" ${w.targetDev ? '' : 'disabled'}>下一步</button>`);
  $('#wBack').onclick = renderHome;
  $('#wNext').onclick = () => { goStep(2); loadHwdiagData(); };
}

async function loadHwdiagData() {
  const w = state.wiz;
  if (!w.targetDev) return;
  const [smart, sensors] = await Promise.all([
    call(`/api/smart?dev=${encodeURIComponent(w.targetDev)}`),
    call('/api/sensors'),
  ]);
  if (!state.wiz || state.wiz !== w) return;
  w.smartData = (smart && smart.data) || null;
  w.smartErr = (smart && smart.status !== 200) ? `HTTP ${smart.status}` : null;
  w.sensorsData = (sensors && sensors.data) || null;
  if (w.step === 2) renderWizard();
}

function hwdiagStepData() {
  const w = state.wiz;
  let smartHtml;
  if (!w.smartData && !w.smartErr) {
    smartHtml = '<div class="empty">正在读取…</div>';
  } else if (w.smartErr) {
    smartHtml = `<div class="errbox">SMART 读取失败：${esc(w.smartErr)}</div>`;
  } else {
    const out = (w.smartData && w.smartData.output) || '（无数据）';
    smartHtml = `<div class="data-block"><div class="db-head">SMART 数据（${esc(w.targetDev)}）</div><pre class="data-pre">${esc(out)}</pre></div>`;
  }
  const sensorsOut = (w.sensorsData && w.sensorsData.output) || '（无数据）';
  const sensorsHtml = `<div class="data-block"><div class="db-head">传感器</div><pre class="data-pre">${esc(sensorsOut)}</pre></div>`;
  $('#wizBody').innerHTML = smartHtml + sensorsHtml;
  foot(`<button class="btn" id="wBack">上一步</button>
        <div class="spacer"></div>
        <button class="btn primary" id="wNext">压力测试</button>`);
  $('#wBack').onclick = () => goStep(1);
  $('#wNext').onclick = () => goStep(3);
}

function hwdiagStepStress() {
  const w = state.wiz;
  if (w.job) { stepRunGeneric(); return; }
  $('#wizBody').innerHTML =
    `<div class="wiz-hint">运行压力测试，检测硬件是否稳定。</div>
     <div style="margin-top:12px">
       <label style="display:block;margin-bottom:8px">
         测试类型：
         <select id="wMode" style="font:inherit;padding:4px">
           <option value="cpu" ${w.stressMode === 'cpu' ? 'selected' : ''}>CPU</option>
           <option value="memory" ${w.stressMode === 'memory' ? 'selected' : ''}>内存</option>
           <option value="disk" ${w.stressMode === 'disk' ? 'selected' : ''}>磁盘 IO</option>
           <option value="all" ${w.stressMode === 'all' ? 'selected' : ''}>全部</option>
         </select>
       </label>
       <label style="display:block">
         持续秒数：
         <input id="wDur" type="number" value="${w.stressDuration}" min="10" max="3600"
                style="font:inherit;padding:4px;width:80px">
       </label>
     </div>
     <div class="hwarn" style="margin-top:10px">压力测试会让硬件满载运行。散热不良的机器可能过热降频。</div>`;
  $('#wMode').onchange = e => { w.stressMode = e.target.value; };
  $('#wDur').oninput = e => { w.stressDuration = parseInt(e.target.value) || 60; };
  foot(`<button class="btn" id="wBack">上一步</button>
        <div class="spacer"></div>
        <button class="btn primary" id="wGo">开始测试</button>`);
  $('#wBack').onclick = () => goStep(2);
  $('#wGo').onclick = async () => {
    const { status, data } = await call('/api/jobs', 'POST',
      { type: 'stress', args: { duration: w.stressDuration, mode: w.stressMode } });
    if (status !== 200 || !data.ok) { w.submitErr = data.reason || `提交失败（HTTP ${status}）`; }
    else { w.jobId = data.job.id; w.job = data.job; w.poll = setInterval(pollJob, 500); }
    renderWizard();
  };
}

// ── 内存测试（memtester 用户态 或 MemTest86+ 重启） ──────────────
function openMemtest() {
  state.wiz = {
    kind: 'memtest', step: 1, steps: ['选择方式', '执行'],
    mode: null, rounds: 1, jobId: null, job: null, poll: null, submitErr: null,
  };
  $('#backBtn').hidden = false;
  renderWizard();
}

function renderMemtestWizard() {
  const w = state.wiz;
  $('#catTitle').textContent = '内存测试';
  $('#catDesc').textContent = '完整的内存颗粒检测。';
  $('#contentBody').innerHTML = wizardShell(renderStepsBar(w));

  if (w.step === 1) {
    $('#wizBody').innerHTML =
      `<div class="wiz-hint">选择测试方式：</div>
       <div class="tool-pick">
         <div class="tool-opt" data-mode="memtester">
           <div class="tool-title">用户态内存测试（memtester）</div>
           <div class="tool-desc">在救援环境内直接运行，不需重启。快速排查用。</div>
         </div>
         <div class="tool-opt" data-mode="reboot">
           <div class="tool-title">完整内存检测（MemTest86+）</div>
           <div class="tool-desc">重启进入独立的 MemTest86+ 环境，完整检测需数小时。</div>
         </div>
       </div>`;
    document.querySelectorAll('.tool-opt').forEach(el => {
      el.onclick = () => {
        w.mode = el.dataset.mode;
        if (w.mode === 'reboot') {
          showHelpRaw('MemTest86+',
            '重启后在 GRUB 菜单选择 MemTest86+ 即可进入完整检测。'
            + '完整跑一遍需要数小时，建议睡前开始。按 Esc 可中止。');
        } else {
          goStep(2);
        }
      };
    });
    foot(`<button class="btn" id="wBack">返回</button><div class="spacer"></div>`);
    $('#wBack').onclick = renderHome;
  } else {
    if (w.job) { stepRunGeneric(); return; }
    $('#wizBody').innerHTML =
      `<div class="wiz-hint">设置 memtester 轮数：</div>
       <div style="margin-top:10px">
         <label>测试轮数：
           <input id="wRounds" type="number" value="${w.rounds}" min="1" max="20"
                  style="font:inherit;padding:4px;width:80px">
         </label>
       </div>`;
    $('#wRounds').oninput = e => { w.rounds = parseInt(e.target.value) || 1; };
    foot(`<button class="btn" id="wBack">上一步</button>
          <div class="spacer"></div>
          <button class="btn primary" id="wGo">开始测试</button>`);
    $('#wBack').onclick = () => goStep(1);
    $('#wGo').onclick = async () => {
      const { status, data } = await call('/api/jobs', 'POST',
        { type: 'memtester', args: { rounds: w.rounds } });
      if (status !== 200 || !data.ok) { w.submitErr = data.reason || `提交失败（HTTP ${status}）`; }
      else { w.jobId = data.job.id; w.job = data.job; w.poll = setInterval(pollJob, 500); }
      renderWizard();
    };
  }
}

// ── 网络位置（SMB 挂载） ────────────────────────────────────────
async function openShare() {
  if (!(await ensureNetwork('网络位置（SMB 共享）'))) return;
  state.wiz = {
    kind: 'share', step: 1, steps: ['填写共享信息', '已挂载'],
    host: '', share: '', user: '', password: '', name: '',
    mountErr: null, shares: [],
  };
  $('#backBtn').hidden = false;
  renderWizard();
}

function renderShareWizard() {
  const w = state.wiz;
  $('#catTitle').textContent = '网络位置';
  $('#catDesc').textContent = '挂载 SMB/CIFS 共享目录。';
  $('#contentBody').innerHTML = wizardShell(renderStepsBar(w));

  if (w.step === 1) {
    if (w.mountErr) {
      $('#wizBody').innerHTML = `<div class="errbox"><b>挂载失败</b><br>${esc(w.mountErr)}</div>`;
    } else {
      $('#wizBody').innerHTML =
        `<div class="wiz-hint">填写 SMB 共享信息：</div>
         <div class="form-grid">
           <label>服务器地址<input id="wHost" value="${esc(w.host)}" placeholder="如 192.168.1.100"></label>
           <label>共享名<input id="wShare" value="${esc(w.share)}" placeholder="如 Public"></label>
           <label>挂载点名（可选）<input id="wName" value="${esc(w.name || w.share)}" placeholder="默认用共享名"></label>
           <label>用户名（可选）<input id="wUser" value="${esc(w.user)}" placeholder="留空用访客"></label>
           <label>密码<input id="wPass" type="password" value="${esc(w.password)}" placeholder="留空用访客"></label>
         </div>`;
      $('#wHost').oninput = e => w.host = e.target.value;
      $('#wShare').oninput = e => w.share = e.target.value;
      $('#wName').oninput = e => w.name = e.target.value;
      $('#wUser').oninput = e => w.user = e.target.value;
      $('#wPass').oninput = e => w.password = e.target.value;
    }
    foot(`<button class="btn" id="wBack">返回</button>
          <div class="spacer"></div>
          <button class="btn primary" id="wGo">挂载</button>`);
    $('#wBack').onclick = renderHome;
    $('#wGo').onclick = async () => {
      $('#wGo').disabled = true;
      const { status, data } = await call('/api/shares', 'POST', {
        host: w.host, share: w.share,
        user: w.user, password: w.password,
        name: w.name || w.share,
      });
      if (status === 200 && data.ok) { goStep(2); loadShares(); }
      else { w.mountErr = (data && data.reason) || `挂载失败（HTTP ${status}）`; renderWizard(); }
    };
  } else {
    let listHtml;
    if (!w.shares.length) {
      listHtml = '<div class="empty">没有已挂载的共享</div>';
    } else {
      listHtml = w.shares.map(s => `
        <div class="share-row">
          <div class="share-name">${esc(s.name)}</div>
          <div class="share-meta">${fmtSize(s.freeBytes)} 可用 / ${fmtSize(s.totalBytes)}</div>
          <button class="btn ghost" data-unmount="${esc(s.name)}">卸载</button>
        </div>`).join('');
    }
    $('#wizBody').innerHTML =
      `<div class="wiz-hint">已挂载的 SMB 共享：</div>
       <div class="share-list">${listHtml}</div>`;
    document.querySelectorAll('[data-unmount]').forEach(btn => {
      btn.onclick = async () => {
        btn.disabled = true;
        await call(`/api/shares/${encodeURIComponent(btn.dataset.unmount)}/unmount`, 'POST');
        loadShares();
      };
    });
    foot(`<button class="btn" id="wMore">再挂一个</button>
          <div class="spacer"></div>
          <button class="btn primary" id="wHome">完成</button>`);
    $('#wMore').onclick = () => { w.mountErr = null; goStep(1); };
    $('#wHome').onclick = renderHome;
  }
}

async function loadShares() {
  const w = state.wiz;
  const { status, data } = await call('/api/shares');
  if (status === 200 && data) w.shares = data.shares || [];
  if (w.step === 2) renderWizard();
}

// ── 向导壳子辅助函数 ────────────────────────────────────────────
function renderStepsBar(w) {
  return w.steps.map((s, i) => {
    const n = i + 1;
    const cls = n === w.step ? 'active' : (n < w.step ? 'done' : '');
    return `<div class="step ${cls}"><span class="num">${n < w.step ? '✓' : n}</span>${esc(s)}</div>`;
  }).join('');
}

function wizardShell(stepsHtml) {
  return `<div class="wizard">
    <div class="steps">${stepsHtml}</div>
    <div class="wiz-body" id="wizBody"></div>
    <div class="wiz-foot" id="wizFoot"></div>
  </div>`;
}

/* ═══════════════════════════════════════════════════════════════
   高级功能 + 显卡诊断 向导
   ═══════════════════════════════════════════════════════════════ */

// ── Linux 终端 / 文件管理器：走现成的 launch 通道 ───────────────
function openTerminal() {
  openLaunch('Linux 终端', {
    desc: '打开命令行终端，直接操作救援系统。',
    hint: '即将打开终端窗口。救援系统是 Debian Linux。',
    note: '终端里的修改重启即消失；对硬盘分区的修改是永久的。',
    launchArgs: { app: 'terminal' },
    launchLabel: '终端（lxterminal）',
  });
}

function openFilemgr() {
  openLaunch('文件管理器', {
    desc: '图形化浏览与管理本机文件。',
    hint: '即将打开 PCManFM 文件管理器。',
    warn: '在文件管理器里删除的文件不进回收站，直接没了。',
    launchArgs: { app: 'files' },
    launchLabel: 'PCManFM 文件管理器',
  });
}

// ── 显卡诊断：一步出全部数据 ────────────────────────────────────
function openGpudiag() {
  state.wiz = {
    kind: 'gpudiag', step: 1, steps: ['显卡信息'],
    gpuData: null, gpuErr: null,
  };
  $('#backBtn').hidden = false;
  renderWizard();
  loadGpuData();
}

async function loadGpuData() {
  const w = state.wiz;
  const { status, data } = await call('/api/gpu');
  if (!state.wiz || state.wiz !== w) return;
  w.gpuData = (status === 200) ? data : null;
  w.gpuErr = (status !== 200) ? `HTTP ${status}` : null;
  if (w.step === 1) renderWizard();
}

function renderGpudiagWizard() {
  const w = state.wiz;
  $('#catTitle').textContent = '显卡诊断';
  $('#catDesc').textContent = '显卡型号、显存、驱动状态与内核报错。';
  $('#contentBody').innerHTML = wizardShell(renderStepsBar(w));

  if (!w.gpuData && !w.gpuErr) {
    $('#wizBody').innerHTML = '<div class="empty">正在读取显卡数据…</div>';
    foot(`<div class="spacer"></div><button class="btn" id="wBack">返回</button>`);
    $('#wBack').onclick = renderHome;
    return;
  }
  if (w.gpuErr) {
    $('#wizBody').innerHTML = `<div class="errbox">读取失败：${esc(w.gpuErr)}</div>`;
    foot(`<div class="spacer"></div><button class="btn" id="wBack">返回</button>`);
    $('#wBack').onclick = renderHome;
    return;
  }

  const d = w.gpuData;
  let html = '';
  if (!d.cards || !d.cards.length) {
    html = `<div class="errbox">没有检测到显卡。${esc(d.note || '')}</div>`;
  } else {
    html = d.cards.map(c => `
      <div class="data-block">
        <div class="db-head">${esc(c.name)}</div>
        <div class="kv">
          <div><span>型号</span><b>${esc(c.name)}</b></div>
          <div><span>PCI 位置 / ID</span><b>${esc(c.slot)} · ${esc(c.pciId || '—')}</b></div>
          <div><span>驱动</span><b>${esc(c.driver)}${c.card ? '（' + esc(c.card) + '）' : ''}</b></div>
          <div><span>显存</span><b>${c.vramBytes ? fmtSize(c.vramBytes) + (c.vramUsedBytes ? '（已用 ' + fmtSize(c.vramUsedBytes) + '）' : '') : '内核未报告（仅 AMD 卡提供）'}</b></div>
        </div>
        ${c.connectors && c.connectors.length ? `
          <div class="kv" style="margin-top:8px">
            ${c.connectors.map(cn => `
              <div><span>接口 ${esc(cn.name)}</span>
                  <b>${cn.status === 'connected'
                    ? '已连接' + (cn.bestMode ? ' · 最高 ' + esc(cn.bestMode) : '')
                    : '未连接'}</b></div>`).join('')}
          </div>` : ''}
      </div>`).join('');
  }

  const errs = (d.kernelErrors || []).length ? `
    <div class="data-block">
      <div class="db-head">内核 GPU 报错 / 警告（${d.kernelErrors.length} 条）</div>
      <pre class="data-pre">${esc(d.kernelErrors.join('\n'))}</pre>
    </div>` : `
    <div class="data-block">
      <div class="db-head">内核 GPU 报错</div>
      <div class="empty">没有 GPU 相关的报错记录。</div>
    </div>`;

  if (d.nvidiaSmi) {
    html += `<div class="data-block">
      <div class="db-head">nvidia-smi</div>
      <pre class="data-pre">${esc(d.nvidiaSmi)}</pre>
    </div>`;
  }

  $('#wizBody').innerHTML = html + errs;
  foot(`<button class="btn" id="wRefresh">刷新</button>
        <div class="spacer"></div>
        <button class="btn primary" id="wHome">完成</button>`);
  $('#wRefresh').onclick = () => { w.gpuData = null; w.gpuErr = null; renderWizard(); loadGpuData(); };
  $('#wHome').onclick = renderHome;
}

/* ═══════════════════════════════════════════════════════════════
   实用工具箱（CPU-Z / AIDA64 / 硬盘读速 / 内存读速）

   四个工具全部直接跑 Linux 等效的原生二进制，把完整 stdout 作为最可信的
   主呈现返回给 UI——不再在前端拼"看起来像 CPU-Z/AIDA64"的假卡片。
   结构化字段作为辅助（tab 切换 / 高亮指标 / 生成摘要）。

   • CPU-Z        →  GET /api/toolbox/cpuz
                     返回 lscpu 全文 + /proc/cpuinfo 全文 + dmidecode 6 类全文
   • AIDA64       →  GET /api/toolbox/aida64
                     返回 lshw -short / lshw / sensors -A + smartctl -x (每块盘)
                     + lspci -mm / lsusb + ip -d link + upower --dump
   • 硬盘读速     →  GET /api/toolbox/hdparm?dev=... (hdparm -Tt + dd 顺序直读)
   • 内存读速     →  GET /api/toolbox/mbw?size_mb=128&loop=3
   ═══════════════════════════════════════════════════════════════ */

const TOOLBOX_TITLES = {
  'cpu-z':    ['CPU-Z（处理器详情）',   '直接运行 lscpu + dmidecode，完整原生命令输出。'],
  'aida64':   ['AIDA64（整机传感器）',  '直接运行 lshw + sensors + smartctl，整机硬件与传感器全貌。'],
  'hdd-speed':['硬盘读速测试',           'hdparm -T（缓存读）/ -t（缓冲读） + dd 顺序直读，完整原始输出。'],
  'ram-speed':['内存读速测试',           'mbw 内存带宽基准（MCMP / MPROBE / MEMCPY 三组平均）。'],
};

function openToolbox(toolId) {
  const meta = TOOLBOX_TITLES[toolId] || [toolId, ''];
  if (toolId === 'hdd-speed') {
    // 硬盘读速：先选盘 → 再执行（因为要用户确认要测哪块，避免把 SSD 寿命读坏）
    state.wiz = {
      kind: 'toolbox', toolId, step: 1, steps: [meta[0], '执行 hdparm + dd'],
      title: meta[0], data: null, err: null, targetDisk: null,
    };
  } else if (toolId === 'ram-speed') {
    // 内存读速：调参数 → 执行
    state.wiz = {
      kind: 'toolbox', toolId, step: 1, steps: [meta[0], '执行 mbw'],
      title: meta[0], data: null, err: null,
      sizeMb: 128, loop: 3,
    };
  } else {
    // CPU-Z / AIDA64：一步出数据，异步拉
    state.wiz = {
      kind: 'toolbox', toolId, step: 1, steps: [meta[0]],
      title: meta[0], data: null, err: null,
    };
  }
  $('#backBtn').hidden = false;
  renderWizard();
  if (toolId === 'cpu-z') loadToolboxCpu();
  else if (toolId === 'aida64') loadToolboxAida();
}

async function loadToolboxCpu() {
  const w = state.wiz;
  const data = await get('/api/toolbox/cpuz');
  if (!state.wiz || state.wiz !== w) return;
  if (!data || !data.ok) { w.err = (data && data.reason) || 'CPU-Z 工具运行失败'; }
  else { w.data = data; }
  if (w.step === 1) renderWizard();
}

async function loadToolboxAida() {
  const w = state.wiz;
  const data = await get('/api/toolbox/aida64');
  if (!state.wiz || state.wiz !== w) return;
  if (!data || !data.ok) { w.err = (data && data.reason) || 'AIDA64 工具运行失败'; }
  else { w.data = data; }
  if (w.step === 1) renderWizard();
}

/** Tab 化的 raw 输出：给多个 stdout 时，减少滚动高度。 */
function rawTabs(idPrefix, sections, activeKey) {
  // sections: [{key, label, text}]
  const keys = sections.map(s => s.key);
  const active = (keys.includes(activeKey) ? activeKey : keys[0]) || '';
  const heads = sections.map(s => `
    <button type="button" class="tbx-tab ${s.key === active ? 'on' : ''}"
      data-tbx-group="${idPrefix}" data-tbx-key="${esc(s.key)}">${esc(s.label)}</button>
  `).join('');
  const bodies = sections.map(s => {
    const text = (s.text == null) ? '' : String(s.text);
    const size = (s.size != null) ? `<span class="tbx-size">${esc(s.size)}</span>` : '';
    return `
      <div class="tbx-pane" data-tbx-group="${idPrefix}" data-tbx-key="${esc(s.key)}"
        style="${s.key === active ? '' : 'display:none'}">
        <div class="tbx-pane-head">
          <b>${esc(s.label)}</b>${size}
          <button type="button" class="tbx-copy"
            data-tbx-copy="${idPrefix}__${esc(s.key)}">复制</button>
        </div>
        <pre class="data-pre" id="${idPrefix}__${esc(s.key)}">${esc(text) || '（无输出）'}</pre>
      </div>`;
  }).join('');
  return `<div class="tbx-tabs">${heads}</div>${bodies}`;
}

function bindRawTabs() {
  document.querySelectorAll('.tbx-tab').forEach(btn => {
    btn.addEventListener('click', () => {
      const g = btn.dataset.tbxGroup;
      const k = btn.dataset.tbxKey;
      document.querySelectorAll(`.tbx-tab[data-tbx-group="${g}"]`).forEach(b =>
        b.classList.toggle('on', b.dataset.tbxKey === k));
      document.querySelectorAll(`.tbx-pane[data-tbx-group="${g}"]`).forEach(p =>
        p.style.display = (p.dataset.tbxKey === k) ? '' : 'none');
    });
  });
  document.querySelectorAll('.tbx-copy').forEach(btn => {
    btn.addEventListener('click', async () => {
      const id = btn.dataset.tbxCopy;
      const pre = document.getElementById(id);
      const text = pre ? pre.textContent : '';
      try {
        if (navigator.clipboard) await navigator.clipboard.writeText(text);
        const old = btn.textContent;
        btn.textContent = '✓ 已复制';
        setTimeout(() => { btn.textContent = old; }, 1200);
      } catch (e) { /* noop */ }
    });
  });
}
function renderToolboxWizard() {
  const w = state.wiz;
  const meta = TOOLBOX_TITLES[w.toolId] || ['', ''];
  $('#catTitle').textContent = meta[0];
  $('#catDesc').textContent = meta[1];

  if (w.toolId === 'cpu-z' || w.toolId === 'aida64') {
    $('#contentBody').innerHTML = wizardShell(renderStepsBar(w));
    if (!w.data && !w.err) {
      $('#wizBody').innerHTML = `<div class="empty">正在运行原生工具（${
        w.toolId === 'cpu-z' ? 'lscpu + dmidecode' : 'lshw + sensors + smartctl'
      }）…几秒到二十秒。</div>`;
      foot(`<div class="spacer"></div><button class="btn" id="wBack">返回</button>`);
      $('#wBack').onclick = renderHome;
      return;
    }
    if (w.err) {
      $('#wizBody').innerHTML = `<div class="errbox">${esc(w.err)}</div>`;
      foot(`<button class="btn" id="wRefresh">重试</button>
            <div class="spacer"></div>
            <button class="btn" id="wBack">返回</button>`);
      $('#wRefresh').onclick = () => {
        w.data = null; w.err = null; renderWizard();
        if (w.toolId === 'cpu-z') loadToolboxCpu(); else loadToolboxAida();
      };
      $('#wBack').onclick = renderHome;
      return;
    }
    if (w.toolId === 'cpu-z') renderToolboxCpuView();
    else renderToolboxAidaView();
    foot(`<button class="btn" id="wRefresh">重新运行</button>
          <div class="spacer"></div>
          <button class="btn primary" id="wHome">完成</button>`);
    $('#wRefresh').onclick = () => {
      w.data = null; w.err = null; renderWizard();
      if (w.toolId === 'cpu-z') loadToolboxCpu(); else loadToolboxAida();
    };
    $('#wHome').onclick = renderHome;
    bindRawTabs();
    return;
  }

  // 速度测试类向导
  $('#contentBody').innerHTML = wizardShell(renderStepsBar(w));
  if (w.toolId === 'hdd-speed') {
    ({ 1: toolboxHddStepPick, 2: toolboxHddStepConfirm, 3: toolboxHddStepRun })[w.step]();
  } else {
    ({ 1: toolboxRamStepParams, 2: toolboxRamStepConfirm, 3: toolboxRamStepRun })[w.step]();
  }
  bindRawTabs();
}

// ── CPU-Z：原生命令输出 + 关键指标摘要 ─────────────────────────
function renderToolboxCpuView() {
  const w = state.wiz.data;
  const raw = w.raw || {};
  const s = w.structured || {};
  const flat = s.lscpuFlat || {};
  const slots = s.memorySlots || [];
  const board = s.board || {};
  const cores = Array.isArray(s.cpu) ? s.cpu.length : 0;

  const lscpuSummary = [
    ['架构', flat['Architecture']],
    ['CPU(s)', flat['CPU(s)']],
    ['On-line CPU(s) list', flat['On-line CPU(s) list']],
    ['Thread(s) per core', flat['Thread(s) per core']],
    ['Core(s) per socket', flat['Core(s) per socket']],
    ['Socket(s)', flat['Socket(s)']],
    ['NUMA 节点', flat['NUMA node(s)']],
    ['型号名', flat['Model name']],
    ['CPU MHz', flat['CPU MHz']],
    ['CPU max MHz', flat['CPU max MHz']],
    ['CPU min MHz', flat['CPU min MHz']],
    ['虚拟化', flat['Virtualization']],
    ['L1d / L1i / L2 / L3 缓存',
      [flat['L1d cache'], flat['L1i cache'], flat['L2 cache'], flat['L3 cache']].filter(Boolean).join(' / ')],
  ].filter(r => r[1]);

  const slotHtml = slots.length
    ? slots.map(sl => `<div class="ov-line">
        <span class="ov-k">${esc(sl.slot || 'DIMM')}</span>
        <b>${esc(sl.size || '—')} · ${esc(sl.type || '—')} · ${esc(sl.speed || '—')}</b>
        ${sl.manufacturer || sl.part ? `
          <div class="ov-sub">
            ${sl.manufacturer ? esc(sl.manufacturer) : ''}
            ${sl.part ? ' · P/N ' + esc(sl.part) : ''}
          </div>` : ''}
      </div>`).join('')
    : `<div class="ov-line">SMBIOS 未提供 SPD 明细（机型常见限制）。</div>`;

  const dmi = raw.dmidecode || {};
  const tabs = rawTabs('cpuz', [
    { key: 'lscpu',  label: 'lscpu', text: raw.lscpu },
    { key: 'cpuinfo', label: '/proc/cpuinfo', text: raw.cpuinfo },
    { key: 'dmiproc', label: 'DMI processor', text: dmi.processor },
    { key: 'dmicache', label: 'DMI cache', text: dmi.cache },
    { key: 'dmimem', label: 'DMI memory', text: dmi.memory },
    { key: 'dmiboard', label: 'DMI baseboard', text: dmi.baseboard },
    { key: 'dmibios', label: 'DMI BIOS', text: dmi.bios },
    { key: 'dmicase', label: 'DMI chassis', text: dmi.chassis },
  ], 'lscpu');

  $('#wizBody').innerHTML = `
    <div class="data-block">
      <div class="db-head">摘要（从 lscpu + dmidecode 结构化抽取）</div>
      <div class="kv" style="margin-top:6px">
        ${lscpuSummary.map(([k,v]) =>
          `<div><span>${esc(k)}</span><b>${esc(String(v))}</b></div>`).join('')}
      </div>
      <div class="ov-title" style="margin-top:14px">主板 / 芯片组</div>
      <div class="kv" style="margin-top:6px">
        ${[
          ['厂商', board.boardVendor],
          ['型号', board.boardModel],
          ['版本', board.boardVersion],
          ['芯片组', board.chipset],
          ['机箱类型', board.chassis],
        ].filter(r => r[1]).map(([k,v]) =>
          `<div><span>${esc(k)}</span><b>${esc(String(v))}</b></div>`).join('') ||
          '<div><span>—</span><b>未识别</b></div>'}
      </div>
      <div class="ov-title" style="margin-top:14px">内存 SPD（共 ${cores ? 'CPU 核数 ' + cores + '，' : ''}${slots.length} 条 DIMM 有信息）</div>
      ${slotHtml}
    </div>
    <div class="data-block">
      <div class="db-head">完整原生命令输出（切换标签查看）</div>
      ${tabs}
    </div>`;
}

// ── AIDA64：原生 lshw + sensors + smartctl -x 全量输出 + 摘要 ──
function renderToolboxAidaView() {
  const w = state.wiz.data;
  const raw = w.raw || {};
  const structured = w.structured || {};

  // 快速解析温度/风扇/电压（从 sensors 全文简单抓取行里含 °C / RPM / V 的）
  const sensorsLines = (raw.sensors || '').split(/\r?\n/);
  const quick = { temp: [], fan: [], volt: [], misc: [] };
  sensorsLines.forEach(ln => {
    if (!ln.trim() || /^\s*Adapter\s*:/.test(ln)) return;
    if (/°C|degrees C/.test(ln)) quick.temp.push(ln);
    else if (/RPM/.test(ln)) quick.fan.push(ln);
    else if (/\b[0-9.]+\s*V\b/.test(ln)) quick.volt.push(ln);
    else quick.misc.push(ln);
  });
  const quickKv = (title, arr, empty) => `
    <div class="data-block">
      <div class="db-head">${esc(title)}</div>
      ${arr.length
        ? `<pre class="data-pre">${esc(arr.join('\n'))}</pre>`
        : `<div class="empty">${esc(empty)}</div>`}
    </div>`;

  const smarts = Array.isArray(raw.smart) ? raw.smart : [];
  const smartTabs = rawTabs('aida-smart', smarts.map((s, i) => ({
    key: 'disk' + i,
    label: String(s.name || ('disk' + i)),
    text: String(s.raw || ''),
    size: s.exitCode === 0 ? '' : `exit=${s.exitCode}`,
  })), smarts.length ? 'disk0' : 'disk0');

  const mainTabs = rawTabs('aida-main', [
    { key: 'lshw-short', label: 'lshw -short（设备总览）', text: raw.lshwShort },
    { key: 'lshw',       label: 'lshw（详细层级）', text: raw.lshw },
    { key: 'sensors',    label: 'sensors -A',       text: raw.sensors },
    { key: 'upower',     label: 'upower --dump',    text: raw.upower || '（upower 未安装，改用系统概况「电池」一节）' },
    { key: 'lspci',      label: 'lspci -mm',        text: raw.lspci },
    { key: 'lsusb',      label: 'lsusb',            text: raw.lsusb },
    { key: 'ip-link',    label: 'ip -d link',       text: raw.ipLink },
    { key: 'ip-addr',    label: 'ip addr show',     text: raw.ipAddr },
  ], 'lshw-short');

  // lshw -short 的第 3-4 列最关键，直接给出摘要
  const shortSummary = (raw.lshwShort || '').split(/\r?\n/)
    .filter(l => l.trim() && !l.match(/^H\/W path|^Device/i))
    .slice(0, 40).join('\n');

  $('#wizBody').innerHTML = `
    <div class="data-block">
      <div class="db-head">设备总览（lshw -short，前 40 行）</div>
      <pre class="data-pre">${esc(shortSummary || '（无输出）')}</pre>
    </div>
    ${quickKv('温度（快速抓取含 °C 的行）', quick.temp,
      '传感器没输出温度行。请打开右边「sensors -A」标签查看完整原始输出。')}
    ${quickKv('风扇转速（含 RPM）', quick.fan, '无风扇数据。')}
    ${quickKv('电压（含 V）', quick.volt, '无电压数据。')}
    <div class="data-block">
      <div class="db-head">硬盘 SMART（每块盘一张标签，smartctl -x 完整扩展输出）</div>
      ${smarts.length
        ? smartTabs
        : '<div class="empty">没有检测到磁盘，或缺少 smartctl 工具。</div>'}
    </div>
    <div class="data-block">
      <div class="db-head">完整原生命令输出（切换标签查看）</div>
      ${mainTabs}
    </div>`;
}

function toolboxHddStepPick() {
  const w = state.wiz;
  const disks = state.disks || [];
  const rows = disks.length
    ? renderDiskList(disks, d => ({ tag: `${d.size || ''}${d.kind ? ' · ' + d.kind : ''}` }), w.targetDisk, 'tbx-hdd')
    : '<div class="errbox">没有检测到磁盘。</div>';
  $('#wizBody').innerHTML = `
    <div class="wiz-hint">要测哪块盘？顺序大文件读会读前 2GB，全程<b>只读</b>不写入。</div>
    ${rows}`;
  bindDiskPick('tbx-hdd', name => { w.targetDisk = name; toolboxHddStepPick(); });
  foot(`<button class="btn" id="wBack">返回</button>
        <div class="summary">${w.targetDisk ? '已选：' + esc(w.targetDisk) : '尚未选择'}</div>
        <div class="spacer"></div>
        <button class="btn primary" id="wNext" ${w.targetDisk ? '' : 'disabled'}>下一步</button>`);
  $('#wBack').onclick = renderHome;
  $('#wNext').onclick = () => goStep(2);
}

function toolboxHddStepConfirm() {
  const w = state.wiz;
  const d = (state.disks || []).find(x => x.name === w.targetDisk) || {};
  $('#wizBody').innerHTML = `
    <div class="wiz-hint">将在 <b>${esc(w.targetDisk)}</b> 上执行：</div>
    <div class="data-block">
      <div class="db-head">测试项（同步执行，完整原生输出直接返回）</div>
      <pre class="data-pre">hdparm -T ${esc(w.targetDisk)}   # 缓存读取（控制器+总线）
hdparm -t ${esc(w.targetDisk)}   # 磁盘缓冲读取（真实盘）
dd if=${esc(w.targetDisk)} of=/dev/null bs=1M count=2048 iflag=direct status=progress   # 顺序直读 2GB</pre>
    </div>
    <div class="kv" style="margin-top:10px">
      <div><span>磁盘型号</span><b>${esc(d.model || '—')}</b></div>
      <div><span>容量 / 介质</span><b>${esc(d.size || '—')}${d.kind ? ' · ' + esc(d.kind) : ''}</b></div>
    </div>
    <div class="hnote" style="margin-top:10px">全程<b>只读</b>；整块 SSD 顺序读一遍约 20~60 秒，进度会直接写到下方原生输出里。</div>`;
  foot(`<button class="btn" id="wBack">上一步</button>
        <div class="spacer"></div>
        <button class="btn primary" id="wGo">开始测试</button>`);
  $('#wBack').onclick = () => goStep(1);
  $('#wGo').onclick = async () => {
    w.submitErr = null; w.data = null;
    goStep(3);
    const url = `/api/toolbox/hdparm?dev=${encodeURIComponent(w.targetDisk)}`;
    const data = await get(url);
    if (!state.wiz || state.wiz !== w) return;
    if (!data || !data.ok) {
      w.submitErr = (data && data.reason) || '硬盘读速测试失败';
    } else {
      w.data = data;
    }
    renderWizard();
  };
}

function toolboxHddStepRun() {
  const w = state.wiz;
  if (w.submitErr) {
    $('#wizBody').innerHTML = `<div class="errbox">${esc(w.submitErr)}</div>`;
    foot(`<button class="btn" id="wBack">上一步</button>
          <div class="spacer"></div>
          <button class="btn" id="wRetry">重试</button>`);
    $('#wBack').onclick = () => goStep(2);
    $('#wRetry').onclick = () => {
      const btn = document.querySelector('#wGo');
      if (btn) { btn.click(); } else {
        w.submitErr = null; w.data = null; goStep(2); renderWizard();
      }
    };
    return;
  }
  if (!w.data) {
    $('#wizBody').innerHTML = `<div class="empty">正在运行 hdparm + dd…<br>
      顺序读取 2GB，大概 20~60 秒。</div>`;
    foot(`<div class="spacer"></div><button class="btn" id="wBack">上一步</button>`);
    $('#wBack').onclick = () => goStep(2);
    return;
  }
  // 有结果：展示 summary + 每步的原生输出
  const steps = w.data.steps || [];
  const summary = w.data.summary || {};
  const summaryHtml = `
    <div class="data-block">
      <div class="db-head">结果摘要（MB/s，越大越好）</div>
      <div class="kv" style="margin-top:6px">
        <div><span>hdparm -T 缓存读</span><b>${summary.hdparm_cache != null ? esc(String(summary.hdparm_cache)) + ' MB/s' : '—'}</b></div>
        <div><span>hdparm -t 缓冲读</span><b>${summary.hdparm_buf != null ? esc(String(summary.hdparm_buf)) + ' MB/s' : '—'}</b></div>
        <div><span>dd 顺序直读 (2GB)</span><b>${summary.dd_seq != null ? esc(String(summary.dd_seq)) + ' MB/s' : '—'}</b></div>
        <div><span>被测设备</span><b>${esc(w.data.device || '—')}</b></div>
      </div>
    </div>`;
  const tabs = rawTabs('hdd', steps.map((s, i) => ({
    key: 'step' + i,
    label: s.title,
    text: s.output,
    size: s.metricMBs != null ? String(s.metricMBs) + ' MB/s' : (s.exitCode === 0 ? '' : `exit=${s.exitCode}`),
  })), 'step0');
  $('#wizBody').innerHTML = summaryHtml + `
    <div class="data-block">
      <div class="db-head">原生命令输出（每步一张标签）</div>
      ${tabs}
    </div>`;
  foot(`<button class="btn" id="wRetry">换盘/重新运行</button>
        <div class="spacer"></div>
        <button class="btn primary" id="wHome">完成</button>`);
  $('#wRetry').onclick = () => { w.data = null; w.submitErr = null; goStep(1); renderWizard(); };
  $('#wHome').onclick = renderHome;
}

function toolboxRamStepParams() {
  const w = state.wiz;
  $('#wizBody').innerHTML = `
    <div class="wiz-hint">设置 mbw 参数。缓冲区越大越能反映真实应用场景，但会占用更多内存（救援系统在内存里跑）。</div>
    <div class="data-block">
      <div class="db-head">参数</div>
      <div class="form-grid">
        <label>缓冲区大小（MB）
          <input id="wSize" type="number" min="16" max="1024" step="16" value="${w.sizeMb}">
        </label>
        <label>重复次数（越大越稳）
          <input id="wLoop" type="number" min="1" max="10" step="1" value="${w.loop}">
        </label>
      </div>
    </div>
    <div class="hnote" style="margin-top:10px">推荐：默认 128 MB × 3 次约 5~10 秒，结果稳定。</div>`;
  const s = $('#wSize'), l = $('#wLoop');
  if (s) s.onchange = e => w.sizeMb = Math.max(16, Math.min(1024, parseInt(e.target.value || 128, 10) || 128));
  if (l) l.onchange = e => w.loop    = Math.max(1,  Math.min(10,   parseInt(e.target.value || 3,   10) || 3));
  foot(`<button class="btn" id="wBack">返回</button>
        <div class="spacer"></div>
        <button class="btn primary" id="wNext">下一步</button>`);
  $('#wBack').onclick = renderHome;
  $('#wNext').onclick = () => goStep(2);
}

function toolboxRamStepConfirm() {
  const w = state.wiz;
  $('#wizBody').innerHTML = `
    <div class="wiz-hint">将执行 mbw（同步运行，原生 stdout 直接返回）：</div>
    <div class="data-block">
      <div class="db-head">命令</div>
      <pre class="data-pre">mbw -n ${esc(String(w.loop))} -q ${esc(String(w.sizeMb))}
# AVG 行：平均带宽（MCMP / MPROBE / MEMCPY 三种拷贝策略）</pre>
    </div>
    <div class="kv" style="margin-top:10px">
      <div><span>缓冲区 / 次数</span><b>${w.sizeMb} MB × ${w.loop} 次</b></div>
      <div><span>预计耗时</span><b>~ ${Math.max(2, w.loop * Math.round(w.sizeMb / 256) + 3)} 秒</b></div>
    </div>`;
  foot(`<button class="btn" id="wBack">上一步</button>
        <div class="spacer"></div>
        <button class="btn primary" id="wGo">开始测试</button>`);
  $('#wBack').onclick = () => goStep(1);
  $('#wGo').onclick = async () => {
    w.submitErr = null; w.data = null;
    goStep(3);
    const url = `/api/toolbox/mbw?size_mb=${encodeURIComponent(w.sizeMb)}&loop=${encodeURIComponent(w.loop)}`;
    const data = await get(url);
    if (!state.wiz || state.wiz !== w) return;
    if (!data) {
      w.submitErr = 'mbw 调用失败（无返回）';
    } else if (!data.ok) {
      w.submitErr = (data.reason || 'mbw 运行失败') +
        (data.exitCode != null ? ` (exit=${data.exitCode})` : '');
      w.data = data;
    } else {
      w.data = data;
    }
    renderWizard();
  };
}

function toolboxRamStepRun() {
  const w = state.wiz;
  if (!w.data && !w.submitErr) {
    $('#wizBody').innerHTML = `<div class="empty">正在运行 mbw…<br>
      ${w.sizeMb} MB × ${w.loop} 次，约 ${Math.max(2, w.loop * Math.round(w.sizeMb / 256) + 3)} 秒。</div>`;
    foot(`<div class="spacer"></div><button class="btn" id="wBack">上一步</button>`);
    $('#wBack').onclick = () => goStep(2);
    return;
  }
  const d = w.data || {};
  const sum = d.summary || {};
  const rows = ['MCMP', 'MPROBE', 'MEMCPY'].map(k => {
    const v = sum[k];
    return `<div><span>${k}</span><b>${v ? (esc(String(v.value)) + ' ' + esc(v.unit)) : (w.submitErr ? '失败' : '未解析')}</b></div>`;
  }).join('');

  $('#wizBody').innerHTML = `
    <div class="data-block">
      <div class="db-head">结果摘要（mbw AVG 带宽，越大越好）</div>
      ${w.submitErr ? `<div class="errbox">${esc(w.submitErr)}</div>` : ''}
      <div class="kv" style="margin-top:6px">${rows}</div>
      <div class="kv" style="margin-top:10px">
        <div><span>参数</span><b>${(d.params ? d.params.size_mb : w.sizeMb) + ' MB × ' +
          (d.params ? d.params.loop : w.loop) + ' 次'}</b></div>
        <div><span>命令</span><b><code>${esc(d.argv || `mbw -n ${w.loop} -q ${w.sizeMb}`)}</code></b></div>
      </div>
    </div>
    <div class="data-block">
      <div class="db-head">mbw 原生输出</div>
      <pre class="data-pre">${esc(d.raw || (w.submitErr ? '（无输出）' : '（无输出）'))}</pre>
    </div>`;
  foot(`<button class="btn" id="wRetry">换参数重试</button>
        <div class="spacer"></div>
        <button class="btn primary" id="wHome">完成</button>`);
  $('#wRetry').onclick = () => { w.data = null; w.submitErr = null; goStep(1); renderWizard(); };
  $('#wHome').onclick = renderHome;
}

// 渲染磁盘列表（多选一）—— 与 hwdiagStepSelect 同结构，避免重复逻辑
function renderDiskList(disks, decorate, activeName, pickName) {
  return `<ul class="vol-list">${disks.map(d => {
    const dec = decorate ? decorate(d) || {} : {};
    const active = activeName === d.name;
    return `
      <li class="vol-item ${active ? 'active' : ''}" data-${pickName}="${esc(d.name)}">
        <div class="vol-name">${esc(d.name)}${d.model ? ' · ' + esc(d.model) : ''}</div>
        <div class="vol-meta">${dec.tag || ''}</div>
        ${active ? '<div class="vol-check">✓</div>' : ''}
      </li>`;
  }).join('')}</ul>`;
}

function bindDiskPick(pickName, cb) {
  document.querySelectorAll(`[data-${pickName}]`).forEach(el => {
    el.addEventListener('click', () => cb(el.getAttribute(`data-${pickName}`)));
  });
}

// ── SSH 设置 + 远程协助（共用状态加载） ─────────────────────────
function openSsh() {
  state.wiz = {
    kind: 'ssh', step: 1, steps: ['服务状态'],
    ssh: null, busy: false, pwd: '', pwd2: '', msg: null, msgErr: null,
  };
  $('#backBtn').hidden = false;
  renderWizard();
  loadSshStatus();
}

function openRemote() {
  state.wiz = {
    kind: 'remote', step: 1, steps: ['连接信息'],
    ssh: null, busy: false, pwd: '', pwd2: '', msg: null, msgErr: null,
  };
  $('#backBtn').hidden = false;
  renderWizard();
  loadSshStatus();
}

async function loadSshStatus() {
  const w = state.wiz;
  const { status, data } = await call('/api/ssh');
  if (!state.wiz || state.wiz !== w) return;
  w.ssh = (status === 200) ? data : null;
  renderWizard();
}

async function sshAction(w, body) {
  w.busy = true; w.msg = null; w.msgErr = null;
  renderWizard();
  const { status, data } = await call('/api/ssh', 'POST', body);
  w.busy = false;
  if (status === 200 && data.ok) {
    w.ssh = data.status || w.ssh;
    if (body.action === 'setpass') w.msg = '密码已设置。';
    else w.msg = body.action === 'start' ? 'SSH 服务已开启。' : 'SSH 服务已停止。';
  } else {
    w.msgErr = (data && data.reason) || `操作失败（HTTP ${status}）`;
  }
  renderWizard();
}

// SSH 面板（ssh / remote 两个向导共用）
function sshPanelHtml(w, isRemote) {
  const s = w.ssh;
  if (!s) return '<div class="empty">正在读取 SSH 状态…</div>';

  if (!s.installed) {
    return `<div class="errbox">这个救援系统没有内置 SSH 服务。
      请从官网获取包含 SSH 的完整版本。</div>`;
  }

  const ips = (s.ips || []).length ? s.ips : ['（网络未连接，先到「通信 → 网络设置」联网）'];
  const cmd = ips[0].startsWith('（')
    ? `ssh root@<本机IP>` : `ssh root@${esc(ips[0])}`;

  let html = `
    <div class="data-block">
      <div class="db-head">服务状态</div>
      <div class="kv">
        <div><span>SSH 服务</span><b style="color:${s.active ? 'var(--ok, green)' : 'var(--err, #b00)'}">${s.active ? '运行中' : '已停止'}</b></div>
        <div><span>端口</span><b>${s.port}</b></div>
        <div><span>本机地址</span><b>${ips.map(esc).join(' · ')}</b></div>
        <div><span>root 密码</span><b>${s.rootPasswordSet ? '已设置' : '未设置（必须设一个才能登录）'}</b></div>
      </div>
    </div>`;

  if (isRemote) {
    html += `
    <div class="data-block">
      <div class="db-head">把下面的信息告诉技术人员</div>
      <div class="kv">
        <div><span>对方在他们的电脑上输入</span><b><code>${cmd}</code></b></div>
        <div><span>密码</span><b>就是下面设置的那个</b></div>
      </div>
      <div class="hnote" style="margin-top:8px">
        对方连上后看到的就是这台机器救援系统的命令行，可以执行所有救援操作。
        两台设备必须在同一个局域网（同一台路由器）里。
      </div>
    </div>`;
  }

  html += `
    <div class="data-block">
      <div class="db-head">设置 root 登录密码</div>
      <div class="form-grid">
        <label>新密码（6-64 位）<input id="wPwd" type="password" value="${esc(w.pwd)}" placeholder="字母、数字、符号"></label>
        <label>再输一遍<input id="wPwd2" type="password" value="${esc(w.pwd2)}"></label>
      </div>
    </div>`;

  if (w.msg) html += `<div class="hnote" style="margin-top:10px">${esc(w.msg)}</div>`;
  if (w.msgErr) html += `<div class="errbox" style="margin-top:10px">${esc(w.msgErr)}</div>`;

  html += `<div class="hwarn" style="margin-top:10px">
    救援系统运行在内存里，密码与服务状态重启即失效。
    用完记得点「停止服务」。</div>`;
  return html;
}

function bindSshPanel(w) {
  const p1 = $('#wPwd'), p2 = $('#wPwd2');
  if (p1) p1.oninput = e => w.pwd = e.target.value;
  if (p2) p2.oninput = e => w.pwd2 = e.target.value;

  const startBtn = $('#wSshStart'), stopBtn = $('#wSshStop'), setBtn = $('#wSshPass');
  if (startBtn) startBtn.onclick = () => sshAction(w, { action: 'start' });
  if (stopBtn) stopBtn.onclick = () => sshAction(w, { action: 'stop' });
  if (setBtn) setBtn.onclick = () => {
    if (!w.pwd || w.pwd.length < 6) { w.msgErr = '密码至少 6 位。'; renderWizard(); return; }
    if (w.pwd !== w.pwd2) { w.msgErr = '两次输入的密码不一样。'; renderWizard(); return; }
    sshAction(w, { action: 'setpass', password: w.pwd });
  };
}

function renderSshWizard() {
  const w = state.wiz;
  $('#catTitle').textContent = 'SSH 设置';
  $('#catDesc').textContent = '开启 SSH 服务、设置登录密码。';
  $('#contentBody').innerHTML = wizardShell(renderStepsBar(w));
  $('#wizBody').innerHTML = sshPanelHtml(w, false);
  bindSshPanel(w);
  foot(`<button class="btn" id="wSshStop" ${w.ssh && w.ssh.active && !w.busy ? '' : 'disabled'}>停止服务</button>
        <button class="btn" id="wSshPass" ${w.busy ? 'disabled' : ''}>保存密码</button>
        <div class="spacer"></div>
        <button class="btn primary" id="wSshStart" ${w.busy ? 'disabled' : ''}>${w.ssh && w.ssh.active ? '重启服务' : '开启服务'}</button>`);
  $('#wBack') && ($('#wBack').onclick = renderHome);
}

function renderRemoteWizard() {
  const w = state.wiz;
  $('#catTitle').textContent = '远程协助';
  $('#catDesc').textContent = '让技术人员通过网络连接到这台机器。';
  $('#contentBody').innerHTML = wizardShell(renderStepsBar(w));
  $('#wizBody').innerHTML = sshPanelHtml(w, true);
  bindSshPanel(w);
  const active = w.ssh && w.ssh.active && w.ssh.rootPasswordSet;
  foot(`<button class="btn" id="wSshStop" ${w.ssh && w.ssh.active && !w.busy ? '' : 'disabled'}>停止服务</button>
        <div class="spacer"></div>
        <button class="btn primary" id="wSshStart" ${w.busy ? 'disabled' : ''}>${active ? '服务运行中' : (w.ssh && w.ssh.active ? '已开启，去设密码 →' : '一键开启协助')}</button>`);
  if ($('#wSshStart')) $('#wSshStart').onclick = async () => {
    if (!w.ssh || !w.ssh.active) { await sshAction(w, { action: 'start' }); }
    else if (!w.ssh.rootPasswordSet) {
      // 开着但没密码：引导填密码
      const el = $('#wPwd');
      if (el) { el.focus(); w.msgErr = '服务已开启，请先设置密码才能远程登录。'; renderWizard(); }
    }
  };
  $('#wBack') && ($('#wBack').onclick = renderHome);
}

// ── 网络代理：单页设置（应急访问 GitHub 等场景） ─────────────────
async function openProxy() {
  if (!(await ensureNetwork('网络代理'))) return;
  state.wiz = {
    kind: 'proxy', step: 1, steps: ['代理设置'],
    proxy: '', loaded: false, busy: false, msg: null, msgErr: null,
  };
  $('#backBtn').hidden = false;
  renderWizard();
  loadProxy();
}

async function loadProxy() {
  const w = state.wiz;
  const { status, data } = await call('/api/proxy');
  if (!state.wiz || state.wiz !== w) return;
  if (status === 200) w.proxy = data.proxy || '';
  w.loaded = true;
  renderWizard();
}

function renderProxyWizard() {
  const w = state.wiz;
  $('#catTitle').textContent = '网络代理';
  $('#catDesc').textContent = '设置应急代理，访问 GitHub 等被卡站点。';
  $('#contentBody').innerHTML = wizardShell(renderStepsBar(w));

  if (!w.loaded) {
    $('#wizBody').innerHTML = '<div class="empty">正在读取代理状态…</div>';
    foot(`<div class="spacer"></div><button class="btn" id="wBack">返回</button>`);
    $('#wBack').onclick = renderHome;
    return;
  }

  const on = !!w.proxy;
  $('#wizBody').innerHTML =
    `<div class="data-block">
       <div class="db-head">当前状态</div>
       <div class="kv">
         <div><span>HTTP 代理</span><b>${on ? '已设置：' + esc(w.proxy) : '未设置（直连）'}</b></div>
       </div>
     </div>
     <div class="data-block">
       <div class="db-head">设置代理</div>
       <div class="form-grid">
         <label>代理地址（host:port）<input id="wProxy" value="${esc(w.proxy)}"
                placeholder="如 192.168.1.1:7890"></label>
       </div>
       <div class="hnote" style="margin-top:8px">
         写路由器或局域网里已有的代理服务地址。设置后浏览器、curl/wget、apt 都走它。
       </div>
     </div>
     ${w.msg ? `<div class="hnote" style="margin-top:10px">${esc(w.msg)}</div>` : ''}
     ${w.msgErr ? `<div class="errbox" style="margin-top:10px">${esc(w.msgErr)}</div>` : ''}
     <div class="hnote" style="margin-top:10px">
       代理设置保存在内存里，重启救援环境自动清除。</div>`;

  $('#wProxy').oninput = e => w.proxy = e.target.value;
  foot(`<button class="btn" id="wClear" ${on && !w.busy ? '' : 'disabled'}>清除代理</button>
        <div class="spacer"></div>
        <button class="btn primary" id="wSave" ${w.busy ? 'disabled' : ''}>保存并启用</button>`);
  $('#wClear').onclick = async () => {
    w.busy = true; w.msgErr = null; renderWizard();
    const { status, data } = await call('/api/proxy', 'POST', { proxy: '' });
    w.busy = false;
    if (status === 200 && data.ok) { w.proxy = ''; w.msg = '代理已清除，恢复直连。'; }
    else w.msgErr = (data && data.reason) || '操作失败';
    renderWizard();
  };
  $('#wSave').onclick = async () => {
    const p = (w.proxy || '').trim();
    if (!p) { w.msgErr = '请填写代理地址，或点「清除代理」。'; renderWizard(); return; }
    if (!/^[!-~]{3,128}$/.test(p)) { w.msgErr = '地址只能用字母数字符号（host:port）。'; renderWizard(); return; }
    w.busy = true; w.msg = null; w.msgErr = null; renderWizard();
    const { status, data } = await call('/api/proxy', 'POST', { proxy: p });
    w.busy = false;
    if (status === 200 && data.ok) w.msg = '代理已启用。新打开的浏览器会自动走代理。';
    else w.msgErr = (data && data.reason) || '操作失败';
    renderWizard();
  };
  $('#wBack') && ($('#wBack').onclick = renderHome);
}

// ── Linux 工具：选分区 → （密码） → 确认 → 执行 ─────────────────
const LINUXFIX_CMDS = {
  'dpkg-fix': ['dpkg --configure -a', 'apt-get install -f -y', 'apt-get update'],
  'wifi-firmware': ['apt-get update', 'apt-get install --reinstall -y linux-firmware wireless-regdb', 'rfkill unblock all'],
  'nomodeset': ['sed -i …/etc/default/grub（加 nomodeset）', 'grub-mkconfig -o /boot/grub/grub.cfg'],
  'sound': ['apt-get update', 'apt-get install --reinstall -y alsa-utils pipewire pipewire-pulse wireplumber', 'rm -rf /var/lib/wireplumber'],
  'nvidia-clean': ['apt-get purge -y nvidia-driver-* libnvidia-*', 'apt-get autoremove -y', 'update-initramfs -u'],
  'grub-rebuild': ['grub-install --target=x86_64-efi --efi-directory=/boot/efi', 'grub-mkconfig -o /boot/grub/grub.cfg'],
  'passwd-root': ['chpasswd（改 root 密码）'],
  'fsck-repair': ['fsck -y <目标分区>'],
};

function openLinuxfix(fixId) {
  const cat = CATEGORIES.find(c => c.id === 'linuxtools');
  const task = cat && cat.tasks.find(t => t.id === fixId);
  state.wiz = {
    kind: 'linuxfix', step: 1,
    steps: ['选择 Linux 分区', '确认执行', '执行中'],
    fixId, fixTitle: task ? task.title : fixId,
    targetDev: null, newPwd: '', newPwd2: '',
    jobId: null, job: null, submitErr: null, poll: null,
  };
  $('#backBtn').hidden = false;
  renderWizard();
}

function renderLinuxfixWizard() {
  const w = state.wiz;
  $('#catTitle').textContent = w.fixTitle;
  $('#catDesc').textContent = 'Linux 工具 · 修复目标 Linux 系统。';
  $('#contentBody').innerHTML = wizardShell(renderStepsBar(w));
  ({ 1: lfStepSelect, 2: lfStepConfirm, 3: stepRunGeneric })[w.step]();
}

function lfStepSelect() {
  const w = state.wiz;
  const vols = allVolumes().filter(v =>
    /ext4|ext3|ext2|btrfs/i.test(v.fstype || '') && !v.isKlaRescue);
  let pwdHtml = '';
  if (w.fixId === 'passwd-root') {
    pwdHtml = `
     <div class="data-block" style="margin-top:12px">
       <div class="db-head">新密码</div>
       <div class="form-grid">
         <label>新 root 密码（6-64 位）<input id="wPwd" type="password" value="${esc(w.newPwd)}"></label>
         <label>再输一遍<input id="wPwd2" type="password" value="${esc(w.newPwd2)}"></label>
       </div>
     </div>`;
  }
  $('#wizBody').innerHTML =
    `<div class="wiz-hint">要修复哪个 Linux 分区？（就是平时开机进的那个系统）</div>
     ${vols.length
       ? renderVolList(vols, v => ({ tag: v.fstype.toUpperCase() }), w.targetDev, 'lf-dev')
       : '<div class="errbox">没有找到 Linux 分区（ext4/btrfs）。先确认系统还在这块盘上。</div>'}
     ${pwdHtml}`;

  bindVolPick('lf-dev', name => { w.targetDev = name; lfStepSelect(); });
  const p1 = $('#wPwd'), p2 = $('#wPwd2');
  if (p1) p1.oninput = e => w.newPwd = e.target.value;
  if (p2) p2.oninput = e => w.newPwd2 = e.target.value;

  const needPwd = w.fixId === 'passwd-root' && (!w.newPwd || w.newPwd !== w.newPwd2);
  foot(`<button class="btn" id="wBack">返回</button>
        <div class="summary">${w.targetDev ? '已选：' + esc(w.targetDev) : '尚未选择'}</div>
        <div class="spacer"></div>
        <button class="btn primary" id="wNext" ${w.targetDev && !needPwd ? '' : 'disabled'}>下一步</button>`);
  $('#wBack').onclick = renderHome;
  $('#wNext').onclick = () => goStep(2);
}

function lfStepConfirm() {
  const w = state.wiz;
  const cmds = LINUXFIX_CMDS[w.fixId] || [];
  $('#wizBody').innerHTML =
    `<div class="wiz-hint">将在 <b>${esc(w.targetDev)}</b> 上执行「${esc(w.fixTitle)}」：</div>
     <div class="data-block">
       <div class="db-head">将要执行的命令</div>
       <pre class="data-pre">${esc(cmds.join('\n'))}</pre>
     </div>
     ${w.fixId === 'passwd-root'
       ? '<div class="hwarn" style="margin-top:10px">root 密码将被改为你刚才填的那个。</div>' : ''}
     <div class="hwarn" style="margin-top:10px">
       修复过程会以读写方式挂载该分区并在其中执行命令。执行前请确认已选中正确的分区。
       ${/wifi|sound|nvidia/.test(w.fixId) ? '此修复需要联网，请先连好网络。' : ''}
     </div>`;
  foot(`<button class="btn" id="wBack">上一步</button>
        <div class="spacer"></div>
        <button class="btn primary" id="wGo">开始修复</button>`);
  $('#wBack').onclick = () => goStep(1);
  $('#wGo').onclick = async () => {
    const args = { fix_id: w.fixId, target_dev: w.targetDev, confirm: 'YES',
                   new_password: w.fixId === 'passwd-root' ? w.newPwd : null };
    const { status, data } = await call('/api/jobs', 'POST', { type: 'linuxfix', args });
    if (status !== 200 || !data.ok) {
      w.submitErr = data.reason || `提交失败（HTTP ${status}）`;
    } else {
      w.jobId = data.job.id; w.job = data.job;
      w.poll = setInterval(pollJob, 800);
    }
    goStep(3);
  };
}

// ── 安装全新系统：选 ISO → 选目标盘 → 输入 ERASE 确认 → 执行 ──
function openOsinstall() {
  state.wiz = {
    kind: 'osinstall', step: 1,
    steps: ['选 ISO 所在分区', '选 ISO 文件', '选目标盘', '确认', '制作中'],
    isoDev: null, cwd: '/', entries: [], loading: false, browseErr: null,
    isoPath: null, isoSize: 0, targetDisk: null, confirmText: '',
    keepKlaGrub: true,
    jobId: null, job: null, submitErr: null, poll: null,
  };
  $('#backBtn').hidden = false;
  renderWizard();
}

function renderOsinstallWizard() {
  const w = state.wiz;
  $('#catTitle').textContent = '安装全新系统';
  $('#catDesc').textContent = '把 ISO 制作成可启动的系统安装盘。';
  $('#contentBody').innerHTML = wizardShell(renderStepsBar(w));
  ({ 1: osiStepDev, 2: osiStepBrowse, 3: osiStepDisk, 4: osiStepConfirm, 5: stepRunGeneric })[w.step]();
}

function osiStepDev() {
  const w = state.wiz;
  const vols = allVolumes().filter(v => v.fstype && !v.isKlaRescue);
  $('#wizBody').innerHTML =
    `<div class="wiz-hint">ISO 文件在哪个分区上？（U 盘、移动硬盘、或电脑里的数据分区）</div>
     ${renderVolList(vols, v => {
       if (v.isWindows) return { tag: 'Windows 数据' };
       return { tag: v.fstype.toUpperCase() };
     }, w.isoDev, 'osi-dev')}`;
  bindVolPick('osi-dev', name => { w.isoDev = name; w.cwd = '/'; w.isoPath = null; osiStepDev(); });

  foot(`<button class="btn" id="wBack">返回</button>
        <div class="summary">${w.isoDev ? '已选：' + esc(w.isoDev) : '尚未选择'}</div>
        <div class="spacer"></div>
        <button class="btn primary" id="wNext" ${w.isoDev ? '' : 'disabled'}>下一步</button>`);
  $('#wBack').onclick = renderHome;
  $('#wNext').onclick = () => { goStep(2); osiLoadDir('/'); };
}

async function osiLoadDir(path) {
  const w = state.wiz;
  w.loading = true; w.browseErr = null;
  renderOsinstallWizard();
  const url = `/api/browse?dev=${encodeURIComponent(w.isoDev)}&path=${encodeURIComponent(path)}`;
  const { status, data } = await call(url);
  if (!state.wiz || state.wiz !== w || w.step !== 2) return;
  w.loading = false;
  if (status === 200 && data.ok) {
    w.cwd = path;
    w.entries = data.entries || [];
  } else {
    w.browseErr = data.reason || `读取失败（HTTP ${status}）`;
    w.entries = [];
  }
  renderOsinstallWizard();
}

function osiStepBrowse() {
  const w = state.wiz;
  const segs = w.cwd.split('/').filter(Boolean);
  let acc = '';
  const crumbs = [`<span class="crumb" data-go="/">[${esc(w.isoDev)} 根目录]</span>`].concat(
    segs.map((s, i) => {
      acc = joinPath(acc || '/', s);
      return `<span class="sep">›</span><span class="crumb" data-go="${esc(acc)}">${esc(s)}</span>`;
    })
  ).join('');

  let body;
  if (w.loading) {
    body = '<div class="empty">正在读取…</div>';
  } else if (w.browseErr) {
    body = `<div class="errbox">${esc(w.browseErr)}</div>`;
  } else {
    const dirs = w.entries.filter(e => e.isDir);
    const isos = w.entries.filter(e => !e.isDir && /\.iso$/i.test(e.name));
    const others = w.entries.filter(e => !e.isDir && !/\.iso$/i.test(e.name));
    body = dirs.map(e => {
      const p = joinPath(w.cwd, e.name);
      return `<div class="frow dir" data-cd="${esc(p)}">
        ${icon('folder', 'ico16')}<span class="fname">${esc(e.name)}</span>
        <span class="fsize">目录</span></div>`;
    }).join('') + isos.map(e => {
      const p = joinPath(w.cwd, e.name);
      const on = w.isoPath === p;
      return `<div class="frow ${on ? 'picked' : ''}" data-iso="${esc(p)}" data-size="${e.sizeBytes || 0}">
        ${icon('disc', 'ico16')}<span class="fname">${esc(e.name)}</span>
        <span class="fsize">${fmtSize(e.sizeBytes)}</span></div>`;
    }).join('') + (others.length
      ? `<div class="empty">（该目录还有 ${others.length} 个非 ISO 文件未显示）</div>` : '');
    if (!dirs.length && !isos.length) body = '<div class="empty">这个目录里没有 ISO 文件。</div>';
  }

  $('#wizBody').innerHTML =
    `<div class="brow">
       <div class="crumbs">${crumbs}</div>
       <div class="flist">${body}</div>
     </div>`;

  $('#wizBody').querySelectorAll('.crumb[data-go]').forEach(el => {
    el.onclick = () => osiLoadDir(el.dataset.go);
  });
  $('#wizBody').querySelectorAll('.frow[data-cd]').forEach(el => {
    el.onclick = () => osiLoadDir(el.dataset.cd);
  });
  $('#wizBody').querySelectorAll('.frow[data-iso]').forEach(el => {
    el.onclick = () => { w.isoPath = el.dataset.iso; w.isoSize = +el.dataset.size || 0; osiStepBrowse(); };
  });

  foot(`<button class="btn" id="wBack">上一步</button>
        <div class="summary">${w.isoPath ? '已选：' + esc(w.isoPath.split('/').pop()) : '尚未选择 ISO'}</div>
        <div class="spacer"></div>
        <button class="btn primary" id="wNext" ${w.isoPath ? '' : 'disabled'}>下一步</button>`);
  $('#wBack').onclick = () => goStep(1);
  $('#wNext').onclick = () => goStep(3);
}

function osiStepDisk() {
  const w = state.wiz;
  const disks = (state.disks || []).filter(d => d.name && !w.isoDev.startsWith(d.name));
  if (!disks.length) {
    $('#wizBody').innerHTML = `<div class="errbox">
      没有可作为目标的其他磁盘。目标盘不能是 ISO 所在的那块盘——
      把 ISO 拷到另一个 U 盘再试。</div>`;
    foot(`<button class="btn" id="wBack">上一步</button><div class="spacer"></div>`);
    $('#wBack').onclick = () => goStep(2);
    return;
  }
  $('#wizBody').innerHTML =
    `<div class="wiz-hint">制作到哪块盘？（这块盘会被<b>完全清空</b>，一般用 U 盘）</div>
     <div class="vol-list">${disks.map(d => {
       const on = w.targetDisk === d.name;
       return `<div class="vol ${on ? 'sel' : ''}" data-disk="${esc(d.name)}">
         ${icon('disk', 'ico32')}
         <div>
           <div class="vol-name">${esc(d.model)} <span style="font-weight:normal;opacity:.6">(整块盘 · ${esc(d.name)})</span></div>
           <div class="vol-meta">${fmtSize(d.sizeBytes)} · 已有 ${d.partitions.length} 个分区，将全部删除</div>
         </div>
       </div>`;
     }).join('')}</div>
     <div class="hwarn" style="margin-top:12px">
       目标盘上的<b>所有分区和文件都会被永久删除</b>。拿不准的话先拔掉不想动的那块盘。</div>`;
  $('#wizBody').querySelectorAll('.vol[data-disk]').forEach(el => {
    el.onclick = () => { w.targetDisk = el.dataset.disk; osiStepDisk(); };
  });
  foot(`<button class="btn" id="wBack">上一步</button>
        <div class="summary">${w.targetDisk ? '目标：' + esc(w.targetDisk) : '尚未选择'}</div>
        <div class="spacer"></div>
        <button class="btn primary" id="wNext" ${w.targetDisk ? '' : 'disabled'}>下一步</button>`);
  $('#wBack').onclick = () => goStep(2);
  $('#wNext').onclick = () => goStep(4);
}

function osiStepConfirm() {
  const w = state.wiz;
  $('#wizBody').innerHTML =
    `<div class="wiz-hint">最后确认：</div>
     <div class="kv">
       <div><span>ISO 文件</span><b>${esc(w.isoDev)} · ${esc(w.isoPath)}</b></div>
       <div><span>ISO 大小</span><b>${fmtSize(w.isoSize)}</b></div>
       <div><span>目标盘</span><b style="color:var(--err, #b00)">${esc(w.targetDisk)}（整块盘清空）</b></div>
     </div>
     <div class="hnote" style="margin-top:12px;display:flex;gap:8px;align-items:flex-start">
       <input type="checkbox" id="wKeepKla" ${w.keepKlaGrub ? 'checked' : ''}
              style="margin-top:3px">
       <label for="wKeepKla" style="cursor:pointer">安装完成后保持 KARL'S LIGHT 启动菜单为第一启动项
         （推荐。装完新系统后固件常把新系统的引导顶到最前，勾上这个保证
         「开机先进 KLA 菜单」不变。）</label>
     </div>
     <div class="hwarn" style="margin-top:12px">
       这是不可撤销的操作。输入 <b>ERASE</b>（全大写）并点「开始制作」。
       Windows ISO 制作约需 5-20 分钟（取决于 U 盘速度）。</div>
     <div class="form-grid" style="margin-top:12px">
       <label>输入 ERASE 确认<input id="wConfirm" value="${esc(w.confirmText)}" placeholder="ERASE"></label>
     </div>`;
  $('#wKeepKla').onchange = e => w.keepKlaGrub = e.target.checked;
  $('#wConfirm').oninput = e => w.confirmText = e.target.value;
  foot(`<button class="btn" id="wBack">上一步</button>
        <div class="spacer"></div>
        <button class="btn primary" id="wGo" ${w.confirmText === 'ERASE' ? '' : 'disabled'}
                style="background:var(--err, #b00)">开始制作（清空 ${esc(w.targetDisk)}）</button>`);
  $('#wBack').onclick = () => goStep(3);
  $('#wGo').onclick = async () => {
    if (w.confirmText !== 'ERASE') return;
    const { status, data } = await call('/api/jobs', 'POST', {
      type: 'install',
      args: {
        iso_dev: w.isoDev, iso_path: w.isoPath,
        target_disk: w.targetDisk, confirm: 'ERASE',
        keep_kla_grub: w.keepKlaGrub,
      },
    });
    if (status !== 200 || !data.ok) {
      w.submitErr = data.reason || `提交失败（HTTP ${status}）`;
    } else {
      w.jobId = data.job.id; w.job = data.job;
      w.poll = setInterval(pollJob, 800);
    }
    goStep(5);
  };
}

// ── 横幅按钮 / 返回 ─────────────────────────────────────────────
document.querySelectorAll('.banner-btn').forEach(btn => {
  btn.addEventListener('click', async () => {
    const act = btn.dataset.action;
    if (act === 'mode') { showModePick(); return; }
    if (act === 'about') { openAbout(); return; }
    if (act === 'help')  { openHelp();  return; }
    if (act === 'wifi')  { openWifi();  return; }
    if (!leaveWizard()) return;
    // /api/power 调 systemctl reboot / poweroff
    btn.disabled = true;
    await call('/api/power', 'POST', { action: act === 'reboot' ? 'reboot' : 'shutdown' });
    showHelpRaw(act === 'reboot' ? '正在重启' : '正在关机',
                '如果机器没有自动执行，请手动按电源按钮。', 'hnote');
  });
});

// 左栏底部版本行：点击也弹「关于」
const sf = document.querySelector('.sidebar-foot');
if (sf) sf.addEventListener('click', openAbout);

$('#backBtn').addEventListener('click', () => { if (leaveWizard()) renderHome(); });

/* ═══════════════════════════════════════════════════════════════
   模态：关于 / 帮助 / 法律协议
   所有模态都通过 #modalHost 挂载，点遮罩或右上角 × 关闭。
   ═══════════════════════════════════════════════════════════════ */

const KLA_VERSION = '2.0 Public Beta';
const KLA_WEBSITE = 'www.kezdx.com';
const KLA_WEBSITE_URL = 'https://www.kezdx.com';

function closeModal() {
  const host = $('#modalHost');
  if (host) host.innerHTML = '';
  document.body.style.overflow = '';
}
function mountModal(headHtml, bodyHtml, wide, footHtml) {
  const host = $('#modalHost');
  host.innerHTML =
    `<div class="modal-overlay" id="__mOverlay">
       <div class="modal ${wide ? 'wide' : ''}" role="dialog" aria-modal="true">
         <div class="modal-head">
           <h3>${headHtml}</h3>
           <button class="modal-close" title="关闭" id="__mClose">×</button>
         </div>
         <div class="modal-body">${bodyHtml}</div>
         <div class="modal-foot">${footHtml || '<button class="mbtn primary" id="__mOk">关闭</button>'}</div>
       </div>
     </div>`;
  const overlay = $('#__mOverlay');
  const close = () => closeModal();
  $('#__mClose').onclick = close;
  const ok = document.getElementById('__mOk');
  if (ok) ok.onclick = close;
  // 点遮罩空白（不弹内容区）就关
  overlay.addEventListener('mousedown', e => {
    if (e.target.id === '__mOverlay') close();
  });
  // Esc 关
  const onKey = ev => { if (ev.key === 'Escape') { close(); document.removeEventListener('keydown', onKey); } };
  document.addEventListener('keydown', onKey);
}

function openWifi() {
  // Wi-Fi 连接面板。救援系统驱动/固件是齐的，缺的是一个输密码的地方——
  // 没有这个面板，物理机无线用户会以为「网卡没驱动」。
  mountModal('Wi-Fi 连接',
    `<div id="wifiPanel" style="min-width:420px">
       <div id="wifiState" class="hnote" style="margin:0 0 10px">正在扫描附近的网络…</div>
       <div id="wifiList"></div>
     </div>`,
    true,
    `<button class="mbtn" id="wifiRescan">重新扫描</button>
     <button class="mbtn primary" id="__mOk">关闭</button>`);
  $('#__mOk').onclick = closeModal;
  $('#wifiRescan').onclick = () => loadWifi();

  async function loadWifi() {
    const st = $('#wifiState'), list = $('#wifiList');
    st.textContent = '正在扫描附近的网络…';
    list.innerHTML = '';
    let d;
    try {
      const r = await call('/api/wifi');
      d = r.data || {};
      if (r.status !== 200 && !d.reason) d.reason = '后端返回 ' + r.status;
    }
    catch (e) { st.textContent = '扫描失败：' + e; return; }
    if (!d.ok) {
      st.textContent = d.reason || '扫描失败';
      if ((d.networks || []).length === 0) return;
    }
    // 当前联网状态顺手刷出来
    let net = null;
    try { net = (await call('/api/network')).data; } catch (_) {}
    if (net && net.online) st.textContent = '已联网（' + (net.active || []).map(i => i.name).join('、') + '）';
    else st.textContent = (d.ok ? '' : (d.reason + '。')) + '未联网——点下面的网络连接';
    if (!d.networks || d.networks.length === 0) {
      list.innerHTML = '<div class="hnote" style="padding:14px 0">没扫到网络。插网线可以直接用有线；或点「重新扫描」。</div>';
      return;
    }
    d.networks.forEach(n => {
      const row = document.createElement('div');
      row.style.cssText = 'display:flex;align-items:center;gap:10px;padding:10px 8px;' +
        'border-bottom:1px solid rgba(255,255,255,.08);cursor:pointer';
      // 0-100% 信号 → wifi-0/wifi-1/wifi-2/wifi-3（4 档）
      const sig = typeof n.signal === 'number' ? Math.max(0, Math.min(100, n.signal)) : 0;
      const bars = sig === 0 ? 0 : Math.max(0, Math.min(3, Math.floor((sig - 1) / 30)));
      const lock = n.security && n.security !== '开放' ? '🔒 ' : '';
      row.innerHTML =
        '<span style="flex:1">' + lock + esc(n.ssid) +
          (n.inUse ? ' <span style="opacity:.7;font-size:12px">✓ 已连接</span>' : '') + '</span>' +
        '<span style="font-size:12px;opacity:.75">' + esc(n.security || '') + '</span>' +
        '<span style="display:inline-flex;align-items:center;gap:4px" title="信号 ' + sig + '%">' +
          icon('wifi-' + bars, 'ico16') +
          '<span style="opacity:.65;font-size:11px">' + String(sig) + '%</span></span>';
      row.onclick = () => pickWifi(n, row);
      list.appendChild(row);
    });
  }

  function pickWifi(n, row) {
    // 行内展开密码框（开放网络直接连）
    const old = $('#wifiPwdBox'); if (old) old.remove();
    if (!n.security || n.security === '开放') { connectWifi(n.ssid, ''); return; }
    const box = document.createElement('div');
    box.id = 'wifiPwdBox';
    box.style.cssText = 'padding:10px 8px;border-bottom:1px solid rgba(255,255,255,.08)';
    box.innerHTML =
      '<div style="font-size:12px;opacity:.85;margin-bottom:6px">连接「' + esc(n.ssid) + '」，输入密码：</div>' +
      '<div style="display:flex;gap:8px">' +
      '<input id="wifiPwd" type="password" style="flex:1;padding:7px 10px;border-radius:6px;' +
      'border:1px solid rgba(255,255,255,.25);background:rgba(0,0,0,.35);color:#eee" ' +
      'placeholder="Wi-Fi 密码" autocomplete="off">' +
      '<button class="mbtn primary" id="wifiGo">连接</button></div>' +
      '<div id="wifiErr" style="color:#ff8087;font-size:12px;margin-top:6px"></div>';
    row.after(box);
    const inp = box.querySelector('#wifiPwd');
    inp.focus();
    inp.onkeydown = e => { if (e.key === 'Enter') box.querySelector('#wifiGo').click(); };
    box.querySelector('#wifiGo').onclick = () => connectWifi(n.ssid, inp.value, box);
  }

  async function connectWifi(ssid, pwd, box) {
    const st = $('#wifiState');
    const btn = box && box.querySelector('#wifiGo');
    if (btn) btn.disabled = true;
    st.textContent = '正在连接「' + ssid + '」…';
    let d = {};
    try {
      const r = await call('/api/wifi/connect', 'POST', { ssid, password: pwd });
      d = r.data || {};
      if (r.status !== 200 && !d.reason) d.reason = '后端返回 ' + r.status;
    }
    catch (e) {
      st.textContent = '连接失败：' + e;
      if (btn) btn.disabled = false;
      return;
    }
    if (!d.ok) {
      st.textContent = d.reason || '连接失败';
      if (box) {
        const err = box.querySelector('#wifiErr');
        if (err) err.textContent = d.detail || d.reason || '';
        if (btn) btn.disabled = false;
      }
      return;
    }
    st.textContent = '已连接「' + ssid + '」';
    if (box) box.remove();
    loadWifi();
  }

  loadWifi();
}

function openAbout() {
  const body =
    `<div class="about-body">
       <div class="brand-row">
         <img src="assets/logo.png" alt="">
         <div>
           <div class="about-name">KARL'S LIGHT <em style="font-weight:normal;font-style:normal">Access</em></div>
           <div class="about-sub">救 援 与 恢 复 环 境</div>
         </div>
       </div>
       <div class="about-ver">版本 ${esc(KLA_VERSION)}</div>
       <div class="about-copy">
         © KARL'S LIGHT CO., LTD. All Rights Reserved.<br>
         <span class="sub">Designed by KARL'S LIGHT in Guiyang</span>
       </div>
       <div class="link-list">
         <a href="${KLA_WEBSITE_URL}" target="_blank" rel="noopener">
           <span>官方网站</span><span class="lbl">${esc(KLA_WEBSITE)}  ↗</span>
         </a>
         <a href="#" data-doc="help">
           <span>使用帮助文档</span><span class="lbl">查看完整操作手册 →</span>
         </a>
         <a href="#" data-doc="eula">
           <span>最终用户许可协议</span><span class="lbl">查看 →</span>
         </a>
         <a href="#" data-doc="privacy">
           <span>隐私政策</span><span class="lbl">查看 →</span>
         </a>
       </div>
     </div>`;
  mountModal('关于 KARL\'S LIGHT Access', body, false);
  const links = $('#modalHost').querySelectorAll('[data-doc]');
  links.forEach(a => a.addEventListener('click', ev => {
    ev.preventDefault();
    const d = a.dataset.doc;
    if (d === 'eula') openLegal('eula');
    else if (d === 'privacy') openLegal('privacy');
    else if (d === 'help') openHelp();
  }));
}

// ── 轻量 MD → HTML（子集够帮助/协议用）─────────────────────────
function mdToHtml(src) {
  const lines = (src || '').replace(/\r\n?/g, '\n').split('\n');
  const out = [];
  for (let i = 0; i < lines.length; i++) {
    let line = lines[i].replace(/\s+$/, '');
    if (!line) { out.push(''); continue; }
    if (line.startsWith('---')) continue;
    // 表格：跳过表头、分隔、所有行直到空行
    if (line.startsWith('|')) { while (i < lines.length && lines[i].startsWith('|')) i++; i--; continue; }

    let cls = '';
    if (line.startsWith('# '))      { out.push(`<h1>${esc(line.slice(2).trim())}</h1>`); continue; }
    if (line.startsWith('## '))     { out.push(`<h2>${esc(line.slice(3).trim())}</h2>`); continue; }
    if (line.startsWith('### '))    { out.push(`<h3>${esc(line.slice(4).trim())}</h3>`); continue; }
    if (/^\s*\d+\.\s/.test(line))   cls = 'pad';
    if (/^\s*（\d+）/.test(line))   cls = 'pad';
    if (line.startsWith('· ') || line.startsWith('- ')) cls = 'pad';
    // **粗体**（整行）
    if (line.startsWith('**') && line.endsWith('**')) {
      line = line.slice(2, -2).trim();
      out.push(`<p class="${cls}"><b>${esc(line)}</b></p>`);
      continue;
    }
    out.push(`<p class="${cls}">${esc(line)}</p>`);
  }
  return out.join('\n');
}

async function openHelp() {
  let html = '';
  try {
    const r = await fetch('help.html');
    if (r.ok) html = await r.text();
  } catch {}
  if (!html) {
    html = `<p style="color:var(--err)">帮助文档缺失。请联系官方获取完整版本。</p>`;
  }
  mountModal('KARL\'S LIGHT Access — 使用帮助文档', html, true);
}

async function openLegal(kind) {
  const map = {
    eula:    { file: 'eula-zh.html',    title: '最终用户许可协议', md: 'eula-zh.md'    },
    privacy: { file: 'privacy-zh.html', title: '软件隐私政策',     md: 'privacy-zh.md' },
  };
  const info = map[kind] || map.eula;
  let html = '';
  // 注意取文件顺序：必须先 .md 后 .html。eula/privacy-zh.html 是「直接用
  // 浏览器打开」的独立文档，里面有 <script> 异步拉 md 渲染；模态用
  // innerHTML 注入时 <script> 不会执行，#docBody 就永远停在「正在加载…」
  // ——这正是「关于里两个协议一直加载中」的根因。md 直渲没有这个问题。
  try {
    const r = await fetch(info.md);
    if (r.ok) html = mdToHtml(await r.text());
  } catch {}
  if (!html) {
    try {
      const r = await fetch(info.file);
      if (r.ok) {
        let t = await r.text();
        // 兜底：剥掉独立文档的骨架（script/head/backbar），只留正文，
        // 并把它内联样式类收编到 legal-body 里
        const m = t.match(/<div id="docBody">[\s\S]*?<\/div>\s*<footer/);
        html = m ? m[1] : t;
      }
    } catch {}
  }
  if (!html) html = `<p style="color:var(--err)">文档缺失。</p>`;
  mountModal(info.title, `<div class="legal-body">${html}</div>`, true);
}


// ── 启动 ────────────────────────────────────────────────────────
(async function boot() {
  // 进入主页前的硬件检查动画：壁纸 + 文本框 + 进度条。
  // 接口实际是并行发的（后端都是本地只读探测，快），但 UI 按步骤
  // 报进度——用户要的是「它在检查」的可视反馈，不是跑分。
  // 每步垫 300ms 让进度条走得出来；总时长约 1.5s。
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const tip = document.getElementById('splashTip');
  const fill = document.getElementById('splashFill');
  const setStep = (text, pct) => {
    if (tip) tip.textContent = text;
    if (fill) fill.style.width = pct + '%';
  };

  setStep('正在检测系统与固件信息…', 10);
  const sysP = get('/api/system');
  await Promise.all([sysP, sleep(320)]);

  setStep('正在识别磁盘与分区…', 30);
  const disksP = get('/api/disks');
  await Promise.all([disksP, sleep(320)]);

  setStep('正在检测处理器、内存与显卡…', 52);
  await sleep(340);

  setStep('正在探测网络环境…', 72);
  const netP = get('/api/network');
  const toolsP = get('/api/tools');
  await Promise.all([netP, toolsP, sleep(320)]);

  setStep('正在加载备份索引…', 90);
  const backupsP = get('/api/backups');
  await Promise.all([backupsP, sleep(300)]);

  setStep('检查完成，正在进入救援系统…', 100);
  await sleep(280);

  const [sys, disks, net, tools, backups] =
    await Promise.all([sysP, disksP, netP, toolsP, backupsP]);
  state.tools = tools || {};
  state.disks = (disks && disks.disks) || [];
  loadMode();
  if (!state.mode) {
    // 第一次进（或救援环境重启后）：先选救援目标系统，主界面按选择渲染。
    // splash 先拿掉，模式选择层就是新的"第一屏"。
    $('#splash').remove();
    showModePick();
    updateModeLabel();
    startWifiBannerPolling();
    renderStatus({ sys, net, backups });
    return;
  }
  // 老用户直接落在系统概况——硬件检查的结果页
  state.current = 'overview';
  updateModeLabel();
  startWifiBannerPolling();
  renderHome();
  renderStatus({ sys, net, backups });

  // 直接移除（不依赖 setTimeout）：之前用 setTimeout(400) 做渐隐，但无头
  // 测试的 driver 在 nav 渲染后立即断言，会抢在 timer fire 前检查 #splash，
  // 误报"还在"。PLAIN 的 --dump-dom 能等到 timer fire，driver 不会——两者
  // 不一致。index.html 注释本就写"拿到接口数据后移除"，渐隐是过度设计。
  $('#splash').remove();
})();
