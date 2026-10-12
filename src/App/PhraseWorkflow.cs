using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using SimIme.Engine;

namespace SimIme.App
{
    // 用户词表读取：优先 A 的 UserWords(int)；未交付时反射读 HabitStore 的词频字典（并明确标注来源）
    internal static class UserWordTable
    {
        private static MethodInfo _userWords;
        private static bool _probed;
        private static string _probeNote = "";          // 找不到时的原因（真取不到才写）
        private static string _apiName = "";

        // 优先用契约里的接口方法 IHabitStore.UserWords(int)（最稳），其次 HabitStore 实例方法，最后 EngineFactory 静态方法
        internal static void Probe()
        {
            if (_probed) return;
            _probed = true;
            List<string> tried = new List<string>();
            try
            {
                _userWords = typeof(IHabitStore).GetMethod("UserWords", BindingFlags.Public | BindingFlags.Instance,
                    null, new Type[] { typeof(int) }, null);
                if (_userWords != null) _apiName = "IHabitStore.UserWords";
            }
            catch (Exception ex) { tried.Add("IHabitStore: " + ex.Message); }

            if (_userWords == null)
            {
                try
                {
                    _userWords = typeof(HabitStore).GetMethod("UserWords", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                        null, new Type[] { typeof(int) }, null);
                    if (_userWords != null) _apiName = "HabitStore.UserWords";
                }
                catch (Exception ex) { tried.Add("HabitStore: " + ex.Message); }
            }
            if (_userWords == null)
            {
                try
                {
                    _userWords = typeof(EngineFactory).GetMethod("UserWords", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                        null, new Type[] { typeof(int) }, null);
                    if (_userWords != null) _apiName = "EngineFactory.UserWords";
                }
                catch (Exception ex) { tried.Add("EngineFactory: " + ex.Message); }
            }
            _probeNote = _userWords != null ? "" : ("在 IHabitStore / HabitStore / EngineFactory 上都没找到 UserWords(int)"
                + (tried.Count > 0 ? "（异常：" + string.Join("；", tried.ToArray()) + "）" : ""));
        }

        // 返回 (词, 次数)，按次数降序；source 说明取自哪条路径
        internal static List<KeyValuePair<string, int>> TopWords(IHabitStore habits, int max, out string source)
        {
            Probe();
            List<KeyValuePair<string, int>> result = new List<KeyValuePair<string, int>>();
            source = "";
            if (habits == null)
            {
                source = "(习惯库不可用)";
                return result;
            }

            if (_userWords != null)
            {
                try
                {
                    object target = _userWords.IsStatic ? null : (object)habits;
                    object value = _userWords.Invoke(target, new object[] { max });
                    List<string> words = new List<string>();
                    string single = value as string;
                    if (single != null) words.Add(single);
                    else
                    {
                        IEnumerable seq = value as IEnumerable;
                        if (seq != null)
                        {
                            foreach (object item in seq)
                            {
                                if (item == null) continue;
                                string s = item as string;
                                if (s == null)
                                {
                                    PropertyInfo p = item.GetType().GetProperty("Word", BindingFlags.Public | BindingFlags.Instance);
                                    if (p != null) s = p.GetValue(item, null) as string;
                                }
                                if (!string.IsNullOrEmpty(s)) words.Add(s);
                            }
                        }
                    }
                    for (int i = 0; i < words.Count && i < max; i++)
                    {
                        int count = 0;
                        try { count = (int)Math.Round(habits.WordWeight(words[i])); } catch { }
                        result.Add(new KeyValuePair<string, int>(words[i], count));
                    }
                    // 能调通就是成功：**读到 0 条也返回 0 条**（空库 ≠ 取不到）
                    source = _apiName + "（库为空，尚无学习数据）";
                    if (result.Count == 0) LogEmptyOnce();
                    else source = _apiName;
                    return result;
                }
                catch (Exception ex)
                {
                    Log.Error("调用 " + _apiName + " 失败，回退到词频字典：" + ex.GetType().Name + ": " + ex.Message, ex);
                    _apiFailed = "调用 " + _apiName + " 失败（" + ex.GetType().Name + "），回退到词频字典";
                }
            }
            else
            {
                _apiFailed = "未找到 UserWords，回退到词频字典" + (_probeNote.Length > 0 ? "（" + _probeNote + "）" : "");
                LogMissingOnce(_apiFailed);
            }

            // 真的取不到时才走这里：反射读词频字典（只读，不改动引擎）
            try
            {
                FieldInfo field = habits.GetType().GetField("_words", BindingFlags.NonPublic | BindingFlags.Instance);
                IDictionary dict = field == null ? null : field.GetValue(habits) as IDictionary;
                if (dict != null)
                {
                    List<KeyValuePair<string, int>> all = new List<KeyValuePair<string, int>>();
                    foreach (DictionaryEntry entry in dict)
                    {
                        string word = entry.Key as string;
                        if (string.IsNullOrEmpty(word)) continue;
                        int count = 0;
                        try { count = Convert.ToInt32(entry.Value); } catch { }
                        all.Add(new KeyValuePair<string, int>(word, count));
                    }
                    all.Sort(delegate(KeyValuePair<string, int> a, KeyValuePair<string, int> b)
                    {
                        if (b.Value != a.Value) return b.Value.CompareTo(a.Value);
                        return string.CompareOrdinal(a.Key, b.Key);
                    });
                    for (int i = 0; i < all.Count && i < max; i++) result.Add(all[i]);
                    source = (_apiFailed.Length > 0 ? _apiFailed : "反射读取词频字典") + "（词频字典路径）";
                    return result;
                }
            }
            catch (Exception ex)
            {
                Log.Error("反射读取用户词表失败", ex);
            }
            source = (_apiFailed.Length > 0 ? _apiFailed : "无法读取用户词表");
            return result;
        }

