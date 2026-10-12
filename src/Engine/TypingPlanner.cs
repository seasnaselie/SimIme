// SimIme 引擎：拟人化打字计划生成器（纯逻辑，无 UI / 无 Win32）
using System;
using System.Collections.Generic;
using System.Text;

namespace SimIme.Engine
{
    internal static class TypingPlanner
    {
        public const string ReasonThink = "思考停顿";
        public const string ReasonRest = "休息";
        public const string ReasonNewline = "换行";
        public const string ReasonHomophone = "同音字错字";
        public const string ReasonNasal = "鼻音错字";
        public const string ReasonHeadRedo = "句首重打";
        public const string ReasonOmission = "漏字回填";
        public const string ReasonRepeat = "重复输入";
        public const string ReasonWordHomophone = "词组错词";

        // 【应用层硬依赖，重构时不要破坏】App 用"带 Reason 的字符"做 LIFO 归属修复：
        // 错字演出里**多打的 Char 必须带 Reason**，且结构必须是 Char(多打) -> 等量 Backspace(紧随其后) -> Char(正确)。
        // 目前各错字类型都遵守：同音/鼻音/词组错词 = 错字 Char -> 退格 -> 正确 Char；句首重打 = k 个错字 Char -> k 个退格 -> 正确文本。
        // 任何新增类型都不要把"多打的字"与"正确字"混在一个不带 Reason 的 Char 里。

        public static IList<Step> Build(string text, EngineConfig cfg, IPinyinTable table, IHabitStore habits)
        {
            if (text == null) return new List<Step>();
            // 行结束符先归一化：CRLF / 单独 CR / 单独 LF 都恰好对应一个 LineBreak 步（不产生 Char(CR)）
            string normalized = NormalizeLineEndings(text);
            if (normalized.Length == 0) return new List<Step>();
            EngineConfig c = cfg == null ? new EngineConfig() : cfg;
            Planner p = new Planner(normalized, c, table, habits);
            return p.Run();
        }

        // 行结束符归一化：CRLF -> LF，单独 CR -> LF，单独 LF 不变。只处理行结束符，其它字符（含 \r 以外的控制符）原样保留。
        // 因此计划的落屏等价物 = 把输入的行结束符统一成 LF 后的文本。
        internal static string NormalizeLineEndings(string text)
        {
            if (text == null) return null;
            if (text.IndexOf('\r') < 0) return text;
            StringBuilder sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (ch == '\r')
                {
                    sb.Append('\n');
                    if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                }
                else
                {
                    sb.Append(ch);
                }
            }
            return sb.ToString();
        }

        // 供 PhraseSplitter / KeyboardSlip / EngineFactory 复用的工具（实现位于 Planner 内）
        internal static Random CreateRandom(int randomSeed)
        {
            return Planner.CreateRandom(randomSeed);
        }

        internal static bool IsSpace(char c)
        {
            return Planner.IsSpace(c);
        }

        internal static bool IsPunctuation(char c)
        {
            return Planner.IsPunctuation(c);
        }

        private sealed class Planner
        {
            private readonly string _text;
            private readonly EngineConfig _cfg;
            private readonly IPinyinTable _table;
            private readonly IHabitStore _habits;
            private readonly Random _rnd;
            private readonly List<Step> _plan = new List<Step>();

            private int _charsSinceThink;
            private int _thinkThreshold;
            private int _sentencesSinceRest;
            private int _restThreshold;
            private int _typosInSentence;
            private int _burstLeft;
            private int _burstSlowPct;
            private readonly Dictionary<int, int> _blockLen = new Dictionary<int, int>();
            private int[] _blockStartAt = new int[0];   // 每个位置所属词块的起点
            private int[] _blockEndAt = new int[0];     // 每个位置所属词块的终点（不含）

            public Planner(string text, EngineConfig cfg, IPinyinTable table, IHabitStore habits)
            {
                _text = text;
                _cfg = cfg;
                _table = table;
                _habits = habits;
                _rnd = CreateRandom(cfg.RandomSeed);
                BuildBlockMap(cfg);
                _thinkThreshold = NextThinkThreshold();
                _restThreshold = NextRestThreshold();
            }

