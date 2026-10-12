// SimIme 引擎：内置常用词表索引（用于习惯权重回退与句首重打素材）
using System;
using System.Collections.Generic;

namespace SimIme.Engine
{
    internal sealed class CommonWordIndex
    {
        public const string ResourceName = "SimIme.Assets.common_words.tsv";
        public const string FileName = "common_words.tsv";
        private const int MaxFillers = 400;

        private static CommonWordIndex _instance;
        private static readonly object SyncRoot = new object();

        private readonly Dictionary<string, int> _freq = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<string> _fillers = new List<string>();
        private double _maxLog = 1.0;

        public static CommonWordIndex Get()
        {
            CommonWordIndex inst = _instance;
            if (inst != null) return inst;
            lock (SyncRoot)
            {
                if (_instance == null) _instance = Load();
                return _instance;
            }
        }

        public bool IsLoaded { get { return _freq.Count > 0; } }
        public int Count { get { return _freq.Count; } }
        public int FillerCount { get { return _fillers.Count; } }

        public double DefaultWeight(string word)
        {
            if (string.IsNullOrEmpty(word)) return 0.0;
            int f;
            if (!_freq.TryGetValue(word, out f) || f <= 0) return 0.0;
            double v = 3.0 * Math.Log(1.0 + (double)f) / _maxLog;
            if (v < 0.0) return 0.0;
            if (v > 3.0) v = 3.0;
            return v;
        }

        public bool Has(string word)
        {
            if (string.IsNullOrEmpty(word)) return false;
            return _freq.ContainsKey(word);
        }

        public string PickFiller(Random rnd, int len)
        {
            if (_fillers.Count == 0 || rnd == null || len <= 0) return null;
            string w = _fillers[rnd.Next(_fillers.Count)];
            if (w == null) return null;
            if (len >= w.Length) return w;
            return w.Substring(0, len);
        }

        // 文件格式："词<TAB>语料词频"，词长 1~2，按词频降序
        // 注意：不能用 AssetLoader.TrySplitPair（那是给"单字<TAB>值"用的，会把 2 字词的第二个字截掉）
        private static CommonWordIndex Load()
        {
            CommonWordIndex idx = new CommonWordIndex();
            string[] lines = AssetLoader.ReadLines(ResourceName, FileName);
            double maxLog = 1.0;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                int tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                string word = line.Substring(0, tab);
                if (word.Length == 0) continue;
                int f;
                if (!int.TryParse(line.Substring(tab + 1).Trim(), out f) || f <= 0) continue;
                idx._freq[word] = f;
                double lg = Math.Log(1.0 + (double)f);
                if (lg > maxLog) maxLog = lg;
                if (word.Length == 2 && idx._fillers.Count < MaxFillers) idx._fillers.Add(word);
            }
            idx._maxLog = maxLog;
            return idx;
        }
    }
}
