// SimIme 引擎：习惯库（词频 / 用户词组 / 节奏），纯文本持久化，不使用 System.Text.Json
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SimIme.Engine
{
    public class HabitStore : IHabitStore
    {
        private const string Header = "SimImeHabit\t1";
        private const int MaxWordKinds = 200000;
        private const int MaxSavedWords = 20000;
        private const int MinPhraseCount = 2;      // 用户词组只保留出现 >= 2 次的
        private const int MaxPhraseKinds = 5000;   // 用户词组总上限，超出按次数淘汰最小的
        private const int MaxPendingKinds = 20000; // 只出现 1 次的暂存上限，满了清空（它们是噪声）
        private const int MaxSavedPhrases = 5000;

        private readonly string _path;
        private readonly Dictionary<string, int> _words = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _phrases = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _pending = new Dictionary<string, int>(StringComparer.Ordinal);
        private long _totalChars;
        private long _rhythmSamples;
        private long _rhythmCharSum;
        private long _rhythmThinkSum;
        private long _phraseEvictions;

        public HabitStore(string path)
        {
            _path = path == null ? "" : path;
        }

        public string Path { get { return _path; } }
        public int TotalChars { get { return _totalChars > int.MaxValue ? int.MaxValue : (int)_totalChars; } }
        public int WordKindCount { get { return _words.Count; } }
        public int PhraseKindCount { get { return _phrases.Count; } }
        public int PendingKindCount { get { return _pending.Count; } }
        public long PhraseEvictions { get { return _phraseEvictions; } }
        public long RhythmSamples { get { return _rhythmSamples; } }
        public double AverageCharDelayMs { get { return _rhythmSamples == 0 ? 0.0 : (double)_rhythmCharSum / _rhythmSamples; } }
        public double AverageThinkPauseMs { get { return _rhythmSamples == 0 ? 0.0 : (double)_rhythmThinkSum / _rhythmSamples; } }

        // 记录词频（连续汉字段落内的 1 元与 2 元词）、用户词组（2~4 元，出现 >= 2 次）与总字数
        public void ObserveText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            _totalChars += text.Length;
            int runStart = -1;
            for (int i = 0; i <= text.Length; i++)
            {
                bool cjk = i < text.Length && IsCjk(text[i]);
                if (cjk)
                {
                    if (runStart < 0) runStart = i;
                }
                else if (runStart >= 0)
                {
                    ObserveRun(text, runStart, i - runStart);
                    runStart = -1;
                }
            }
        }

        private void ObserveRun(string text, int start, int len)
        {
            for (int i = 0; i < len; i++)
            {
                AddWord(text.Substring(start + i, 1));
                if (i + 1 < len) AddWord(text.Substring(start + i, 2));
                for (int L = 2; L <= 4 && i + L <= len; L++) AddPhrase(text.Substring(start + i, L));
            }
        }

        private void AddWord(string w)
        {
            int c;
            if (_words.TryGetValue(w, out c))
            {
                if (c < int.MaxValue) _words[w] = c + 1;
            }
            else if (_words.Count < MaxWordKinds)
            {
                _words[w] = 1;
            }
        }

        // 2~4 字用户词组：出现 1 次先入暂存，出现第 2 次才升级为词组
        private void AddPhrase(string w)
        {
            int c;
            if (_phrases.TryGetValue(w, out c))
            {
                if (c < int.MaxValue) _phrases[w] = c + 1;
                return;
            }
            if (_pending.TryGetValue(w, out c))
            {
                _pending.Remove(w);
                PromotePhrase(w, c + 1);
                return;
            }
            if (_pending.Count >= MaxPendingKinds) _pending.Clear();
            _pending[w] = 1;
        }

        private void PromotePhrase(string w, int count)
        {
            if (_phrases.Count >= MaxPhraseKinds) EvictWeakestPhrase();
            _phrases[w] = count;
        }

        private void EvictWeakestPhrase()
        {
            string weakest = null;
            int weakestCount = int.MaxValue;
            foreach (KeyValuePair<string, int> kv in _phrases)
            {
                if (kv.Value < weakestCount ||
                    (kv.Value == weakestCount && (weakest == null || string.CompareOrdinal(kv.Key, weakest) < 0)))
                {
                    weakest = kv.Key;
                    weakestCount = kv.Value;
                }
            }
            if (weakest != null)
            {
                _phrases.Remove(weakest);
                _phraseEvictions++;
            }
        }

        // 按次数降序返回用户词组（同次数按序数升序，保证稳定）
        public IList<string> UserWords(int max)
        {
            List<string> result = new List<string>();
            if (max <= 0 || _phrases.Count == 0) return result;
            List<KeyValuePair<string, int>> list = new List<KeyValuePair<string, int>>(_phrases);
            list.Sort(delegate(KeyValuePair<string, int> a, KeyValuePair<string, int> b)
            {
                if (a.Value != b.Value) return b.Value.CompareTo(a.Value);
                return string.CompareOrdinal(a.Key, b.Key);
            });
            int cap = max < list.Count ? max : list.Count;
            for (int i = 0; i < cap; i++) result.Add(list[i].Key);
            return result;
        }

        public int UserWordCount(string word)
        {
            if (string.IsNullOrEmpty(word)) return 0;
            int c;
            if (_phrases.TryGetValue(word, out c)) return c;
            return 0;
        }

        public void ObserveRhythm(int charDelayMs, int thinkPauseMs)
        {
            if (charDelayMs < 0) charDelayMs = 0;
            if (thinkPauseMs < 0) thinkPauseMs = 0;
            _rhythmSamples++;
            _rhythmCharSum += charDelayMs;
            _rhythmThinkSum += thinkPauseMs;
        }

        // 词频权重：>=0。已观察到的词条用对数频次；用户词组同样给对数权重；未观察到的回退到内置常用词表权重
        public double WordWeight(string word)
        {
            if (string.IsNullOrEmpty(word)) return 0.0;
            int f;
            if (_words.TryGetValue(word, out f) && f > 0)
            {
                return LogWeight(f);
            }
            if (_phrases.TryGetValue(word, out f) && f > 0)
            {
                return LogWeight(f);
            }
            return CommonWordIndex.Get().DefaultWeight(word);
        }

        private static double LogWeight(int f)
        {
            double v = 1.5 * Math.Log(1.0 + (double)f);
            if (v > 4.0) v = 4.0;
            return v;
        }

        public void Save()
        {
            if (string.IsNullOrEmpty(_path)) return;
            try
            {
                string dir = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                List<KeyValuePair<string, int>> list = new List<KeyValuePair<string, int>>(_words);
                list.Sort(delegate(KeyValuePair<string, int> a, KeyValuePair<string, int> b)
                {
                    if (a.Value != b.Value) return b.Value.CompareTo(a.Value);
                    return string.CompareOrdinal(a.Key, b.Key);
                });
                int cap = list.Count < MaxSavedWords ? list.Count : MaxSavedWords;

                List<KeyValuePair<string, int>> plist = new List<KeyValuePair<string, int>>(_phrases);
                plist.Sort(delegate(KeyValuePair<string, int> a, KeyValuePair<string, int> b)
                {
                    if (a.Value != b.Value) return b.Value.CompareTo(a.Value);
                    return string.CompareOrdinal(a.Key, b.Key);
                });
                int pcap = plist.Count < MaxSavedPhrases ? plist.Count : MaxSavedPhrases;

                StringBuilder sb = new StringBuilder();
                sb.Append(Header).Append('\n');
                sb.Append("total\t").Append(_totalChars).Append('\n');
                sb.Append("rhythm\t").Append(_rhythmSamples).Append('\t').Append(_rhythmCharSum).Append('\t').Append(_rhythmThinkSum).Append('\n');
                for (int i = 0; i < cap; i++)
                {
                    sb.Append("word\t").Append(list[i].Key).Append('\t').Append(list[i].Value).Append('\n');
                }
                for (int i = 0; i < pcap; i++)
                {
                    sb.Append("phrase\t").Append(plist[i].Key).Append('\t').Append(plist[i].Value).Append('\n');
                }
                File.WriteAllText(_path, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        public void Load()
        {
            if (string.IsNullOrEmpty(_path) || !File.Exists(_path)) return;
            try
            {
                string[] lines = File.ReadAllLines(_path, Encoding.UTF8);
                _words.Clear();
                _phrases.Clear();
                _pending.Clear();
                _totalChars = 0;
                _rhythmSamples = 0;
                _rhythmCharSum = 0;
                _rhythmThinkSum = 0;
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (line.Length == 0) continue;
                    string[] parts = line.Split('\t');
                    if (parts.Length < 2) continue;
                    if (parts[0] == "total")
                    {
                        long v;
                        if (long.TryParse(parts[1], out v) && v >= 0) _totalChars = v;
                    }
                    else if (parts[0] == "rhythm" && parts.Length >= 4)
                    {
                        long a, b, c;
                        if (long.TryParse(parts[1], out a) && long.TryParse(parts[2], out b) && long.TryParse(parts[3], out c))
                        {
                            _rhythmSamples = a; _rhythmCharSum = b; _rhythmThinkSum = c;
                        }
                    }
                    else if (parts[0] == "word" && parts.Length >= 3)
                    {
                        int c;
                        if (int.TryParse(parts[2], out c) && c > 0 && parts[1].Length > 0) _words[parts[1]] = c;
                    }
                    else if (parts[0] == "phrase" && parts.Length >= 3)
                    {
                        int c;
                        if (int.TryParse(parts[2], out c) && c >= MinPhraseCount && parts[1].Length >= 2 && parts[1].Length <= 4)
                        {
                            if (_phrases.Count >= MaxPhraseKinds) EvictWeakestPhrase();
                            _phrases[parts[1]] = c;
                        }
                    }
                }
            }
            catch (Exception) { }
        }

        public static bool IsCjk(char c)
        {
            return c >= '\u4e00' && c <= '\u9fff';
        }
    }

    // 只用于自检/回放校验：不落盘的习惯库
    internal sealed class MemoryHabitStore : HabitStore
    {
        public MemoryHabitStore() : base("") { }
    }
}
