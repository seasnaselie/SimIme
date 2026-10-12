using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using SimIme.Engine;

namespace SimIme.App
{
    // --bartest 的驱动：按 1.5 秒轮换样例，到点自动退出（不注入、不弹窗）
    internal sealed class BarTestContext : ApplicationContext
    {
        private readonly FloatingBar _bar;
        private readonly BarStatus[] _samples;
        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();
        private readonly System.Diagnostics.Stopwatch _watch = new System.Diagnostics.Stopwatch();
        private readonly int _seconds;
        private int _index;

        internal BarTestContext(FloatingBar bar, BarStatus[] samples, int seconds)
        {
            _bar = bar;
            _samples = samples;
            _seconds = seconds;
            _timer.Interval = 250;
            _timer.Tick += OnTick;
            _watch.Start();
            _timer.Start();
        }

        private void OnTick(object sender, EventArgs e)
        {
            try
            {
                if (_watch.Elapsed.TotalSeconds >= _seconds)
                {
                    _timer.Stop();
                    _bar.HideBar();
                    Log.Info("--bartest：展示结束并退出（" + _seconds + " 秒）");
                    ExitThread();
                    return;
                }
                int idx = (int)(_watch.Elapsed.TotalSeconds / 1.5);
                if (idx != _index && idx < _samples.Length)
                {
                    _index = idx;
                    BarStatus s = _samples[idx];
                    _bar.ShowAt(s.Anchor, s);
                    Log.Info(string.Format("--bartest：样例 {0}/{1} 上行='{2}{3}' 下行='{4}' 高亮='{5}' 进度='{6}'",
                        idx + 1, _samples.Length, s.Trail, s.Composing, s.Candidates, s.Highlight, s.Progress));
                }
            }
            catch (Exception ex)
            {
                Log.Error("--bartest 刷新失败", ex);
            }
        }
    }

    internal static class SelfTest
    {
        // 固定自检串：中文 + 标点 + 换行 + 英文数字 + emoji 代理对
        internal const string TestText = "SimIme 自检：中文标点，测试！\r\n第二行（含括号）与「引号」——English 123。\r\n代理对：🙂🚀";

        // --bartest [秒数]：只演真实运行会出现的样子；--bartest-all [秒数]：连 BarVerbose 排障模式的样例一起演
        internal static int RunBarTest(AppConfig cfg, string secondsText, bool showAll)
        {
            bool attached = ConsoleBridge.Attach();
            Log.Enabled = true;
            int seconds = 8;
            int parsed;
            if (!string.IsNullOrEmpty(secondsText) && int.TryParse(secondsText, out parsed) && parsed > 0 && parsed <= 600) seconds = parsed;
            StringBuilder mirror = new StringBuilder();
            try
            {
                string mode = showAll ? "--bartest-all" : "--bartest";
                ConsoleBridge.WriteLine(mirror, "SimIme " + mode + "（纯外观展示，不注入任何字符）");
                FloatingBar bar = new FloatingBar(cfg.Behavior);
                bar.EnsureHandle();

                string tableInfo;
                BarStatus[] samples = BuildBarSamples(cfg, showAll, out tableInfo);
                // 首行就说清楚：默认展示的这批就是真实运行会看到的全部内容
                string banner = showAll
                    ? "--bartest-all：这是 BarVerbose=true 排障模式的样例集——停顿/换行/已暂停/锁定等待/进度数字**只在这种模式下**才会出现在悬浮条上，默认运行不会出现。"
                    : "--bartest：下面这批样例就是真实运行时悬浮条的**全部**样子——①汉字输入（上行：已输入拼音灰色 + 当前音节蓝色加粗下划线；下行：带序号候选，当前字浅蓝高亮）②纠错时拼音串变短（组串变化）。除此之外（停顿/换行/已暂停/锁定等待/进度数字/Reason 状态词）真实运行时都不会出现在悬浮条上，它们走托盘气泡 + 主界面状态区 + 日志。";
                Log.Info(banner);
                ConsoleBridge.WriteLine(mirror, banner);
                string policy = string.Format("悬浮条策略：BarVerbose={0}（false=默认只显示输入法外观；true=排障模式，才显示停顿/换行/等待/进度等详细状态）", cfg.Behavior.BarVerbose);
                Log.Info("--bartest：" + policy);
                ConsoleBridge.WriteLine(mirror, policy);
                Rectangle area = Screen.PrimaryScreen.WorkingArea;
                Point anchor = new Point(area.Left + area.Width / 2 - 210, area.Top + area.Height / 2);
                for (int i = 0; i < samples.Length; i++) samples[i].Anchor = anchor;

                bar.ShowAt(anchor, samples[0]);
                Log.Info(string.Format("{0}：显示 {1} 秒，共 {2} 个样例（每 1.5 秒一个）", mode, seconds, samples.Length));
                Log.Info("--bartest：外观 " + bar.DescribeLook());
                Log.Info("--bartest：窗口位置=(" + anchor.X + "," + anchor.Y + ") 标题=" + bar.Text);
                Log.Info("--bartest：拼音表 " + tableInfo);
                ConsoleBridge.WriteLine(mirror, "外观：" + bar.DescribeLook());
                ConsoleBridge.WriteLine(mirror, "位置：(" + anchor.X + "," + anchor.Y + ")");
                ConsoleBridge.WriteLine(mirror, "拼音表：" + tableInfo);
                for (int i = 0; i < samples.Length; i++)
                {
                    BarStatus sample = samples[i];
                    // 结构化字段：方便把截图与数据对上
                    List<string> cand = new List<string>();
                    string[] parts = (sample.Candidates ?? "").Split(' ');
                    for (int c = 0; c < parts.Length; c++) if (parts[c].Length > 0) cand.Add(parts[c]);
                    int highlightIndex = -1;
                    for (int c = 0; c < cand.Count; c++)
                    {
                        if ((sample.SelectedCandidate ?? "").Length > 0 && cand[c].EndsWith(sample.SelectedCandidate, StringComparison.Ordinal)) highlightIndex = c + 1;
                        else if (highlightIndex < 0 && (sample.Highlight ?? "").Length > 0 && cand[c].IndexOf(sample.Highlight, StringComparison.Ordinal) >= 0) highlightIndex = c + 1;
                    }
                    string structured = string.Format("样例 {0}：上行='{1}{2}{3}' 候选=[{4}] 高亮索引={5} 选中='{6}' 进度='{7}' 说明='{8}'",
                        i + 1, sample.Trail, sample.Composing, sample.Suffix, string.Join(",", cand.ToArray()),
                        highlightIndex, sample.SelectedCandidate, sample.Progress, sample.Foot);
                    ConsoleBridge.WriteLine(mirror, structured);
                    string note = i < LastSampleNotes.Length ? LastSampleNotes[i] : "";
                    Log.Info("--bartest " + structured + (note.Length > 0 ? "｜" + note : ""));
                }

                Application.Run(new BarTestContext(bar, samples, seconds));
                ConsoleBridge.WriteLine(mirror, "--bartest 结束（" + seconds + " 秒）");
                if (!attached) Log.Info("--bartest：无父控制台，输出同时写在本日志与 stdout");
                return 0;
            }
            catch (Exception ex)
            {
                Log.Error("--bartest 失败", ex);
                ConsoleBridge.WriteLine(mirror, "--bartest 失败：" + ex.Message);
                return 1;
            }
            finally
            {
                try { File.WriteAllText(AppPaths.SelfTestLogPath, mirror.ToString(), new UTF8Encoding(true)); } catch { }
            }
        }