        private static string _apiFailed = "";
        private static bool _emptyLogged;
        private static bool _missingLogged;

        private static void LogEmptyOnce()
        {
            if (_emptyLogged) return;
            _emptyLogged = true;
            Log.Info("用户词表：0 个用户词组（习惯库为空，尚无学习数据）——正常，输入几次后就会累积");
        }

        private static void LogMissingOnce(string detail)
        {
            if (_missingLogged) return;
            _missingLogged = true;
            Log.Error("警告：" + detail);
        }

        // 用户词组阈值：A 的 EngineConfig.UserWordMinCount（未交付时明确说明，不猜默认值）
        internal static string MinCountInfo(AppConfig cfg)
        {
            try
            {
                FieldInfo f = typeof(EngineConfig).GetField("UserWordMinCount", BindingFlags.Public | BindingFlags.Instance);
                if (f == null) return "（未找到 EngineConfig.UserWordMinCount，当前切分不含用户词表阈值）";
                object v = f.GetValue(cfg.Engine);
                return v == null ? "(空)" : v.ToString();
            }
            catch (Exception ex)
            {
                Log.Error("读取 UserWordMinCount 失败", ex);
                return "(读取失败)";
            }
        }

        internal static int MinCountValue(AppConfig cfg, int fallback)
        {
            try
            {
                FieldInfo f = typeof(EngineConfig).GetField("UserWordMinCount", BindingFlags.Public | BindingFlags.Instance);
                if (f != null)
                {
                    object v = f.GetValue(cfg.Engine);
                    if (v is int) return (int)v;
                }
            }
            catch (Exception ex)
            {
                Log.Error("读取 UserWordMinCount 失败", ex);
            }
            return fallback;
        }

        // 用户词组个数（2~4 字、达到阈值；走 A 的 UserWords 或反射词表）
        private const int WordQueryCap = 10000;   // 别把 int.MaxValue 丢给引擎（词多时那条路径有风险）

        internal static int UserWordCount(IHabitStore habits)
        {
            string source;
            return TopWords(habits, WordQueryCap, out source).Count;
        }

        internal static int WordKindCount(IHabitStore habits)
        {
            try
            {
                PropertyInfo p = habits.GetType().GetProperty("WordKindCount", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null) return Convert.ToInt32(p.GetValue(habits, null));
            }
            catch { }
            try
            {
                string source;
                return TopWords(habits, WordQueryCap, out source).Count;
            }
            catch { return 0; }
        }

        // 主界面状态行的简短文案（与 --words / 启动日志同一套判据）
        internal static string ShortStatus(IHabitStore habits)
        {
            if (habits == null) return "用户词表：习惯库不可用";
            int userWords = UserWordCount(habits);
            string note = ReadNote();
            if (userWords == 0) return "用户词表：0 个用户词组（库为空，尚无学习数据）" + note;
            return "用户词表：" + userWords + " 个用户词组";
        }

        // 只有"真取不到"才会是非空字符串；空库不算异常
        internal static string ReadNote()
        {
            if (_apiFailed.Length > 0) return "｜" + _apiFailed;
            return "";
        }

        internal static string Describe(IHabitStore habits, int sampleCount)
        {
            string source;
            List<KeyValuePair<string, int>> top = TopWords(habits, sampleCount <= 0 ? 5 : sampleCount, out source);
            int total = WordKindCount(habits);   // 词频表条目数（含 1 元词）
            int userWords = UserWordCount(habits);   // 用户词组总数（不是采样数）
            if (userWords == 0 && source.IndexOf("库为空", StringComparison.Ordinal) >= 0)
            {
                // 空库：说清是"还没有学习数据"，不要写成"A 尚未交付"
                return "用户词表：0 个用户词组（习惯库为空，尚无学习数据）；词频表条目 " + total + " 个｜来源=" + source
                    + "（输入几次后会自动累积）";
            }
            StringBuilder sb = new StringBuilder();
            sb.Append("用户词表：已学习 ").Append(userWords).Append(" 个用户词组（2~4 字）；词频表条目 ").Append(total).Append(" 个");
            if (top.Count > 0)
            {
                sb.Append("（例：");
                for (int i = 0; i < top.Count; i++)
                {
                    if (i > 0) sb.Append('、');
                    sb.Append(top[i].Key).Append('×').Append(top[i].Value);
                }
                sb.Append('）');
            }
            if (!string.IsNullOrEmpty(source)) sb.Append("｜来源=").Append(source);
            return sb.ToString();
        }
    }

