/*
 * ==================================================================
 * 存档管理 (Services/ArchiveService.cs) — S1A5 版
 * ==================================================================
 * S1A5 的存档是【三件套】:
 *     s1a5.db       主库 (data\s1a5.db, SQLite 单文件)
 *     s1a5.db-wal   预写日志 (未合并的改动)
 *     s1a5.db-shm   共享内存索引
 *
 * 【为什么这里所有写操作都先查锁】
 *   只拷主库、或边运行边拷, 会得到残缺的库。所以备份 / 还原 / 重新开局
 *   一律要求"服务端已停止" (S1A5 没有 60US 的 gm-lock 文件, IsDbLocked 恒
 *   false —— 只查服务端进程是否在跑)。做不到就拒绝执行, 并说清楚原因。
 *
 * 【备份的原子性】
 *   先写到临时目录 <名字>.tmp, 校验主库长度对得上, 再整体改名成正式名字。
 *
 * 【还原的顺序】
 *   先把新库落成 .new 并校验长度, 之后才动旧库; 同时必须删掉 -wal/-shm ——
 *   那两个文件是【旧库】的预写日志, 配着新库用等于把两代数据混在一起。
 * ==================================================================
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ServerUI.Services;

public class ArchiveEntry
{
    public string Name = "";          // 显示名 (文件夹名, 或 GM 备份的文件名)
    public string Dir = "";           // 启动器备份的文件夹
    public string FilePath = "";      // GM 自动备份的单文件
    public bool IsGmBackup;
    public long Size;
    public DateTime Time;
    public bool Valid;
    public string Note = "";

    public string TimeText => Time == DateTime.MinValue ? "--" : Time.ToString("yyyy-MM-dd HH:mm:ss");
    public string SizeText
    {
        get
        {
            var mb = Size / 1048576.0;
            return mb >= 1 ? mb.ToString("0.0") + " MB" : (Size / 1024.0).ToString("0") + " KB";
        }
    }
    public string SourceText => IsGmBackup ? "GM 工具自动备份" : "启动器备份";

    /// <summary>列表第二行: 来源 + 备注 (备注为空时不显示)</summary>
    public string SourceAndNote => string.IsNullOrWhiteSpace(Note) ? SourceText : SourceText + " · " + Note;
}

public static class ArchiveService
{
    public static string Root => Path.Combine(AppContext.BaseDirectory, "runtime", "存档备份");

    // =================================================================
    // 列表
    // =================================================================
    /// <summary>列出启动器备份 + GM 工具自动备份, 按时间倒序</summary>
    public static List<ArchiveEntry> List(string serverDir)
    {
        var list = new List<ArchiveEntry>();
        list.AddRange(ListLauncher());
        list.AddRange(ListGm(serverDir));
        return list.OrderByDescending(e => e.Time).ToList();
    }

    static IEnumerable<ArchiveEntry> ListLauncher()
    {
        var res = new List<ArchiveEntry>();
        try
        {
            if (!Directory.Exists(Root)) return res;
            foreach (var d in Directory.GetDirectories(Root))
            {
                var name = Path.GetFileName(d);
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;   // 没写完的, 不认
                var main = Path.Combine(d, ServerControl.DbName);
                var e = new ArchiveEntry { Name = name, Dir = d };
                if (File.Exists(main))
                {
                    var fi = new FileInfo(main);
                    e.Size = fi.Length;
                    e.Time = fi.LastWriteTime;
                    e.Valid = fi.Length > 0;
                    var extra = new List<string>();
                    if (File.Exists(Path.Combine(d, ServerControl.DbName + "-wal"))) extra.Add("含 WAL");
                    if (extra.Count > 0) e.Note = string.Join(" · ", extra) + "（未停止服务端时备份的，还原前请注意）";
                }
                else { e.Valid = false; e.Note = "备份不完整（缺少主库文件）"; }
                res.Add(e);
            }
        }
        catch { }
        return res;
    }

    static IEnumerable<ArchiveEntry> ListGm(string serverDir)
    {
        var res = new List<ArchiveEntry>();
        try
        {
            var dir = ServerControl.GmBackupDir(serverDir);
            if (!Directory.Exists(dir)) return res;
            foreach (var f in Directory.GetFiles(dir, "*.db"))
            {
                var fi = new FileInfo(f);
                res.Add(new ArchiveEntry
                {
                    Name = Path.GetFileName(f),
                    FilePath = f,
                    IsGmBackup = true,
                    Size = fi.Length,
                    Time = fi.LastWriteTime,
                    Valid = fi.Length > 0,
                    Note = "写库前自动留的整库快照"
                });
            }
        }
        catch { }
        return res;
    }

    // =================================================================
    // 备份
    // =================================================================
    /// <summary>当前存档是否允许被写操作 (服务端已停止)</summary>
    public static bool CanWrite(string serverDir, out string why)
    {
        why = null;
        if (!ServerControl.IsValidRoot(serverDir)) { why = "还没定位到服务端目录。"; return false; }
        if (ServerControl.IsServerRunning(serverDir))
        {
            why = "服务端正在运行。\n\n服务端运行时存档在被写入，此时备份/还原/重开都可能得到残缺的库。\n请先点【停止服务端】。";
            return false;
        }
        return true;
    }

    /// <summary>备份当前存档三件套。返回备份名; 失败时 error 里有原因</summary>
    public static string Backup(string serverDir, string name, out string error)
    {
        error = null;
        if (!CanWrite(serverDir, out error)) return null;

        var main = ServerControl.DbPath(serverDir);
        if (!File.Exists(main)) { error = "还没有存档可以备份（" + ServerControl.DbName + " 不存在）。"; return null; }

        try
        {
            if (string.IsNullOrWhiteSpace(name)) name = "存档_" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            name = Sanitize(name);

            Directory.CreateDirectory(Root);
            var tmp = Path.Combine(Root, name + ".tmp");
            var dst = Path.Combine(Root, name);
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            if (Directory.Exists(dst)) { error = "已经有一个叫“" + name + "”的备份了，换个名字。"; return null; }

            Directory.CreateDirectory(tmp);
            long want = new FileInfo(main).Length;
            File.Copy(main, Path.Combine(tmp, ServerControl.DbName), true);

            // -wal/-shm 只在确实存在时才带 (停止服务端且正常退出后它们会被回收)
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                var src = main + suffix;
                if (File.Exists(src))
                    try { File.Copy(src, Path.Combine(tmp, ServerControl.DbName + suffix), true); } catch { }
            }

            // 校验长度, 之后才允许它变成"正式备份"
            var got = new FileInfo(Path.Combine(tmp, ServerControl.DbName)).Length;
            if (got != want)
            {
                try { Directory.Delete(tmp, true); } catch { }
                error = "备份时长度对不上（源 " + want + " 字节 / 备份 " + got + " 字节），已丢弃这次备份。";
                return null;
            }

            Directory.Move(tmp, dst);
            return name;
        }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    // =================================================================
    // 还原
    // =================================================================
    public static bool Restore(string serverDir, ArchiveEntry e, out string error)
    {
        error = null;
        if (!CanWrite(serverDir, out error)) return false;
        if (e == null || !e.Valid) { error = "这个备份不完整，不能用。"; return false; }

        try
        {
            var main = ServerControl.DbPath(serverDir);
            var srcMain = e.IsGmBackup ? e.FilePath : Path.Combine(e.Dir, ServerControl.DbName);
            if (!File.Exists(srcMain)) { error = "备份里的主库文件不见了。"; return false; }

            var want = new FileInfo(srcMain).Length;
            if (want <= 0) { error = "备份里的主库是 0 字节。"; return false; }

            // 1) 先把新库落成 .new 并校验 —— 这一步失败, 旧库一个字节都没动
            var staged = main + ".new";
            if (File.Exists(staged)) File.Delete(staged);
            File.Copy(srcMain, staged, true);
            if (new FileInfo(staged).Length != want)
            {
                try { File.Delete(staged); } catch { }
                error = "写入新存档时长度对不上，已放弃（当前存档没有被改动）。";
                return false;
            }

            // 2) 留一份"还原前"的快照, 万一玩家点错了还能回去
            var undoName = "_还原前_" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string undoErr;
            Backup(serverDir, undoName, out undoErr);

            // 3) 必须删掉旧库的 -wal/-shm: 它们是上一代数据的预写日志,
            //    留着和新库混用 = 把两代数据拼在一起, 库会直接坏掉
            foreach (var suffix in new[] { "-wal", "-shm" })
                try { var f = main + suffix; if (File.Exists(f)) File.Delete(f); } catch { }

            // 4) 原子提交
            if (File.Exists(main))
            {
                try { File.Replace(staged, main, null); }
                catch { File.Copy(staged, main, true); try { File.Delete(staged); } catch { } }
            }
            else File.Move(staged, main);

            // 5) 从启动器备份还原时, 备份里如果带了 -wal 也一起放回去
            if (!e.IsGmBackup)
            {
                foreach (var suffix in new[] { "-wal", "-shm" })
                {
                    var s = Path.Combine(e.Dir, ServerControl.DbName + suffix);
                    if (File.Exists(s))
                        try { File.Copy(s, main + suffix, true); } catch { }
                }
            }
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    // =================================================================
    // 删除 / 重命名 / 导入 / 重新开局
    // =================================================================
    public static bool Delete(ArchiveEntry e, out string error)
    {
        error = null;
        try
        {
            if (e.IsGmBackup) { if (File.Exists(e.FilePath)) File.Delete(e.FilePath); }
            else if (Directory.Exists(e.Dir)) Directory.Delete(e.Dir, true);
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    public static bool Rename(ArchiveEntry e, string newName, out string error)
    {
        error = null;
        if (e.IsGmBackup) { error = "GM 工具自动备份不支持改名。"; return false; }
        try
        {
            newName = Sanitize(newName);
            if (string.IsNullOrWhiteSpace(newName)) { error = "名字不能为空。"; return false; }
            var dst = Path.Combine(Root, newName);
            if (Directory.Exists(dst)) { error = "已经有一个叫“" + newName + "”的备份了。"; return false; }
            Directory.Move(e.Dir, dst);
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    /// <summary>把一个外部的 .db 收进备份库 (例如玩家从别处拿到的存档)</summary>
    public static string Import(string srcFile, string name, out string error)
    {
        error = null;
        try
        {
            if (!File.Exists(srcFile)) { error = "找不到文件。"; return null; }
            var fi = new FileInfo(srcFile);
            if (fi.Length <= 0) { error = "这个文件是 0 字节。"; return null; }

            if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(srcFile);
            name = Sanitize(name);
            Directory.CreateDirectory(Root);
            var tmp = Path.Combine(Root, name + ".tmp");
            var dst = Path.Combine(Root, name);
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            if (Directory.Exists(dst)) { error = "已经有一个叫“" + name + "”的备份了。"; return null; }

            Directory.CreateDirectory(tmp);
            File.Copy(srcFile, Path.Combine(tmp, ServerControl.DbName), true);
            if (new FileInfo(Path.Combine(tmp, ServerControl.DbName)).Length != fi.Length)
            {
                try { Directory.Delete(tmp, true); } catch { }
                error = "复制后长度对不上，已丢弃。";
                return null;
            }
            Directory.Move(tmp, dst);
            return name;
        }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    /// <summary>重新开局: 删掉存档三件套 (服务端会重新建一个空库)</summary>
    public static bool ResetGame(string serverDir, out string error)
    {
        error = null;
        if (!CanWrite(serverDir, out error)) return false;
        try
        {
            var n = 0;
            foreach (var f in new[]
            {
                ServerControl.DbPath(serverDir),
                ServerControl.DbWal(serverDir),
                ServerControl.DbShm(serverDir)
            })
            {
                if (File.Exists(f)) { File.Delete(f); n++; }
            }
            if (n == 0) error = "本来就没有存档文件。";
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    static string Sanitize(string s)
    {
        s = (s ?? "").Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }
}
