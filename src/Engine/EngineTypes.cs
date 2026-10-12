// SimIme 引擎契约 —— 冻结版 v1
// 规则：本文件中的现有类型名、成员名、签名不得改动；实现方可新增成员（不得新增到本文件）。
// 编码：所有 .cs 必须保存为 UTF-8 带 BOM；编译必须追加 /codepage:65001。
// 语义要点：
//  1) Step.Char.Text 是一个"字"(可能是代理对，1~2 个 UTF-16 码元)，注入方需按代理对整体处理。
//  2) BuildPlan 不含启动等待；调用方在锁定后自行 Sleep(cfg.Rhythm.StartupDelayMs) 再执行计划。
//  3) 计划必须满足：把 Char/LineBreak 施加到目标，并执行 Backspace，最终落屏文本 === 输入原文（错字必须被退格删干净）。
//  4) 非汉字（英文/数字/标点/换行）原样输出，不参与错字生成；换行用 StepKind.LineBreak。
//  5) 同一 RandomSeed 必须产生逐字段完全相同的计划；RandomSeed=0 表示每次随机。
using System.Collections.Generic;
using System.IO;

namespace SimIme.Engine
{
    public enum StepKind
    {
        Char = 0,
        Backspace = 1,
        LineBreak = 2,
        Sleep = 3
    }

    public sealed class Step
    {
        public StepKind Kind;
        public string Text;
        public int DelayMs;
        public string Reason;

        public Step() { }
        public Step(StepKind kind, string text, int delayMs, string reason)
        {
            Kind = kind; Text = text; DelayMs = delayMs; Reason = reason;
        }
    }

    public sealed class RhythmConfig
    {
        public int CharDelayMinMs = 90;
        public int CharDelayMaxMs = 260;
        public int SpeedVariancePct = 35;
        public int BurstChancePct = 12;
        public int ThinkPauseMinMs = 500;
        public int ThinkPauseMaxMs = 2500;
        public int ThinkEveryMinChars = 8;
        public int ThinkEveryMaxChars = 20;
        public int RestPauseMinMs = 3000;
        public int RestPauseMaxMs = 8000;
        public int RestEveryMinSentences = 2;
        public int RestEveryMaxSentences = 5;
        public int StartupDelayMs = 1500;
        public int PunctuationExtraMinMs = 80;
        public int PunctuationExtraMaxMs = 400;
    }

    public sealed class TypoConfig
    {
        public bool Enabled = true;
        public int HomophonePct = 4;
        public int NasalPct = 3;
        public int HeadRedoPct = 15;
        public int OmissionPct = 2;
        public int RepeatWordPct = 2;
        public int MaxTyposPerSentence = 2;
        // 词组级同音错词：整个词组块被同音错词替换后修正（百分比，0=关闭）
        public int WordHomophonePct = 2;
        // 键盘邻键错拼：仅用于拼音组串/显示层，不影响落屏文本（百分比，0=关闭）
        public int KeyboardSlipPct = 3;
    }

    public sealed class EngineConfig
    {
        public RhythmConfig Rhythm = new RhythmConfig();
        public TypoConfig Typo = new TypoConfig();
        public int RandomSeed = 0;
        // 用户词组参与切分的最低出现次数（第八轮新增；仅三参 SplitPhrases 使用）
        public int UserWordMinCount = 3;
        // 词典无匹配时的兜底块长上限（第十七轮新增，钳 1~4）：
        //   1（默认）= 只取单字，绝不造出"去公"这类非词块；
        //   >1 = 按种子在 1~该值内抖动，用于制造"随手分块"的观感，但会造出非词块。
        public int PhraseJitterMaxChars = 1;
    }

    public interface IPinyinTable
    {
        bool IsReady { get; }
        int CharCount { get; }
        string GetPinyin(char c);
        IList<string> HomophonesOf(char c, int max);
        IList<string> NasalVariantsOf(char c, int max);
    }

    public interface IHabitStore
    {
        void ObserveText(string text);
        void ObserveRhythm(int charDelayMs, int thinkPauseMs);
        double WordWeight(string word);
        void Save();
        void Load();
        // 用户词表（第八轮新增）：按次数降序返回学到的 2~4 字用户词组（只含出现 >= 2 次的）
        IList<string> UserWords(int max);
        // 该用户词组的出现次数；不是用户词组时返回 0
        int UserWordCount(string word);
    }
}