    internal sealed class PhraseBlock
    {
        internal string Text = "";
        internal string Pinyin = "";
        internal int Start;
        internal bool Chinese;
        internal int Length { get { return Text.Length; } }
        internal int End { get { return Start + Length; } }
    }

    // 词组级输入法工作流。A 的 EngineFactory.SplitPhrases / MakeKeyboardSlip 通过反射桥接：
    // 交付后自动切到 A 的实现；未交付时用本地回退，并在启动日志里写明用的是哪一个。
    internal static class PhraseWorkflow
    {
        internal static string SplitterSource = "(未初始化)";
        internal static string SlipSource = "(未初始化)";

        private static MethodInfo _splitPhrases;
        private static MethodInfo _splitPhrasesWithHabits;
        private static MethodInfo _makeKeyboardSlip;
        private static bool _habitSplitWarned;
        private static FieldInfo _slipPctField;

        private const string Row1 = "qwertyuiop";
        private const string Row2 = "asdfghjkl";
        private const string Row3 = "zxcvbnm";

        internal static void Init()
        {
            try
            {
                // 注意：MakeKeyboardSlip 有多个重载，必须按签名精确匹配（GetMethod(name, flags) 会抛 AmbiguousMatchException）
                Type factory = typeof(EngineFactory);
                _splitPhrases = FindStatic(factory, "SplitPhrases", new Type[] { typeof(string), typeof(EngineConfig) });
                _splitPhrasesWithHabits = FindStatic(factory, "SplitPhrases", new Type[] { typeof(string), typeof(EngineConfig), typeof(IHabitStore) });
                _makeKeyboardSlip = FindStatic(factory, "MakeKeyboardSlip", new Type[] { typeof(string), typeof(EngineConfig) });
                SplitterSource = _splitPhrasesWithHabits != null
                    ? "EngineFactory.SplitPhrases(text,cfg,habits)（含用户词表）"
                    : (_splitPhrases == null ? "回退实现（A 的 SplitPhrases 未交付）" : "EngineFactory.SplitPhrases(text,cfg)（不含用户词表）");
                SlipSource = _makeKeyboardSlip == null ? "回退实现（相邻键映射）" : "EngineFactory.MakeKeyboardSlip";
                _slipPctField = typeof(TypoConfig).GetField("KeyboardSlipPct", BindingFlags.Public | BindingFlags.Instance);
            }
            catch (Exception ex)
            {
                Log.Error("词组工作流初始化失败", ex);
            }
            finally
            {
                if (SplitterSource == "(未初始化)") SplitterSource = "回退实现（初始化异常）";
                if (SlipSource == "(未初始化)") SlipSource = "回退实现（初始化异常）";
            }
        }

        private static MethodInfo FindStatic(Type type, string name, Type[] parameters)
        {
            try
            {
                return type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null, parameters, null);
            }
            catch (Exception ex)
            {
                Log.Error("查找引擎方法失败：" + name, ex);
                return null;
            }
        }

        // 键盘错拼概率只有一个来源：Engine.Typo.KeyboardSlipPct（A 的引擎）。
        // 取不到就明确告警并**禁用**错拼——绝不静默回退到另一个旋钮（那种「改了没反应」的开关是陷阱）。
        internal static int KeyboardSlipPct(AppConfig cfg)
        {
            if (_slipPctField == null)
            {
                LogNotAvailableOnce();
                return 0;
            }
            try
            {
                object v = _slipPctField.GetValue(cfg.Engine.Typo);
                if (v is int) return Math.Max(0, Math.Min(100, (int)v));
            }
            catch (Exception ex)
            {
                Log.Error("读取 Engine.Typo.KeyboardSlipPct 失败，键盘错拼已禁用", ex);
                return 0;
            }
            LogNotAvailableOnce();
            return 0;
        }

        private static bool _slipWarned;

        private static void LogNotAvailableOnce()
        {
            if (_slipWarned) return;
            _slipWarned = true;
            Log.Error("警告：Engine.Typo.KeyboardSlipPct 取不到，键盘相邻键错拼已禁用（不再回退到其它配置项）");
        }

        // 组串缓冲区间：从「还没组串过的块」开始，最多 3 块（生产代码与测试共用同一份决策）
        internal static void BufferRange(PhraseState state, out int first, out int last)
        {
            BufferRange(state, 3, out first, out last);
        }

        // 缓冲上限可配：1=确认一段就落一段；调大=多个词组凑一个缓冲一起落
        internal static void BufferRange(PhraseState state, int maxPhrases, out int first, out int last)
        {
            first = -1;
            last = -1;
            if (state == null || state.Blocks == null || state.Blocks.Count == 0) return;
            if (maxPhrases < 1) maxPhrases = 1;
            first = Math.Min(state.ComposedUpTo + 1, state.Blocks.Count - 1);
            last = Math.Min(state.Blocks.Count - 1, first + maxPhrases - 1);
        }

