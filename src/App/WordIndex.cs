using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using SimIme.Engine;

namespace SimIme.App
{
    // 词组候选索引：拼音 -> 词（按频次降序）。数据源三合一：
    //   ① 内置 common_words.tsv（嵌入资源 SimIme.Assets.common_words.tsv）
    //   ② 外部词表 dict_extra.tsv（Behavior.ExtraDictFile）
    //   ③ 习惯库用户词组（IHabitStore.UserWords）
    // 只读数据、不改引擎；一次建索引，之后按拼音 O(1) 查（并缓存查询结果）。
    internal static class WordIndex
    {
        internal sealed class Entry
        {
            internal string Word;
            internal int Freq;
            internal string Source = "";     // 内置 / 外部 / 用户 / 生成（排障用）
            internal bool Generated;         // true=同音字凑出来的"非词"候选（排在真实词之后）
        }

        private static readonly Dictionary<string, List<Entry>> _byPinyin = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
        private static readonly Dictionary<string, List<Entry>> _cache = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
        private static readonly Dictionary<char, string> _charPinyin = new Dictionary<char, string>();
        private static bool _built;
        private static string _pinyinTableKind = "";
        private static IPinyinTable _table;
        private static string[] _sortedKeys;          // 排序后的拼音键，供前缀二分
        private static readonly Dictionary<string, List<Entry>> _prefixCache = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
        internal static int PrefixQueryCount;
        internal static long PrefixQueryMs;

        internal static string Describe = "(未建立)";
        internal static long BuildMs;
        internal static int WordCount;
        internal static int PinyinCount;
        internal static int BuiltInCount;
        internal static int ExtraCount;
        internal static int UserCount;
        internal static int DuplicatesMerged;   // 同名同词在内置/外部/用户表里重复出现时的合并计数
        internal static int GeneratedCount;     // 本次运行生成候选（同音字组合）数量
        internal static int QueryCount;
        internal static long QueryMs;

