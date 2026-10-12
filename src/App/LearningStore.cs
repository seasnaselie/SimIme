using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SimIme.Engine;

namespace SimIme.App
{
    // 学习库（第十八轮修正版）：
    //   · 只从真正提交过的块学习（+ 相邻块的克制搭配），不再对原文做任意窗口计数；
    //   · 迁移改为**先清盘、再建库**——旧实现是先 Load 进内存再删文件，随后任何 Save 都会把旧词写回（真机实测没生效）；
    //   · 版本号放 %APPDATA%\SimIme\learning.version（build.ps1 清 dist 波及不到），兼容 exe 同目录的旧 habits.version；
    //   · 删除失败就不写版本号，下次启动重试，并写明确 ERROR。
    internal static class LearningStore
    {
        internal const int CurrentVersion = 3;
        private const int MaxMergedLength = 3;

        internal sealed class MigrationResult
        {
            internal bool Cleared;
            internal bool DeleteFailed;
            internal int ClearedCount;
            internal string LogLine;
        }

        private static string VersionPathInAppData
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SimIme");
                return Path.Combine(dir, "learning.version");
            }
        }
        private static string VersionPathNearExe { get { return Path.Combine(AppPaths.BaseDir, "habits.version"); } }
        private static string MergePath { get { return Path.Combine(AppPaths.BaseDir, "habits_merges.tsv"); } }
        private static readonly Dictionary<string, int> _pairs = new Dictionary<string, int>(StringComparer.Ordinal);

        private static int ReadVersion()
        {
            int best = 0;
            foreach (string path in new string[] { VersionPathInAppData, VersionPathNearExe })
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    int parsed;
                    if (int.TryParse(File.ReadAllText(path).Trim(), out parsed) && parsed > best) best = parsed;
                }
                catch (Exception ex) { Log.Error("读学习库版本失败：" + path, ex); }
            }
            return best;
        }

        // 两处都写：exe 同目录（habits.version，已被 C 加进 build.ps1 保留清单）+ %APPDATA%（重建 dist 也波及不到）。
        // 任一处可读即认为已迁移；读出取最大值。
        private static void WriteVersion()
        {
            string text = CurrentVersion.ToString() + Environment.NewLine;
            foreach (string path in new string[] { VersionPathNearExe, VersionPathInAppData })
            {
                try
                {
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(path, text, new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    Log.Info("写学习库版本失败（该位置不可写，换另一处）" + path + "：" + ex.GetType().Name);
                }
            }
        }

        // 在**建库之前**调用
        internal static MigrationResult Prepare()
        {
            MigrationResult result = new MigrationResult();
            int filesVersion = ReadVersion();
            if (filesVersion >= CurrentVersion) return result;
            if (!File.Exists(AppPaths.HabitStorePath))
            {
                WriteVersion();
                return result;
            }

            int count = 0;
            string sample = "(空)";
            try
            {
                IHabitStore probe = EngineFactory.CreateHabitStore(AppPaths.HabitStorePath);
                if (probe != null)
                {
                    try { probe.Load(); } catch (Exception ex) { Log.Info("旧库 Load 跳过：" + ex.Message); }
                    count = UserWordTable.UserWordCount(probe);
                    string src;
                    List<KeyValuePair<string, int>> top = UserWordTable.TopWords(probe, 5, out src);
                    StringBuilder sb = new StringBuilder();
                    for (int i = 0; i < top.Count; i++)
                    {
                        if (i > 0) sb.Append('、');
                        sb.Append(top[i].Key).Append('×').Append(top[i].Value);
                    }
                    sample = sb.Length == 0 ? "(空)" : sb.ToString();
                }
                probe = null;
            }
            catch (Exception ex)
            {
                Log.Error("统计旧学习库失败（按 0 记）", ex);
            }
            Log.Info(string.Format("清空前追溯：旧学习库共 {0} 个词组（前 5 个示例：{1}）", count, sample));

            try
            {
                if (File.Exists(AppPaths.HabitStorePath)) File.Delete(AppPaths.HabitStorePath);
                if (File.Exists(MergePath)) File.Delete(MergePath);
            }
            catch (Exception ex)
            {
                Log.Error("学习库清空失败（文件被占用？），本次仍在使用旧库；下次启动会重试", ex);
            }
            if (File.Exists(AppPaths.HabitStorePath))
            {
                result.DeleteFailed = true;
                Log.Error("学习库清空失败（文件被占用？），本次仍在使用旧库；下次启动会重试");
                return result;
            }

            WriteVersion();
            result.Cleared = true;
            result.ClearedCount = count;
            result.LogLine = string.Format("学习算法已升级：已清空旧算法学到的 {0} 个词组（含跨词碎片），请重新累积；删除 habits.dat 也可手动重置", count);
            Log.Info(result.LogLine);
            return result;
        }

        internal static void LoadPairs()
        {
            _pairs.Clear();
            try
            {
                if (!File.Exists(MergePath)) return;
                foreach (string line in File.ReadAllLines(MergePath, Encoding.UTF8))
                {
                    int tab = line.IndexOf('\t');
                    if (tab <= 0) continue;
                    int last = line.LastIndexOf('\t');
                    if (last <= tab) continue;
                    string pair = line.Substring(0, last);
                    int count;
                    if (int.TryParse(line.Substring(last + 1), out count)) _pairs[pair] = count;
                }
            }
            catch (Exception ex) { Log.Error("读取搭配计数失败", ex); }
        }

        private static void SavePairs()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                foreach (KeyValuePair<string, int> kv in _pairs) sb.Append(kv.Key).Append('\t').Append(kv.Value).Append('\n');
                File.WriteAllText(MergePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Error("保存搭配计数失败", ex); }
        }

        internal static string ObserveRun(IList<PhraseBlock> blocks, IHabitStore habits, AppConfig cfg)
        {
            if (habits == null || blocks == null || blocks.Count == 0) return "(无提交块，未学习)";
            int learnPct = cfg.Behavior.LearnMergeMinCount;
            int blockCount = 0;
            for (int i = 0; i < blocks.Count; i++)
            {
                PhraseBlock b = blocks[i];
                if (!b.Chinese || b.Text.Length == 0) continue;
                habits.ObserveText(b.Text);
                blockCount++;
            }
            int merged = 0;
            for (int i = 0; i + 1 < blocks.Count; i++)
            {
                PhraseBlock a = blocks[i];
                PhraseBlock b = blocks[i + 1];
                if (!a.Chinese || !b.Chinese) continue;
                string pair = a.Text + "\t" + b.Text;
                int count;
                _pairs.TryGetValue(pair, out count);
                count++;
                _pairs[pair] = count;
                string mergedText = a.Text + b.Text;
                if (count >= learnPct && mergedText.Length <= MaxMergedLength)
                {
                    if (count == learnPct)
                    {
                        Log.Info(string.Format("学习搭配：'{0}' 连续出现 {1} 次（阈值 {2}）且合并后 {3} 字 ≤ {4} → 学成用户词组",
                            mergedText, count, learnPct, mergedText.Length, MaxMergedLength));
                    }
                    habits.ObserveText(mergedText);
                    if (count == learnPct) habits.ObserveText(mergedText);
                    merged++;
                }
                else if (count == learnPct && mergedText.Length > MaxMergedLength)
                {
                    Log.Info(string.Format("学习搭配：'{0}' 已达 {1} 次，但合并后 {2} 字 > {3}，按规则**不学**（避免跨词碎片）",
                        mergedText, count, mergedText.Length, MaxMergedLength));
                }
            }
            habits.Save();
            SavePairs();
            return string.Format("学习：提交块 {0} 个已计数；搭配命中 {1} 次（阈值 {2}，合并上限 {3} 字）；搭配表 {4} 条",
                blockCount, merged, learnPct, MaxMergedLength, _pairs.Count);
        }
    }
}
