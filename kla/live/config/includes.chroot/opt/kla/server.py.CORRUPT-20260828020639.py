// 直接用 Python 代码整体重写 server.py 的 def _mount_ro 函数（L338..L426）。
// 用法：Run csc to compile to exe then exe.
using System;
using System.IO;
using System.Text;

class P
{
  static int Main(string[] args)
  {
    string file = args[0];
    string src = File.ReadAllText(file);
    // Find anchor: "def _mount_ro(dev, mnt):\n"
    string anchor = "def _mount_ro(dev, mnt):";
    int i = src.IndexOf(anchor, StringComparison.Ordinal);
    if (i < 0) { Console.Error.WriteLine("anchor not found"); return 1; }
    // Find next def (next function) at column 0: "^def "  AFTER i. Or until next "\ndef " on column 0.
    int j = src.IndexOf("\ndef ", i + anchor.Length);
    if (j < 0) { j = src.IndexOf("\n_PROBE_MNT_COUNTER", i); }
    if (j < 0) { Console.Error.WriteLine("end not found"); return 2; }
    int start = i;
    int end = j; // index of "\n" before next def; keep leading \n
    string new_body =
@"def _mount_ro(dev, mnt):
    r""""""只读挂载。失败返回 False，绝不抛异常。

    一律只读：救援环境里误写用户数据卷是不可接受的，写操作等到用户
    在 UI 上明确确认了某个动作再单独挂载。

    2026-08-28 v2 多盘/G盘挂载兜底（解决用户：备份到G盘 Windows OK / 救援端
    chains 为空的根因：NTFS dirty+hiberfil.sys 挂载失败）：
      · 原实现裸写 mount -o ro dev mnt（不指定 -t），Windows NTFS 卷 dirty
        必挂不上（快速启动留下 hiberfil.sys），导致 G 盘直接不进
        find_all_kla_stores() hits 列表；最佳命中退回空 D:\KLA，
        chains=[]，UI 报错 "index.json 里没有任何备份链"。
      · 现在：先 blkid 拿真实 fstype；对 NTFS 跑 6 级 fallback（只读）：
          1) ntfs-3g ro,norecover,umask=0222        (不回放日志、最安全)
          2) ntfs-3g ro,remove_hiberfile,umask=0222 (删休眠标记)
          3) mount -t ntfs3 ro,force                 (Paragon ntfs3 内核)
          4) mount -t ntfs ro                        (老内核 ntfs)
          5) mount -o ro,force                       (通用强制)
          6) mount -o ro                             (兜底)
      · 非 NTFS：mount -t <probe_fs> ro 先，再 fallback 通用 ro。
    r""""""
    try:
        os.makedirs(mnt, exist_ok=True)
    except OSError:
        return False
    if os.path.ismount(mnt):
        return True

    # 1) blkid 拿 fstype（run(cmd,timeout) 返回 stdout；失败 ""；解析最后一行 TYPE 值）
    probe_fs = None
    try:
        out = run(["blkid", "-s", "TYPE", "-o", "value", dev], timeout=10) or ""
        if out:
            last = out.strip().splitlines()[-1].strip().lower()
            if last: probe_fs = last
    except Exception:
        probe_fs = None

    def try_mnt(cmd):
        try:
            # subprocess.run 不通过全局 run()，因为全局 run 只返回 stdout，
            # 无法区分 mount 输出空的成功 vs 失败。直接 subprocess.run 调。
            subprocess.run(cmd, capture_output=True, text=True, timeout=25, check=False)
            return os.path.ismount(mnt)
        except Exception:
            return False

    # ── NTFS 6 级 fallback ─────────────────────────────────────────
    if probe_fs == "ntfs":
        if try_mnt(["ntfs-3g", "-o", "ro,norecover,umask=0222,fmask=0222", dev, mnt]):
            return True
        if try_mnt(["ntfs-3g", "-o", "ro,remove_hiberfile,umask=0222,fmask=0222", dev, mnt]):
            return True
        if try_mnt(["mount", "-t", "ntfs3", "-o", "ro,force", dev, mnt]):
            return True
        if try_mnt(["mount", "-t", "ntfs", "-o", "ro", dev, mnt]):
            return True
        if try_mnt(["mount", "-o", "ro,force", dev, mnt]):
            return True
        if try_mnt(["mount", "-o", "ro", dev, mnt]):
            return True
        return False

    # ── 非 NTFS：先 -t probe_fs，再兜底 ro ──────────────────────────
    if probe_fs and probe_fs not in ("", "unknown"):
        if try_mnt(["mount", "-t", probe_fs, "-o", "ro", dev, mnt]):
            return True
    try:
        subprocess.run(["mount", "-o", "ro", dev, mnt],
                       capture_output=True, text=True, timeout=20, check=False)
    except Exception:
        pass
    return os.path.ismount(mnt)

";
    string prefix = src.Substring(0, start);
    string suffix = src.Substring(end);
    // suffix 以 "\n" 开头；new_body 以 "def ..." 开头。组合：prefix + new_body + suffix
    File.WriteAllText(file, prefix + new_body + suffix, new UTF8Encoding(false));
    Console.WriteLine("OK: _mount_ro rewritten. New size={0}", File.ReadAllText(file).Length);
    return 0;
  }
}
