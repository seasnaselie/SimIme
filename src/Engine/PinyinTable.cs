// SimIme 引擎：拼音表（全量汉字 -> 无调小写拼音），含同音字/鼻音变体查询
using System;
using System.Collections.Generic;

namespace SimIme.Engine
{
    public sealed class PinyinTable : IPinyinTable
    {
        public const string ResourceName = "SimIme.Assets.pinyin_full.tsv";
        public const string FileName = "pinyin_full.tsv";
        public const string FreqResourceName = "SimIme.Assets.char_freq.tsv";
        public const string FreqFileName = "char_freq.tsv";

        private readonly Dictionary<char, string> _pinyin = new Dictionary<char, string>();
        private readonly Dictionary<string, List<char>> _groups = new Dictionary<string, List<char>>();
        private readonly Dictionary<string, List<char>> _nasalGroups = new Dictionary<string, List<char>>();
        private readonly Dictionary<char, int> _commonRank = new Dictionary<char, int>();

        public bool IsReady { get { return _pinyin.Count > 0; } }
        public int CharCount { get { return _pinyin.Count; } }
        // 常用字（GB2312 + 语料频次过滤）数量；为 0 表示 char_freq.tsv 缺失，此时不做常用度排序
        public int CommonCharCount { get { return _commonRank.Count; } }

        public string GetPinyin(char c)
        {
            string v;
            if (_pinyin.TryGetValue(c, out v)) return v;
            return "";
        }

        // 同音字：拼音（无调）完全相同的其他常用字，按常用度降序
        public IList<string> HomophonesOf(char c, int max)
        {
            List<string> result = new List<string>();
            if (max <= 0) return result;
            string p = GetPinyin(c);
            if (p.Length == 0) return result;
            List<char> group;
            if (!_groups.TryGetValue(p, out group)) return result;
            for (int i = 0; i < group.Count && result.Count < max; i++)
            {
                if (group[i] == c) continue;
                result.Add(group[i].ToString());
            }
            return result;
        }

        // 鼻音变体：与原字只差前后鼻音（去掉词尾 ng 后相同）且不同音的其他常用字
        public IList<string> NasalVariantsOf(char c, int max)
        {
            List<string> result = new List<string>();
            if (max <= 0) return result;
            string p = GetPinyin(c);
            if (p.Length == 0) return result;
            string norm = NormalizeNasal(p);
            List<char> group;
            if (!_nasalGroups.TryGetValue(norm, out group)) return result;
            for (int i = 0; i < group.Count && result.Count < max; i++)
            {
                char x = group[i];
                if (x == c) continue;
                // 完全同音的字属于 HomophonesOf，不算鼻音错字
                if (string.Equals(GetPinyin(x), p, StringComparison.Ordinal)) continue;
                result.Add(x.ToString());
            }
            return result;
        }

        // 鼻音归一化：词尾 "ng" 去掉 g  ->  an/ang、en/eng、in/ing、ian/iang、uan/uang 归一后相等
        public static string NormalizeNasal(string pinyin)
        {
            if (pinyin == null) return null;
            if (pinyin.Length >= 2 && pinyin.EndsWith("ng", StringComparison.Ordinal))
                return pinyin.Substring(0, pinyin.Length - 1);
            return pinyin;
        }

        public static PinyinTable Load()
        {
            PinyinTable t = new PinyinTable();

            string[] freqLines = AssetLoader.ReadLines(FreqResourceName, FreqFileName);
            int rank = 0;
            for (int i = 0; i < freqLines.Length; i++)
            {
                char ch;
                string val;
                if (!AssetLoader.TrySplitPair(freqLines[i], out ch, out val)) continue;
                if (!t._commonRank.ContainsKey(ch)) { t._commonRank[ch] = rank; rank++; }
            }

            string[] lines = AssetLoader.ReadLines(ResourceName, FileName);
            for (int i = 0; i < lines.Length; i++)
            {
                char ch;
                string py;
                if (!AssetLoader.TrySplitPair(lines[i], out ch, out py)) continue;
                t._pinyin[ch] = py.ToLowerInvariant();
            }

            t.BuildIndexes();
            return t;
        }

        private void BuildIndexes()
        {
            foreach (KeyValuePair<char, string> kv in _pinyin)
            {
                AddTo(_groups, kv.Value, kv.Key);
                AddTo(_nasalGroups, NormalizeNasal(kv.Value), kv.Key);
            }
            foreach (KeyValuePair<string, List<char>> kv in _groups) Prepare(kv.Value);
            foreach (KeyValuePair<string, List<char>> kv in _nasalGroups) Prepare(kv.Value);
        }

        private static void AddTo(Dictionary<string, List<char>> map, string key, char c)
        {
            List<char> list;
            if (!map.TryGetValue(key, out list))
            {
                list = new List<char>();
                map[key] = list;
            }
            list.Add(c);
        }

        // 只保留常用字作为错字候选（避免生僻字/异体字），再按常用度排序
        private void Prepare(List<char> list)
        {
            if (_commonRank.Count > 0)
            {
                List<char> keep = new List<char>(list.Count);
                for (int i = 0; i < list.Count; i++)
                {
                    if (_commonRank.ContainsKey(list[i])) keep.Add(list[i]);
                }
                list.Clear();
                list.AddRange(keep);
            }
            list.Sort(CompareByRank);
        }

        private int CompareByRank(char a, char b)
        {
            int ra = RankOf(a);
            int rb = RankOf(b);
            if (ra != rb) return ra < rb ? -1 : 1;
            return a.CompareTo(b);
        }

        private int RankOf(char c)
        {
            int r;
            if (_commonRank.TryGetValue(c, out r)) return r;
            return int.MaxValue;
        }
    }
}
