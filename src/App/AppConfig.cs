using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using SimIme.Engine;

namespace SimIme.App
{
    internal static class AppPaths
    {
        internal static readonly string BaseDir = ResolveBaseDir();
        internal static readonly string ConfigPath = Path.Combine(BaseDir, "config.json");
        internal static readonly string ErrorLogPath = Path.Combine(BaseDir, "error.log");
        internal static readonly string SelfTestLogPath = Path.Combine(BaseDir, "selftest.log");
        internal static readonly string HabitStorePath = Path.Combine(BaseDir, "habits.dat");

        private static string ResolveBaseDir()
        {
            try
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                if (string.IsNullOrEmpty(dir)) return ".";
                return dir;
            }
            catch
            {
                return ".";
            }
        }
    }

    // ---------- 可序列化配置（JavaScriptSerializer；字段缺省容错） ----------

    public sealed class HotkeyConfig
    {
        public int StartKey = 0x78;   // F9  二次确认 / 开始
        public int LocateKey = 0x77;  // F8  点击定位
        public int StopKey = 0x7B;    // F12 停止（首选）

        // 停止键候选：首选被占用就按此顺序回退，保证用户永远有停止键；
        // 与 StartKey/LocateKey 相同的键会被自动跳过
        public int[] StopKeyCandidates = new int[] { 0x7B, 0x79, 0x7A, 0x75, 0x76 };   // F12 F10 F11 F6 F7
    }

    public sealed class InjectConfig
    {
        public string Method = "SendInput";        // SendInput | Clipboard
        public bool ClipboardFallback = true;      // SendInput 被拒时自动转剪贴板兜底
        public bool RestoreClipboard = true;       // 结束后还原用户原剪贴板
        public int ClipboardPasteDelayMs = 150;
        // 字母预览默认关闭：实测在某些目标程序里退格清不掉预览字母（残留字母 + 触发联想弹窗），
        // 悬浮条仍保持输入法外观（那是显示层，与本注入方式无关）；用户需要时可在界面上打开
        public bool ImeSimulation = false;         // true = 汉字先敲拼音字母再退格再上屏
        public int ImePreviewMs = 200;             // 拼音字母停留时间（建议 120~300；逐字预览用）
        public bool PinyinInTarget = false;        // false=拼音只在悬浮条内组串（目标只收词组级汉字）；true=整块拼音字母真敲进目标再退格
        public int PinyinLetterMs = 110;           // 组串阶段每个字母节奏（建议 80~150）
        public int CommitCharMs = 25;              // 词组确认时块内字间隔（建议 15~40）
        public int CandidateMax = 6;               // 每段词组候选数量（5~9）
        public int SelectHighlightMs = 220;        // 选词时候选高亮停留时长（150~300，模拟按空格/数字选词那一下）
        // 第二十一轮起默认 1：逐词串行（组串→选词→立刻提交→清空），与真输入法一致。
        // 该字段保留为兼容/实验项：调大会把多个词凑一个缓冲一起提交，**观感不再是逐词**，默认路径不使用。
        public int BufferMaxPhrases = 1;   // 1=逐词串行（默认）；>1=多词缓冲（兼容字段，改变观感）
        public int BeforeDeletePauseMs = 500;      // 改错：第一次退格之前的停顿（0~5000）
        public int DeleteCharMs = 180;             // 改错：逐字退格的间隔（30~1000）
        public int AfterDeletePauseMs = 250;       // 改错：删完之后的停顿（0~5000，原为硬编码的 BeforeDeletePauseMs/2）
        public int BeforePunctPauseMs = 200;       // 标点/符号前置停顿（0~2000）：真人是先伸手再按逗号/括号，不是紧贴上一个词蹦出来
        public int AfterRetypePauseMs = 400;       // 改错：重打完成后的停顿（0~5000）
        public int MinComposeVisibleMs = 260;      // 第三十三轮：组串/重打字母阶段的**最小可见时长**（0~2000）——
                                                   // 短单位（如 yi 只有 2 帧）也要让人看清"从 y 到 yi"，否则像直接出现
    }

    public sealed class AnchorConfig
    {
        public string Method = "Auto";             // Auto | Caret | Uia | Mouse
        public int UiaTimeoutMs = 800;             // UIA 锚定后台线程超时
    }

    public sealed class BehaviorConfig
    {
        public int ClickLocateCountdownMs = 3000;  // 点击定位后的倒计时
        public string FocusLostPolicy = "Pause";   // Pause | Abort
        public bool LogEnabled = true;             // 默认开启运行期诊断（可用界面开关关闭）
        public bool BarVerbose = false;            // 悬浮条默认像真输入法：只在输入/纠错时出现；true=显示停顿/等待等详细状态（排障用）
        public bool LearnUserWords = true;          // 本地学习用户词组（只学**真正提交过的块**；关掉则完全不学习也不写盘）
        public int LearnMergeMinCount = 5;          // 相邻两块搭配学成词组所需的最少重复次数（2~100）
        public string DictMirror = "http://mirrors.aliyun.com/pypi/simple/";   // 联网更新词典的镜像（本机 HTTPS 不可用，务必 http）
        public string ExtraDictFile = "";           // 外部词表路径（空=exe 同目录 dict_extra.tsv）
        public int BarOffsetX = 6;                 // 悬浮条相对插入符偏移
        public int BarOffsetY = 22;
    }

    public sealed class AppConfig
    {
        internal const int BufferMaxPhrasesDefault = 1;   // 代码默认（逐词串行）；配置覆盖时启动日志会提示

        public HotkeyConfig Hotkeys = new HotkeyConfig();
        public InjectConfig Inject = new InjectConfig();
        public AnchorConfig Anchor = new AnchorConfig();
        public BehaviorConfig Behavior = new BehaviorConfig();
        public EngineConfig Engine = new EngineConfig();

        internal static AppConfig Load(string path)
        {
            AppConfig cfg = null;
            try
            {
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path, Encoding.UTF8);
                    if (!string.IsNullOrEmpty(json) && json.Trim().Length > 0)
                    {
                        JavaScriptSerializer ser = new JavaScriptSerializer();
                        ser.MaxJsonLength = 8 * 1024 * 1024;
                        cfg = ser.Deserialize<AppConfig>(json);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("读取配置失败，改用默认值：" + path, ex);
            }
            if (cfg == null) cfg = new AppConfig();
            cfg.Normalize();
            return cfg;
        }

        internal void Save(string path)
        {
            try
            {
                string json = ToJson();
                File.WriteAllText(path, json, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log.Error("写配置失败：" + path, ex);
            }
        }

        internal string ToJson()
        {
            string json;
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = 8 * 1024 * 1024;
                json = ser.Serialize(this);
            }
            catch (Exception ex)
            {
                Log.Error("序列化配置失败", ex);
                json = "{}";
            }
            return JsonFormat.Pretty(json) + "\r\n";
        }

        // 缺省容错：任何字段缺失/越界都在这里被修补，UI 与运行期只看到合法值
        internal void Normalize()
        {
            if (Hotkeys == null) Hotkeys = new HotkeyConfig();
            if (Inject == null) Inject = new InjectConfig();
            if (Anchor == null) Anchor = new AnchorConfig();
            if (Behavior == null) Behavior = new BehaviorConfig();
            if (Engine == null) Engine = new EngineConfig();
            if (Engine.Rhythm == null) Engine.Rhythm = new RhythmConfig();
            if (Engine.Typo == null) Engine.Typo = new TypoConfig();

            Hotkeys.StartKey = ClampKey(Hotkeys.StartKey, 0x78);
            Hotkeys.LocateKey = ClampKey(Hotkeys.LocateKey, 0x77);
            Hotkeys.StopKey = ClampKey(Hotkeys.StopKey, 0x7B);
            Hotkeys.StopKeyCandidates = NormalizeKeyList(Hotkeys.StopKeyCandidates, DefaultStopCandidates);

            Inject.Method = OneOf(Inject.Method, new string[] { "SendInput", "Clipboard" }, "SendInput");
            Inject.ClipboardPasteDelayMs = Clamp(Inject.ClipboardPasteDelayMs, 0, 5000, 150);
            Inject.ImePreviewMs = Clamp(Inject.ImePreviewMs, 0, 2000, 200);
            Inject.PinyinLetterMs = Clamp(Inject.PinyinLetterMs, 20, 1000, 110);
            Inject.CommitCharMs = Clamp(Inject.CommitCharMs, 5, 200, 25);
            Inject.CandidateMax = Clamp(Inject.CandidateMax, 3, 9, 6);
            Inject.SelectHighlightMs = Clamp(Inject.SelectHighlightMs, 0, 2000, 220);
            Inject.BufferMaxPhrases = Clamp(Inject.BufferMaxPhrases, 1, 8, BufferMaxPhrasesDefault);
            Behavior.LearnMergeMinCount = Clamp(Behavior.LearnMergeMinCount, 2, 100, 5);
            Inject.BeforeDeletePauseMs = Clamp(Inject.BeforeDeletePauseMs, 0, 5000, 500);
            Inject.DeleteCharMs = Clamp(Inject.DeleteCharMs, 30, 1000, 180);
            Inject.AfterDeletePauseMs = Clamp(Inject.AfterDeletePauseMs, 0, 5000, 250);
            Inject.MinComposeVisibleMs = Clamp(Inject.MinComposeVisibleMs, 0, 2000, 260);
            Inject.BeforePunctPauseMs = Clamp(Inject.BeforePunctPauseMs, 0, 2000, 200);
            Inject.AfterRetypePauseMs = Clamp(Inject.AfterRetypePauseMs, 0, 5000, 400);
            if (string.IsNullOrEmpty(Behavior.DictMirror)) Behavior.DictMirror = "http://mirrors.aliyun.com/pypi/simple/";
            if (string.IsNullOrEmpty(Behavior.ExtraDictFile)) Behavior.ExtraDictFile = Path.Combine(AppPaths.BaseDir, "dict_extra.tsv");

            Anchor.Method = OneOf(Anchor.Method, new string[] { "Auto", "Caret", "Uia", "Mouse" }, "Auto");
            Anchor.UiaTimeoutMs = Clamp(Anchor.UiaTimeoutMs, 100, 10000, 800);

            Behavior.FocusLostPolicy = OneOf(Behavior.FocusLostPolicy, new string[] { "Pause", "Abort" }, "Pause");
            Behavior.ClickLocateCountdownMs = Clamp(Behavior.ClickLocateCountdownMs, 0, 30000, 3000);
            Behavior.BarOffsetX = Clamp(Behavior.BarOffsetX, -400, 400, 6);
            Behavior.BarOffsetY = Clamp(Behavior.BarOffsetY, -400, 400, 22);

            RhythmConfig r = Engine.Rhythm;
            r.CharDelayMinMs = Clamp(r.CharDelayMinMs, 1, 10000, 90);
            r.CharDelayMaxMs = Clamp(r.CharDelayMaxMs, r.CharDelayMinMs, 20000, 260);
            r.SpeedVariancePct = Clamp(r.SpeedVariancePct, 0, 100, 35);
            r.BurstChancePct = Clamp(r.BurstChancePct, 0, 100, 12);
            r.ThinkPauseMinMs = Clamp(r.ThinkPauseMinMs, 0, 60000, 500);
            r.ThinkPauseMaxMs = Clamp(r.ThinkPauseMaxMs, r.ThinkPauseMinMs, 120000, 2500);
            r.ThinkEveryMinChars = Clamp(r.ThinkEveryMinChars, 1, 500, 8);
            r.ThinkEveryMaxChars = Clamp(r.ThinkEveryMaxChars, r.ThinkEveryMinChars, 1000, 20);
            r.RestPauseMinMs = Clamp(r.RestPauseMinMs, 0, 300000, 3000);
            r.RestPauseMaxMs = Clamp(r.RestPauseMaxMs, r.RestPauseMinMs, 600000, 8000);
            r.RestEveryMinSentences = Clamp(r.RestEveryMinSentences, 1, 500, 2);
            r.RestEveryMaxSentences = Clamp(r.RestEveryMaxSentences, r.RestEveryMinSentences, 1000, 5);
            r.StartupDelayMs = Clamp(r.StartupDelayMs, 0, 120000, 1500);
            r.PunctuationExtraMinMs = Clamp(r.PunctuationExtraMinMs, 0, 10000, 80);
            r.PunctuationExtraMaxMs = Clamp(r.PunctuationExtraMaxMs, r.PunctuationExtraMinMs, 20000, 400);

            TypoConfig t = Engine.Typo;
            t.HomophonePct = Clamp(t.HomophonePct, 0, 100, 4);
            t.NasalPct = Clamp(t.NasalPct, 0, 100, 3);
            t.HeadRedoPct = Clamp(t.HeadRedoPct, 0, 100, 15);
            t.OmissionPct = Clamp(t.OmissionPct, 0, 100, 2);
            t.RepeatWordPct = Clamp(t.RepeatWordPct, 0, 100, 2);
            t.MaxTyposPerSentence = Clamp(t.MaxTyposPerSentence, 0, 20, 2);

            if (Engine.RandomSeed < 0) Engine.RandomSeed = 0;
        }

        internal static readonly int[] DefaultStopCandidates = new int[] { 0x7B, 0x79, 0x7A, 0x75, 0x76 };   // F12 F10 F11 F6 F7

        private static int[] NormalizeKeyList(int[] values, int[] fallback)
        {
            if (values == null) return (int[])fallback.Clone();
            List<int> list = new List<int>();
            for (int i = 0; i < values.Length; i++)
            {
                int vk = values[i];
                if (vk < 0x08 || vk > 0xFE) continue;
                if (!list.Contains(vk)) list.Add(vk);
            }
            if (list.Count == 0) return (int[])fallback.Clone();
            return list.ToArray();
        }

        private static int Clamp(int value, int min, int max, int fallback)
        {
            if (value < min || value > max) return fallback;
            return value;
        }

        private static int ClampKey(int value, int fallback)
        {
            if (value < 0x08 || value > 0xFE) return fallback;
            return value;
        }

        private static string OneOf(string value, string[] allowed, string fallback)
        {
            if (!string.IsNullOrEmpty(value))
            {
                for (int i = 0; i < allowed.Length; i++)
                {
                    if (string.Equals(allowed[i], value, StringComparison.OrdinalIgnoreCase)) return allowed[i];
                }
            }
            return fallback;
        }
    }

    internal static class JsonFormat
    {
        internal static string Pretty(string json)
        {
            if (string.IsNullOrEmpty(json)) return json;
            StringBuilder sb = new StringBuilder(json.Length * 2);
            int indent = 0;
            bool inString = false;
            bool escape = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (inString)
                {
                    sb.Append(c);
                    if (escape) escape = false;
                    else if (c == '\\') escape = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; sb.Append(c); continue; }
                if (c == '{' || c == '[') { sb.Append(c); indent++; NewLine(sb, indent); continue; }
                if (c == '}' || c == ']') { indent--; NewLine(sb, indent); sb.Append(c); continue; }
                if (c == ',') { sb.Append(c); NewLine(sb, indent); continue; }
                if (c == ':') { sb.Append(": "); continue; }
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static void NewLine(StringBuilder sb, int indent)
        {
            if (indent < 0) indent = 0;
            sb.Append("\r\n");
            sb.Append(' ', indent * 2);
        }
    }
}
