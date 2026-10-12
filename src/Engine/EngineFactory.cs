// SimIme 引擎：公开门面（契约 API，签名冻结）
// 资源嵌入要求（构建脚本必须带上，名称一字不差）：
//   /resource:src\Assets\pinyin_full.tsv,SimIme.Assets.pinyin_full.tsv
//   /resource:src\Assets\char_freq.tsv,SimIme.Assets.char_freq.tsv
//   /resource:src\Assets\common_words.tsv,SimIme.Assets.common_words.tsv
// 资源缺失时会回退到 exe 同目录同名文件，再不行也不抛异常（功能降级）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SimIme.Engine
{
    public static class EngineFactory
    {
        private static volatile IPinyinTable _tableCache;

        // 读嵌入资源 SimIme.Assets.pinyin_full.tsv；找不到时回退读 exe 同目录同名文件；
        // 再找不到返回 IsReady=false 的空表。
        public static IPinyinTable CreatePinyinTable()
        {
            IPinyinTable cached = _tableCache;
            if (cached != null) return cached;
            PinyinTable t = PinyinTable.Load();
            if (t.IsReady) _tableCache = t;
            return t;
        }

        // 不存在则新建，Load() 后返回
        public static IHabitStore CreateHabitStore(string path)
        {
            HabitStore store = new HabitStore(path);
            if (!string.IsNullOrEmpty(path))
            {
                try
                {
                    if (!File.Exists(path)) store.Save();
                }
                catch (Exception) { }
            }
            store.Load();
            return store;
        }

        public static IList<Step> BuildPlan(string text, EngineConfig cfg, IPinyinTable table, IHabitStore habits)
        {
            return TypingPlanner.Build(text, cfg, table, habits);
        }

        // 词组切分：各块顺序拼接逐字等于 NormalizeLineEndings(text)（逐块提交仍可保证落屏===原文）
        // 空串返回空列表；同一种子可复现
        public static IList<string> SplitPhrases(string text, EngineConfig cfg)
        {
            return PhraseSplitter.Split(TypingPlanner.NormalizeLineEndings(text), cfg, null);
        }

        // 用户词表版：用户词组（次数 >= cfg.UserWordMinCount）与内置词典一同参与最长匹配，且用户词组优先。
        // 硬契约不变：各块拼接逐字等于 NormalizeLineEndings(text)。habits 为 null 时与两参重载一致。
        public static IList<string> SplitPhrases(string text, EngineConfig cfg, IHabitStore habits)
        {
            return PhraseSplitter.Split(TypingPlanner.NormalizeLineEndings(text), cfg, habits);
        }

        // 外部词表（新增）：注册一份与内置 common_words.tsv 同格式的词表文件，进程内生效。
        // 层级：习惯库用户词组 > 外部词表 > 内置词典。失败/缺失/为空时只记状态并退回内置词典，不抛异常。
        // 返回状态字符串；若设置了 LogSink 会同时回调一行日志。传 null/空串表示注销。
        public static string SetExtraWordFile(string tsvPath)
        {
            string status = ExtraWordIndex.SetFile(tsvPath);
            Notify(status);
            return status;
        }

        // 最近一次外部词表注册的结果（App 可直接读出来写自己的日志）
        public static string ExtraWordStatus
        {
            get { return ExtraWordIndex.Status; }
        }

        // 可选的日志回调（App 设置后，引擎的状态行会经此输出）
        public static Action<string> LogSink;

        private static void Notify(string line)
        {
            Action<string> sink = LogSink;
            if (sink == null) return;
            try { sink(line); }
            catch (Exception) { }
        }

        // 键盘邻键错拼（组串/显示层用，不改变落屏文本）：按 cfg.Typo.KeyboardSlipPct 判定，无错时返回原串
        public static string MakeKeyboardSlip(string pinyin, EngineConfig cfg)
        {
            if (string.IsNullOrEmpty(pinyin)) return pinyin;
            EngineConfig c = cfg == null ? new EngineConfig() : cfg;
            Random rnd = TypingPlanner.CreateRandom(c.RandomSeed);
            return KeyboardSlip.Apply(pinyin, c.Typo.KeyboardSlipPct, rnd);
        }

        // 同上，显式种子且必定尝试一次（便于复现与自检）
        public static string MakeKeyboardSlip(string pinyin, int randomSeed)
        {
            if (string.IsNullOrEmpty(pinyin)) return pinyin;
            Random rnd = TypingPlanner.CreateRandom(randomSeed);
            return KeyboardSlip.Apply(pinyin, 100, rnd);
        }

        // QWERTY 邻键（字母，忽略大小写）：返回邻键字母串；非字母或无邻键返回空串
        public static string NeighborKeysOf(char key)
        {
            return KeyboardSlip.NeighborsOf(key);
        }

        // 回放校验：把计划回放成最终文本，与原文比对，并统计错字/退格/停顿
        public static string VerifyReplay(string text, EngineConfig cfg)
        {
            if (text == null) text = "";
            if (cfg == null) cfg = new EngineConfig();

            IPinyinTable table = CreatePinyinTable();
            IHabitStore habits = new MemoryHabitStore();
            IList<Step> plan = BuildPlan(text, cfg, table, habits);

            StringBuilder outText = new StringBuilder();
            Dictionary<string, int> reasons = new Dictionary<string, int>(StringComparer.Ordinal);
            int charSteps = 0;
            int backspaceSteps = 0;
            int lineBreakSteps = 0;
            int sleepSteps = 0;
            int minDelay = int.MaxValue;
            int maxDelay = 0;
            bool underflow = false;

            for (int i = 0; i < plan.Count; i++)
            {
                Step st = plan[i];
                if (st == null) continue;
                if (st.DelayMs < minDelay) minDelay = st.DelayMs;
                if (st.DelayMs > maxDelay) maxDelay = st.DelayMs;
                if (st.Reason != null && st.Reason.Length > 0)
                {
                    int n;
                    if (reasons.TryGetValue(st.Reason, out n)) reasons[st.Reason] = n + 1;
                    else reasons[st.Reason] = 1;
                }
                if (st.Kind == StepKind.Char)
                {
                    charSteps++;
                    outText.Append(st.Text == null ? "" : st.Text);
                }
                else if (st.Kind == StepKind.LineBreak)
                {
                    lineBreakSteps++;
                    outText.Append('\n');
                }
                else if (st.Kind == StepKind.Backspace)
                {
                    backspaceSteps++;
                    if (outText.Length == 0) underflow = true;
                    else outText.Length = outText.Length - 1;
                }
                else
                {
                    sleepSteps++;
                }
            }

            // 规划会把行结束符归一化为 LF，故比对基准是归一化后的原文
            string expected = TypingPlanner.NormalizeLineEndings(text);
            string finalText = outText.ToString();
            bool matches = string.Equals(finalText, expected, StringComparison.Ordinal);
            bool rawMatches = string.Equals(finalText, text, StringComparison.Ordinal);
            bool eolNormalized = !string.Equals(expected, text, StringComparison.Ordinal);
            int firstMismatch = -1;
            if (!matches)
            {
                int n = finalText.Length < expected.Length ? finalText.Length : expected.Length;
                int idx = 0;
                while (idx < n && finalText[idx] == expected[idx]) idx++;
                firstMismatch = idx;
            }

            string[] typoReasons = new string[]
            {
                TypingPlanner.ReasonHomophone, TypingPlanner.ReasonNasal, TypingPlanner.ReasonHeadRedo,
                TypingPlanner.ReasonOmission, TypingPlanner.ReasonRepeat
            };
            int typoSteps = 0;
            int typoKinds = 0;
            for (int i = 0; i < typoReasons.Length; i++)
            {
                int n;
                if (reasons.TryGetValue(typoReasons[i], out n))
                {
                    typoSteps += n;
                    typoKinds++;
                }
            }

            int thinkCount = 0;
            int restCount = 0;
            int newlineCount = 0;
            reasons.TryGetValue(TypingPlanner.ReasonThink, out thinkCount);
            reasons.TryGetValue(TypingPlanner.ReasonRest, out restCount);
            reasons.TryGetValue(TypingPlanner.ReasonNewline, out newlineCount);

            bool pass = matches && !underflow;

            StringBuilder sb = new StringBuilder();
            sb.Append("RESULT=").Append(pass ? "PASS" : "FAIL").Append('\n');
            sb.Append("finalMatches=").Append(matches ? "True" : "False").Append('\n');
            sb.Append("firstMismatchIndex=").Append(firstMismatch).Append('\n');
            sb.Append("inputChars=").Append(text.Length).Append('\n');
            sb.Append("finalChars=").Append(finalText.Length).Append('\n');
            sb.Append("expectedChars=").Append(expected.Length).Append('\n');
            sb.Append("lineEndingsNormalized=").Append(eolNormalized ? "True" : "False").Append('\n');
            sb.Append("rawInputMatches=").Append(rawMatches ? "True" : "False").Append('\n');
            sb.Append("planSteps=").Append(plan.Count).Append('\n');
            sb.Append("charSteps=").Append(charSteps).Append('\n');
            sb.Append("backspaceSteps=").Append(backspaceSteps).Append('\n');
            sb.Append("lineBreakSteps=").Append(lineBreakSteps).Append('\n');
            sb.Append("sleepSteps=").Append(sleepSteps).Append('\n');
            sb.Append("backspaceUnderflow=").Append(underflow ? "True" : "False").Append('\n');
            sb.Append("typoReasonSteps=").Append(typoSteps).Append('\n');
            sb.Append("typoReasonKinds=").Append(typoKinds).Append('\n');
            sb.Append("thinkPauses=").Append(thinkCount).Append('\n');
            sb.Append("restPauses=").Append(restCount).Append('\n');
            sb.Append("newlineReasonSteps=").Append(newlineCount).Append('\n');
            sb.Append("minDelayMs=").Append(minDelay == int.MaxValue ? 0 : minDelay).Append('\n');
            sb.Append("maxDelayMs=").Append(maxDelay).Append('\n');
            sb.Append("pinyinTableReady=").Append(table.IsReady ? "True" : "False").Append('\n');
            sb.Append("pinyinTableChars=").Append(table.CharCount).Append('\n');
            sb.Append("randomSeed=").Append(cfg.RandomSeed).Append('\n');
            sb.Append("hasTypos=").Append((typoSteps > 0 && backspaceSteps > 0) ? "True" : "False").Append('\n');
            sb.Append("reasons=").Append(FormatReasons(reasons)).Append('\n');
            return sb.ToString();
        }

        private static string FormatReasons(Dictionary<string, int> reasons)
        {
            List<string> keys = new List<string>(reasons.Keys);
            keys.Sort(StringComparer.Ordinal);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < keys.Count; i++)
            {
                if (sb.Length > 0) sb.Append(';');
                sb.Append(keys[i]).Append('=').Append(reasons[keys[i]]);
            }
            return sb.ToString();
        }
    }
}