        // 缓冲区间内的拼音串（只取汉字块）
        internal static string RangePinyin(PhraseState state, int from, int to)
        {
            if (state == null || state.Blocks == null || state.Blocks.Count == 0) return "";
            if (from < 0) from = 0;
            if (to >= state.Blocks.Count) to = state.Blocks.Count - 1;
            StringBuilder sb = new StringBuilder();
            for (int i = from; i <= to; i++)
            {
                if (i < 0 || i >= state.Blocks.Count) continue;
                PhraseBlock b = state.Blocks[i];
                if (b.Chinese) sb.Append(b.Pinyin);
            }
            return sb.ToString();
        }

        // 把每个计划步预分配到块：Char/LineBreak 按"落屏位置"推进，Backspace 继承当前块（不回头），
        // 于是 planBlock 单调不减，循环可以安全地按块分批注入（引擎的错字/回退步仍按原顺序执行）
        internal static int[] AssignStepsToBlocks(IList<Step> plan, PhraseState state)
        {
            int count = plan == null ? 0 : plan.Count;
            int[] map = new int[count];
            if (count == 0) return map;

            // 按**真实回放模型**定位每一步：Char 追加、LineBreak 追加 '\n'、Backspace 回收一个字符。
            // （不能用"Backspace 不回收"的单调模型：引擎的错字是『错字→退格→正字』，
            //   那样每遇一次错字映射就永久漂移一个字符，导致注入窗口把下一个缓冲的字提前注入——实测踩过。）
            // 块边界用**归一化后**的偏移（\r\n -> \n），与回放模型一致；落屏===原文已由引擎保证。
            int[] start = new int[state.Blocks.Count];
            int[] end = new int[state.Blocks.Count];
            int acc = 0;
            for (int i = 0; i < state.Blocks.Count; i++)
            {
                start[i] = acc;
                acc += NormalizeText(state.Blocks[i].Text).Length;
                end[i] = acc;
            }

            // 按**执行时刻的回放位置**定位：该步落在哪个块的文本区间里就属于哪个缓冲。
            // Char 追加、LineBreak 追加 '\n'、Backspace **回收一个字符**（不回收的话每遇一次
            // 引擎的『错字→退格→正字』映射就永久漂移一个字符——真机 bug 的根因）。
            StringBuilder replay = new StringBuilder();
            int[] own = new int[count];
            // LIFO（栈式）归属：退格删的是"最近一个还没被删的字符"，所以它的块归属 = 那个字符的块归属。
            // 位置算术在多打一个字/跨块边界时必然出错（重复输入、句首重打、漏字回填都会），栈式与边界无关。
            List<int> stack = new List<int>();
            // 第三十一轮修正：带 Reason 的 Char 是"错字演出"的字符（引擎结构：Char(多打,带Reason) ×k →
            // Backspace ×k → Char(正确)），它属于**它要代表/纠正的那个词**所在的块。
            // 归属规则 = 用**该错字连起点**的回放位置定位（等价于"前瞻到它要打的正确字"，见下面注释）；
            // 旧的"取栈顶"规则在"错字演出正好发生在词与词的边界上"时会把错字归到**上一个词**，
            // 于是下一个词（甚至下下个词）的错字被提前打进上一个词的切片（真机：块41 注入了 '抖动收紧收紧'）。
            int typoRunPos = 0;      // 当前错字连起点的落屏位置
            int typoRunIndex = 0;    // 连里已经要写第几个字（重复输入按位回绕用）
            int typoRunWordLen = 0;  // 连是"重复输入"型时被重复的词长（0=不复绕）
            for (int i = 0; i < count; i++)
            {
                Step step = plan[i];
                if (step.Kind == StepKind.Backspace)
                {
                    if (stack.Count > 0)
                    {
                        own[i] = stack[stack.Count - 1];
                        stack.RemoveAt(stack.Count - 1);
                    }
                    else
                    {
                        own[i] = BlockAtOffset(start, end, 0);
                    }
                    if (replay.Length > 0) replay.Length -= 1;
                    continue;
                }
                // 只认 Char 的 Reason：LineBreak 步永远带 Reason（换行），它不是错字演出（旧代码把它也当错字 → 取栈顶）。
                bool typoChar = step.Kind == StepKind.Char && !string.IsNullOrEmpty(step.Reason);
                if (typoChar)
                {
                    // 连续多个带 Reason 的 Char 属于同一个演出；连的起点 = 这个演出要代表/纠正的那个词的落屏位置。
                    // 为什么不用"前瞻第一个不带 Reason 的 Char"：『重复输入』把整个词以错字形式打出来
                    // （如 抖动 重复输入 = 抖,动,抖,动 全带 Reason + 2 个退格，词本身没有单独的"正确字"步），
                    // 前瞻会跳过它自己的词、落到**下一个词**上；连起点定位对这种结构才正确。
                    // 判断"是否延续上一个连"时**跳过 Sleep**（真机见过思考停顿插在错字连中间）：
                    // 否则后半截会被当成新连、按更靠后的回放位置算到下一个块上，连里的"多打的字"就留在屏上了
                    // （落屏不再是原文前缀）。但**不跳过退格**：相邻两个演出之间必有多打字 + 等量退格，
                    // 退格之后的错字属于新演出。
                    bool prevTypo = false;
                    for (int b = i - 1; b >= 0; b--)
                    {
                        Step prev = plan[b];
                        if (prev == null) break;
                        if (prev.Kind == StepKind.Sleep) continue;
                        prevTypo = prev.Kind == StepKind.Char && !string.IsNullOrEmpty(prev.Reason);
                        break;
                    }
                    if (!prevTypo)
                    {
                        typoRunPos = replay.Length;                       // 连起点 = 连开始写之前的落屏位置
                        typoRunIndex = 0;
                        typoRunWordLen = RepeatRunWordLen(plan, i);
                    }
                    // 第三十二轮：归属 = **这个连当前要写的那个字所代表的落屏位置**所在块，
                    // 且块判定是左闭右开（位置 == 块起点 → 属于该块，不是上一块）。
                    // 重复输入型（2n 字 = 同一个词打两遍 + n 个退格）必须**按位回绕**：第 3、4 个字代表的是
                    // 词的第 1、2 个字，不能顺着回放位置一路往后算——否则连跨块时会把后一块的首字
                    // （真机：块2 的"现"）算进前一块，于是"块1 净 4 字"而它只有 3 个字。
                    // 重复输入型才按位回绕；其它类型（同音字/鼻音/句首重打/漏字回填/词组错词）整连都归**连起点**所在块——
                    // 非重复型的多打字是"跳着打的相邻字"（漏字回填/句首重打），按字符逐个推进会把同一次演出的
                    // 多打字与它的退格拆到不同切片，落屏就不再是原文前缀（实测 112 条违规）。
                    int typoPos = typoRunWordLen > 0 ? typoRunPos + (typoRunIndex % typoRunWordLen) : typoRunPos;
                    own[i] = BlockAtOffset(start, end, typoPos);
                    typoRunIndex++;
                }
                else if (step.Kind == StepKind.Char || step.Kind == StepKind.LineBreak)
                {
                    own[i] = BlockAtOffset(start, end, replay.Length);
                }
                else
                {
                    // Sleep/其它：跟随前一步的块。演出中途引擎会插思考停顿（真机见过），
                    // 这个停顿不该按"多打字之后的回放位置"把后面所有步推到更后面的块。
                    own[i] = i > 0 ? own[i - 1] : BlockAtOffset(start, end, replay.Length);
                }
                if (step.Kind == StepKind.Char && !string.IsNullOrEmpty(step.Text))
                {
                    string t = NormalizeText(step.Text);
                    replay.Append(t);
                    for (int c = 0; c < t.Length; c++) stack.Add(own[i]);   // 每个字符压栈
                    continue;
                }
                if (step.Kind == StepKind.LineBreak)
                {
                    replay.Append('\n');
                    stack.Add(own[i]);
                    continue;
                }
            }
            for (int i = 0; i < count; i++) if (own[i] < 0) own[i] = end.Length == 0 ? 0 : end.Length - 1;

            // 前缀最大 -> 单调不减：退格回退（位置可能回到前一个块）不会打乱分区
            int runMax = 0;
            for (int i = 0; i < count; i++)
            {
                if (own[i] > runMax) runMax = own[i];
                map[i] = runMax;
            }
            return map;
        }