            private static int _zeroSeedCounter;

            // 随机源约定：RandomSeed != 0 逐字段可复现；= 0 每次随机（TickCount 混合自增序号，避免同毫秒重复）
            internal static Random CreateRandom(int randomSeed)
            {
                if (randomSeed != 0) return new Random(randomSeed);
                int n = System.Threading.Interlocked.Increment(ref _zeroSeedCounter);
                return new Random(unchecked(Environment.TickCount * 397 + n * 104729));
            }

            // 词组块表：起点 -> 块长。与 SplitPhrases 用同一套切分，保证词组级错词与提交块对齐
            private void BuildBlockMap(EngineConfig cfg)
            {
                IList<string> blocks = PhraseSplitter.Split(_text, cfg);
                _blockStartAt = new int[_text.Length];
                _blockEndAt = new int[_text.Length];
                int pos = 0;
                for (int b = 0; b < blocks.Count; b++)
                {
                    int len = blocks[b] == null ? 0 : blocks[b].Length;
                    if (len > 0)
                    {
                        int end = pos + len;
                        _blockLen[pos] = len;
                        for (int t = pos; t < end && t < _text.Length; t++)
                        {
                            _blockStartAt[t] = pos;
                            _blockEndAt[t] = end;
                        }
                    }
                    pos += len;
                }
                // 兜底：任何没有被分块覆盖的位置自成一个块（正常不会发生）
                for (int t = 0; t < _text.Length; t++)
                {
                    if (_blockEndAt[t] == 0) { _blockStartAt[t] = t; _blockEndAt[t] = t + 1; }
                }
            }

            // 位置所在词块的块首/块尾（不含）；所有错字演出都必须留在这两者之间
            private int BlockStartOf(int pos)
            {
                if (pos < 0 || pos >= _blockStartAt.Length) return pos;
                return _blockStartAt[pos];
            }

            private int BlockEndOf(int pos)
            {
                if (pos < 0 || pos >= _blockEndAt.Length) return pos + 1;
                int e = _blockEndAt[pos];
                return e <= pos ? pos + 1 : e;
            }

            // 词组级同音错词：整块替换为同音错词 -> 退格整块 -> 打正确块
            private bool TryWordHomophone(int i, out int consumed)
            {
                consumed = 0;
                if (!TyposAllowed()) return false;
                if (!Roll(_cfg.Typo.WordHomophonePct)) return false;
                int len;
                if (!_blockLen.TryGetValue(i, out len)) return false;   // 只在块首判定
                int wordBlockEnd = BlockEndOf(i);
                if (len > wordBlockEnd - i) len = wordBlockEnd - i;     // 不跨块
                if (len < 2 || i + len > _text.Length) return false;
                if (_table == null || !_table.IsReady) return false;

                List<char>[] alts = new List<char>[len];
                int usable = 0;
                for (int t = 0; t < len; t++)
                {
                    char c = _text[i + t];
                    if (!HabitStore.IsCjk(c)) return false;
                    List<char> list = new List<char>();
                    IList<string> cands = _table.HomophonesOf(c, 16);
                    if (cands != null)
                    {
                        for (int k = 0; k < cands.Count; k++)
                        {
                            string s = cands[k];
                            if (!string.IsNullOrEmpty(s) && s[0] != c && !list.Contains(s[0])) list.Add(s[0]);
                        }
                    }
                    for (int k = list.Count - 1; k > 0; k--)
                    {
                        int sw = _rnd.Next(k + 1);
                        char tmp = list[k]; list[k] = list[sw]; list[sw] = tmp;
                    }
                    alts[t] = list;
                    if (list.Count > 0) usable++;
                }
                if (usable == 0) return false;

                // 优先挑"真实存在的同音词"（等价于人打对拼音却选错候选，如 星星 -> 猩猩），否则每个字随机取同音字
                char[] pick = new char[len];
                bool hasWord = false;
                if (usable == len)
                {
                    CommonWordIndex words = CommonWordIndex.Get();
                    int budget = 2000;
                    hasWord = PickHomophoneWord(alts, pick, 0, words, ref budget);
                }
                if (!hasWord)
                {
                    for (int t = 0; t < len; t++)
                    {
                        List<char> list = alts[t];
                        pick[t] = list.Count > 0 ? list[0] : _text[i + t];
                    }
                }
                if (new string(pick) == _text.Substring(i, len)) return false;
                string wrongText = new string(pick);
                for (int t = 0; t < wrongText.Length; t++) AddCharRaw(wrongText[t], ReasonWordHomophone);
                for (int t = 0; t < wrongText.Length; t++) AddBackspace(ReasonWordHomophone);
                for (int t = 0; t < len; t++) AddChar(_text[i + t], i + t);
                _typosInSentence++;
                consumed = len;
                return true;
            }