        // 每个样例的结构化说明（缓冲范围等，给日志用；不写进 Foot，避免影响"真实运行会出现的"外观）
        internal static string[] LastSampleNotes = new string[0];

        private static BarStatus[] Finish(List<BarStatus> list, List<string> notes)
        {
            while (notes.Count < list.Count) notes.Add("");
            LastSampleNotes = notes.ToArray();
            return list.ToArray();
        }

        // 新选词流程的样例：候选行用生产 WordIndex（去重 + 同音字补齐），与真实运行一致
        private static BarStatus SelectionSample(AppConfig cfg, IPinyinTable table, IHabitStore habits,
            string trail, string composing, string suffix, string targetWord, string pinyin, bool selected, out string note)
        {
            note = "";
            // 用生产构造函数造帧（Trail/Composing/Suffix 语义与运行时完全一致）
            BarStatus s = SimImeContext.BuildComposeStatus(trail, composing, "");
            try
            {
                WordIndex.EnsureBuilt(table, cfg, habits);
                string degrade;
                List<WordIndex.Entry> words = WordIndex.Lookup(pinyin, targetWord, cfg.Inject.CandidateMax, out degrade);
                s.Candidates = WordIndex.FormatCandidates(words);
                if (selected) s.SelectedCandidate = targetWord;
                List<string> names = new List<string>();
                for (int i = 0; i < words.Count; i++) names.Add(words[i].Word + (words[i].Generated ? "(生成)" : ""));
                note = string.Format("样例由生产构造函数生成：SimImeContext.BuildComposeStatus｜候选={0}（目标词 '{1}' 第一={2}{3}）",
                    words.Count, targetWord, words.Count > 0 && words[0].Word == targetWord ? "是" : "否",
                    degrade == null ? "" : "｜" + degrade);
            }
            catch (Exception ex)
            {
                Log.Error("bartest 造候选失败", ex);
                note = "候选生成失败：" + ex.Message;
            }
            return s;
        }