        // 预计算缓冲切片：第 k 个缓冲 = 块 [FirstBlock..LastBlock] + 计划步 [StepStart..StepEnd)
        internal sealed class BufferSlice
        {
            internal int FirstBlock;
            internal int LastBlock;
            internal int StepStart;
            internal int StepEnd;
        }

        internal static List<BufferSlice> BuildBufferSlices(IList<Step> plan, PhraseState state, int maxPhrases)
        {
            List<BufferSlice> slices = new List<BufferSlice>();
            if (plan == null || plan.Count == 0 || state == null || state.Blocks.Count == 0) return slices;
            int[] map = AssignStepsToBlocks(plan, state);
            int blockCount = state.Blocks.Count;
            int stepIndex = 0;
            int blockFrom = 0;
            while (stepIndex < plan.Count && blockFrom < blockCount)
            {
                int last = Math.Min(blockCount - 1, blockFrom + Math.Max(1, maxPhrases) - 1);
                int end = stepIndex;
                while (end < plan.Count && map[end] <= last) end++;
                if (end == stepIndex && last < blockCount - 1)
                {
                    // 本缓冲没有步骤（例如全是标点块的边界情况）：扩到下一个有步骤的块
                    blockFrom = last + 1;
                    continue;
                }
                BufferSlice slice = new BufferSlice();
                slice.FirstBlock = blockFrom;
                slice.LastBlock = last;
                slice.StepStart = stepIndex;
                slice.StepEnd = end;
                slices.Add(slice);
                stepIndex = end;
                blockFrom = last + 1;
            }
            return slices;
        }