            // 在候选组合里找真实存在的同音词；budget 限制搜索规模
            private static bool PickHomophoneWord(List<char>[] alts, char[] pick, int depth, CommonWordIndex words, ref int budget)
            {
                if (budget <= 0) return false;
                if (depth == alts.Length)
                {
                    budget--;
                    return words.Has(new string(pick));
                }
                List<char> list = alts[depth];
                for (int k = 0; k < list.Count; k++)
                {
                    pick[depth] = list[k];
                    if (PickHomophoneWord(alts, pick, depth + 1, words, ref budget)) return true;
                    if (budget <= 0) return false;
                }
                return false;
            }

            public List<Step> Run()
            {
                int i = 0;
                while (i < _text.Length)
                {
                    char c = _text[i];
                    bool sentenceStart = IsSentenceStart(i);
                    if (sentenceStart) _typosInSentence = 0;

                    if (c == '\n')
                    {
                        AddLineBreak();
                        CountSentenceDone();
                        i++;
                        continue;
                    }

                    if (sentenceStart && !IsSpace(c)) TryHeadRedo(i);

                    if (HabitStore.IsCjk(c))
                    {
                        int blockConsumed;
                        if (TryWordHomophone(i, out blockConsumed)) { i += blockConsumed; continue; }
                        if (TryOmission(i)) { i++; continue; }
                        int repeatConsumed;
                        if (TryRepeatWord(i, out repeatConsumed)) { i += repeatConsumed; continue; }
                        if (TrySubstitution(i, true)) { i++; continue; }
                        if (TrySubstitution(i, false)) { i++; continue; }
                    }

                    AddChar(c, i);
                    if (IsSentenceEnd(c)) CountSentenceDone();
                    i++;
                }
                return _plan;
            }

            // ---------- 步骤产生 ----------

            private void AddChar(char c, int index)
            {
                _plan.Add(new Step(StepKind.Char, c.ToString(), NextCharDelay(c, index), null));
                AfterCharEmitted();
            }

            private void AddCharRaw(char c, string reason)
            {
                _plan.Add(new Step(StepKind.Char, c.ToString(), NextCharDelay(c, -1), reason));
                AfterCharEmitted();
            }

            private void AddBackspace(string reason)
            {
                RhythmConfig r = _cfg.Rhythm;
                int min = NonNegative(r.CharDelayMinMs);
                int max = NonNegative(r.CharDelayMaxMs);
                if (max < min) max = min;
                int hi = min + (max - min) / 2;
                _plan.Add(new Step(StepKind.Backspace, null, RandRange(min, hi), reason));
            }

            private void AddLineBreak()
            {
                RhythmConfig r = _cfg.Rhythm;
                int min = NonNegative(r.CharDelayMinMs);
                int max = NonNegative(r.CharDelayMaxMs);
                if (max < min) max = min;
                _plan.Add(new Step(StepKind.LineBreak, "\n", RandRange(min, max), ReasonNewline));
                AfterCharEmitted();
            }