        // 样例内容尽量贴近运行时：拼音与同音字候选直接取自真实 IPinyinTable
        // showAll=false：只给真实运行会出现的样子（汉字输入 + 纠错组串变化）
        // showAll=true ：额外给 BarVerbose 排障模式才有的样例（停顿/换行/已暂停/锁定等待/进度数字）
        private static BarStatus[] BuildBarSamples(AppConfig cfg, bool showAll, out string tableInfo)
        {
            tableInfo = "(未创建)";
            IPinyinTable indexTable = null;
            IHabitStore indexHabits = null;
            try { indexTable = EngineFactory.CreatePinyinTable(); } catch (Exception ex) { Log.Error("bartest 建拼音表失败", ex); }
            try { indexHabits = EngineFactory.CreateHabitStore(AppPaths.HabitStorePath); } catch (Exception ex) { Log.Error("bartest 建习惯库失败", ex); }
            string pinyinPrev = "zhong";
            string pinyinCur = "guo";
            string candidates = "1.国 2.果 3.过 4.锅 5.郭";
            string candidatesPrev = "1.中 2.钟 3.种 4.终 5.众";
            try
            {
                IPinyinTable table = EngineFactory.CreatePinyinTable();
                if (table != null)
                {
                    tableInfo = string.Format("IsReady={0} CharCount={1}", table.IsReady, table.CharCount);
                    pinyinPrev = PinyinOr(table, '中', pinyinPrev);
                    pinyinCur = PinyinOr(table, '国', pinyinCur);
                    candidates = CandidateText(table, '国', 4, candidates);
                    candidatesPrev = CandidateText(table, '中', 4, candidatesPrev);
                }
            }
            catch (Exception ex)
            {
                Log.Error("--bartest 取拼音表失败（样例改为内置文本）", ex);
            }

            // 真实运行会出现的第 1 类：汉字输入（上行拼音串，下行带序号候选）
            BarStatus han = new BarStatus();
            han.Visible = true;
            han.Trail = pinyinPrev;
            han.Composing = pinyinCur;
            han.Candidates = candidates;
            han.Highlight = "国";

            // 真实运行会出现的第 2 类：纠错——按下退格后拼音串变短（组串变化，而不是显示「退格修正」）
            BarStatus corrected = new BarStatus();
            corrected.Visible = true;
            corrected.Trail = "";
            corrected.Composing = pinyinPrev;
            corrected.Candidates = candidatesPrev;
            corrected.Highlight = "中";

            // 真实运行会出现的第 3~5 类（第十轮新流程）：混排组串（已确认=汉字）+ 词组候选 + 选词高亮
            // 第二十二轮：样例**只演生产可达的状态**。逐词模型（BufferMaxPhrases=1）下本缓冲没有已确认块，
            // 所以组串/选词帧的 trail 恒为空——不再手工喂 '一个红色' 那种不可能出现的灰色汉字。
            string noteA, noteB, noteC;
            BarStatus selA = SelectionSample(cfg, indexTable, indexHabits, "", "yige", "", "一个", "yige", false, out noteA);     // ①逐词组串中
            BarStatus selB = SelectionSample(cfg, indexTable, indexHabits, "", "pingguo", "", "苹果", "pingguo", true, out noteB); // ②选词高亮帧
            BarStatus selC = new BarStatus();   // ③删除期：默认模式悬浮条不可见
            selC.Visible = false;
            noteC = "默认模式删除期间悬浮条不可见（BarVerbose=true 才显示删除过程）";
            BarStatus selD = new BarStatus();   // ④重打期：同上
            selD.Visible = false;
            BarStatus selE = new BarStatus();   // ⑤词间：无残留
            selE.Visible = false;
            List<BarStatus> baseList = new List<BarStatus>();
            baseList.Add(selA);    // ①逐词组串中：上行只有已敲出拼音，trail 恒空
            baseList.Add(selB);    // ②选词高亮：trail 恒空 + 候选浅蓝底加粗
            baseList.Add(selC);    // ③删除期：不可见
            baseList.Add(selD);    // ④重打期：不可见
            baseList.Add(selE);    // ⑤词间：不可见/无残留
            // 第二十三轮：默认组只演生产可达的样子——旧外观样例（带 Progress 的 zhongguo/zhong）移到 --bartest-all
            List<string> baseNotes = new List<string>();
            baseNotes.Add("逐词组串中（trail 恒空：逐词模型下本缓冲无已确认块）｜" + noteA);
            baseNotes.Add("选词高亮帧（trail 恒空 + 候选高亮）｜" + noteB);
            baseNotes.Add("删除期（不可见）｜" + noteC);
            baseNotes.Add("重打期（不可见：默认模式只写日志）");
            baseNotes.Add("词间（不可见/无残留：上一词提交完成即收起，下一词组串前不留帧）");
            if (!showAll) return Finish(baseList, baseNotes);

            han.Progress = "12/56";
            corrected.Progress = "14/56";

            BarStatus punct = new BarStatus();
            punct.Visible = true;
            punct.Composing = "，";
            punct.Foot = "原样输出（英文/数字/标点）";
            punct.Progress = "13/56";

            BarStatus pause = new BarStatus();
            pause.Visible = true;
            pause.Composing = "停顿 1200ms";
            pause.Foot = "思考停顿";
            pause.Progress = "13/56";

            BarStatus line = new BarStatus();
            line.Visible = true;
            line.Composing = "换行";
            line.Foot = "换行";
            line.Progress = "14/56";

            BarStatus halted = new BarStatus();
            halted.Visible = true;
            halted.Composing = "已暂停：目标窗口不在前台";
            halted.Foot = "切回目标窗口自动继续 · F12 停止";

            BarStatus locked = new BarStatus();
            locked.Visible = true;
            locked.Composing = "已锁定目标窗口：启动等待中…";
            locked.Foot = "F12 停止";

            List<BarStatus> allList = new List<BarStatus>();
            allList.Add(selA);
            allList.Add(selB);
            allList.Add(selC);
            allList.Add(han);
            allList.Add(corrected);
            allList.Add(punct);
            allList.Add(pause);
            allList.Add(line);
            allList.Add(halted);
            allList.Add(locked);
            List<string> allNotes = new List<string>();
            allNotes.Add("缓冲=第1~3段（当前第1段，组串中）｜" + noteA);
            allNotes.Add("缓冲=第1~3段（当前第2段，已确认'一个'）｜" + noteB);
            allNotes.Add("缓冲=第1~3段（第3段'苹果'被选中，候选行浅蓝底加粗）｜" + noteC);
            allNotes.Add("真实运行样例（旧外观，保留对照）");
            allNotes.Add("真实运行样例（旧外观，保留对照）");
            allNotes.Add("[排障模式专属] 标点原样输出");
            allNotes.Add("[排障模式专属] 停顿/思考");
            allNotes.Add("[排障模式专属] 换行");
            allNotes.Add("[排障模式专属] 已暂停");
            allNotes.Add("[排障模式专属] 锁定等待");
            return Finish(allList, allNotes);
        }

        private static string PinyinOr(IPinyinTable table, char c, string fallback)
        {
            try
            {
                string p = table.GetPinyin(c);
                return string.IsNullOrEmpty(p) ? fallback : p;
            }
            catch
            {
                return fallback;
            }
        }