        // 第三十二轮：这个错字连是不是"重复输入"型（2n 个字 = 同一个词打两遍 + n 个退格）？
        // 是则返回词长 n（供按位回绕归属），否则 0。Sleep 不算断点（真机见过停顿插在连中间）。
        internal static int RepeatRunWordLen(IList<Step> plan, int start)
        {
            if (plan == null || start < 0 || start >= plan.Count) return 0;
            StringBuilder run = new StringBuilder();
            int k = start;
            for (; k < plan.Count; k++)
            {
                Step s = plan[k];
                if (s == null) break;
                if (s.Kind == StepKind.Sleep) continue;
                if (s.Kind != StepKind.Char || string.IsNullOrEmpty(s.Reason)) break;
                run.Append(NormalizeText(s.Text));
            }
            int nbs = 0;
            for (int b = k; b < plan.Count; b++)
            {
                Step s = plan[b];
                if (s == null) break;
                if (s.Kind == StepKind.Sleep) continue;
                if (s.Kind != StepKind.Backspace) break;
                nbs++;
            }
            int len = run.Length;
            if (len < 2 || nbs <= 0 || len != nbs * 2) return 0;
            int half = len / 2;
            if (!string.Equals(run.ToString(0, half), run.ToString(half, half), StringComparison.Ordinal)) return 0;
            return half;
        }

        private static int BlockAtOffset(int[] start, int[] end, int pos)
        {
            for (int i = 0; i < end.Length; i++)
            {
                if (pos < end[i]) return i;
            }
            return end.Length == 0 ? -1 : end.Length - 1;
        }

