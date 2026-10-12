// SimIme 引擎：词组切分（词组级输入法工作流）
// 硬约束：各块顺序拼接必须逐字等于传入文本（调用方已做行结束符归一化）
using System;
using System.Collections.Generic;

namespace SimIme.Engine
{
    internal static class PhraseSplitter
    {
        private const int MaxBlockChars = 4;
        private const int MaxAsciiRun = 16;
        private const int MaxSpaceRun = 3;
        private const int MaxUserWords = 5000;

        public static IList<string> Split(string text, EngineConfig cfg)
        {
            return Split(text, cfg, null);
        }

        // habits 非空时：用户词组（次数 >= cfg.UserWordMinCount）与内置词典一同参与匹配，且用户词组优先。
        // habits 为空时与本方法的两参重载逐字段一致（可复现、不依赖习惯库）。
        public static IList<string> Split(string text, EngineConfig cfg, IHabitStore habits)
        {
            List<string> blocks = new List<string>();
            if (text == null || text.Length == 0) return blocks;
            EngineConfig c = cfg == null ? new EngineConfig() : cfg;
            Random rnd = TypingPlanner.CreateRandom(c.RandomSeed);
            CommonWordIndex words = CommonWordIndex.Get();
            Dictionary<string, int> userWords = SnapshotUserWords(habits, c.UserWordMinCount);
            Dictionary<string, int> extraWords = ExtraWordIndex.Words;
            int jitter = c.PhraseJitterMaxChars;
            if (jitter < 1) jitter = 1;
            if (jitter > MaxBlockChars) jitter = MaxBlockChars;

            int i = 0;
            while (i < text.Length)
            {
                char ch = text[i];
                if (ch == '\n')
                {
                    blocks.Add("\n");
                    i++;
                    continue;
                }
                if (TypingPlanner.IsSpace(ch))
                {
                    int n = RunLength(text, i, IsSpaceChar, MaxSpaceRun);
                    blocks.Add(text.Substring(i, n));
                    i += n;
                    continue;
                }
                if (IsAsciiWordChar(ch))
                {
                    int n = RunLength(text, i, IsAsciiWordChar, MaxAsciiRun);
                    blocks.Add(text.Substring(i, n));
                    i += n;
                    continue;
                }
                if (TypingPlanner.IsPunctuation(ch))
                {
                    blocks.Add(ch.ToString());
                    i++;
                    continue;
                }
                if (HabitStore.IsCjk(ch))
                {
                    int runLen = RunLength(text, i, IsCjkChar, MaxBlockChars);
                    int len = 0;
                    if (userWords != null) len = ChooseUserBlock(text, i, runLen, userWords, c.UserWordMinCount);
                    if (len < 2 && extraWords != null) len = ChooseLongestWord(text, i, runLen, extraWords);
                    if (len < 2) len = ChooseHanziBlock(text, i, runLen, words, rnd, jitter);
                    if (len < 1) len = 1;
                    if (len > runLen) len = runLen;
                    blocks.Add(text.Substring(i, len));
                    i += len;
                    continue;
                }
                blocks.Add(ch.ToString());
                i++;
            }
            return blocks;
        }

        // 词优先：取以 text[start] 开头的最长词典词（2~4 字）；有一定概率只提交其中较短的一段（仍是词典词）
        // 词典无 >=2 字匹配时：jitterMax<=1 只取单字（默认，不造非词块）；jitterMax>1 时按 seed 在 1~jitterMax 内抖动
        private static int ChooseHanziBlock(string text, int start, int runLen, CommonWordIndex words, Random rnd, int jitterMax)
        {
            int cap = runLen < MaxBlockChars ? runLen : MaxBlockChars;
            int best = 0;
            for (int L = cap; L >= 2; L--)
            {
                if (words.Has(text.Substring(start, L))) { best = L; break; }
            }
            if (best >= 2)
            {
                if (best > 2 && rnd.Next(100) < 30)
                {
                    for (int L = best - 1; L >= 2; L--)
                    {
                        if (words.Has(text.Substring(start, L))) return L;
                    }
                }
                return best;
            }
            if (jitterMax <= 1) return 1;
            int jcap = cap < jitterMax ? cap : jitterMax;
            int roll = rnd.Next(100);
            if (roll < 55) return 1;
            if (roll < 85) return jcap >= 2 ? 2 : 1;
            if (jcap >= 3) return 3 + rnd.Next(jcap - 2);
            return jcap >= 2 ? 2 : 1;
        }

        // 拉一份用户词组快照（只取达到阈值的），避免在切分热路径上反复查询习惯库
        private static Dictionary<string, int> SnapshotUserWords(IHabitStore habits, int minCount)
        {
            if (habits == null) return null;
            if (minCount < 2) minCount = 2;
            Dictionary<string, int> map = new Dictionary<string, int>(StringComparer.Ordinal);
            try
            {
                IList<string> list = habits.UserWords(MaxUserWords);
                if (list == null) return null;
                for (int i = 0; i < list.Count; i++)
                {
                    string w = list[i];
                    if (string.IsNullOrEmpty(w) || w.Length < 2 || w.Length > MaxBlockChars) continue;
                    int cnt = habits.UserWordCount(w);
                    if (cnt < minCount) continue;
                    map[w] = cnt;
                }
            }
            catch (Exception)
            {
                return null;
            }
            if (map.Count == 0) return null;
            return map;
        }

        // 用户词组优先：取以该字开头的最长用户词组。
        // 4 字用户词组若与它的 3 字前缀同频（= 前缀永远跟着同一个字出现），则不单独成块，退回 3 字。
        private static int ChooseUserBlock(string text, int start, int runLen, Dictionary<string, int> userWords, int minCount)
        {
            int cap = runLen < MaxBlockChars ? runLen : MaxBlockChars;
            for (int L = cap; L >= 2; L--)
            {
                int cnt;
                if (!userWords.TryGetValue(text.Substring(start, L), out cnt) || cnt < minCount) continue;
                if (L == MaxBlockChars)
                {
                    int pc;
                    if (userWords.TryGetValue(text.Substring(start, L - 1), out pc) && pc >= minCount && cnt >= pc) continue;
                }
                return L;
            }
            return 0;
        }

        // 外部词表：与内置词典等同的最长匹配（不消耗随机数）
        private static int ChooseLongestWord(string text, int start, int runLen, Dictionary<string, int> table)
        {
            int cap = runLen < MaxBlockChars ? runLen : MaxBlockChars;
            for (int L = cap; L >= 2; L--)
            {
                if (table.ContainsKey(text.Substring(start, L))) return L;
            }
            return 0;
        }

        private static int RunLength(string text, int start, Predicate<char> match, int cap)
        {
            int n = 0;
            while (n < cap && start + n < text.Length && match(text[start + n])) n++;
            return n;
        }

        private static bool IsSpaceChar(char ch) { return TypingPlanner.IsSpace(ch); }

        private static bool IsCjkChar(char ch) { return HabitStore.IsCjk(ch); }

        private static bool IsAsciiWordChar(char ch)
        {
            if (ch >= '0' && ch <= '9') return true;
            if (ch >= 'a' && ch <= 'z') return true;
            if (ch >= 'A' && ch <= 'Z') return true;
            return false;
        }
    }
}