        private static string CandidateText(IPinyinTable table, char c, int extra, string fallback)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("1.").Append(c);
                IList<string> homo = table.HomophonesOf(c, extra);
                int index = 2;
                if (homo != null)
                {
                    for (int i = 0; i < homo.Count; i++)
                    {
                        if (string.IsNullOrEmpty(homo[i])) continue;
                        sb.Append(' ').Append(index).Append('.').Append(homo[i]);
                        index++;
                    }
                }
                return sb.ToString();
            }
            catch
            {
                return fallback;
            }
        }

        // --verify "<窗口标题片段>" [测试文本]：默认注入路径 + 回读比对，机器判定「落屏是否等于原文」
        // 绝不抢前台、绝不盲目注入；剪贴板必定还原
        internal static int RunVerify(AppConfig cfg, string titleFragment, string overrideText)
        {
            ConsoleBridge.Attach();
            Log.Enabled = true;
            StringBuilder mirror = new StringBuilder();
            Injector injector = new Injector(cfg);
            try
            {
                string text = string.IsNullOrEmpty(overrideText) ? TestText : overrideText;
                ConsoleBridge.WriteLine(mirror, "SimIme --verify（默认注入路径 + 回读比对）");
                ConsoleBridge.WriteLine(mirror, "测试文本（换行显示为 \\n）：" + Describe(text));
                if (string.IsNullOrEmpty(titleFragment))
                {
                    ConsoleBridge.WriteLine(mirror, "缺少目标窗口标题片段。用法：SimIme.exe --verify \"记事本\" [测试文本]");
                    Log.Error("--verify：缺少目标窗口标题片段");
                    return 4;
                }

                IntPtr hwnd = WindowFinder.FindByTitle(titleFragment);
                if (hwnd == IntPtr.Zero)
                {
                    ConsoleBridge.WriteLine(mirror, "未找到标题包含该片段的可见窗口，已放弃（绝不盲目注入）。");
                    Log.Error("--verify：未找到标题包含「" + titleFragment + "」的可见窗口");
                    return 3;
                }
                ConsoleBridge.WriteLine(mirror, string.Format("目标窗口 = 0x{0:X}  标题 = {1}", hwnd.ToInt64(), Native.GetWindowTitle(hwnd)));

                if (Native.GetForegroundWindow() != hwnd)
                {
                    string msg = "目标窗口不是当前前台窗口：已放弃注入（本模式绝不盲目输入、也不抢前台）。请先点一下目标窗口再运行。";
                    ConsoleBridge.WriteLine(mirror, msg);
                    Log.Error("--verify 中止：" + msg);
                    return 2;
                }

                // 完整性护栏：跨完整性时 SendInput 会被静默丢弃（返回成功），那样只会得到「不一致」的假象
                int selfRid = AppInfo.IntegrityRid;
                uint targetPid = Native.WindowProcessId(hwnd);
                int openError;
                int targetRid = Native.ProcessIntegrityRid(targetPid, out openError);
                if (selfRid > 0 && targetRid > 0 && selfRid < targetRid)
                {
                    string msg = string.Format("我方完整性={0}，目标={1}：Windows 会丢弃按键，注入必然无效。请用 SimIme-Admin.exe 运行后再试。",
                        Integrity.Name(selfRid), Integrity.Name(targetRid));
                    ConsoleBridge.WriteLine(mirror, msg);
                    Log.Error("--verify 中止：" + msg);
                    return 6;
                }
                if (selfRid > 0 && targetRid <= 0 && openError == Native.ERROR_ACCESS_DENIED)
                {
                    string msg = string.Format("目标进程完整性读取被拒绝(win32=5)，我方={0}：目标极可能完整性更高，按键会被丢弃。请用 SimIme-Admin.exe 运行后再试。",
                        Integrity.Name(selfRid));
                    ConsoleBridge.WriteLine(mirror, msg);
                    Log.Error("--verify 中止：" + msg);
                    return 6;
                }

                bool savedSimulation = cfg.Inject.ImeSimulation;
                cfg.Inject.ImeSimulation = false;   // 本模式强制直投，让比对针对默认注入路径
                int done = 0;
                int failed = 0;
                bool lostFocus = false;
                try
                {
                    IPinyinTable table = null;
                    IHabitStore habits = null;
                    try { table = EngineFactory.CreatePinyinTable(); }
                    catch (Exception ex) { ConsoleBridge.WriteLine(mirror, "拼音表创建失败：" + ex.Message); }
                    try { habits = EngineFactory.CreateHabitStore(AppPaths.HabitStorePath); }
                    catch (Exception ex) { ConsoleBridge.WriteLine(mirror, "习惯库创建失败：" + ex.Message); }

                    IList<Step> plan = null;
                    try { plan = EngineFactory.BuildPlan(text, cfg.Engine, table, habits); }
                    catch (Exception ex) { ConsoleBridge.WriteLine(mirror, "BuildPlan 失败：" + ex.Message); }
                    if (plan == null || plan.Count == 0) plan = DirectPlan(text);
                    ConsoleBridge.WriteLine(mirror, "注入方式=直接上屏（--verify 强制 ImeSimulation=false）；计划步数=" + plan.Count);
                    Log.Info("--verify：强制直投 ImeSimulation=false，计划步数=" + plan.Count);

                    bool clipboardUsed = injector.UseClipboardForChars();
                    if (clipboardUsed) injector.SaveClipboard();
                    int startup = cfg.Engine.Rhythm.StartupDelayMs;
                    if (startup > 0) Thread.Sleep(startup);
                    try
                    {
                        for (int i = 0; i < plan.Count; i++)
                        {
                            if (Native.GetForegroundWindow() != hwnd) { lostFocus = true; break; }
                            Step step = plan[i];
                            bool ok = injector.ApplyStep(step, null);
                            if (!ok && !clipboardUsed && cfg.Inject.ClipboardFallback && step.Kind == StepKind.Char)
                            {
                                if (!clipboardUsed) { injector.SaveClipboard(); clipboardUsed = true; }
                                ok = injector.PasteTextViaClipboard(step.Text);
                            }
                            if (ok) done++; else failed++;
                            if (step.DelayMs > 0) Thread.Sleep(step.DelayMs);
                        }
                    }
                    finally
                    {
                        if (clipboardUsed) injector.RestoreClipboard();
                    }
                }
                finally
                {
                    cfg.Inject.ImeSimulation = savedSimulation;
                }

                ConsoleBridge.WriteLine(mirror, string.Format("注入完成：成功 {0} 步 / 失败 {1} 步{2}", done, failed, lostFocus ? "（中途目标失焦）" : ""));
                Thread.Sleep(300);   // 给目标窗口处理完最后几个按键

                // 三路回读：WM_GETTEXT(经典焦点控件) → UIA → 剪贴板；带「等于窗口标题即无效」护栏
                ReadBackResult readback = TextReadback.Read(hwnd, cfg);
                string actual = readback.Text == null ? "" : readback.Text;
                ConsoleBridge.WriteLine(mirror, string.Format("回读方式={0}｜读回长度={1}｜有效={2}", readback.Method, actual.Length, readback.Valid));
                Log.Info(string.Format("--verify：回读方式={0}，读回长度={1}，有效={2}", readback.Method, actual.Length, readback.Valid));

                string verdict;
                bool same = CompareTexts(text, actual, out verdict);
                ConsoleBridge.WriteLine(mirror, verdict);
                Log.Info("--verify " + verdict);
                if (!same) Log.Error("--verify 落屏文本与原文不一致：" + verdict);
                if (failed > 0 || lostFocus) return 5;
                return same ? 0 : 5;
            }
            catch (Exception ex)
            {
                Log.Error("--verify 异常", ex);
                ConsoleBridge.WriteLine(mirror, "--verify 异常：" + ex.Message);
                return 9;
            }
            finally
            {
                try { File.WriteAllText(AppPaths.SelfTestLogPath, mirror.ToString(), new UTF8Encoding(true)); } catch { }
            }
        }

        internal static string NormalizeNewlines(string value)
        {
            if (value == null) return "";
            return value.Replace("\r\n", "\n").Replace('\r', '\n');
        }

        // 纯函数比对：结论 + 预期/实际字符数 + 首个不同位置与前后 20 字片段 + 类型
        internal static bool CompareTexts(string expected, string actual, out string verdict)
        {
            string e = NormalizeNewlines(expected);
            string a = NormalizeNewlines(actual);
            bool same = string.Equals(e, a, StringComparison.Ordinal);
            bool trailingOnly = false;
            if (!same)
            {
                // 编辑控件常在末尾补一个换行：这类差异单独标明，不当作不一致（结论里写明）
                string eTrim = e.TrimEnd('\n');
                string aTrim = a.TrimEnd('\n');
                if (string.Equals(eTrim, aTrim, StringComparison.Ordinal))
                {
                    same = true;
                    trailingOnly = true;
                }
            }
            StringBuilder sb = new StringBuilder();
            sb.Append(string.Format("结果={0}｜预期字符数={1}｜实际字符数={2}", same ? "一致" : "不一致", e.Length, a.Length));
            if (trailingOnly) sb.Append("｜备注=仅末尾换行不同（编辑器补行），按一致处理");
            if (!same)
            {
                int i = 0;
                while (i < e.Length && i < a.Length && e[i] == a[i]) i++;
                int from = Math.Max(0, i - 20);
                sb.Append(string.Format("｜首个不同位置={0}（从 0 起）", i));
                sb.Append("｜预期片段=" + Slice(e, from));
                sb.Append("｜实际片段=" + Slice(a, from));
                sb.Append("｜类型=" + Classify(e, a, i));
            }
            verdict = sb.ToString();
            return same;
        }

        private static string Slice(string value, int from)
        {
            if (value == null || from >= value.Length) return "(已到末尾)";
            string part = value.Substring(from, Math.Min(41, value.Length - from));
            return "'" + part.Replace("\n", "\\n") + "'";
        }

        private static string Classify(string expected, string actual, int diffIndex)
        {
            if (actual.Length < expected.Length) return "少字（有内容缺失）";
            if (actual.Length > expected.Length)
            {
                if (diffIndex < actual.Length)
                {
                    char av = actual[diffIndex];
                    if (av < 128 && (diffIndex >= expected.Length || expected[diffIndex] >= 0x2E80)) return "多字（疑似拼音字母残留）";
                }
                return "多字（疑似多输）";
            }
            if (diffIndex < actual.Length && diffIndex < expected.Length)
            {
                char av = actual[diffIndex];
                char ev = expected[diffIndex];
                if (av < 128 && ev >= 0x2E80) return "字符不同（疑似拼音字母残留）";
            }
            return "字符不同";
        }

        // --words：打印本地用户词表（前 50，按次数降序）与存储位置；删除该文件即重置学习
        internal static int RunWords(AppConfig cfg)
        {
            ConsoleBridge.Attach();
            Log.Enabled = true;
            PhraseWorkflow.Init();   // 打印切分实现前先探测 A 的 API
            StringBuilder mirror = new StringBuilder();
            try
            {
                ConsoleBridge.WriteLine(mirror, "SimIme --words（本地用户词表）");
                ConsoleBridge.WriteLine(mirror, "存储位置：" + AppPaths.HabitStorePath);
                bool exists = File.Exists(AppPaths.HabitStorePath);
                long size = exists ? new FileInfo(AppPaths.HabitStorePath).Length : 0;
                ConsoleBridge.WriteLine(mirror, string.Format("文件状态：存在={0}　大小={1} 字节", exists, size));
                ConsoleBridge.WriteLine(mirror, "重置办法：删除该文件即重置学习（下次运行重新累积）");
                ConsoleBridge.WriteLine(mirror, "学习开关：Behavior.LearnUserWords="
                    + (cfg.Behavior.LearnUserWords ? "true（默认：每次输入的原文都会交给习惯库并 Save）" : "false（完全不学习、不写盘）"));
                ConsoleBridge.WriteLine(mirror, "词组阈值：Engine.UserWordMinCount=" + UserWordTable.MinCountInfo(cfg));
                ConsoleBridge.WriteLine(mirror, "切分实现：" + PhraseWorkflow.SplitterSource);

                IHabitStore habits = null;
                try { habits = EngineFactory.CreateHabitStore(AppPaths.HabitStorePath); }
                catch (Exception ex) { ConsoleBridge.WriteLine(mirror, "习惯库创建失败：" + ex.Message); }
                if (habits != null)
                {
                    try { habits.Load(); } catch (Exception ex) { Log.Info("习惯库 Load 跳过：" + ex.Message); }
                    string source;
                    System.Collections.Generic.List<KeyValuePair<string, int>> top = UserWordTable.TopWords(habits, 50, out source);
                    ConsoleBridge.WriteLine(mirror, "用户词组（2~4 字，≥阈值）：" + UserWordTable.UserWordCount(habits) + " 个｜词频表条目：" + UserWordTable.WordKindCount(habits) + " 个｜来源=" + source);
                    ConsoleBridge.WriteLine(mirror, "状态行文案（与主界面同一套逻辑）：" + UserWordTable.ShortStatus(habits));
                    if (top.Count == 0)
                    {
                        ConsoleBridge.WriteLine(mirror, "  （习惯库为空，尚无学习数据：先正常输入几次，或删掉 habits.dat 后重跑）");
                    }
                    else
                    {
                        for (int i = 0; i < top.Count; i++)
                        {
                            ConsoleBridge.WriteLine(mirror, string.Format("  {0,2}. {1}  ×{2}", i + 1, top[i].Key, top[i].Value));
                        }
                    }
                    Log.Info("--words：" + UserWordTable.Describe(habits, 5));
                }
                return 0;
            }
            catch (Exception ex)
            {
                Log.Error("--words 异常", ex);
                ConsoleBridge.WriteLine(mirror, "--words 异常：" + ex.Message);
                return 1;
            }
            finally
            {
                try { File.WriteAllText(AppPaths.SelfTestLogPath, mirror.ToString(), new UTF8Encoding(true)); } catch { }
            }
        }

        // --iltest：只读诊断，打印自身完整性/提权 + 前台窗口所属进程完整性 + UIPI 结论（不做任何注入）
        internal static int RunIlTest(AppConfig cfg)
        {
            ConsoleBridge.Attach();
            Log.Enabled = true;
            StringBuilder mirror = new StringBuilder();
            try
            {
                int selfRid = AppInfo.IntegrityRid;
                bool elevated = AppInfo.IsElevatedToken;
                ConsoleBridge.WriteLine(mirror, "SimIme --iltest（只读诊断，不注入任何字符）");
                ConsoleBridge.WriteLine(mirror, string.Format("自身完整性 = {0}   已提权 = {1}", Integrity.Describe(selfRid), elevated ? "是" : "否"));
                ConsoleBridge.WriteLine(mirror, string.Format("自身 PID = {0}   位数 = {1}   目录 = {2}",
                    System.Diagnostics.Process.GetCurrentProcess().Id, IntPtr.Size == 8 ? "x64" : "x86", AppPaths.BaseDir));
                Log.Info(string.Format("--iltest：自身完整性={0} 提权={1}", Integrity.Describe(selfRid), elevated ? "是" : "否"));

                IntPtr fg = Native.GetForegroundWindow();
                if (fg == IntPtr.Zero)
                {
                    ConsoleBridge.WriteLine(mirror, "当前没有前台窗口：无法比对（先点一下目标窗口再运行 --iltest）");
                    Log.Info("--iltest：无前台窗口");
                }
                else
                {
                    uint pid = Native.WindowProcessId(fg);
                    int openError;
                    int targetRid = Native.ProcessIntegrityRid(pid, out openError);
                    ConsoleBridge.WriteLine(mirror, string.Format("前台窗口 = 0x{0:X}  标题 = {1}", fg.ToInt64(), Native.GetWindowTitle(fg)));
                    ConsoleBridge.WriteLine(mirror, string.Format("前台窗口类 = {0}  所属 PID = {1}", Native.GetWindowClass(fg), pid));
                    ConsoleBridge.WriteLine(mirror, "目标完整性 = " + Integrity.Describe(targetRid));
                    Log.Info(string.Format("--iltest：前台 hwnd=0x{0:X} pid={1} 目标完整性={2}", fg.ToInt64(), pid, Integrity.Describe(targetRid)));
                    if (targetRid <= 0 && openError == Native.ERROR_ACCESS_DENIED)
                    {
                        ConsoleBridge.WriteLine(mirror, string.Format("目标完整性读取被拒绝（win32=5 ACCESS_DENIED，我方={0}）", Integrity.Name(selfRid)));
                        ConsoleBridge.WriteLine(mirror, "结论：**会被 Windows UIPI 静默丢弃** —— 同级或更低完整性的目标一定读得到，拒绝访问即说明目标完整性更高；请用 SimIme-Admin.exe 启动");
                    }
                    else if (targetRid <= 0)
                    {
                        ConsoleBridge.WriteLine(mirror, string.Format("结论：目标完整性读取失败(win32={0})（进程已退出或其他原因）→ 运行期会跳过该检查，不做误判", openError));
                    }
                    else if (selfRid > 0 && selfRid < targetRid)
                    {
                        ConsoleBridge.WriteLine(mirror, "结论：**会被 Windows UIPI 静默丢弃** —— 必须用 SimIme-Admin.exe（管理员）启动");
                    }
                    else
                    {
                        ConsoleBridge.WriteLine(mirror, "结论：可以注入（我方完整性 >= 目标完整性）");
                    }
                    ConsoleBridge.WriteLine(mirror, "附注：UIPI 拦截时 SendInput 仍返回成功计数、GetLastError 是陈旧值，因此运行期只以完整性比对为准");
                }
                return 0;
            }
            catch (Exception ex)
            {
                Log.Error("--iltest 异常", ex);
                ConsoleBridge.WriteLine(mirror, "--iltest 异常：" + ex.Message);
                return 1;
            }
            finally
            {
                try { File.WriteAllText(AppPaths.SelfTestLogPath, mirror.ToString(), new UTF8Encoding(true)); } catch { }
            }
        }

        // --updatedict [--mirror URL] [--dictout PATH]：主界面「联网更新词典」按钮的等价命令行入口
        internal static int RunUpdateDict(AppConfig cfg, string mirrorOverride, string outputOverride)
        {
            ConsoleBridge.Attach();
            Log.Enabled = true;
            StringBuilder mirror = new StringBuilder();
            try
            {
                ConsoleBridge.WriteLine(mirror, "SimIme --updatedict（联网更新外部词表）");
                ConsoleBridge.WriteLine(mirror, "镜像=" + (string.IsNullOrEmpty(mirrorOverride) ? cfg.Behavior.DictMirror : mirrorOverride));
                ConsoleBridge.WriteLine(mirror, "目标=" + (string.IsNullOrEmpty(outputOverride) ? cfg.Behavior.ExtraDictFile : outputOverride));
                ConsoleBridge.WriteLine(mirror, "更新前：" + DictUpdater.Describe(cfg));
                DictUpdateResult result = DictUpdater.Run(cfg, mirrorOverride, outputOverride);
                ConsoleBridge.WriteLine(mirror, string.Format("索引页用时={0}ms ｜分发包={1} 字节（下载用时 {2}ms）｜解出内容={3} 字节 ｜数据源={4} ｜总用时={5}ms",
                    result.IndexMs, result.DownloadedBytes, result.DownloadMs, result.PayloadBytes, result.SourceKind, result.ElapsedMs));
                ConsoleBridge.WriteLine(mirror, "下载地址=" + result.Url);
                if (result.Ok)
                {
                    string path = result.WrittenPath;
                    long size = File.Exists(path) ? new FileInfo(path).Length : 0;
                    ConsoleBridge.WriteLine(mirror, string.Format("成功：写入 {0}（{1} 条，{2} 字节）", path, result.Count, size));
                    ConsoleBridge.WriteLine(mirror, "注册结果：" + (result.RegisteredThisRun ? "已调用 SetExtraWordFile，本次运行即生效" : "未注册（重启后生效）"));
                    if (!string.IsNullOrEmpty(result.RegisterNote)) ConsoleBridge.WriteLine(mirror, "  " + result.RegisterNote);
                    string extraStatus = DictUpdater.ExtraWordStatusText();
                    if (extraStatus != null) ConsoleBridge.WriteLine(mirror, "ExtraWordStatus = " + extraStatus);
                    ConsoleBridge.WriteLine(mirror, "更新后：" + DictUpdater.Describe(cfg));
                    Log.Info("外部词表已更新：" + result.Count + " 条，来源=" + result.Url + "，写入=" + path
                        + "，大小=" + size + " 字节，用时=" + result.ElapsedMs + "ms，下载=" + result.DownloadedBytes + " 字节");
                }
                else
                {
                    ConsoleBridge.WriteLine(mirror, "更新失败：" + result.Message);
                    ConsoleBridge.WriteLine(mirror, "更新后（应保持原样）：" + DictUpdater.Describe(cfg));
                }
                return result.Ok ? 0 : 3;
            }
            catch (Exception ex)
            {
                Log.Error("--updatedict 异常", ex);
                ConsoleBridge.WriteLine(mirror, "更新异常：" + ex.Message);
                return 1;
            }
            finally
            {
                try { File.WriteAllText(AppPaths.SelfTestLogPath, mirror.ToString(), new UTF8Encoding(true)); } catch { }
            }
        }

        internal static int RunPrintConfig(AppConfig cfg)
        {
            bool attached = ConsoleBridge.Attach();
            StringBuilder mirror = new StringBuilder();
            try
            {
                cfg.Normalize();
                cfg.Save(AppPaths.ConfigPath);
                ConsoleBridge.WriteLine(mirror, cfg.ToJson());
                ConsoleBridge.WriteLine(mirror, "config.json => " + AppPaths.ConfigPath);
                ConsoleBridge.WriteLine(mirror, DictUpdater.Describe(cfg));
                ConsoleBridge.WriteLine(mirror, "词典镜像=" + cfg.Behavior.DictMirror);
                ConsoleBridge.WriteLine(mirror, "stdout 已附着父控制台 = " + (attached ? "是" : "否"));
                Log.Info("--printconfig 完成｜" + DictUpdater.Describe(cfg));
                return 0;
            }
            catch (Exception ex)
            {
                Log.Error("--printconfig 失败", ex);
                return 1;
            }
        }

        internal static int RunSelfTest(AppConfig cfg, string titleFragment)
        {
            ConsoleBridge.Attach();
            StringBuilder mirror = new StringBuilder();
            int code = 0;
            try
            {
                Log.Enabled = true;
                ConsoleBridge.WriteLine(mirror, "SimIme --selftest");
                ConsoleBridge.WriteLine(mirror, "目标窗口标题片段：" + (titleFragment == null ? "(未给出)" : titleFragment));
                ConsoleBridge.WriteLine(mirror, "exe 目录：" + AppPaths.BaseDir);

                if (string.IsNullOrEmpty(titleFragment))
                {
                    ConsoleBridge.WriteLine(mirror, "缺少标题片段。用法：SimIme.exe --selftest \"记事本\"");
                    return 4;
                }

                IntPtr hwnd = WindowFinder.FindByTitle(titleFragment);
                if (hwnd == IntPtr.Zero)
                {
                    ConsoleBridge.WriteLine(mirror, "未找到标题包含该片段的可见窗口，自检中止。");
                    return 3;
                }
                ConsoleBridge.WriteLine(mirror, string.Format("命中窗口：hwnd=0x{0:X} class={1} title={2}",
                    hwnd.ToInt64(), Native.GetWindowClass(hwnd), Native.GetWindowTitle(hwnd)));

                // 只有目标窗口确实在前台才注入：绝不做盲目输入
                Native.SetForegroundWindow(hwnd);
                if (!WaitForeground(hwnd, 20000))
                {
                    ConsoleBridge.WriteLine(mirror, "目标窗口在 20 秒内未成为前台窗口，已放弃注入（不做盲目输入）。");
                    ConsoleBridge.WriteLine(mirror, "请手动点一下目标窗口后重新运行 --selftest。");
                    return 2;
                }
                ConsoleBridge.WriteLine(mirror, "目标窗口已在前台，继续自检。");

                int startup = cfg.Engine.Rhythm.StartupDelayMs;
                if (startup > 0) Thread.Sleep(startup);

                AnchorInfo anchor = AnchorService.Resolve(cfg);
                anchor.Hwnd = hwnd;
                ConsoleBridge.WriteLine(mirror, "锚定：" + anchor.Describe());

                IPinyinTable table = null;
                IHabitStore habits = null;
                try { table = EngineFactory.CreatePinyinTable(); }
                catch (Exception ex) { ConsoleBridge.WriteLine(mirror, "拼音表创建失败：" + ex.Message); }
                try { habits = EngineFactory.CreateHabitStore(AppPaths.HabitStorePath); }
                catch (Exception ex) { ConsoleBridge.WriteLine(mirror, "习惯库创建失败：" + ex.Message); }

                IList<Step> plan = null;
                try { plan = EngineFactory.BuildPlan(TestText, cfg.Engine, table, habits); }
                catch (Exception ex) { ConsoleBridge.WriteLine(mirror, "BuildPlan 失败：" + ex.Message); }
                if (plan == null || plan.Count == 0)
                {
                    ConsoleBridge.WriteLine(mirror, "计划为空，改用直通计划。");
                    plan = DirectPlan(TestText);
                }
                ConsoleBridge.WriteLine(mirror, "计划步数：" + plan.Count + " 测试串：" + Describe(TestText));

                Injector injector = new Injector(cfg);
                bool clipboardMode = injector.UseClipboardForChars();
                bool clipboardUsed = clipboardMode;
                if (clipboardMode) injector.SaveClipboard();

                int done = 0;
                int failed = 0;
                bool lostFocus = false;
                try
                {
                    for (int i = 0; i < plan.Count; i++)
                    {
                        if (Native.GetForegroundWindow() != hwnd)
                        {
                            lostFocus = true;
                            ConsoleBridge.WriteLine(mirror, "目标窗口失去前台（第 " + (i + 1) + " 步），按规程中止。");
                            break;
                        }
                        Step step = plan[i];
                        bool ok = injector.ApplyStep(step);
                        if (!ok && !clipboardMode && cfg.Inject.ClipboardFallback && step.Kind == StepKind.Char)
                        {
                            if (!clipboardUsed) { injector.SaveClipboard(); clipboardUsed = true; }
                            ok = injector.PasteTextViaClipboard(step.Text);
                        }
                        if (!ok)
                        {
                            failed++;
                            ConsoleBridge.WriteLine(mirror, "第 " + (i + 1) + " 步注入失败（Kind=" + step.Kind + "）");
                        }
                        else
                        {
                            done++;
                        }
                        if (step.DelayMs > 0) Thread.Sleep(step.DelayMs);
                    }
                }
                finally
                {
                    if (clipboardUsed) injector.RestoreClipboard();
                }

                ConsoleBridge.WriteLine(mirror, string.Format("自检结束：成功 {0} 步 / 失败 {1} 步 / 共 {2} 步{3}",
                    done, failed, plan.Count, lostFocus ? "（中途失焦）" : ""));
                ConsoleBridge.WriteLine(mirror, "请核对目标窗口内容是否等于：" + TestText.Replace("\r\n", "\\n"));
                code = (failed == 0 && !lostFocus) ? 0 : 5;
            }
            catch (Exception ex)
            {
                Log.Error("--selftest 异常", ex);
                ConsoleBridge.WriteLine(mirror, "自检异常：" + ex.Message);
                code = 9;
            }
            finally
            {
                try
                {
                    string text = mirror.ToString();
                    File.WriteAllText(AppPaths.SelfTestLogPath, text, new UTF8Encoding(true));
                }
                catch (Exception ex)
                {
                    Log.Error("写 selftest.log 失败", ex);
                }
            }
            return code;
        }

        private static bool WaitForeground(IntPtr hwnd, int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                if (Native.GetForegroundWindow() == hwnd) return true;
                Thread.Sleep(200);
                waited += 200;
            }
            return Native.GetForegroundWindow() == hwnd;
        }

        private static string Describe(string text)
        {
            if (text == null) return "";
            return text.Replace("\r\n", "\\n\\r").Replace("\n", "\\n");
        }

        private static IList<Step> DirectPlan(string text)
        {
            List<Step> plan = new List<Step>();
            if (text == null) return plan;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r') continue;
                if (c == '\n') { plan.Add(new Step(StepKind.LineBreak, null, 60, "direct-line")); continue; }
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    plan.Add(new Step(StepKind.Char, text.Substring(i, 2), 90, "direct-surrogate"));
                    i++;
                    continue;
                }
                plan.Add(new Step(StepKind.Char, c.ToString(), 90, "direct-char"));
            }
            return plan;
        }
    }
}