            // Char 步延时：
            //   普通字  = 字延时，抖动在 [CharDelayMinMs, CharDelayMaxMs]
            //   标点/句末 = 上述字延时 + PunctuationExtra（[PunctuationExtraMinMs, PunctuationExtraMaxMs]）叠加
            // 因此标点步落在 [CharDelayMinMs + PunctuationExtraMinMs, CharDelayMaxMs + PunctuationExtraMaxMs]
            private int NextCharDelay(char c, int index)
            {
                RhythmConfig r = _cfg.Rhythm;
                int min = NonNegative(r.CharDelayMinMs);
                int max = NonNegative(r.CharDelayMaxMs);
                if (max < min) max = min;

                bool punct = IsPunctuation(c);
                double d = RandRange(min, max);

                if (punct)
                {
                    _burstLeft = 0;
                }
                else
                {
                    if (_burstLeft <= 0) StartBurstIfDue();
                    if (_burstLeft > 0)
                    {
                        d = d * (100.0 - _burstSlowPct) / 100.0;
                        _burstLeft--;
                    }
                }

                d = ApplyHabitSpeedup(d, index);

                int baseDelay = (int)Math.Round(d);
                if (baseDelay < min) baseDelay = min;
                if (baseDelay > max) baseDelay = max;
                if (baseDelay < 0) baseDelay = 0;

                if (!punct) return baseDelay;

                int lo = NonNegative(r.PunctuationExtraMinMs);
                int hi = NonNegative(r.PunctuationExtraMaxMs);
                if (hi < lo) hi = lo;
                int total = baseDelay + RandRange(lo, hi);
                if (total < 0) total = 0;
                return total;
            }

            private void StartBurstIfDue()
            {
                int chance = NonNegative(_cfg.Rhythm.BurstChancePct);
                if (chance > 100) chance = 100;
                if (_rnd.Next(100) >= chance) return;
                int v = NonNegative(_cfg.Rhythm.SpeedVariancePct);
                if (v > 90) v = 90;
                int half = v / 2;
                _burstSlowPct = half + _rnd.Next(half + 1);
                _burstLeft = RandRange(3, 10);
            }

            // 习惯库微调：常用词打得更快；只减速不加速，且最终仍被夹在配置区间内
            private double ApplyHabitSpeedup(double d, int index)
            {
                if (_habits == null || index < 0 || index + 1 >= _text.Length) return d;
                if (!HabitStore.IsCjk(_text[index]) || !HabitStore.IsCjk(_text[index + 1])) return d;
                double w;
                try
                {
                    w = _habits.WordWeight(_text.Substring(index, 2));
                }
                catch (Exception)
                {
                    return d;
                }
                if (double.IsNaN(w) || double.IsInfinity(w) || w <= 0.0) return d;
                if (w > 4.0) w = 4.0;
                return d / (1.0 + w * 0.15);
            }

            private void AfterCharEmitted()
            {
                _charsSinceThink++;
                if (_charsSinceThink < _thinkThreshold) return;
                RhythmConfig r = _cfg.Rhythm;
                int lo = NonNegative(r.ThinkPauseMinMs);
                int hi = NonNegative(r.ThinkPauseMaxMs);
                if (hi < lo) hi = lo;
                _plan.Add(new Step(StepKind.Sleep, null, RandRange(lo, hi), ReasonThink));
                _charsSinceThink = 0;
                _thinkThreshold = NextThinkThreshold();
            }

            private void CountSentenceDone()
            {
                _sentencesSinceRest++;
                if (_sentencesSinceRest < _restThreshold) return;
                RhythmConfig r = _cfg.Rhythm;
                int lo = NonNegative(r.RestPauseMinMs);
                int hi = NonNegative(r.RestPauseMaxMs);
                if (hi < lo) hi = lo;
                _plan.Add(new Step(StepKind.Sleep, null, RandRange(lo, hi), ReasonRest));
                _sentencesSinceRest = 0;
                _restThreshold = NextRestThreshold();
            }

            private int NextThinkThreshold()
            {
                int lo = _cfg.Rhythm.ThinkEveryMinChars;
                int hi = _cfg.Rhythm.ThinkEveryMaxChars;
                if (lo < 1) lo = 1;
                if (hi < lo) hi = lo;
                return RandRange(lo, hi);
            }

            private int NextRestThreshold()
            {
                int lo = _cfg.Rhythm.RestEveryMinSentences;
                int hi = _cfg.Rhythm.RestEveryMaxSentences;
                if (lo < 1) lo = 1;
                if (hi < lo) hi = lo;
                return RandRange(lo, hi);
            }

            // ---------- 错字 ----------