        // 丢弃 \r（"\r\n"->"\n"，孤立 "\r"->""）：块与计划两侧必须用同一套，否则 CRLF 会造成 +1 偏移
        internal static string NormalizeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (text.IndexOf('\r') < 0) return text;
            return text.Replace("\r\n", "\n").Replace("\r", "");
        }

        // 现在该不该开始一个新的组串缓冲（返回块下标，-1 表示不用）
        internal static int NextComposeBlock(PhraseState state)
        {
            if (state == null || state.Blocks == null || state.Blocks.Count == 0) return -1;
            int idx = state.BlockIndexAt(state.OutputPos);
            if (idx < 0) return -1;
            return idx > state.ComposedUpTo ? idx : -1;
        }

        internal static string DescribeBlocks(IList<PhraseBlock> blocks)
        {
            if (blocks == null || blocks.Count == 0) return "(无)";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < blocks.Count; i++)
            {
                if (i > 0) sb.Append(" | ");
                sb.Append('[').Append(blocks[i].Text).Append('/').Append(blocks[i].Pinyin).Append(']');
            }
            return sb.ToString();
        }

        internal static IList<PhraseBlock> Split(string text, AppConfig cfg, IPinyinTable table)
        {
            return Split(text, cfg, table, null);
        }

        // habits != null 时优先走 A 的三参重载（用户词表参与最长匹配）；
        // 取不到会打一行明确告警，然后**降级到两参版本**（有告警的降级，不是静默回退到别的旋钮）
        internal static IList<PhraseBlock> Split(string text, AppConfig cfg, IPinyinTable table, IHabitStore habits)
        {
            if (habits != null)
            {
                if (_splitPhrasesWithHabits != null)
                {
                    IList<PhraseBlock> withHabits = TrySplitWithHabits(text, cfg, table, habits);
                    if (withHabits != null && withHabits.Count > 0) return withHabits;
                }
                else if (!_habitSplitWarned)
                {
                    _habitSplitWarned = true;
                    Log.Error("警告：EngineFactory.SplitPhrases(text,cfg,habits) 取不到（引擎未提供该重载），"
                        + "已改用**本地用户词最长匹配**（读 habits 用户词表，阈值暂用 3）参与切分；A 交付后自动切回它的实现。");
                }
                if (_splitPhrasesWithHabits == null)
                {
                    IList<PhraseBlock> local = TrySplitWithLocalUserWords(text, cfg, table, habits);
                    if (local != null && local.Count > 0) return local;
                }
            }
            IList<PhraseBlock> fromEngine = TrySplitFromEngine(text, cfg, table);
            if (fromEngine != null && fromEngine.Count > 0) return fromEngine;
            return SplitFallback(text, table);
        }

        private static IList<PhraseBlock> TrySplitWithHabits(string text, AppConfig cfg, IPinyinTable table, IHabitStore habits)
        {
            try
            {
                object result = _splitPhrasesWithHabits.Invoke(null, new object[] { text, cfg.Engine, habits });
                return ToBlocks(result, text, table, "SplitPhrases(text,cfg,habits)");
            }
            catch (Exception ex)
            {
                Log.Error("调用 EngineFactory.SplitPhrases(text,cfg,habits) 失败，降级为两参版本", ex);
                return null;
            }
        }

        private static IList<PhraseBlock> TrySplitFromEngine(string text, AppConfig cfg, IPinyinTable table)
        {
            if (_splitPhrases == null || string.IsNullOrEmpty(text)) return null;
            try
            {
                object result = _splitPhrases.Invoke(null, new object[] { text, cfg.Engine });
                return ToBlocks(result, text, table, "SplitPhrases");
            }
            catch (Exception ex)
            {
                Log.Error("调用 EngineFactory.SplitPhrases 失败，改用回退切分", ex);
                return null;
            }
        }

        // A 的三参重载未交付时的本地实现：用学到的用户词组做**最长匹配**，
        // 命中的词组整块提交，其余片段仍交给引擎两参切分（保证块拼接逐字等于原文）
        private static IList<PhraseBlock> TrySplitWithLocalUserWords(string text, AppConfig cfg, IPinyinTable table, IHabitStore habits)
        {
            if (string.IsNullOrEmpty(text) || habits == null) return null;
            string source;
            List<KeyValuePair<string, int>> words = UserWordTable.TopWords(habits, 5000, out source);
            if (words.Count == 0) return null;

            int minCount = UserWordTable.MinCountValue(cfg, 0);
            if (minCount <= 0) minCount = 3;   // A 的 UserWordMinCount 未交付时用它的默认值 3
            List<string> learned = new List<string>();
            for (int i = 0; i < words.Count; i++)
            {
                string w = words[i].Key;
                if (string.IsNullOrEmpty(w) || w.Length < 2 || w.Length > 4) continue;
                if (words[i].Value < minCount) continue;
                bool allHan = true;
                for (int k = 0; k < w.Length; k++) if (!IsHan(w[k])) { allHan = false; break; }
                if (!allHan) continue;
                learned.Add(w);
            }
            if (learned.Count == 0) return null;
            learned.Sort(delegate(string a, string b)
            {
                if (b.Length != a.Length) return b.Length.CompareTo(a.Length);
                return string.CompareOrdinal(a, b);
            });

            List<PhraseBlock> blocks = new List<PhraseBlock>();
            int pos = 0;
            int gapStart = -1;
            int hits = 0;
            while (pos < text.Length)
            {
                string hit = null;
                for (int i = 0; i < learned.Count; i++)
                {
                    string w = learned[i];
                    if (pos + w.Length <= text.Length && string.CompareOrdinal(text, pos, w, 0, w.Length) == 0)
                    {
                        hit = w;
                        break;
                    }
                }
                if (hit != null)
                {
                    FlushGap(blocks, text, gapStart, pos, cfg, table);
                    gapStart = -1;
                    blocks.Add(MakeBlock(text, pos, hit.Length, table));
                    pos += hit.Length;
                    hits++;
                }
                else
                {
                    if (gapStart < 0) gapStart = pos;
                    pos++;
                }
            }
            FlushGap(blocks, text, gapStart, text.Length, cfg, table);
            Log.Info(string.Format("本地用户词最长匹配：命中 {0} 处（阈值≥{1}，候选词 {2} 个，词表来源={3}）",
                hits, minCount, learned.Count, source));
            return blocks;
        }

        private static void FlushGap(List<PhraseBlock> blocks, string text, int gapStart, int gapEnd, AppConfig cfg, IPinyinTable table)
        {
            if (gapStart < 0 || gapEnd <= gapStart) return;
            string gap = text.Substring(gapStart, gapEnd - gapStart);
            IList<PhraseBlock> sub = null;
            try { sub = TrySplitFromEngine(gap, cfg, table); }
            catch (Exception ex) { Log.Error("段内切分失败，改用回退切分", ex); }
            if (sub == null || sub.Count == 0) sub = SplitFallback(gap, table);
            for (int i = 0; i < sub.Count; i++)
            {
                blocks.Add(MakeBlock(text, gapStart + sub[i].Start, sub[i].Length, table));
            }
        }

        // 把引擎返回的片段序列（string 或含 Text 字段的对象）定位回原文并转成块
        private static IList<PhraseBlock> ToBlocks(object result, string text, IPinyinTable table, string tag)
        {
            if (result == null) return null;
            List<string> pieces = new List<string>();
            string single = result as string;
            if (single != null)
            {
                pieces.Add(single);
            }
            else
            {
                IEnumerable seq = result as IEnumerable;
                if (seq == null) return null;
                foreach (object item in seq)
                {
                    if (item == null) continue;
                    string s = item as string;
                    if (s == null)
                    {
                        FieldInfo f = item.GetType().GetField("Text", BindingFlags.Public | BindingFlags.Instance);
                        if (f != null) s = f.GetValue(item) as string;
                    }
                    if (!string.IsNullOrEmpty(s)) pieces.Add(s);
                }
            }
            if (pieces.Count == 0) return null;

            List<PhraseBlock> blocks = new List<PhraseBlock>();
            int cursor = 0;
            for (int i = 0; i < pieces.Count; i++)
            {
                int at = text.IndexOf(pieces[i], cursor, StringComparison.Ordinal);
                if (at < 0)
                {
                    Log.Info(tag + " 片段无法在原文定位，改用回退切分：" + pieces[i]);
                    return null;
                }
                blocks.Add(MakeBlock(text, at, pieces[i].Length, table));
                cursor = at + pieces[i].Length;
            }
            if (cursor != text.Length)
            {
                Log.Info(tag + " 片段未覆盖全文，改用回退切分");
                return null;
            }
            return blocks;
        }

        // 回退切分：连续汉字成块（<=4 字整块，例如「天上有」；更长的按 2 字切），
        // 标点/英文/空白/换行各自成块（不组串）
        private static IList<PhraseBlock> SplitFallback(string text, IPinyinTable table)
        {
            List<PhraseBlock> blocks = new List<PhraseBlock>();
            if (string.IsNullOrEmpty(text)) return blocks;
            int i = 0;
            while (i < text.Length)
            {
                if (IsHan(text[i]))
                {
                    int j = i;
                    while (j < text.Length && IsHan(text[j])) j++;
                    int run = j - i;
                    if (run <= 4)
                    {
                        blocks.Add(MakeBlock(text, i, run, table));
                        i = j;
                    }
                    else
                    {
                        blocks.Add(MakeBlock(text, i, 2, table));
                        i += 2;
                    }
                }
                else
                {
                    int j = i;
                    while (j < text.Length && !IsHan(text[j])) j++;
                    blocks.Add(MakeBlock(text, i, j - i, table));
                    i = j;
                }
            }
            return blocks;
        }

        internal static bool IsHan(char c)
        {
            return c >= 0x4E00 && c <= 0x9FFF;
        }

        private static PhraseBlock MakeBlock(string text, int start, int length, IPinyinTable table)
        {
            PhraseBlock block = new PhraseBlock();
            block.Start = start;
            block.Text = text.Substring(start, length);
            block.Chinese = block.Text.Length > 0 && IsHan(block.Text[0]);
            if (block.Chinese && table != null)
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < block.Text.Length; i++)
                {
                    string pinyin = null;
                    try { pinyin = table.GetPinyin(block.Text[i]); }
                    catch { }
                    sb.Append(string.IsNullOrEmpty(pinyin) ? block.Text[i].ToString() : pinyin);
                }
                block.Pinyin = sb.ToString();
            }
            return block;
        }

        // 键盘相邻键错拼：A 交付 MakeKeyboardSlip 后优先用它的结果
        internal static string KeyboardSlip(string pinyin, AppConfig cfg)
        {
            if (string.IsNullOrEmpty(pinyin)) return pinyin;
            if (_makeKeyboardSlip != null)
            {
                try
                {
                    object result = _makeKeyboardSlip.Invoke(null, new object[] { pinyin, cfg.Engine });
                    string s = result as string;
                    if (!string.IsNullOrEmpty(s) && !string.Equals(s, pinyin, StringComparison.Ordinal)) return s;
                }
                catch (Exception ex)
                {
                    Log.Error("调用 EngineFactory.MakeKeyboardSlip 失败，改用回退映射", ex);
                }
            }
            return SlipFallback(pinyin);
        }

        private static string SlipFallback(string pinyin)
        {
            if (string.IsNullOrEmpty(pinyin)) return pinyin;
            string neighbor = Neighbor(pinyin[pinyin.Length - 1]);
            if (neighbor == null) return pinyin;
            return pinyin.Substring(0, pinyin.Length - 1) + neighbor;
        }

        private static string Neighbor(char c)
        {
            char lower = char.ToLowerInvariant(c);
            string[] rows = new string[] { Row1, Row2, Row3 };
            for (int r = 0; r < rows.Length; r++)
            {
                int idx = rows[r].IndexOf(lower);
                if (idx < 0) continue;
                int pick = idx + 1 < rows[r].Length ? idx + 1 : idx - 1;
                if (pick < 0 || pick >= rows[r].Length) return null;
                return rows[r][pick].ToString();
            }
            return null;
        }
    }

    // 组串/确认进度状态：只在显示与边界判定上用，不改变注入顺序（落屏仍由引擎计划保证）
    internal sealed class PhraseState
    {
        internal readonly IList<PhraseBlock> Blocks;
        internal int OutputPos;
        internal int ComposedUpTo = -1;
        internal int BufferFirst = -1;
        internal int BufferLast = -1;

        internal PhraseState(IList<PhraseBlock> blocks)
        {
            Blocks = blocks == null ? new List<PhraseBlock>() : blocks;
        }

        internal int TotalLength { get { return Blocks.Count == 0 ? 0 : Blocks[Blocks.Count - 1].End; } }

        internal int BlockIndexAt(int pos)
        {
            for (int i = 0; i < Blocks.Count; i++)
            {
                if (pos < Blocks[i].End) return i;
            }
            return Blocks.Count == 0 ? -1 : Blocks.Count - 1;
        }

        internal void ApplyStep(Step step)
        {
            if (step == null) return;
            if (step.Kind == StepKind.Char && !string.IsNullOrEmpty(step.Text)) OutputPos += step.Text.Length;
            else if (step.Kind == StepKind.Backspace) OutputPos -= 1;
            if (OutputPos < 0) OutputPos = 0;
            if (OutputPos > TotalLength) OutputPos = TotalLength;
        }
    }
}