        internal static void EnsureBuilt(IPinyinTable table, AppConfig cfg, IHabitStore habits)
        {
            if (_built) return;
            _built = true;
            DateTime t0 = DateTime.Now;
            try
            {
                _pinyinTableKind = table == null ? "(无拼音表)" : table.GetType().Name;
                _table = table;
                if (table != null)
                {
                    BuiltInCount = LoadTsv(ReadEmbeddedOrFile("SimIme.Assets.common_words.tsv", "common_words.tsv"), table, "内置");
                    string extraPath = cfg == null ? null : cfg.Behavior.ExtraDictFile;
                    if (!string.IsNullOrEmpty(extraPath) && File.Exists(extraPath))
                    {
                        ExtraCount = LoadTsv(File.ReadAllText(extraPath, Encoding.UTF8), table, "外部");
                    }
                    if (habits != null)
                    {
                        string source;
                        List<KeyValuePair<string, int>> userWords = UserWordTable.TopWords(habits, int.MaxValue, out source);
                        for (int i = 0; i < userWords.Count; i++) Add(userWords[i].Key, userWords[i].Value, table, "用户");
                        UserCount = userWords.Count;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("建立词组候选索引失败（候选将退化为同音字）", ex);
            }
            finally
            {
                BuildMs = (long)(DateTime.Now - t0).TotalMilliseconds;
                PinyinCount = _byPinyin.Count;
                List<string> keys = new List<string>(_byPinyin.Keys);
                keys.Sort(StringComparer.Ordinal);
                _sortedKeys = keys.ToArray();   // 前缀二分用
                WordCount = BuiltInCount + ExtraCount + UserCount;
                Describe = string.Format("候选词索引：{0} 个词 / {1} 个拼音段（去重合并 {2} 条：内置 {3} ｜外部 {4} ｜用户 {5} 条原始记录），建索引用时 {6}ms，拼音表={7}",
                    WordCount, PinyinCount, DuplicatesMerged, BuiltInCount, ExtraCount, UserCount, BuildMs, _pinyinTableKind);
                Log.Info(Describe);
            }
        }

        private static string ReadEmbeddedOrFile(string resourceName, string fileName)
        {
            try
            {
                Assembly asm = typeof(WordIndex).Assembly;
                using (Stream stream = asm.GetManifestResourceStream(resourceName))
                {
                    if (stream != null)
                    {
                        using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            return reader.ReadToEnd();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Info("读嵌入资源失败（" + resourceName + "）：" + ex.Message);
            }
            string path = Path.Combine(AppPaths.BaseDir, fileName);
            if (File.Exists(path)) return File.ReadAllText(path, Encoding.UTF8);
            Log.Error("找不到内置词表（资源 " + resourceName + " 与文件 " + path + " 都没有）");
            return "";
        }

        // 每行「词<TAB>频次」
        private static int LoadTsv(string text, IPinyinTable table, string source)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int count = 0;
            using (StringReader reader = new StringReader(text))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    int tab = line.IndexOf('\t');
                    if (tab <= 0) continue;
                    string word = line.Substring(0, tab);
                    int freq;
                    if (!int.TryParse(line.Substring(tab + 1).Trim(), out freq)) freq = 1;
                    if (Add(word, freq, table, source)) count++;
                }
            }
            return count;
        }

        // 按词去重：同一段拼音下同一个词只保留一条，频次取较大者（内置/外部/用户重复时不再出现两个「一个」）
        private static bool Add(string word, int freq, IPinyinTable table, string source)
        {
            if (string.IsNullOrEmpty(word) || word.Length < 1 || word.Length > 4) return false;
            bool allHan = true;
            for (int i = 0; i < word.Length; i++)
            {
                if (word[i] < 0x4E00 || word[i] > 0x9FFF) { allHan = false; break; }
            }
            if (!allHan) return false;
            string pinyin = PinyinOf(word, table);
            if (pinyin.Length == 0) return false;

            List<Entry> list;
            if (!_byPinyin.TryGetValue(pinyin, out list))
            {
                list = new List<Entry>();
                _byPinyin[pinyin] = list;
            }
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i].Word, word, StringComparison.Ordinal))
                {
                    if (freq > list[i].Freq) list[i].Freq = freq;
                    if (!string.IsNullOrEmpty(source) && list[i].Source.IndexOf(source, StringComparison.Ordinal) < 0)
                    {
                        list[i].Source = list[i].Source.Length == 0 ? source : (list[i].Source + "+" + source);
                    }
                    DuplicatesMerged++;
                    return false;
                }
            }
            Entry entry = new Entry();
            entry.Word = word;
            entry.Freq = freq <= 0 ? 1 : freq;
            entry.Source = source;
            list.Add(entry);
            return true;
        }

        internal static string PinyinOf(string word, IPinyinTable table)
        {
            if (string.IsNullOrEmpty(word) || table == null) return "";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < word.Length; i++)
            {
                char c = word[i];
                string py;
                if (!_charPinyin.TryGetValue(c, out py))
                {
                    try { py = table.GetPinyin(c); }
                    catch (Exception ex) { Log.Error("查拼音失败：" + c, ex); py = null; }
                    if (py == null) py = "";
                    _charPinyin[c] = py;
                }
                if (py.Length == 0) return "";
                sb.Append(py);
            }
            return sb.ToString();
        }

        // 候选：目标词永远排第一（哪怕词表里没有它），其后是同音词按频次降序；最多 max 个
        internal static List<Entry> Lookup(string pinyin, string targetWord, int max, out string note)
        {
            DateTime t0 = DateTime.Now;
            note = null;
            List<Entry> result = new List<Entry>();
            if (string.IsNullOrEmpty(pinyin)) { note = "(空拼音段)"; return result; }
            if (max < 1) max = 1;

            List<Entry> bucket;
            if (!_cache.TryGetValue(pinyin, out bucket))
            {
                List<Entry> raw;
                _byPinyin.TryGetValue(pinyin, out raw);
                bucket = raw == null ? new List<Entry>() : new List<Entry>(raw);
                bucket.Sort(delegate(Entry a, Entry b)
                {
                    if (b.Freq != a.Freq) return b.Freq.CompareTo(a.Freq);
                    return string.CompareOrdinal(a.Word, b.Word);
                });
                if (bucket.Count > 64) bucket.RemoveRange(64, bucket.Count - 64);
                _cache[pinyin] = bucket;
            }

            // 目标词**只有本身在词表里**才作为候选出现（绝不把块原文硬塞进候选——真机反馈过）
            if (!string.IsNullOrEmpty(targetWord))
            {
                for (int i = 0; i < bucket.Count; i++)
                {
                    if (string.Equals(bucket[i].Word, targetWord, StringComparison.Ordinal)) { result.Add(bucket[i]); break; }
                }
            }
            for (int i = 0; i < bucket.Count && result.Count < max; i++)
            {
                if (string.Equals(bucket[i].Word, targetWord, StringComparison.Ordinal)) continue;
                result.Add(bucket[i]);
            }
            if (result.Count < max) PadWithGenerated(result, pinyin, targetWord, max, null);
            if (bucket.Count == 0)
            {
                note = "拼音段 '" + pinyin + "' 在词表（内置+外部+用户）里没有同音词，已用同音字组合补齐";
            }
            QueryCount++;
            QueryMs += (long)(DateTime.Now - t0).TotalMilliseconds;
            return result;
        }

        // 前缀候选：拼音以 prefix 开头的词（词优先、频次降序）。用排序键二分定位区间，再取区间内词，
        // 结果按前缀缓存（真输入法就是"边打边变"的行为）。
        internal static List<Entry> LookupPrefix(string prefix, string targetWord, int max, out string note)
        {
            DateTime t0 = DateTime.Now;
            note = null;
            List<Entry> result = new List<Entry>();
            if (_sortedKeys == null || _sortedKeys.Length == 0) { note = "(索引未建立)"; return result; }
            if (string.IsNullOrEmpty(prefix))
            {
                // 还没敲字母：留空（不剧透后面的候选）——口径写进日志
                note = "前缀为空：候选行留空（等敲第一个字母再给候选）";
                return result;
            }
            if (max < 1) max = 1;

            List<Entry> bucket;
            if (!_prefixCache.TryGetValue(prefix, out bucket))
            {
                // 二分找 [prefix, prefix+0xFFFF) 区间
                int lo = LowerBound(prefix);
                StringBuilder top = new StringBuilder(prefix);
                top.Append('\uffff');
                int hi = LowerBound(top.ToString());
                List<Entry> all = new List<Entry>();
                HashSet<string> seenWord = new HashSet<string>(StringComparer.Ordinal);
                // 先放"拼音完全等于前缀"的词（yige → 一个/亿个），再放仅前缀相容的（yig → 一个/一股…）
                List<Entry> exact;
                if (_byPinyin.TryGetValue(prefix, out exact))
                {
                    for (int j = 0; j < exact.Count; j++)
                    {
                        if (seenWord.Add(exact[j].Word)) all.Add(exact[j]);
                    }
                }
                for (int i = lo; i < hi && i < _sortedKeys.Length; i++)
                {
                    List<Entry> list;
                    if (_byPinyin.TryGetValue(_sortedKeys[i], out list))
                    {
                        for (int j = 0; j < list.Count; j++)
                        {
                            if (seenWord.Add(list[j].Word)) all.Add(list[j]);
                        }
                    }
                }
                all.Sort(delegate(Entry a, Entry b)
                {
                    if (b.Freq != a.Freq) return b.Freq.CompareTo(a.Freq);
                    return string.CompareOrdinal(a.Word, b.Word);
                });
                if (all.Count > 128) all.RemoveRange(128, all.Count - 128);
                bucket = all;
                _prefixCache[prefix] = bucket;
            }

            // 目标词只在**它本身在词表里**且拼音与前缀相容时才作为候选出现
            if (!string.IsNullOrEmpty(targetWord))
            {
                if (PinyinOf(targetWord, _table).StartsWith(prefix, StringComparison.Ordinal))
                {
                    for (int i = 0; i < bucket.Count; i++)
                    {
                        if (string.Equals(bucket[i].Word, targetWord, StringComparison.Ordinal)) { result.Add(bucket[i]); break; }
                    }
                }
            }
            // 第三十二轮（用户帧：输入 jiany 时第一项是"建议你"，而当前正要打的是"建议"）：
            // 其余候选按"还差几个字母就敲完"升序（同长度内保持原频率序）——最短可完成词在前，
            // 不让"更长但频率更高"的词压住用户当下正要打的词。目标词本身（若在词表里）仍排第一。
            List<Entry> rest = new List<Entry>();
            for (int i = 0; i < bucket.Count; i++)
            {
                if (string.Equals(bucket[i].Word, targetWord, StringComparison.Ordinal)) continue;
                rest.Add(bucket[i]);
            }
            rest.Sort(delegate(Entry a, Entry b)
            {
                int la = Math.Max(0, PinyinOf(a.Word, _table).Length - prefix.Length);
                int lb = Math.Max(0, PinyinOf(b.Word, _table).Length - prefix.Length);
                if (la != lb) return la.CompareTo(lb);
                if (b.Freq != a.Freq) return b.Freq.CompareTo(a.Freq);
                return string.CompareOrdinal(a.Word, b.Word);
            });
            for (int i = 0; i < rest.Count && result.Count < max; i++) result.Add(rest[i]);
            if (result.Count < max) PadWithGenerated(result, null, targetWord, max, prefix);
            if (bucket.Count == 0)
            {
                note = "前缀 '" + prefix + "' 在词表里没有词，已用同音字组合补齐（前提前缀拼音相容）";
            }
            PrefixQueryCount++;
            PrefixQueryMs += (long)(DateTime.Now - t0).TotalMilliseconds;
            return result;
        }

        private static int LowerBound(string key)
        {
            int lo = 0, hi = _sortedKeys.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (string.CompareOrdinal(_sortedKeys[mid], key) < 0) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        internal static string PrefixStats()
        {
            return string.Format("前缀候选查询：{0} 次，累计 {1}ms（缓存前缀 {2} 个，排序键 {3} 个）",
                PrefixQueryCount, PrefixQueryMs, _prefixCache.Count, _sortedKeys == null ? 0 : _sortedKeys.Length);
        }

        // 真实同音词不够 CandidateMax 时，用「同音字组合」补齐（真输入法就是这么给候选的）：
        // 一次只替换一个字位（pingguo → 平果/萍果…），要求整串拼音仍等于该段拼音；排在真实词之后
        private static void PadWithGenerated(List<Entry> result, string pinyin, string targetWord, int max, string prefix)
        {
            if (_table == null || string.IsNullOrEmpty(targetWord) || targetWord.Length < 1) return;
            HashSet<string> have = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < result.Count; i++) have.Add(result[i].Word);

            for (int pos = 0; pos < targetWord.Length && result.Count < max; pos++)
            {
                IList<string> homo = null;
                try { homo = _table.HomophonesOf(targetWord[pos], 24); }
                catch (Exception ex) { Log.Error("取同音字失败：" + targetWord[pos], ex); }
                if (homo == null) continue;
                for (int i = 0; i < homo.Count && result.Count < max; i++)
                {
                    string ch = homo[i];
                    if (string.IsNullOrEmpty(ch) || ch.Length != 1) continue;
                    if (ch[0] == targetWord[pos]) continue;
                    string candidate = targetWord.Substring(0, pos) + ch + targetWord.Substring(pos + 1);
                    if (have.Contains(candidate)) continue;
                    string candPinyin = PinyinOf(candidate, _table);
                    if (pinyin != null && !string.Equals(candPinyin, pinyin, StringComparison.Ordinal)) continue;
                    // 前缀模式：补齐项必须与前缀拼音相容
                    if (prefix != null && !candPinyin.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    have.Add(candidate);
                    Entry entry = new Entry();
                    entry.Word = candidate;
                    entry.Freq = 0;
                    entry.Generated = true;
                    entry.Source = "生成";
                    result.Add(entry);
                    GeneratedCount++;
                }
            }
        }

        // 悬浮条候选行：1.一个 2.一各 …
        internal static string FormatCandidates(List<Entry> list)
        {
            if (list == null || list.Count == 0) return "";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(i + 1).Append('.').Append(list[i].Word);
            }
            return sb.ToString();
        }

        internal static string QueryStats()
        {
            return string.Format("候选查询：{0} 次，累计 {1}ms（缓存拼音 {2} 个）", QueryCount, QueryMs, _cache.Count);
        }
    }
}