            private bool TyposAllowed()
            {
                if (!_cfg.Typo.Enabled) return false;
                if (_cfg.Typo.MaxTyposPerSentence <= 0) return false;
                return _typosInSentence < _cfg.Typo.MaxTyposPerSentence;
            }

            private bool Roll(int pct)
            {
                if (pct <= 0) return false;
                if (pct >= 100) return true;
                return _rnd.Next(100) < pct;
            }

            // 同音字错字 / 鼻音错字：打错 -> 退格 -> 重打正确字
            private bool TrySubstitution(int i, bool homophone)
            {
                if (!TyposAllowed()) return false;
                int pct = homophone ? _cfg.Typo.HomophonePct : _cfg.Typo.NasalPct;
                if (!Roll(pct)) return false;
                if (_table == null || !_table.IsReady) return false;
                char c = _text[i];
                IList<string> cands = homophone ? _table.HomophonesOf(c, 8) : _table.NasalVariantsOf(c, 8);
                if (cands == null || cands.Count == 0) return false;
                string wrong = cands[_rnd.Next(cands.Count)];
                if (string.IsNullOrEmpty(wrong)) return false;
                string reason = homophone ? ReasonHomophone : ReasonNasal;
                AddCharRaw(wrong[0], reason);
                AddBackspace(reason);
                AddChar(c, i);
                _typosInSentence++;
                return true;
            }

            // 句首重打：把"即将输入的开头"先打成同音/近音错字，退格删净，再正常输入。
            // 片段**逐字取自即将输入的文本本身**（位置 i..i+k-1），只是把其中能改的字换成同音字（拿不到同音就换近音字=前后鼻音），
            // 因此屏幕上不会出现与上下文无关的字。例：即将输入 抖动(dou dong) -> 先打 斗冬 -> 退格 2 次 -> 再打 抖动。
            private void TryHeadRedo(int i)
            {
                if (!TyposAllowed()) return;
                if (!Roll(_cfg.Typo.HeadRedoPct)) return;
                if (_table == null || !_table.IsReady) return;

                int k = RandRange(1, 3);
                int blockEndHr = BlockEndOf(i);   // 句首重打的片段也必须留在同一个词块内
                int avail = 0;
                while (avail < k && i + avail < blockEndHr)
                {
                    char c = _text[i + avail];
                    if (c == '\n' || IsSentenceEnd(c) || IsSpace(c)) break;
                    avail++;
                }
                if (avail < 1) return;

                char[] wrong = new char[avail];
                int changed = 0;
                for (int t = 0; t < avail; t++)
                {
                    char c = _text[i + t];
                    char w = PickRelatedChar(c);
                    if (w != '\0' && w != c)
                    {
                        wrong[t] = w;
                        changed++;
                    }
                    else
                    {
                        wrong[t] = c;
                    }
                }
                if (changed == 0) return; // 一个字都改不动就不演这个错字（绝不凭空造无关字）

                for (int t = 0; t < avail; t++) AddCharRaw(wrong[t], ReasonHeadRedo);
                for (int t = 0; t < avail; t++) AddBackspace(ReasonHeadRedo);
                _typosInSentence++;
            }

            // 与给定汉字"同音"或"近音"（前后鼻音）的常用字；拿不到返回 '\0'
            private char PickRelatedChar(char c)
            {
                if (!HabitStore.IsCjk(c)) return '\0';
                IList<string> hom = _table.HomophonesOf(c, 8);
                if (hom != null && hom.Count > 0)
                {
                    string s = hom[_rnd.Next(hom.Count)];
                    if (!string.IsNullOrEmpty(s) && s[0] != c) return s[0];
                }
                IList<string> nas = _table.NasalVariantsOf(c, 8);
                if (nas != null && nas.Count > 0)
                {
                    string s = nas[_rnd.Next(nas.Count)];
                    if (!string.IsNullOrEmpty(s) && s[0] != c) return s[0];
                }
                return '\0';
            }

            // 漏字回填：跳过一个字继续打 1~3 个字，再退格回来补上
            private bool TryOmission(int i)
            {
                if (!TyposAllowed()) return false;
                if (!Roll(_cfg.Typo.OmissionPct)) return false;
                int want = RandRange(1, 3);
                int blockEndOm = BlockEndOf(i);   // 漏字回填往前打的字也必须留在同一个词块内
                int avail = 0;
                while (avail < want && i + 1 + avail < blockEndOm && _text[i + 1 + avail] != '\n') avail++;
                if (avail < 1) return false;
                for (int t = 1; t <= avail; t++) AddCharRaw(_text[i + t], ReasonOmission);
                for (int t = 1; t <= avail; t++) AddBackspace(ReasonOmission);
                AddChar(_text[i], i);
                _typosInSentence++;
                return true;
            }

            // 词重复：**只在本词块内**取字 —— 块内还剩 >=2 个连续汉字时 a b a b + 2 退格（净 a b）；
            // 块内只剩 1 个字时 a a + 1 退格（净 a）；取不到就跳过（宁可不演，绝不跨块）。
            // 跨块会让打出的串横跨两个词（如 先别|关程序 打出 别关别关），应用层的组串显示会与落屏对不上。
            private bool TryRepeatWord(int i, out int consumed)
            {
                consumed = 0;
                if (!TyposAllowed()) return false;
                if (!Roll(_cfg.Typo.RepeatWordPct)) return false;
                if (i >= _text.Length) return false;
                char a = _text[i];
                if (!HabitStore.IsCjk(a)) return false;
                int blockEnd = BlockEndOf(i);
                if (i + 1 < blockEnd)
                {
                    char b = _text[i + 1];
                    if (!HabitStore.IsCjk(b)) return false;
                    AddCharRaw(a, ReasonRepeat);
                    AddCharRaw(b, ReasonRepeat);
                    AddCharRaw(a, ReasonRepeat);
                    AddCharRaw(b, ReasonRepeat);
                    AddBackspace(ReasonRepeat);
                    AddBackspace(ReasonRepeat);
                    _typosInSentence++;
                    consumed = 2;
                    return true;
                }
                AddCharRaw(a, ReasonRepeat);
                AddCharRaw(a, ReasonRepeat);
                AddBackspace(ReasonRepeat);
                _typosInSentence++;
                consumed = 1;
                return true;
            }

            // ---------- 文本分类辅助 ----------

            private bool IsSentenceStart(int i)
            {
                if (i <= 0) return true;
                char prev = _text[i - 1];
                return prev == '\n' || IsSentenceEnd(prev);
            }

            internal static bool IsSpace(char c)
            {
                return c == ' ' || c == '\t' || c == '\r';
            }

            private static bool IsSentenceEnd(char c)
            {
                if (c == '.' || c == '!' || c == '?' || c == ';') return true;
                switch (c)
                {
                    case '。': return true;
                    case '！': return true;
                    case '？': return true;
                    case '；': return true;
                    case '…': return true;
                    default: return false;
                }
            }

            internal static bool IsPunctuation(char c)
            {
                if (c < (char)128)
                {
                    if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) return false;
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r') return false;
                    return true;
                }
                switch (c)
                {
                    case '。': return true;
                    case '，': return true;
                    case '、': return true;
                    case '；': return true;
                    case '：': return true;
                    case '？': return true;
                    case '！': return true;
                    case '…': return true;
                    case '—': return true;
                    case '～': return true;
                    case '·': return true;
                    case '「': return true;
                    case '」': return true;
                    case '『': return true;
                    case '』': return true;
                    case '《': return true;
                    case '》': return true;
                    case '〈': return true;
                    case '〉': return true;
                    case '【': return true;
                    case '】': return true;
                    case '（': return true;
                    case '）': return true;
                    case '〔': return true;
                    case '〕': return true;
                    case '“': return true;
                    case '”': return true;
                    case '‘': return true;
                    case '’': return true;
                    case '－': return true;
                    case '＋': return true;
                    default: return false;
                }
            }

            // ---------- 通用 ----------

            private static int NonNegative(int v)
            {
                return v < 0 ? 0 : v;
            }

            private int RandRange(int lo, int hi)
            {
                if (hi < lo) hi = lo;
                if (hi == lo) return lo;
                long span = (long)hi - (long)lo + 1L;
                if (span <= 0) return lo;
                if (span > int.MaxValue) span = int.MaxValue;
                return lo + _rnd.Next((int)span);
            }
        }
    }
}
