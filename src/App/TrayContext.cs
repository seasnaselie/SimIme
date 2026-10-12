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
    internal sealed class SimImeContext : ApplicationContext
    {
        private readonly AppConfig _cfg;
        private readonly MessageWindow _messageWindow = new MessageWindow();
        private readonly HotkeyManager _hotkeys;
        private readonly MainForm _form;
        private readonly FloatingBar _bar;
        private readonly NotifyIcon _tray;
        private readonly System.Windows.Forms.Timer _uiTimer = new System.Windows.Forms.Timer();
        private readonly LowLevelMouseHook _mouseHook = new LowLevelMouseHook();

        private volatile BarStatus _barStatus = new BarStatus();
        private volatile AppState _state = AppState.Idle;
        private volatile bool _stopRequested;
        private volatile bool _attention;
        private volatile bool _pendingSilent;
        private volatile bool _exiting;
        private volatile string _runText = "";
        private volatile string _finishReason;

        private Thread _worker;
        private AnchorInfo _clickAnchor;
        private AnchorInfo _runAnchor;
        private DateTime _countdownEnd = DateTime.MinValue;
        private bool _countdownActive;
        private bool _barShown;
        private IPinyinTable _table;
        private IHabitStore _habits;
        private string _lastTrayText;
        private string _lastStateLine;
        private int _attentionTick;
        private bool _attentionPhase;
        private readonly Random _random = new Random();
        private DateTime _lastAnchorRefresh = DateTime.MinValue;
        private DateTime _lastAnchorMissLog = DateTime.MinValue;
        private int _anchorMissCount;
        private Point _barAnchorPoint = new Point(int.MinValue, int.MinValue);

        internal SimImeContext(AppConfig cfg)
        {
            _cfg = cfg;
            Log.Enabled = cfg.Behavior.LogEnabled;
            Log.Info(string.Format("启动诊断：版本={0}｜完整性={1}｜提权={2}｜OS={3}｜运行时={4}｜系统DPI={5}｜目录={6}",
                AppInfo.Version, Integrity.Describe(AppInfo.IntegrityRid), AppInfo.IsElevatedToken ? "是" : "否",
                AppInfo.OsInfo, AppInfo.RuntimeInfo, AppInfo.DpiInfo, AppPaths.BaseDir));

            if (cfg.Inject.BufferMaxPhrases != AppConfig.BufferMaxPhrasesDefault)
            {
                Log.Info(string.Format("配置覆盖：BufferMaxPhrases={0}（代码默认 {1}）——这是多词缓冲模型、观感不是逐词；若要逐词模型请改 config.json",
                    cfg.Inject.BufferMaxPhrases, AppConfig.BufferMaxPhrasesDefault));
            }
            Log.Info(cfg.Inject.ImeSimulation
                ? string.Format("注入方式=字母预览（ImeSimulation=true，拼音停顿 {0}ms 后退格再上屏）", cfg.Inject.ImePreviewMs)
                : "注入方式=直接上屏（ImeSimulation=false，不向目标键入拼音字母）");

            _messageWindow.EnsureHandle();
            _hotkeys = new HotkeyManager(_messageWindow);
            _hotkeys.ActionPressed += OnHotkeyAction;
            _messageWindow.WakeRequested += OnWakeRequested;

            _form = new MainForm(cfg);
            _form.ConfirmRequested += OnConfirmRequested;
            _form.LocateRequested += OnLocateRequested;
            _form.StopRequested += OnStopRequested;
            _form.HideRequested += OnHideRequested;
            _form.ExitRequested += OnExitRequested;
            _form.ElevateRequested += OnElevateRequested;
            _form.DictUpdateRequested += OnDictUpdateRequested;

            _bar = new FloatingBar(cfg.Behavior);
            _bar.EnsureHandle();

            _mouseHook.LeftButtonDown += OnGlobalLeftClick;

            _tray = new NotifyIcon();
            _tray.Icon = SystemIcons.Application;
            _tray.Text = "SimIme · 就绪";
            _tray.Visible = true;
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("唤醒 / 显示主界面", null, delegate { Wake(); });
            menu.Items.Add("进入静默待机", null, delegate { RequestSilent(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出 SimIme", null, delegate { Shutdown(); });
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate { Wake(); };

            InitEngine();
            PhraseWorkflow.Init();
            Log.Info("词组工作流：切分实现=" + PhraseWorkflow.SplitterSource + "｜错拼实现=" + PhraseWorkflow.SlipSource
                + "｜拼音字母入目标=" + (_cfg.Inject.PinyinInTarget ? "是" : "否")
                + "｜字母节奏=" + _cfg.Inject.PinyinLetterMs + "ms｜块内字间隔=" + _cfg.Inject.CommitCharMs + "ms"
                + "｜相邻键错拼概率=" + PhraseWorkflow.KeyboardSlipPct(_cfg) + "%");
            bool hotkeysOk = _hotkeys.RegisterAll(_cfg);

            _uiTimer.Interval = 120;
            _uiTimer.Tick += OnUiTick;
            _uiTimer.Start();

            _form.Show();
            _form.SetStateLine("就绪（Idle）");
            _form.SetAdminLine(AppInfo.PermissionLine());
            _form.SetHint(hotkeysOk
                ? "粘贴文本 → 点「确认开始」（窗口隐身）→ 切到目标窗口按 F9 二次确认。结束或按 F12 后自动静默，仅托盘可唤醒。"
                : "部分全局热键注册失败（可能被其他程序占用）；详见 exe 同目录 error.log。");
            Log.Info("SimIme 启动完成：" + AppPaths.BaseDir);
        }

        // ---------------- 引擎 ----------------

        private void InitEngine()
        {
            try { _table = EngineFactory.CreatePinyinTable(); }
            catch (Exception ex) { Log.Error("创建拼音表失败", ex); }
            try
            {
                // 迁移必须在**建库之前**：需要清就清盘（并验证删干净），随后再建 → 拿到的就是空库。
                // （旧顺序是先 Load 进内存再删文件，之后任何 Save 都会把旧词写回——真机实测没生效。）
                try
                {
                    LearningStore.Prepare();
                    LearningStore.LoadPairs();
                }
                catch (Exception ex) { Log.Error("学习库迁移准备失败", ex); }
                _habits = EngineFactory.CreateHabitStore(AppPaths.HabitStorePath);
                if (_habits != null)
                {
                    try { _habits.Load(); } catch (Exception ex) { Log.Info("习惯库 Load 跳过：" + ex.Message); }
                    Log.Info(UserWordTable.Describe(_habits, 5) + "｜路径=" + AppPaths.HabitStorePath
                        + "｜学习开关=" + (_cfg.Behavior.LearnUserWords ? "开" : "关")
                        + "｜搭配阈值=" + _cfg.Behavior.LearnMergeMinCount);
                    _form.SetUserWordInfo(UserWordTable.ShortStatus(_habits));
                }
            }
            catch (Exception ex) { Log.Error("创建习惯库失败", ex); }

            // 词组候选索引（拼音 -> 词）
            try
            {
                WordIndex.EnsureBuilt(_table, _cfg, _habits);
            }
            catch (Exception ex) { Log.Error("建立候选词索引失败", ex); }

            // 外部词表（联网更新词典）状态
            try
            {
                Log.Info(DictUpdater.Describe(_cfg) + "｜镜像=" + _cfg.Behavior.DictMirror);
                _form.SetDictInfo(DictUpdater.Describe(_cfg));
            }
            catch (Exception ex) { Log.Error("读取外部词表状态失败", ex); }
        }

        // ---------------- 联网更新外部词表（后台线程，绝不卡 UI、绝不弹模态框） ----------------

        private volatile string _dictPendingText;   // 后台线程写完，UI 定时器读到后刷新界面
        private volatile bool _dictBusy;

        private void OnDictUpdateRequested()
        {
            try
            {
                if (_dictBusy)
                {
                    _form.SetDictInfo("正在更新中，请稍候…（" + DictUpdater.Describe(_cfg) + "）");
                    return;
                }
                _dictBusy = true;
                _form.SetDictBusy(true, "正在更新…");
                _form.SetDictInfo("正在联网更新词典…（镜像=" + _cfg.Behavior.DictMirror + "）");
                Log.Info("词典更新：开始（镜像=" + _cfg.Behavior.DictMirror + "，目标=" + _cfg.Behavior.ExtraDictFile + "）");

                System.Threading.Thread worker = new System.Threading.Thread(delegate()
                {
                    DictUpdateResult result = null;
                    try
                    {
                        result = DictUpdater.Run(_cfg, null, null);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("词典更新线程异常", ex);
                    }
                    finally
                    {
                        string text;
                        if (result == null) text = "失败：词典更新异常中断（详见 error.log）";
                        else if (result.Ok)
                        {
                            text = string.Format("成功：外部词表 {0} 条（用时 {1}ms，下载 {2} 字节）｜{3}",
                                result.Count, result.ElapsedMs, result.DownloadedBytes, result.RegisteredThisRun ? "本次运行已生效" : "已写入，重启后生效");
                        }
                        else text = result.Message;
                        _dictPendingText = text;
                        _dictBusy = false;
                    }
                });
                worker.IsBackground = true;
                worker.Name = "SimIme.DictUpdate";
                worker.Start();
            }
            catch (Exception ex)
            {
                _dictBusy = false;
                Log.Error("发起词典更新失败", ex);
                try { _form.SetDictBusy(false, "联网更新词典"); _form.SetDictInfo("失败：无法发起更新（详见 error.log）"); } catch { }
            }
        }

        private void ApplyDictUpdateResult()
        {
            string text = _dictPendingText;
            if (text == null) return;
            _dictPendingText = null;
            try
            {
                _form.SetDictBusy(false, "联网更新词典");
                if (text.StartsWith("成功", StringComparison.Ordinal))
                {
                    string note;
                    bool registered = DictUpdater.TryRegisterForSelfTest(_cfg.Behavior.ExtraDictFile, out note);
                    string status = DictUpdater.Describe(_cfg) + (registered ? "｜本次运行已生效" : "｜" + note);
                    _form.SetDictInfo(status);
                    Log.Info("外部词表已更新：" + text + "｜" + status);
                }
                else
                {
                    _form.SetDictInfo(text + "｜" + DictUpdater.Describe(_cfg));
                    Log.Error("外部词表更新失败：" + text);
                }
                NotifyAlert("SimIme 词典更新", text, Native.CursorPosition());
            }
            catch (Exception ex)
            {
                Log.Error("应用词典更新结果失败", ex);
            }
        }

        // ---------------- 用户入口 ----------------

        private void OnConfirmRequested()
        {
            try
            {
                if (!CaptureText()) return;
                _form.ApplyToConfig();
                _cfg.Save(AppPaths.ConfigPath);
                _stopRequested = false;
                _finishReason = null;
                SetState(AppState.Arming);
                _form.Hide();
                _form.SetHint("已武装：切到目标窗口按 F9 开始（F8 点击定位 · F12 静默）。");
                ShowStatusBar("已武装：切到目标窗口按 F9 开始", "F8 点击定位 · F12 取消", Native.CursorPosition());
                Log.Info("第一次确认完成 → Arming");
            }
            catch (Exception ex)
            {
                Log.Error("确认流程失败", ex);
            }
        }

        private void OnLocateRequested()
        {
            try
            {
                if (_state == AppState.Arming || _state == AppState.Idle) BeginLocate();
            }
            catch (Exception ex)
            {
                Log.Error("点击定位启动失败", ex);
            }
        }

        private void OnStopRequested()
        {
            StopRun();
        }

        private void OnHideRequested()
        {
            try
            {
                _form.Hide();
                _form.SetHint("已隐藏到托盘：双击托盘图标可唤醒。");
            }
            catch (Exception ex)
            {
                Log.Error("隐藏主界面失败", ex);
            }
        }

        // 以管理员身份重启：调用同目录 SimIme-Admin.exe（带 requireAdministrator 清单，由 C 交付）
        private void OnElevateRequested()
        {
            string adminPath = Path.Combine(AppPaths.BaseDir, "SimIme-Admin.exe");
            try
            {
                if (!File.Exists(adminPath))
                {
                    _form.SetHint("同目录未找到 SimIme-Admin.exe，无法提权重启；请以管理员身份手动运行本程序。");
                    Log.Error("提权重启失败：找不到 " + adminPath);
                    return;
                }
                // 必须先释放单实例互斥体，否则提权实例会把自己当成「已有实例」直接退出
                SingleInstance.ReleaseCurrent();
                try
                {
                    System.Diagnostics.ProcessStartInfo info = new System.Diagnostics.ProcessStartInfo(adminPath);
                    info.UseShellExecute = true;      // 交给 Windows 弹 UAC
                    info.WorkingDirectory = AppPaths.BaseDir;
                    System.Diagnostics.Process.Start(info);
                    Log.Info("已请求以管理员身份重启：" + adminPath);
                }
                catch (Exception ex)
                {
                    // UAC 被取消等：重新持有互斥体继续运行，不静默失败
                    bool back = SingleInstance.Reacquire();
                    _form.SetHint("提权重启未完成（UAC 被取消或启动失败）：" + ex.Message + (back ? "" : "；单实例锁未能恢复"));
                    Log.Error("提权重启被取消或失败", ex);
                    return;
                }
                Shutdown();
            }
            catch (Exception ex)
            {
                Log.Error("提权重启异常", ex);
                _form.SetHint("提权重启异常：" + ex.Message);
            }
        }

        private void OnExitRequested()
        {
            Shutdown();
        }

        private void OnHotkeyAction(string action)
        {
            try
            {
                Log.Info("热键动作：" + action + " 当前状态=" + StateNames.Of(_state));
                if (action == "Stop") { StopRun(); return; }
                if (action == "Cancel") { if (_state == AppState.Locating || _countdownActive) CancelLocate(); return; }
                if (action == "Start")
                {
                    if (_state == AppState.Arming) BeginLockAndRun(null);
                    return;
                }
                if (action == "Locate")
                {
                    if (_state == AppState.Arming || _state == AppState.Idle) BeginLocate();
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Error("热键动作处理失败：" + action, ex);
            }
        }

        // ---------------- 点击定位 ----------------

        private void BeginLocate()
        {
            if (!CaptureText()) return;
            _form.Hide();
            SetState(AppState.Locating);
            bool hookOk = _mouseHook.Install();
            if (!hookOk)
            {
                SetState(_runText.Length > 0 ? AppState.Arming : AppState.Idle);
                SetBar(new BarStatus { Visible = false });
                Log.Error("点击定位不可用：鼠标钩子安装失败");
                return;
            }
            _hotkeys.RegisterOne("Cancel", HotkeyManager.IdCancel, Native.VK_ESCAPE);
            ShowStatusBar("点击定位中：请点击目标位置", "Esc / F12 取消", Native.CursorPosition());
        }

        private void OnGlobalLeftClick(Point point)
        {
            try
            {
                if (_state != AppState.Locating) return;
                _mouseHook.Uninstall();
                _clickAnchor = AnchorService.FromClick(point);
                SetState(AppState.Countdown);
                _countdownEnd = DateTime.UtcNow.AddMilliseconds(Math.Max(0, _cfg.Behavior.ClickLocateCountdownMs));
                _countdownActive = true;
                Log.Info("点击定位完成，进入倒计时");
            }
            catch (Exception ex)
            {
                Log.Error("处理点击定位失败", ex);
            }
        }

        private void CancelLocate()
        {
            _countdownActive = false;
            try { _mouseHook.Uninstall(); } catch (Exception ex) { Log.Error("卸载鼠标钩子失败", ex); }
            _hotkeys.UnregisterOne(HotkeyManager.IdCancel);
            _clickAnchor = null;
            if (_runText.Length > 0)
            {
                SetState(AppState.Arming);
                ShowStatusBar("已取消：仍在武装状态，按 F9 开始", "F8 重新定位 · F12 静默", Native.CursorPosition());
            }
            else
            {
                SetState(AppState.Idle);
                SetBar(new BarStatus { Visible = false });
                _form.Show();
            }
            Log.Info("定位/倒计时已取消");
        }

        // ---------------- 运行 ----------------

        private bool CaptureText()
        {
            string text = _form.InputText;
            if (string.IsNullOrEmpty(text))
            {
                _form.SetHint("文本框为空：请先粘贴要输入的内容。");
                return false;
            }
            _runText = text;
            return true;
        }

        private void BeginLockAndRun(AnchorInfo preset)
        {
            Thread alive = _worker;
            if (alive != null && alive.IsAlive) return;
            _stopRequested = false;
            _finishReason = null;
            _clickAnchor = preset;
            SetState(AppState.Locked);
            ShowStatusBar("已锁定目标窗口：启动等待中…", "F12 停止",
                preset != null ? AnchorService.AnchorPoint(preset) : Native.CursorPosition());
            Thread worker = new Thread(RunLoop);
            worker.IsBackground = true;
            worker.Name = "SimIme.Run";
            try { worker.SetApartmentState(ApartmentState.STA); }
            catch (Exception ex) { Log.Error("设置 STA 失败（剪贴板兜底可能不可用）", ex); }
            _worker = worker;
            worker.Start();
        }

        private void RunLoop()
        {
            try
            {
                AnchorInfo anchor = _clickAnchor;
                if (anchor == null) anchor = AnchorService.Resolve(_cfg);
                _runAnchor = anchor;
                _barAnchorPoint = AnchorService.AnchorPoint(anchor);
                Log.Info("运行锚点：" + anchor.Describe() + " 悬浮条起点=(" + _barAnchorPoint.X + "," + _barAnchorPoint.Y + ")");

                if (!CheckIntegrityGuard(anchor)) return;   // 内部已 FinishRun

                if (!SleepInterruptible(_cfg.Engine.Rhythm.StartupDelayMs))
                {
                    FinishRun("启动等待期间停止");
                    return;
                }

                string text = _runText;
                IList<Step> plan = null;
                try
                {
                    plan = EngineFactory.BuildPlan(text, _cfg.Engine, _table, _habits);
                }
                catch (Exception ex)
                {
                    Log.Error("BuildPlan 失败，改用直通计划", ex);
                }
                if (plan == null || plan.Count == 0) plan = FallbackPlan(text);

                Injector injector = new Injector(_cfg);
                bool clipboardMode = injector.UseClipboardForChars();
                bool clipboardUsed = clipboardMode;
                bool fallbackUsed = false;
                if (clipboardMode) injector.SaveClipboard();

                SetState(AppState.Running);
                int total = plan.Count;
                int done = 0;
                PinyinComposer composer = new PinyinComposer();
                string failReason = null;

                IList<PhraseBlock> blocks = PhraseWorkflow.Split(text, _cfg, _table, _habits);

                // 学习放**切分之后**：只学真正提交的块与相邻块搭配（不再对原文做任意窗口计数）
                if (_cfg.Behavior.LearnUserWords && _habits != null && blocks.Count > 0)
                {
                    try
                    {
                        Log.Info("已学习本次提交块：" + LearningStore.ObserveRun(blocks, _habits, _cfg));
                        _form.SetUserWordInfo(UserWordTable.ShortStatus(_habits));
                    }
                    catch (Exception ex) { Log.Error("学习用户词组失败（不影响本次输入）", ex); }
                }
                else if (!_cfg.Behavior.LearnUserWords)
                {
                    Log.Info("用户词组学习已关闭（Behavior.LearnUserWords=false），本次不学习也不写盘");
                }

                PhraseState phrase = new PhraseState(blocks);
                Log.Info(string.Format("词组切分：{0} 块（实现={1}）：{2}", blocks.Count, PhraseWorkflow.SplitterSource, PhraseWorkflow.DescribeBlocks(blocks)));
                // 缓冲切片**预计算**：每个缓冲 = 块区间 + 计划步区间（单调不减）。
                // 这样"第 k 个缓冲的汉字必须在第 k+1 个缓冲开始组串之前注入完毕"是**构造保证**的，
                // 不再依赖运行中反复推导 bufLast，避免注入与组串错位。
                List<PhraseWorkflow.BufferSlice> slices = PhraseWorkflow.BuildBufferSlices(plan, phrase, _cfg.Inject.BufferMaxPhrases);
                // 第三十一轮：每一步的归属块（与切片划分**同源**，避免日志/断言各算一套导致块号漂移）
                int[] planBlocks = PhraseWorkflow.AssignStepsToBlocks(plan, phrase);
                Log.Info(string.Format("缓冲切片：共 {0} 个（上限 {1} 段/缓冲，计划 {2} 步）", slices.Count, _cfg.Inject.BufferMaxPhrases, total));
                ResetComposedCoverage();                          // 第三十五轮：本次运行从零开始记"已组串覆盖"
                int stepIndex = 0;
                StringBuilder committed = new StringBuilder();   // 与计划同步的落屏模型（仅用于改错显示/日志）
                string deleteUnit = "";
                int deleteLeft = 0;
                try
                {
                    for (int sliceNo = 0; sliceNo < slices.Count; sliceNo++)
                    {
                        if (_stopRequested) { failReason = null; break; }
                        PhraseWorkflow.BufferSlice slice = slices[sliceNo];
                        if (slice.FirstBlock >= blocks.Count) break;
                        // 进缓冲**先**把缓冲区间定下来：这样这一缓冲的每一帧 trail 都只覆盖本缓冲（
                        // 不会把上一缓冲的整句当 trail 露出来——真机反馈的"上一句闪一下"就是这个）
                        phrase.BufferFirst = slice.FirstBlock;
                        phrase.BufferLast = slice.LastBlock;

                        // ---- 阶段 0：本缓冲**头部**的停顿提前到这里执行 ----
                        // （引擎把"思考/休息停顿"排在缓冲首部；若放到注入窗口里，用户选完词后要干等几秒才开始打字。
                        //  真人是先停顿、再敲：上一句打完 → 静一下 → 下一句字母开始长。）
                        DateTime sliceStart = DateTime.Now;
                        long anchorMs = 0, foreMs = 0, hideMs = 0;
                        _sliceHadHighlight = false;
                        _sliceHighlightEnd = DateTime.MinValue;
                        _headPauseMsInSlice = 0;
                        Log.Info(string.Format("本缓冲头部步类型={0}（切片步骤 {1}~{2}，第 {3}~{4} 段）",
                            stepIndex < slice.StepEnd ? plan[stepIndex].Kind.ToString() : "(空)",
                            slice.StepStart, slice.StepEnd - 1, slice.FirstBlock + 1, slice.LastBlock + 1));
                        int leadingSleeps = 0;
                        while (stepIndex < slice.StepEnd && stepIndex < total && plan[stepIndex].Kind == StepKind.Sleep)
                        {
                            Step sleepStep = plan[stepIndex];
                            Log.Info(string.Format("缓冲头部停顿提前：Sleep {0}ms（reason={1}）在组串之前执行 @{2}",
                                sleepStep.DelayMs, string.IsNullOrEmpty(sleepStep.Reason) ? "(空)" : sleepStep.Reason,
                                DateTime.Now.ToString("HH:mm:ss.fff")));
                            if (!SleepInterruptible(sleepStep.DelayMs)) { failReason = null; break; }
                            stepIndex++;
                            leadingSleeps++;
                            _headPauseMsInSlice += sleepStep.DelayMs;
                        }
                        long prepareMs = (long)(DateTime.Now - sliceStart).TotalMilliseconds;

                        // ---- 阶段 0.5：本缓冲**内部**夹在同一个词两个字之间的停顿，同样提到组串之前执行 ----
                        // 真机 bug：引擎把"思考停顿"排在同一个词的两个汉字之间（计划下标 75 '时' / 76 Sleep 1557ms / 77 '候'），
                        // 注入循环里的那次睡眠把整个词拆成"时 …… 候"。真人是先静一下、再一口气把这个词敲完。
                        // 这里在组串之前各睡一次；睡过的下标收进 hoisted，注入循环开头必须跳过它们（绝不睡第二次）。
                        // 判据只看 Char/Char：错字纠正本来就要在两个字之间插退格，Backspace 不算 Char，否则每条错字都会误报。
                        // 起点用 stepIndex（头部停顿已提前执行完毕），避免与"缓冲头部停顿提前"睡同一步。
                        List<int> intraBlockSleeps = CollectIntraBlockSleeps(plan, stepIndex, slice.StepEnd);
                        HashSet<int> hoisted = new HashSet<int>(intraBlockSleeps);
                        if (intraBlockSleeps.Count > 0)
                        {
                            HoistIntraBlockSleepsBeforeCompose(plan, intraBlockSleeps, slice.FirstBlock + 1, SleepInterruptible);
                            if (_stopRequested) { failReason = null; break; }
                        }

                        // 第三十二轮：把本切片每个"要落屏的单位"登记进日志（逐字母组串 + 覆盖的计划下标），
                        // 供对照检查脚本核对"每个落屏字符都属于某个已组串并选词过的单位"。
                        // 正确字走这里（随后 ConfirmBufferDisplay 逐字母组串 + 选词高亮）；
                        // 带 Reason 的错字走 ComposeTypoRunBeforeCommit（落屏前现场演一次）。
                        // 第三十三轮（要求1）：**先看计划里有没有纠错步**（带 Reason 的 Char 或退格）——
                        // 有：不得提前整词组串/整词高亮，改成按计划逐单位（组串→高亮→落屏）；
                        // 无：保持用户认可的"整词组串 → 整词高亮 → 整词落屏"。
                        bool correctionSlice = false;
                        for (int k = slice.StepStart; k < slice.StepEnd && k < total; k++)
                        {
                            Step sc = plan[k];
                            if (sc == null) continue;
                            if (sc.Kind == StepKind.Backspace) { correctionSlice = true; break; }
                            if (sc.Kind == StepKind.Char && !string.IsNullOrEmpty(sc.Reason)) { correctionSlice = true; break; }
                        }
                        if (correctionSlice)
                        {
                            Log.Info(string.Format("第 {0}~{1} 段含纠错步：跳过整词组串与整词高亮，改按计划逐单位（组串→高亮→落屏）",
                                slice.FirstBlock + 1, slice.LastBlock + 1));
                        }
                        else
                        {
                            LogComposeUnitsForSlice(blocks, plan, planBlocks, slice.FirstBlock, slice.LastBlock,
                                slice.StepStart, slice.StepEnd, total, _cfg.Inject.PinyinLetterMs);
                            // 第三十五轮：整词组串也必须登记覆盖下标（四条路径都登记 → 落屏前不会重播动画）
                            RegisterBlockCoverage(blocks, plan, planBlocks, slice.FirstBlock, slice.LastBlock,
                                slice.StepStart, slice.StepEnd, total);

                            // ---- 阶段 1：这个缓冲在悬浮框里逐段确认（不向目标注入任何字符）----
                            if (!ConfirmBufferDisplay(phrase, slice.FirstBlock, slice.LastBlock, anchor, injector))
                            {
                                failReason = null;
                                break;
                            }
                        }
                        if (_stopRequested) { failReason = null; break; }

                        // ---- 阶段 2：缓冲全部确认完，把这一段（且只有这一段）的步骤注入目标 ----
                        DateTime injectStart = DateTime.Now;
                        _injectFirstCharAt = DateTime.MinValue;
                        _injectFirstActionAt = DateTime.MinValue;
                        _preCharNonCharSteps = 0;
                        _preCharNonCharMs = 0;
                        int injectedChars = 0;
                        int injectedBackspaces = 0;             // 第三十一轮：本片退格次数（净落屏增量断言用）
                        int injectedBreaks = 0;                 // 第三十一轮：本片换行次数（也算落屏 1 个字）
                        int retypeRunFrom = -1;                 // 问题2：待演"重打字母阶段"的退格串起点（-1=当前没有待演）
                        int retypeRunUnits = 0;                 // 这一串要重打几个字
                        string retypeRunReason = null;          // 错因（原样透传给 ShowRetypeUnit）
                        StringBuilder injectedText = new StringBuilder();
                        DateTime firstCharAt = DateTime.MinValue, lastCharAt = DateTime.MinValue;
                        string firstChar = null;
                        Log.Info(string.Format("缓冲确认完毕，开始注入：第 {0}~{1} 段（步骤 {2}~{3}，上限 {4} 段/缓冲）",
                            slice.FirstBlock + 1, slice.LastBlock + 1, slice.StepStart, slice.StepEnd - 1, _cfg.Inject.BufferMaxPhrases));
                        // 问题6-A：注入期间悬浮条不得显示尚未落屏的汉字 → 注入一开始就收起；
                        // 只有"逐字删除中"（显示的是目标里真实存在、正被删掉的字）和"重打中"（显示拼音）才临时出现。
                        DateTime hideAt = DateTime.Now;
                        HideBarBeforeCommit("缓冲区提交", stepIndex);   // 问题3：写入目标之前必须收起，且当场断言"已收起"
                        hideMs = (long)(DateTime.Now - hideAt).TotalMilliseconds;
                        long fromHighlight = _lastSelectHighlightEnd == DateTime.MinValue ? -1 : (long)(DateTime.Now - _lastSelectHighlightEnd).TotalMilliseconds;
                        Log.Info("注入开始：悬浮条已收起（避免显示尚未落屏的汉字）");
                        Log.Info(string.Format("缓冲开始 → 开始注入 间隔={0}ms（头部停顿={1}ms 锚点={2}ms 前台检查={3}ms 收起={4}ms）｜距最后一次选词高亮结束={5}ms（高亮本身不计）",
                            (long)(DateTime.Now - sliceStart).TotalMilliseconds, prepareMs, anchorMs, foreMs, hideMs, fromHighlight));
                        // 断言：进入本缓冲注入时，**期望起点 = 切片起点 + 已提前执行的头部停顿步数**（头部 Sleep 已合法提前执行）；
                        // 同时用更本质的判据兜底：本切片不能还有未执行的文本步（Char/Backspace/LineBreak）。
                        int expectedStart = slice.StepStart + leadingSleeps;
                        bool leftTextStep = HasTextStepBefore(plan, stepIndex, slice.StepStart);
                        if (stepIndex != expectedStart || leftTextStep)
                        {
                            Log.Error(string.Format("串行断言破坏：进入第 {0}~{1} 段注入时 stepIndex={2}，期望={3}（切片起点={4}+头部停顿 {5}）；本切片遗留文本步={6}",
                                slice.FirstBlock + 1, slice.LastBlock + 1, stepIndex, expectedStart, slice.StepStart, leadingSleeps, leftTextStep));
                        }
                        while (stepIndex < slice.StepEnd && stepIndex < total)
                        {
                            if (_stopRequested) break;
                            // 修改A：块内停顿已在组串之前睡过（hoisted），这里必须跳过，否则同一个 Sleep 会睡第二次
                            if (hoisted.Contains(stepIndex)) { stepIndex++; continue; }
                            Step step = plan[stepIndex];
                            // 修改A 断言：真走到这里说明上提漏了它——Sleep 的前后（同切片内）都是 Char
                            // （Backspace 不算 Char：错字纠正本来就要在两个字之间插退格，判据只看 Char）
                            if (step.Kind == StepKind.Sleep && stepIndex > slice.StepStart && stepIndex + 1 < slice.StepEnd
                                && plan[stepIndex - 1].Kind == StepKind.Char && plan[stepIndex + 1].Kind == StepKind.Char)
                            {
                                Log.Error(string.Format("块内停顿断言破坏：计划下标={0} 的 Sleep 夹在同块两个 Char 之间仍在注入序列里执行", stepIndex));
                            }
                            // 指标口径："第一个注入动作"(Char/Backspace) 与 "第一个字符" 分开记，并累计中间的非字符步
                            if (_injectFirstActionAt == DateTime.MinValue && (step.Kind == StepKind.Char || step.Kind == StepKind.Backspace))
                            {
                                _injectFirstActionAt = DateTime.Now;
                                long gapAct = _sliceHadHighlight && _sliceHighlightEnd != DateTime.MinValue
                                    ? (long)(_injectFirstActionAt - _sliceHighlightEnd).TotalMilliseconds : -1;
                                if (gapAct >= 0)
                                {
                                    Log.Info(string.Format("本缓冲第一个注入动作={0}（计划下标={1}）@{2}｜距本缓冲选词高亮结束={3}ms（本缓冲有选词，该指标适用）",
                                        step.Kind, stepIndex, _injectFirstActionAt.ToString("HH:mm:ss.fff"), gapAct));
                                    _gapSamples++;
                                    if (gapAct > _gapMaxMs) _gapMaxMs = gapAct;
                                    if (gapAct > 150) _gapOver150++;
                                }
                                else
                                {
                                    Log.Info(string.Format("本缓冲无选词（标点/英文/数字/换行块）：首个动作={0} '{1}' @{2}；上一个词提交完成→本块开始之间是词间停顿（本缓冲头部停顿={3}ms，不计入选词到落屏指标）",
                                        step.Kind, step.Text == null ? "" : step.Text, _injectFirstActionAt.ToString("HH:mm:ss.fff"), _headPauseMsInSlice));
                                }
                            }
                            if (_injectFirstCharAt == DateTime.MinValue && step.Kind != StepKind.Char)
                            {
                                _preCharNonCharSteps++;
                                _preCharNonCharMs += step.DelayMs;
                            }
                            // 标点/符号前置停顿（用户感知的"标点极快"其实是缺前置停顿：延时段一直是正确的）
                            if (step.Kind == StepKind.Char && !string.IsNullOrEmpty(step.Text)
                                && !Injector.IsChineseChar(step.Text)
                                && !IsLetterOrDigit(step.Text[0])
                                && (stepIndex == 0 || plan[stepIndex - 1].Kind != StepKind.Sleep))
                            {
                                int punctPause = _cfg.Inject.BeforePunctPauseMs;
                                if (punctPause > 0)
                                {
                                    Log.Info(string.Format("标点前置停顿：'{0}' 之前等 {1}ms（BeforePunctPauseMs），之后另有引擎延时 {2}ms",
                                        step.Text, punctPause, step.DelayMs));
                                    if (!SleepInterruptible(punctPause)) { failReason = null; break; }
                                }
                            }
                            DateTime tAnchor = DateTime.Now;
                            // 预算内补偿：本缓冲首字还没落屏时不刷新锚点（组串期间已刷新过），避免它恰好落在这一拍把"高亮→首字"顶过 150ms
                            if (_injectFirstCharAt != DateTime.MinValue) RefreshBarAnchor(anchor);
                            anchorMs += (long)(DateTime.Now - tAnchor).TotalMilliseconds;
                            DateTime tFore = DateTime.Now;
                            if (!EnsureForeground(anchor)) { failReason = "目标窗口不在前台，按策略中止"; break; }
                            foreMs += (long)(DateTime.Now - tFore).TotalMilliseconds;
                            if (_injectFirstCharAt == DateTime.MinValue && step.Kind == StepKind.Char && !string.IsNullOrEmpty(step.Text))
                            {
                                _injectFirstCharAt = DateTime.Now;
                                long gap = _sliceHadHighlight && _sliceHighlightEnd != DateTime.MinValue
                                    ? (long)(_injectFirstCharAt - _sliceHighlightEnd).TotalMilliseconds : -1;
                                if (gap < 0) { }
                                else Log.Info(string.Format("本缓冲第一个字符 '{0}' @{1}｜距本缓冲选词高亮结束={2}ms（高亮时长本身不计）｜此前有 {3} 个非字符步（退格/换行），其 delay 合计={4}ms —— 这段不是空等",
                                    step.Text, _injectFirstCharAt.ToString("HH:mm:ss.fff"), gap, _preCharNonCharSteps, _preCharNonCharMs));
                            }

                            // 问题2（真机）：纠错演出分两个阶段，顺序固定为"先删掉错字 → 再逐字母打出重打单位的拼音 → 然后选词 → 然后输出"。
                            //   删除阶段（退格步，就是这里）：默认模式不可见（"删除中：剩 N 字"帧只在排障模式出现，门控不变）；
                            //   重打字母阶段（退格串注入完之后，见 RetypeAfterDelete）：默认模式**也可见**，逐字母长出、Trail 空串。
                            // 所以这里只记下这一串（起点/字数/错因），演出推迟到退格串全部注入完之后，绝不向目标注入字母（字母=0 不变量保持）。
                            if (step.Kind == StepKind.Backspace)
                            {
                                int runEnd = stepIndex;
                                while (runEnd < slice.StepEnd && runEnd < total && plan[runEnd].Kind == StepKind.Backspace) runEnd++;
                                int units = runEnd - stepIndex;
                                deleteUnit = committed.Length >= units ? committed.ToString(committed.Length - units, units) : committed.ToString();
                                deleteLeft = deleteUnit.Length;
                                if (retypeRunFrom < 0)
                                {
                                    retypeRunFrom = stepIndex;      // 首次退格：这一串的全部退格注入完之后再演"重打字母阶段"
                                    retypeRunUnits = units;
                                    retypeRunReason = step.Reason;
                                }
                                // 改错节奏(1)：退格之前先停顿
                                int before = _cfg.Inject.BeforeDeletePauseMs;
                                if (before > 0)
                                {
                                    Log.Info(string.Format("改错停顿：退格前等 {0}ms（错因={1}，共 {2} 个字要删）",
                                        before, string.IsNullOrEmpty(step.Reason) ? "(空)" : step.Reason, units));
                                    if (!SleepInterruptible(before)) { failReason = null; break; }
                                }
                            }
                            else if (retypeRunFrom >= 0)
                            {
                                // 退格串已经全部注入完（错字已从目标里消失）→ 现在才是"逐字母重打 → 选词"
                                Log.Info(string.Format("纠错阶段切换：删除阶段已完成（退格串 {0}~{1}），进入重打字母阶段",
                                    retypeRunFrom, retypeRunFrom + retypeRunUnits - 1));
                                if (!RetypeAfterDelete(phrase, anchor, plan, retypeRunFrom, retypeRunUnits, retypeRunReason,
                                    retypeRunFrom >= 0 && retypeRunFrom < planBlocks.Length ? planBlocks[retypeRunFrom] : 0))
                                {
                                    failReason = null;
                                    break;
                                }
                                retypeRunFrom = -1;
                                retypeRunUnits = 0;
                                retypeRunReason = null;
                            }
                            // 文本已经打完、后面只剩停顿：立刻收起悬浮条，别让整句挂在那儿
                            if (step.Kind == StepKind.Sleep && !HasTextStepAfter(plan, stepIndex + 1, slice.StepEnd))
                            {
                                SetBar(new BarStatus { Visible = false });
                                Log.Info("注入收尾：文本已全部落屏，悬浮条立即收起（不残留整句）");
                            }

                        string chinesePreview = null;
                        bool chineseEmission = false;
                        if (step.Kind == StepKind.Char && !string.IsNullOrEmpty(step.Text))
                        {
                            if (Injector.IsChineseChar(step.Text))
                            {
                                // 词组级模式：字母由 ComposeBuffer 负责（默认只在悬浮条内显示）
                                chineseEmission = true;
                                if (!_cfg.Inject.PinyinInTarget)
                                {
                                    chinesePreview = _cfg.Inject.ImeSimulation ? SafePinyin(step.Text[0]) : null;
                                }
                                composer.Push(step.Text[0], SafePinyin(step.Text[0]));
                            }
                            else
                            {
                                composer.Clear();
                            }
                        }

                        // 第三十二轮（用户核心观感）：任何要落屏的汉字都必须先在悬浮条里被**逐字母打出来**——
                        // 带 Reason 的错字（"打错的那一次"）同样如此：先把错字单位的拼音逐字母敲出来 + 选词高亮，再落屏。
                        // 一整个错字连只演一次（在它第一个字落屏之前），连里的后续字符沿用同一段组串。
                        if (step.Kind == StepKind.Char && !string.IsNullOrEmpty(step.Reason)
                            && !IsTypoRunContinuation(plan, stepIndex, slice.StepStart))
                        {
                            if (!ComposeTypoRunBeforeCommit(phrase, plan, stepIndex, slice.StepEnd, total, planBlocks, anchor))
                            {
                                failReason = null;
                                break;
                            }
                        }
                        // 第三十三轮（硬约束）：落屏之前，**下一个要落屏的单位**必须已经组串过（不够就现补一个单位：组串→高亮→落屏）
                        if (step.Kind == StepKind.Char && !string.IsNullOrEmpty(step.Text))
                        {
                            if (!EnsureUnitComposed(plan, stepIndex, slice.StepEnd, total, planBlocks, anchor))
                            {
                                failReason = null;
                                break;
                            }
                        }
                        // 问题3（真机）：真正写入目标之前必须收起悬浮条——绝不允许"字已落屏、条上还挂着上一个词的组串/选词帧"并存。
                        // 覆盖注入循环内部临时出现的帧（选词高亮、重打字母、删除帧、ShowStepBar 兜底帧）。
                        // 只对"会往目标写字"的步（Char/LineBreak）做：退格（删除阶段）与重打字母阶段不受影响。
                        if (step.Kind == StepKind.Char || step.Kind == StepKind.LineBreak)
                        {
                            HideBarBeforeCommit("逐字提交", stepIndex);
                        }
                        // 要求4的断言：落屏的字符必须属于"最近组串的那个单位"
                        if (step.Kind == StepKind.Char && !string.IsNullOrEmpty(step.Text)) ConsumeComposedUnit(step.Text, stepIndex);

                        bool ok = injector.ApplyStep(step, chinesePreview);
                        if (!ok && !clipboardMode && _cfg.Inject.ClipboardFallback && step.Kind == StepKind.Char)
                        {
                            if (!clipboardUsed) { injector.SaveClipboard(); clipboardUsed = true; }
                            fallbackUsed = true;
                            ok = injector.PasteTextViaClipboard(step.Text);
                        }
                        if (!ok)
                        {
                            failReason = "注入失败（目标窗口可能以管理员权限运行）";
                            break;
                        }
                            // 口径统一：本行与"注入字符"行都用计划下标；done 只是已执行步数。
                            // 延时打印**引擎值 + 实际睡眠值**（判定"应用侧到底睡了多少"的唯一证据）
                            int actualDelay = DelayForStep(step);
                            Log.Info(string.Format("步骤 计划下标={0}（已执行 {1}/{2}）Kind={3} 引擎延时={4}ms 实际睡眠={5}ms {6}",
                                stepIndex, done, total, step.Kind, step.DelayMs, actualDelay, DescribeStepForLog(step, _table)));
                            // 逐字注入日志（取证用）：谁在什么时候真正进了目标
                            if (step.Kind == StepKind.Char && !string.IsNullOrEmpty(step.Text))
                            {
                                if (firstCharAt == DateTime.MinValue) { firstCharAt = DateTime.Now; firstChar = step.Text; }
                                lastCharAt = DateTime.Now;
                                Log.Info(string.Format("注入字符：'{0}'｜计划下标={1}（第{2}段）｜结果={3}｜目标={4}",
                                    step.Text, stepIndex, slice.FirstBlock + 1,
                                    fallbackUsed ? "剪贴板兜底" : "SendInput成功", DescribeTargetWindow()));
                            }
                            if (step.Kind == StepKind.Char && !string.IsNullOrEmpty(step.Text))
                            {
                                injectedChars += step.Text.Length;
                                injectedText.Append(step.Text);
                            }
                            else if (step.Kind == StepKind.Backspace) injectedBackspaces++;
                            else if (step.Kind == StepKind.LineBreak) injectedBreaks++;
                            phrase.ApplyStep(step);
                            // 计划同步模型（Char 追加 / Backspace 回收 / 换行追加）
                            if (step.Kind == StepKind.Char && !string.IsNullOrEmpty(step.Text)) committed.Append(PhraseWorkflow.NormalizeText(step.Text));
                            else if (step.Kind == StepKind.LineBreak) committed.Append('\n');
                            else if (step.Kind == StepKind.Backspace && committed.Length > 0) committed.Length -= 1;
                            if (step.Kind == StepKind.Backspace && deleteLeft > 0)
                            {
                                // 逐字删除的分帧：删除帧的 Trail 必须是空串（纠错演出期间不得显示"灰色已确认汉字"，
                                // 用户明确要求）；删除进度只由 Foot 的"剩 N 字"承担。
                                deleteLeft--;
                                // 第三十一轮：段号取**计划步归属表**（与切片划分同源），不再用会漂移的回放位置——
                                // 真机日志里同一段纠错出现过"第 43 段/第 42 段"两个段号，误导排查。
                                int blockDel = stepIndex >= 0 && stepIndex < planBlocks.Length
                                    ? planBlocks[stepIndex] : Math.Max(0, blocks.Count - 1);
                                if (_cfg.Behavior.BarVerbose)
                                {
                                    SetBar(BuildErrorRepairDeleteFrame(anchor, deleteLeft));
                                }
                                Log.Info(string.Format("改错删除：第 {0} 段删掉 1 个字，剩余 '{1}'（共 {2} 字）｜纠错演出期间悬浮条 Trail 一律为空串",
                                    blockDel + 1, deleteLeft > 0 ? deleteUnit.Substring(0, deleteLeft) : "(空)", deleteUnit.Length));
                            }
                            // 注入期间不再回显整句/整块汉字（问题6-A）——只有删除帧与重打帧会临时显示
                            if (phrase.Blocks.Count == 0) ShowStepBar(step, done, total, composer, anchor, fallbackUsed, chineseEmission);
                            stepIndex++;
                            done++;
                            if (!SleepInterruptible(DelayForStep(step))) break;
                        }
                        // 问题2 防御：退格串正好收在切片末尾（正确字落在下一片）时，重打字母阶段在本片就地补演，绝不跨片
                        if (retypeRunFrom >= 0 && failReason == null && !_stopRequested)
                        {
                            Log.Info(string.Format("重打演出收尾：退格串 {0}~{1} 已在切片末尾结束，就地补演重打字母阶段",
                                retypeRunFrom, retypeRunFrom + retypeRunUnits - 1));
                            RetypeAfterDelete(phrase, anchor, plan, retypeRunFrom, retypeRunUnits, retypeRunReason,
                                retypeRunFrom >= 0 && retypeRunFrom < planBlocks.Length ? planBlocks[retypeRunFrom] : 0);
                            retypeRunFrom = -1;
                        }
                        // 这一段缓冲的注入结果（时间/文本/字节）
                        Log.Info(string.Format("注入完成（第 {0}~{1} 段，用时 {2}ms，{3} 字；退格 {8} 次，净 {9} 字）：'{4}'｜首字'{5}'@{6} 末字@{7}",
                            slice.FirstBlock + 1, slice.LastBlock + 1, (long)(DateTime.Now - injectStart).TotalMilliseconds,
                            injectedChars, injectedText.ToString(),
                            firstChar == null ? "-" : firstChar,
                            firstCharAt == DateTime.MinValue ? "-" : firstCharAt.ToString("HH:mm:ss.fff"),
                            lastCharAt == DateTime.MinValue ? "-" : lastCharAt.ToString("HH:mm:ss.fff"),
                            injectedBackspaces, injectedChars + injectedBreaks - injectedBackspaces));
                        // 逐词断言（第三十一轮升级为"覆盖 + 前缀"两条判据，判据本身抽成 CheckSliceLanding 供宿主取证）：
                        //   ① 落屏模型**至少**覆盖到本段的末尾（本段的字必须都落屏，不能落后）；
                        //   ② 落屏模型必须是**原文的前缀**（多落了不该落的字就不再是前缀——真机 bug：'抖动收紧收紧'）。
                        // 跨块的错字演出会让本片**合法**地多落下一个块的字（那是正确前缀），因此不再误报；
                        // "下一个词的错字字符被提前打进本片"会让落屏不再是前缀，照样报错（并附上净字数）。
                        try
                        {
                            int blockEndForSlice = 0;
                            for (int b = 0; b <= slice.LastBlock && b < blocks.Count; b++) blockEndForSlice += PhraseWorkflow.NormalizeText(blocks[b].Text).Length;
                            List<string> landingErrors = CheckSliceLanding(text, blocks[slice.FirstBlock].Text, slice.FirstBlock + 1,
                                blockEndForSlice, committed.ToString(), injectedChars, injectedBreaks, injectedBackspaces);
                            for (int e = 0; e < landingErrors.Count; e++) Log.Error(landingErrors[e]);
                        }
                        catch (Exception ex) { Log.Error("逐词断言检查失败", ex); }
                        if (stepIndex < slice.StepEnd)
                        {
                            // 防御性断言：正常情况下不可能发生（切片是预计算且单调的）
                            Log.Error(string.Format("缓冲注入未完成：第 {0}~{1} 段仍剩步骤 {2}~{3} 未注入（将导致落字滞后）",
                                slice.FirstBlock + 1, slice.LastBlock + 1, stepIndex, slice.StepEnd - 1));
                        }
                        // 缓冲收尾：注入完成即隐藏悬浮条（与真输入法确认后收起候选窗一致，不留上一句）
                        SetBar(new BarStatus { Visible = false });
                        Log.Info(string.Format("缓冲收尾：第 {0}~{1} 段注入完成，悬浮条已收起", slice.FirstBlock + 1, slice.LastBlock + 1));
                        SetState(AppState.Running);
                        if (failReason != null || _stopRequested) break;
                    }
                    if (stepIndex < total && failReason == null && !_stopRequested)
                    {
                        Log.Error(string.Format("计划未走完：stepIndex={0}/{1}（可能有未分配的尾部步骤）", stepIndex, total));
                    }
                    SetBar(new BarStatus { Visible = false });   // 问题5：运行结束一律隐藏，不留残留帧
                }
                finally
                {
                    if (clipboardUsed) injector.RestoreClipboard();
                }

                Log.Info(injector.DescribeCounters() + "（默认模式字母必须为 0）");
                Log.Info(WordIndex.QueryStats());
                Log.Info(string.Format("选词→落屏 间隔分布：样本={0} 最大={1}ms 超标(>150ms)={2}｜无选词块（标点/英文/换行）不计入该指标",
                    _gapSamples, _gapMaxMs, _gapOver150));
                if (failReason != null) FinishRun(failReason);
                else if (_stopRequested) FinishRun(string.Format("已停止（已输入 {0}/{1} 步）", done, total));
                else FinishRun(string.Format("完成（共 {0} 步）", total));
            }
            catch (Exception ex)
            {
                Log.Error("运行循环异常", ex);
                FinishRun("运行异常，详见 error.log");
            }
        }

        // 运行期跟随光标：只用 Win32 路径、300ms 节流；取不到就保持上一次位置（绝不跳到 0,0），
        // 任何异常都只记日志，绝不中断输入。
        private void RefreshBarAnchor(AnchorInfo anchor)
        {
            try
            {
                if (anchor == null) return;
                if ((DateTime.UtcNow - _lastAnchorRefresh).TotalMilliseconds < 300) return;
                _lastAnchorRefresh = DateTime.UtcNow;

                Rectangle caret;
                if (AnchorService.TryRefreshCaret(anchor.Hwnd, out caret))
                {
                    ApplyRefreshedCaret(anchor, caret);
                }
                else
                {
                    _anchorMissCount++;
                    if (_anchorMissCount == 1 || (DateTime.UtcNow - _lastAnchorMissLog).TotalSeconds >= 5)
                    {
                        _lastAnchorMissLog = DateTime.UtcNow;
                        Log.Info(string.Format("运行中锚点刷新：取不到插入符（连续 {0} 次），保持上一次位置 ({1},{2})",
                            _anchorMissCount, _barAnchorPoint.X, _barAnchorPoint.Y));
                    }
                }

            }
            catch (Exception ex)
            {
                Log.Error("运行期锚点刷新失败（忽略，继续输入）", ex);
            }
        }

        // ---- 词组级组串：只有显示（与 PinyinInTarget=true 时的真实字母）发生在这里 ----

        // 组串阶段：把缓冲（1~3 块）的拼音逐字母显示在悬浮条里；
        // 默认 PinyinInTarget=false —— 字母**只进悬浮条**，目标窗口一个字母都收不到
        // 阶段 1：整个缓冲在悬浮框里逐段组串 + 逐段确认；**不向目标注入任何字符**
        private bool ConfirmBufferDisplay(PhraseState phrase, int first, int last, AnchorInfo anchor, Injector injector)
        {
            if (first < 0 || first >= phrase.Blocks.Count) return true;
            phrase.BufferFirst = first;
            phrase.BufferLast = last;
            Log.Info(string.Format("组串缓冲：第 {0}~{1} 段（共 {2} 段，上限 {3} 段/缓冲）｜拼音={4}",
                first + 1, last + 1, last - first + 1, _cfg.Inject.BufferMaxPhrases,
                PhraseWorkflow.RangePinyin(phrase, first, last)));
            for (int k = first; k <= last; k++)
            {
                if (_stopRequested) return false;
                PhraseBlock block = phrase.Blocks[k];
                if (!block.Chinese || block.Pinyin.Length == 0)
                {
                    phrase.ComposedUpTo = k;   // 标点/英文块不组串
                    continue;
                }
                if (!ComposeOneBlock(phrase, k, anchor, injector)) return false;
                ShowSelectionHighlight(phrase, k, anchor);   // 选词那一下：候选行高亮，仍不落字

                phrase.ComposedUpTo = k;
                Log.Info(string.Format("已确认第 {0} 段：'{1}'（只更新悬浮框，尚未注入目标）", k + 1, block.Text));
            }

            // 第二十一轮：**不再**显示"整串确认（纯汉字）"中间帧——真输入法在按下选词键之前不会把汉字放进组串行；
            // 这里只落一行日志，悬浮条保持为最后一个字的拼音 + 候选（随后由选词高亮帧接手续接提交）。
            StringBuilder whole = new StringBuilder();
            for (int i = first; i <= last; i++)
            {
                if (phrase.Blocks[i].Chinese) whole.Append(phrase.Blocks[i].Text);
            }
            Log.Info(string.Format("本缓冲拼音已敲完（悬浮条仍是拼音，不显示汉字）：'{0}'｜下一步 选词高亮→提交", whole));
            return true;
        }

        private bool ComposeOneBlock(PhraseState phrase, int k, AnchorInfo anchor, Injector injector)
        {
            PhraseBlock block = phrase.Blocks[k];
            if (!block.Chinese || block.Pinyin.Length == 0) return true;
            long t0Block = DateTime.Now.Ticks;      // 第三十三轮：整词组串的最小可见时长基准
            {
                int letters = block.Pinyin.Length;
                int slipPct = PhraseWorkflow.KeyboardSlipPct(_cfg);
                bool slip = slipPct > 0 && letters > 2 && _random.Next(100) < slipPct;
                string wrong = slip ? PhraseWorkflow.KeyboardSlip(block.Pinyin, _cfg) : null;
                bool slipUsable = slip && !string.IsNullOrEmpty(wrong) && wrong.Length == letters
                    && !string.Equals(wrong, block.Pinyin, StringComparison.Ordinal);
                int slipAt = -1;
                if (slipUsable)
                {
                    // A 的错拼可能改动任意位置（例如相邻键互换），定位第一个不同字母作为"敲错"的位置
                    for (int i = 0; i < letters; i++)
                    {
                        if (wrong[i] != block.Pinyin[i]) { slipAt = i; break; }
                    }
                    if (slipAt < 0) slipUsable = false;
                }

                string shown = "";
                int typed = 0;
                for (int pos = 0; pos < letters; pos++)
                {
                    if (_stopRequested) return false;
                    if (pos == slipAt)
                    {
                        // 键盘相邻键错拼：先把 A 给出的错拼串整串显示出来（显示层），短暂停留后自行改正
                        SetPhraseComposeBar(phrase, k, wrong, anchor);
                        Log.Info(string.Format("键盘错拼（{0}）：正确='{1}' → 短暂显示错拼='{2}'（第 {3} 个字母起不同）→ 随后改正",
                            _cfg.Inject.PinyinInTarget ? "真实敲错并退格改正" : "仅显示层",
                            block.Pinyin, wrong, slipAt + 1));
                        if (_cfg.Inject.PinyinInTarget)
                        {
                            injector.SendLetter(wrong[slipAt]);   // 真敲错的那个字母
                            typed++;
                            int wrongWait = _cfg.Inject.PinyinLetterMs * 2;
                            if (!SleepInterruptible(wrongWait)) return false;
                            injector.SendBackspace();             // 退格改正
                            typed--;
                            Log.Info("键盘错拼：已退格删除错字母，改用正确拼写继续");
                        }
                        else
                        {
                            int wait = _cfg.Inject.PinyinLetterMs * 2;
                            if (!SleepInterruptible(wait)) return false;
                        }
                    }
                    shown += block.Pinyin[pos];
                    SetPhraseComposeBar(phrase, k, shown, anchor);
                    if (_cfg.Inject.PinyinInTarget) { injector.SendLetter(block.Pinyin[pos]); typed++; }
                    if (!SleepInterruptible(_cfg.Inject.PinyinLetterMs)) return false;
                }
                // 第三十三轮（要求3）：整词组串的最小可见时长——短词（如 ni 只有 2 帧）也要让人看清
                int minBlk = _cfg.Inject.MinComposeVisibleMs;
                long visBlk = (DateTime.Now.Ticks - t0Block) / TimeSpan.TicksPerMillisecond;
                if (minBlk > 0 && visBlk < minBlk)
                {
                    if (!SleepInterruptible((int)(minBlk - visBlk))) return false;
                    visBlk = (DateTime.Now.Ticks - t0Block) / TimeSpan.TicksPerMillisecond;
                }
                Log.Info(string.Format("组串单位（逐字母）：单位='{0}'｜拼音='{1}'｜帧={2}×{3}ms｜可见={4}ms（最小要求 {5}ms）｜整词组串｜第{6}段｜Trail=''",
                    block.Text, block.Pinyin, block.Pinyin.Length, _cfg.Inject.PinyinLetterMs, visBlk, minBlk, k + 1));

                if (_cfg.Inject.PinyinInTarget && typed > 0)
                {
                    for (int b = 0; b < typed; b++) injector.SendBackspace();   // 整块拼音敲完 -> 退格清除
                    Log.Info(string.Format("拼音入目标：块 '{0}' 的 {1} 个字母已敲入并退格清除", block.Text, typed));
                }
            }
            return true;
        }

        // 组串中的悬浮条：已确认/缓冲内其它块灰，当前块蓝粗下划线（逐字母增长）
        // 生产构造函数（运行时与 --bartest 样例**共用同一个函数**，避免样例与真实外观不一致）
        internal static BarStatus BuildComposeStatus(string trailHan, string typedPrefix, string candidates)
        {
            BarStatus s = new BarStatus();
            s.Visible = true;
            s.Trail = trailHan;        // 已确认汉字（仅本缓冲、且尚未注入）
            s.Composing = typedPrefix; // 已敲出的字母
            s.Suffix = "";             // 绝不预显示未敲出的字母
            s.Candidates = candidates;
            return s;
        }

        private void SetPhraseComposeBar(PhraseState phrase, int currentBlock, string shownPinyin, AnchorInfo anchor)
        {
            if (_cfg.Behavior.BarVerbose)
            {
                BarStatus v = new BarStatus();
                v.Visible = true;
                v.Anchor = AnchorService.AnchorPoint(anchor);
                v.Composing = shownPinyin;
                v.Foot = "组串中（缓冲 " + (phrase.BufferLast - phrase.BufferFirst + 1) + " 块）";
                SetBar(v);
                return;
            }
            // 问题1：Trail 走 TrailFor —— 逐词模型下恒为空串，绝不显示灰色已确认汉字
            BarStatus s = BuildComposeStatus(TrailFor(ConfirmedHan(phrase, currentBlock, phrase.BufferFirst)), shownPinyin, "");
            s.Anchor = AnchorService.AnchorPoint(anchor);
            ApplyPrefixCandidates(s, phrase.Blocks[currentBlock], shownPinyin);  // 候选随已输入前缀变化
            SetBar(s);
        }

        // 已确认部分（缓冲内、当前块之前的块）→ 汉字
        private static string ConfirmedHan(PhraseState phrase, int currentBlock, int bufferFirst)
        {
            if (phrase == null || phrase.Blocks == null) return "";
            int from = bufferFirst >= 0 ? bufferFirst : 0;
            StringBuilder sb = new StringBuilder();
            for (int i = from; i < currentBlock && i < phrase.Blocks.Count; i++)
            {
                if (i < 0) continue;
                if (phrase.Blocks[i].Chinese) sb.Append(phrase.Blocks[i].Text);
            }
            return sb.ToString();
        }

        // 问题1（真机）：逐词模型（BufferMaxPhrases<=1）下悬浮条**永远不得**显示灰色已确认汉字——
        // 用户原话"已经输出完了怎么还在悬浮窗"。逐词模型里"已确认"就等于"已经/马上要落屏"，所以一律返回空串；
        // 多词缓冲（>1）保留原语义：缓冲内已确认、尚未注入的块仍然灰显。
        private string TrailFor(string confirmedHan)
        {
            if (_cfg.Inject.BufferMaxPhrases <= 1) return "";
            return confirmedHan == null ? "" : confirmedHan;
        }

        // 第三十二轮：这个字符是不是"错字连"的续写（前面同一个切片内、跳过 Sleep 后仍是不带 Reason 断点的错字 Char）？
        private static bool IsTypoRunContinuation(IList<Step> plan, int index, int floorIndex)
        {
            for (int b = index - 1; b >= floorIndex; b--)
            {
                Step prev = plan[b];
                if (prev == null) break;
                if (prev.Kind == StepKind.Sleep) continue;
                return prev.Kind == StepKind.Char && !string.IsNullOrEmpty(prev.Reason);
            }
            return false;
        }

        // 第三十二轮：把本切片的"落屏单位"登记进日志（逐字母组串 + 覆盖的计划下标）。
        // 独立成静态函数：运行时与取证宿主**共用同一段生产代码**（避免宿主另写一套日志格式）。
        internal static void LogComposeUnitsForSlice(IList<PhraseBlock> blocks, IList<Step> plan, int[] planBlocks,
            int blockFrom, int blockTo, int stepFrom, int stepTo, int total, int letterMs)
        {
            if (blocks == null || plan == null || planBlocks == null) return;
            for (int b = blockFrom; b <= blockTo && b < blocks.Count; b++)
            {
                if (b < 0) continue;
                PhraseBlock blk = blocks[b];
                if (!blk.Chinese || string.IsNullOrEmpty(blk.Text)) continue;
                List<int> cover = new List<int>();
                for (int k = stepFrom; k < stepTo && k < total && k < plan.Count; k++)
                {
                    if (plan[k] == null || plan[k].Kind != StepKind.Char) continue;
                    if (!string.IsNullOrEmpty(plan[k].Reason)) continue;      // 错字字符只由"错字组串"覆盖
                    if (k < planBlocks.Length && planBlocks[k] == b) cover.Add(k);
                }
                Log.Info(string.Format("组串单位（逐字母）：'{0}'｜拼音='{1}'｜帧={2}×{3}ms｜覆盖计划下标={4}｜第{5}段｜Trail=''",
                    blk.Text, blk.Pinyin, blk.Pinyin == null ? 0 : blk.Pinyin.Length, letterMs, JoinInts(cover), b + 1));
            }
        }

        private static string JoinInts(IList<int> list)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < list.Count; i++) { if (i > 0) sb.Append(','); sb.Append(list[i]); }
            return sb.ToString();
        }

        // 第三十二轮（用户核心观感 —— "悬浮窗里面没有错字直接输出错字"）：
        // 带 Reason 的错字单位必须在**落屏之前**先在悬浮条里逐字母组串 + 选词高亮，看起来就是"打错了拼音、选错了词"。
        // 重复输入型：单位 = 被重复的那个词（连的前半）；其它类型：单位 = 这一连的字本身。
        // 组串行/选词行都写日志，并带上"覆盖计划下标"，供对照检查脚本逐条核对（每个落屏字符都属于某个已组串单位）。
        // 第三十三/三十四轮：已组串单位的**覆盖下标表**（单位文本 → 覆盖的计划下标集合）。
        // 落屏时按下标核对：只要落屏的汉字属于某个已组串单位就算通过（与外部核对工具同一口径）。
        private readonly List<KeyValuePair<string, HashSet<int>>> _composedCover = new List<KeyValuePair<string, HashSet<int>>>();
        private string _lastComposedUnit = null;

        private void EnqueueComposedUnit(string unit) { RegisterComposedUnit(unit, null); }

        // 第三十六轮断言：候选行必须与当前组串单位自洽 —— 候选首项的拼音不得超出当前单位（如单位='处'/chu 却出现 '一处'/yichu）
        private void AssertCandidatesMatchUnit(string unit, string py, BarStatus frame, string where)
        {
            try
            {
                if (frame == null || string.IsNullOrEmpty(unit)) return;
                string cands = frame.Candidates;
                if (string.IsNullOrEmpty(cands)) return;
                int dot = cands.IndexOf('.');
                if (dot < 0) return;
                int end = cands.IndexOf(' ', dot);
                string first = end > 0 ? cands.Substring(dot + 1, end - dot - 1) : cands.Substring(dot + 1);
                if (string.IsNullOrEmpty(first)) return;
                StringBuilder cp = new StringBuilder();
                for (int i = 0; i < first.Length; i++) cp.Append(SafePinyin(first[i]));
                if (!string.Equals(cp.ToString(), py, StringComparison.Ordinal) || first.Length > unit.Length)
                {
                    Log.Error(string.Format("候选行断言破坏：组串单位='{0}'｜拼音='{1}'｜候选首项='{2}'（超出当前单位）", unit, py, first));
                }
            }
            catch (Exception ex) { Log.Error("候选行断言检查失败（不影响落屏）", ex); }
        }

        private void RegisterComposedUnit(string unit, IList<int> coveredIdx)
        {
            if (string.IsNullOrEmpty(unit)) return;
            HashSet<int> set = new HashSet<int>();
            if (coveredIdx != null) for (int i = 0; i < coveredIdx.Count; i++) set.Add(coveredIdx[i]);
            // 第三十五轮断言：同一个计划下标不得被组串两次（否则落屏前会重播一遍动画）
            for (int i = 0; i < _composedCover.Count; i++)
            {
                foreach (int k in set)
                {
                    if (_composedCover[i].Value.Contains(k))
                    {
                        Log.Error(string.Format("重复组串断言破坏：计划下标={0} 已被单位 '{1}' 覆盖，又被单位 '{2}' 覆盖",
                            k, _composedCover[i].Key, unit));
                    }
                }
            }
            _composedCover.Add(new KeyValuePair<string, HashSet<int>>(unit, set));
            _lastComposedUnit = unit;
        }

        // 第三十五轮：每次运行开始都要清空"已组串覆盖表"（否则上一轮的下标会与新计划的下标撞车 → 误报重复组串/漏组串）
        internal void ResetComposedCoverage()
        {
            _composedCover.Clear();
            _lastComposedUnit = null;
        }

        internal bool IsCoveredByComposedUnit(int stepIndex)
        {
            for (int i = _composedCover.Count - 1; i >= 0; i--)
            {
                if (_composedCover[i].Value.Contains(stepIndex)) return true;
            }
            return false;
        }

        // 要求4的断言：落屏字符必须属于"最近组串的那个单位"，否则 ERROR（明确写出下标/落屏字/最近单位）
        private void ConsumeComposedUnit(string ch, int stepIndex)
        {
            if (string.IsNullOrEmpty(ch)) return;
            // 第三十四轮：标点/符号/数字/字母在真输入法里**直接上屏、不经组串与候选** → 断言只对汉字生效
            if (!Injector.IsChineseChar(ch))
            {
                Log.Info(string.Format("标点直接上屏（不经组串）：'{0}' 计划下标={1}", ch, stepIndex));
                return;
            }
            if (IsCoveredByComposedUnit(stepIndex)) return;
            Log.Error(string.Format("组串单位断言破坏：计划下标={0} 落屏 '{1}' 但最近组串的单位是 '{2}'", stepIndex, ch,
                _lastComposedUnit == null ? "(无)" : _lastComposedUnit));
        }

        // 第三十三轮：组串一个单位并高亮它（逐字母 → 最小可见时长 → 高亮该单位 → 入队等待落屏）。
        // 运行时所有"要落屏的单位"都走这里，保证"组串单位 == 下一个落屏单位"。
        private bool ComposeUnitVisible(string unitText, IList<int> coveredIdx, int blockNo, AnchorInfo anchor, string logTag, string highlightTag)
        {
            try
            {
                if (string.IsNullOrEmpty(unitText)) return true;
                StringBuilder py = new StringBuilder();
                for (int i = 0; i < unitText.Length; i++)
                {
                    string one = SafePinyin(unitText[i]);
                    if (one.Length == 0) { Log.Info(logTag + "跳过：拿不到拼音，不演（绝不显示假拼音）"); return true; }
                    py.Append(one);
                }
                PhraseBlock unitBlock = new PhraseBlock();
                unitBlock.Text = unitText;
                unitBlock.Pinyin = py.ToString();
                unitBlock.Chinese = true;
                unitBlock.Start = 0;
                long t0 = DateTime.Now.Ticks;
                for (int i = 0; i < py.Length; i++)
                {
                    if (_stopRequested) return false;
                    BarStatus s = new BarStatus();
                    s.Visible = true;
                    s.Anchor = AnchorService.AnchorPoint(anchor);
                    s.Trail = "";                                        // 逐词模型：Trail 恒空
                    s.Composing = py.ToString(0, i + 1);                  // 逐字母长出
                    s.Suffix = "";
                    ApplyPrefixCandidates(s, unitBlock, py.ToString(0, i + 1));
                    SetBar(s);
                    if (!SleepInterruptible(_cfg.Inject.PinyinLetterMs)) return false;
                }
                // 最小可见时长（要求3）：短单位（yi 只有 2 帧）也要让人看清"从 y 到 yi"，不够就停在最后一帧
                int minMs = _cfg.Inject.MinComposeVisibleMs;
                long visible = (DateTime.Now.Ticks - t0) / TimeSpan.TicksPerMillisecond;
                if (minMs > 0 && visible < minMs)
                {
                    if (!SleepInterruptible((int)(minMs - visible))) return false;
                    visible = (DateTime.Now.Ticks - t0) / TimeSpan.TicksPerMillisecond;
                }
                Log.Info(string.Format("{0}：单位='{1}'｜拼音='{2}'｜帧={3}×{4}ms｜可见={5}ms（最小要求 {6}ms）｜覆盖计划下标={7}｜第{8}段｜Trail=''",
                    logTag, unitText, py, py.Length, _cfg.Inject.PinyinLetterMs, visible, minMs,
                    JoinInts(coveredIdx == null ? new List<int>() : coveredIdx), blockNo));
                RegisterComposedUnit(unitText, coveredIdx);
                int ms = _cfg.Inject.SelectHighlightMs;
                if (ms > 0)
                {
                    BarStatus h = new BarStatus();
                    h.Visible = true;
                    h.Anchor = AnchorService.AnchorPoint(anchor);
                    h.Trail = "";
                    h.Composing = py.ToString();
                    ApplyCandidates(h, unitBlock, true);                  // 候选行第 1 个 = 这个单位（浅蓝底加粗）
                    SetBar(h);
                    Log.Info(string.Format("{0}：'{1}'（候选行第 1 个）高亮 {2}ms 后落屏｜候选行='{3}'", highlightTag, unitText, ms, h.Candidates));
                    AssertCandidatesMatchUnit(unitText, py.ToString(), h, highlightTag);
                    if (!SleepInterruptible(ms)) return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("组串单位失败（不影响落屏，继续注入）", ex);
                return true;
            }
        }

        // 要求1：落屏前保证"下一个落屏的单位"已经组串过；不够就按计划现补一个单位（组串 → 高亮 → 再由调用方落屏）
        // 第三十五轮：把本切片"整词组串"登记进已组串覆盖表（每条组串路径都必须登记，否则落屏前会重播一遍动画）
        private void RegisterBlockCoverage(IList<PhraseBlock> blocks, IList<Step> plan, int[] planBlocks,
            int blockFrom, int blockTo, int stepFrom, int stepTo, int total)
        {
            for (int b = blockFrom; b <= blockTo && b < blocks.Count; b++)
            {
                if (b < 0) continue;
                PhraseBlock blk = blocks[b];
                if (!blk.Chinese || string.IsNullOrEmpty(blk.Text)) continue;
                List<int> cover = new List<int>();
                for (int k = stepFrom; k < stepTo && k < total && k < plan.Count; k++)
                {
                    if (plan[k] == null || plan[k].Kind != StepKind.Char) continue;
                    if (!string.IsNullOrEmpty(plan[k].Reason)) continue;      // 错字字符由"错字组串"覆盖
                    if (k < planBlocks.Length && planBlocks[k] == b) cover.Add(k);
                }
                RegisterComposedUnit(blk.Text, cover);
            }
        }

        private bool EnsureUnitComposed(IList<Step> plan, int index, int sliceEnd, int total, int[] planBlocks, AnchorInfo anchor)
        {
            try
            {
                Step step = plan[index];
                if (step == null || step.Kind != StepKind.Char || string.IsNullOrEmpty(step.Text)) return true;
                if (IsCoveredByComposedUnit(index)) return true;           // 这个下标已被某个已组串单位覆盖
                if (!Injector.IsChineseChar(step.Text)) return true;       // 标点/字母直接上屏，不组串
                List<int> idx = new List<int>();
                StringBuilder unit = new StringBuilder();
                for (int k = index; k < sliceEnd && k < total; k++)
                {
                    Step s = plan[k];
                    if (s == null || s.Kind != StepKind.Char || string.IsNullOrEmpty(s.Text)) break;
                    if (!string.IsNullOrEmpty(s.Reason)) break;             // 错字字符另有"错字组串"，另起单位
                    // 第三十四轮：标点/符号/数字/字母直接上屏、不经组串 → 它们既不是单位、也不能混进单位（否则整单位会被跳过）
                    if (!Injector.IsChineseChar(s.Text)) break;
                    if (IsCoveredByComposedUnit(k)) break;                  // 第三十五轮：已被组串过的下标不重复收录（禁止重复组串）
                    unit.Append(PhraseWorkflow.NormalizeText(s.Text));
                    idx.Add(k);
                    if (unit.Length >= 2) break;                           // 单位最多 2 个字
                }
                if (unit.Length == 0) return true;
                int blockNo = idx.Count > 0 && planBlocks != null && idx[0] < planBlocks.Length ? planBlocks[idx[0]] + 1 : 0;
                return ComposeUnitVisible(unit.ToString(), idx, blockNo, anchor, "组串单位（逐字母）", "选词高亮（单位）");
            }
            catch (Exception ex)
            {
                Log.Error("补组串失败（不影响落屏）", ex);
                return true;
            }
        }

        private bool ComposeTypoRunBeforeCommit(PhraseState phrase, IList<Step> plan, int start, int sliceEnd, int total, int[] planBlocks, AnchorInfo anchor)
        {
            try
            {
                List<int> idx = new List<int>();
                StringBuilder unit = new StringBuilder();
                int end = Math.Min(sliceEnd, total);
                for (int k = start; k < end; k++)
                {
                    Step s = plan[k];
                    if (s == null) break;
                    if (s.Kind == StepKind.Sleep) continue;                 // 停顿不算断点（与归属规则一致）
                    if (s.Kind != StepKind.Char || string.IsNullOrEmpty(s.Reason)) break;
                    idx.Add(k);
                    unit.Append(PhraseWorkflow.NormalizeText(s.Text));
                }
                if (idx.Count == 0) return true;
                string unitText = unit.ToString();
                int wordLen = PhraseWorkflow.RepeatRunWordLen(plan, start);   // 重复输入型 → 单位 = 被重复的词
                if (wordLen > 0 && wordLen <= unitText.Length) unitText = unitText.Substring(0, wordLen);
                int blockNo = start >= 0 && start < planBlocks.Length ? planBlocks[start] + 1 : 0;
                // 第三十三轮：统一走 ComposeUnitVisible（逐字母 → 最小可见时长 → 高亮**这个单位** → 入队等待落屏）
                if (!ComposeUnitVisible(unitText, idx, blockNo, anchor, "错字组串（逐字母）", "选词高亮（错字演出）")) return false;
                // 重复输入：连里其余的字也是同一个单位打的（补进等待落屏队列，保证"组串单位 == 落屏单位"逐字成立）
                // 第三十五轮：ComposeUnitVisible 已经按**整连的覆盖下标**登记过了，这里不再重复登记（否则会触发重复组串断言）
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("错字组串失败（不影响落屏，继续注入）", ex);
                return true;
            }
        }

        // 第三十一轮：切片落屏断言（宿主可直接调用取证）。返回空列表 = 通过；否则每条都是要写进日志的 ERROR 文案。
        //   ① 覆盖：落屏长度必须 >= 本段（含缓冲内多块）末尾的字符位置 —— 本段的字不能落后；
        //   ② 前缀：落屏必须是原文前缀 —— 多落了本片不该落的字（真机 bug：'抖动收紧收紧'）就不再是前缀；
        //   ③ 两条都不过时再摊开字数：净落屏（Char + 换行 - 退格）vs 该段字数。
        // 用"净落屏"而不是"注入的 Char 步数"：错字演出在块内**合法**地多打 k 个字再退格 k 次
        // （重复输入 = 4 个字 + 2 个退格，净 2 字 = '抖动'），按步数算会误报。
        internal static List<string> CheckSliceLanding(string fullText, string blockText, int blockNo, int blockEnd,
            string screen, int injectedChars, int injectedBreaks, int injectedBackspaces)
        {
            List<string> errors = new List<string>();
            string want = PhraseWorkflow.NormalizeText(fullText);
            string bt = PhraseWorkflow.NormalizeText(blockText);
            string s = screen == null ? "" : screen;
            bool covered = s.Length >= blockEnd;
            bool prefixOk = s.Length <= want.Length && string.Equals(want.Substring(0, s.Length), s, StringComparison.Ordinal);
            if (covered && prefixOk) return errors;
            if (!covered)
            {
                errors.Add(string.Format("逐词断言破坏：第 {0} 段（'{1}'）注入完成后落屏只有 {2} 字（少于本段末尾位置 {3}）——本段的字没落完",
                    blockNo, bt, s.Length, blockEnd));
            }
            else
            {
                errors.Add(string.Format("逐词断言破坏：第 {0} 段（'{1}'）注入完成后落屏='{2}'（{3} 字）不是原文前缀（此处应为 '{4}'）",
                    blockNo, bt, s, s.Length, want.Substring(0, Math.Min(s.Length, want.Length))));
            }
            int net = injectedChars + injectedBreaks - injectedBackspaces;
            if (net != bt.Length)
            {
                errors.Add(string.Format("切片字数断言破坏：第 {0} 段期望 {1} 字，实际注入净 {2} 字（Char {3} + 换行 {4} - 退格 {5}）",
                    blockNo, bt.Length, net, injectedChars, injectedBreaks, injectedBackspaces));
            }
            return errors;
        }

        // 问题3（真机）：词提交（写入目标）的瞬间，悬浮条必须已经收起/清空——绝不允许"字已落屏、
        // 条上还挂着上一个词的组串/选词帧"并存（用户帧：记事本已到"咆"，条上还显示着 xianzai / 1.现在）。
        // 所有注入路径都在写入目标之前调它；调用后立即断言：Visible=false 且 Composing/Trail 为空。
        private void HideBarBeforeCommit(string where, int stepIndex)
        {
            BarStatus prev = _barStatus;
            bool prevVisible = prev != null && (prev.Visible || !string.IsNullOrEmpty(prev.Composing)
                || !string.IsNullOrEmpty(prev.Trail) || !string.IsNullOrEmpty(prev.Candidates));
            SetBar(new BarStatus { Visible = false });   // Trail/Composing/Suffix/Candidates/Foot 都是空串（BarStatus 默认值）
            BarStatus now = _barStatus;
            if (now == null || now.Visible || !string.IsNullOrEmpty(now.Composing) || !string.IsNullOrEmpty(now.Trail))
            {
                Log.Error(string.Format("提交前悬浮条断言破坏：{0}（计划下标={1}）写入目标前 _barStatus.Visible={2} Composing='{3}' Trail='{4}'",
                    where, stepIndex, now == null ? "null" : now.Visible.ToString(),
                    now == null ? "" : now.Composing, now == null ? "" : now.Trail));
                return;
            }
            if (prevVisible)
            {
                Log.Info(string.Format("提交前收起：{0}（计划下标={1}）写入目标之前收起上一帧（Composing='{2}' Trail='{3}' Candidates='{4}'）——不与落屏文本并存",
                    where, stepIndex, prev == null ? "" : prev.Composing, prev == null ? "" : prev.Trail, prev == null ? "" : prev.Candidates));
            }
        }

        // 候选随"已输入前缀"变化：y → 一/有/也…；yi → 一/以/已…；yig → 一个（前缀命中，不完整也能命中）
        private void ApplyPrefixCandidates(BarStatus status, PhraseBlock block, string typedPrefix)
        {
            if (status == null || block == null || !block.Chinese) return;
            if (string.IsNullOrEmpty(typedPrefix))
            {
                status.Candidates = "";     // 还没敲字母：候选行留空（口径：不剧透后面的候选）
                return;
            }
            try
            {
                string note;
                List<WordIndex.Entry> words = WordIndex.LookupPrefix(typedPrefix, block.Text, _cfg.Inject.CandidateMax, out note);
                if (note != null && _degradeLogged.Add("P:" + typedPrefix)) Log.Info("前缀候选：" + note);
                status.Candidates = WordIndex.FormatCandidates(words);
            }
            catch (Exception ex)
            {
                Log.Error("前缀候选查询失败", ex);
            }
        }

        // 候选行：先按**词组**查（目标词排第一），词表里没有同音词时退化为同音字候选并记日志
        private void ApplyCandidates(BarStatus status, PhraseBlock block, bool markSelected)
        {
            if (status == null || block == null || !block.Chinese || block.Text.Length == 0) return;
            try
            {
                string note;
                List<WordIndex.Entry> words = WordIndex.Lookup(block.Pinyin, block.Text, _cfg.Inject.CandidateMax, out note);
                if (note != null || words.Count == 0)
                {
                    string head = block.Text.Substring(0, 1);
                    if (_degradeLogged.Add(block.Pinyin))
                    {
                        Log.Info("候选退化：" + (note == null ? ("拼音段 '" + block.Pinyin + "' 无可选词") : note)
                            + "；本段改用同音字候选（第一个字 " + head + "）");
                    }
                    status.Candidates = BuildCandidates(head, _cfg.Inject.CandidateMax);
                    status.Highlight = head;
                    if (markSelected) status.SelectedCandidate = head;
                    return;
                }
                status.Candidates = WordIndex.FormatCandidates(words);
                if (markSelected) status.SelectedCandidate = block.Text;   // 选词那一下：浅蓝底加粗
            }
            catch (Exception ex)
            {
                Log.Error("生成词组候选失败（退化为同音字）", ex);
                string head = block.Text.Substring(0, 1);
                status.Candidates = BuildCandidates(head, _cfg.Inject.CandidateMax);
                status.Highlight = head;
            }
        }

        private readonly HashSet<string> _degradeLogged = new HashSet<string>(StringComparer.Ordinal);
        private DateTime _lastSelectHighlightEnd = DateTime.MinValue;   // 选词高亮结束时刻（用于量"高亮→首字"间隔）
        private DateTime _injectFirstCharAt = DateTime.MinValue;
        private DateTime _injectFirstActionAt = DateTime.MinValue;
        private int _preCharNonCharSteps;
        private long _preCharNonCharMs;
        private bool _sliceHadHighlight;              // 本缓冲内是否发生过选词高亮（指标适用范围）
        private DateTime _sliceHighlightEnd = DateTime.MinValue;
        private long _headPauseMsInSlice;
        private int _gapSamples;                      // 有选词的缓冲样本数
        private long _gapMaxMs;
        private int _gapOver150;

        // "选词"看得见：把候选行里被选中的那个词高亮 150~300ms，再把它并入已确认部分
        private void ShowSelectionHighlight(PhraseState phrase, int blockIndex, AnchorInfo anchor)
        {
            int ms = _cfg.Inject.SelectHighlightMs;
            if (ms <= 0 || blockIndex < 0 || blockIndex >= phrase.Blocks.Count) return;
            PhraseBlock block = phrase.Blocks[blockIndex];
            if (!block.Chinese || block.Text.Length == 0) return;

            BarStatus s = new BarStatus();
            s.Visible = true;
            s.Anchor = AnchorService.AnchorPoint(anchor);
            s.Trail = TrailFor(ConfirmedHan(phrase, blockIndex, phrase.BufferFirst));   // 问题1：逐词模型下为空串
            s.Composing = block.Pinyin;
            s.Suffix = "";   // 第十六轮修正：高亮帧也不得显示本缓冲后续段的未输入拼音（用户抱怨的残留场景）
            ApplyCandidates(s, block, true);
            if (_cfg.Behavior.BarVerbose) s.Foot = "选词：" + block.Text;
            SetBar(s);
            Log.Info(string.Format("选词高亮：'{0}'（候选行第 1 个）高亮 {1}ms 后并入已确认部分", block.Text, ms));
            SleepInterruptible(ms);
            _lastSelectHighlightEnd = DateTime.Now;   // 高亮结束时刻
            _sliceHadHighlight = true;                // 本缓冲确实发生过选词（指标只对这类缓冲有意义）
            _sliceHighlightEnd = _lastSelectHighlightEnd;
        }

        private static string BufferPinyin(PhraseState phrase, int from, int to)
        {
            return PhraseWorkflow.RangePinyin(phrase, from, to);
        }

        // ⚠ 已废弃（第十六轮）：全文件 0 个调用点——注入阶段按问题6-A 一律收起悬浮条，只有"逐字删除帧/重打帧"会临时显示。
        //   保留仅为兼容旧调用签名；**不要再改这里的显示逻辑，改了不会生效**。要改注入期显示请改注入循环里的删除帧与 ShowRetypeUnit。
        private void ShowPhraseBar(PhraseState phrase, Step step, AnchorInfo anchor, int done, int total, bool fallbackUsed)
        {
            bool verbose = _cfg.Behavior.BarVerbose;
            if (phrase.Blocks.Count == 0) { SetBar(new BarStatus { Visible = false }); return; }

            if (step.Kind == StepKind.Sleep || step.Kind == StepKind.LineBreak)
            {
                if (!verbose) { SetBar(new BarStatus { Visible = false }); return; }
                BarStatus vs = new BarStatus();
                vs.Visible = true;
                vs.Anchor = AnchorService.AnchorPoint(anchor);
                vs.Composing = step.Kind == StepKind.LineBreak ? "换行" : ("停顿 " + step.DelayMs + "ms");
                vs.Progress = string.Format("{0}/{1}", done, total);
                SetBar(vs);
                return;
            }

            int idx = phrase.BlockIndexAt(phrase.OutputPos);
            if (idx < 0) { SetBar(new BarStatus { Visible = false }); return; }
            PhraseBlock cur = phrase.Blocks[idx];

            BarStatus s = new BarStatus();
            s.Visible = true;
            s.Anchor = AnchorService.AnchorPoint(anchor);
            if (verbose)
            {
                s.Progress = string.Format("{0}/{1}", done, total);
                s.Foot = (step.Reason ?? "") + (fallbackUsed ? " · 剪贴板兜底" : "");
            }

            if (!cur.Chinese)
            {
                // 标点/英文/换行块：默认不占悬浮条（真输入法在中文态下这些直接上屏）
                if (!verbose) { SetBar(new BarStatus { Visible = false }); return; }
                s.Composing = cur.Text;
                SetBar(s);
                return;
            }

            int first = phrase.BufferFirst >= 0 ? phrase.BufferFirst : Math.Max(0, idx - 2);
            int last = phrase.BufferLast >= 0 ? phrase.BufferLast : idx;
            s.Trail = TrailFor(ConfirmedHan(phrase, idx, first));   // 问题1：逐词模型下为空串（已确认=汉字的老语义只在多词缓冲保留）
            s.Composing = cur.Pinyin;
            s.Suffix = PhraseWorkflow.RangePinyin(phrase, idx + 1, last);
            ApplyCandidates(s, cur, false);
            SetBar(s);
        }

        // 汉字块内确认要快（CommitCharMs）；但**标点/符号/emoji/ASCII/数字也是 Char 步**，
        // 它们的引擎延时里带着额外停顿（TypingPlanner：字延时 + PunctuationExtra 80~400ms），必须保留。
        // ASCII 字母/数字不算"标点/符号"（不给它们加前置停顿）
        private static bool IsLetterOrDigit(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
        }

        // 那一刻焦点窗口的标题与类名（取证用：证明字符发给了谁）
        private static string DescribeTargetWindow()
        {
            try
            {
                IntPtr hwnd = Native.GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return "(无前台窗口)";
                StringBuilder cls = new StringBuilder(256);
                try { Native.GetClassNameW(hwnd, cls, cls.Capacity); } catch { }
                string title = null;
                try { title = Native.ReadWindowText(hwnd, 200); } catch { }
                return string.Format("标题='{0}' 类='{1}' hwnd=0x{2:X}", title == null ? "(读不到)" : title, cls.ToString(), hwnd.ToInt64());
            }
            catch (Exception ex)
            {
                return "(取目标窗口失败：" + ex.GetType().Name + ")";
            }
        }

        // 是否还有**未执行**的文本步（从 from 到 to 之间；用于串行断言）
        private static bool HasTextStepBefore(IList<Step> plan, int from, int to)
        {
            for (int i = to; i < from && i < plan.Count; i++)
            {
                StepKind kind = plan[i].Kind;
                if (kind == StepKind.Char || kind == StepKind.Backspace || kind == StepKind.LineBreak) return true;
            }
            return false;
        }

        private static bool HasTextStepAfter(IList<Step> plan, int from, int to)
        {
            for (int i = from; i < to && i < plan.Count; i++)
            {
                StepKind kind = plan[i].Kind;
                if (kind == StepKind.Char || kind == StepKind.Backspace || kind == StepKind.LineBreak) return true;
            }
            return false;
        }

        // 纠错演出·删除帧：Trail 必须是**空串** —— 纠错期间不得显示"灰色已确认汉字"（用户明确要求：
        // 真机截图里出现过"多/抖动"灰字 + 蓝色重打拼音，那是不该出现的字）。
        // 独立成静态构造函数：删除帧的构造内联在注入循环体内，取证宿主无法直接执行，这里让宿主能跑同一段生产代码。
        internal static BarStatus BuildErrorRepairDeleteFrame(AnchorInfo anchor, int deleteLeft)
        {
            BarStatus s = new BarStatus();
            s.Visible = true;
            s.Anchor = AnchorService.AnchorPoint(anchor);
            s.Trail = "";                              // 纠错演出期间 Trail 一律为空串（不得显示灰色已确认汉字）
            s.Composing = "";
            s.Foot = "删除中：剩 " + deleteLeft + " 字";
            return s;
        }

        // 问题2（真机）：纠错演出的两个阶段与各自语义（顺序固定：删掉错字 → 逐字母重打 → 选词 → 输出）：
        //   删除阶段（退格步）：默认模式**不可见**；排障模式（BarVerbose=true）才显示"删除中：剩 N 字"帧（Trail 空串）。
        //   重打字母阶段（本函数）：默认模式**也可见**，Visible=true；Composing 逐字母长出且**只含被重打单位**的拼音
        //     （单位='抖' → 只显示 dou，不是整词的 doudong）；Trail 一律为空串；候选行照旧跟随当前前缀。
        // 本函数在那一串退格**全部注入完**之后才被调用（见 RetypeAfterDelete），所以字母长出来时错字已经不在目标里了。
        private bool RetypeAfterDelete(PhraseState phrase, AnchorInfo anchor, IList<Step> plan, int backspaceFrom, int units, string reason, int blockOfRun)
        {
            int mid = _cfg.Inject.AfterDeletePauseMs;      // 改错节奏(2)：删完再停顿，然后逐字母重打
            if (mid > 0)
            {
                Log.Info(string.Format("改错停顿：删除完成后等 {0}ms（AfterDeletePauseMs）再逐字母重打", mid));
                if (!SleepInterruptible(mid)) return false;
            }
            string retypeNote;
            if (!ShowRetypeUnit(phrase, anchor, plan, backspaceFrom, units, reason, blockOfRun, out retypeNote)) return false;
            int after = _cfg.Inject.AfterRetypePauseMs;    // 改错节奏(3)：重打完成后停顿，再走"选词高亮 → 正确字落屏"
            if (after > 0)
            {
                Log.Info(string.Format("改错停顿：重打完成后等 {0}ms 再选词落屏", after));
                if (!SleepInterruptible(after)) return false;
            }
            // 第三十三轮：重打的高亮已经在 ShowRetypeUnit 里针对**被重打单位**做了（不再整词高亮）
            return true;
        }

        // 错字重打演出（重打字母阶段）：把"即将被重打的单位"在悬浮条里**逐字母**重现。
        // 单位与拼音都取自**真实计划步**（后续真会落下的那些字），所以显示不说谎；目标窗口不受任何影响。
        private bool ShowRetypeUnit(PhraseState phrase, AnchorInfo anchor, IList<Step> plan, int backspaceAt, int units, string reason, int blockOfRun, out string note)
        {
            note = "";
            try
            {
                if (units <= 0) return true;
                // 第三十一轮：块号取**计划步归属表**（与切片划分同源），不再用会漂移的回放位置
                int[] planBlocks = PhraseWorkflow.AssignStepsToBlocks(plan, phrase);
                int blockIndex0 = blockOfRun >= 0 && blockOfRun < phrase.Blocks.Count
                    ? blockOfRun : phrase.BlockIndexAt(Math.Max(0, phrase.OutputPos - units));
                StringBuilder unit = new StringBuilder();
                List<int> covered = new List<int>();      // 第三十四轮：重打路径也要报『覆盖计划下标=』（与其它三条路径一致）
                int picked = 0;
                for (int i = backspaceAt + units; i < plan.Count && picked < units; i++)
                {
                    Step s = plan[i];
                    if (s.Kind != StepKind.Char || string.IsNullOrEmpty(s.Text)) break;
                    unit.Append(s.Text);
                    covered.Add(i);
                    picked++;
                }
                // 第三十五轮（用户录屏：同一个单位的拼音被播两遍）：若这一串要重打的字**已经组串过**（重复输入型演出：
                // 多打的字就是被重复的那个词，已经由"错字组串"演过一遍），就不再重播第二遍动画。
                {
                    bool allCovered = covered.Count > 0;
                    for (int i = 0; i < covered.Count; i++) { if (!IsCoveredByComposedUnit(covered[i])) { allCovered = false; break; } }
                    if (allCovered)
                    {
                        Log.Info(string.Format("重打演出跳过：单位='{0}' 的下标 {1} 已组串过（不重播第二遍动画）", unit, JoinInts(covered)));
                        note = "重打'" + unit + "'（已组串，跳过重播）";
                        return true;
                    }
                }

                // 第三十一轮：重打单位本来取"退格串**后面**紧跟的正确字"（同音字/鼻音/漏字回填/句首重打都对）。
                // 但『重复输入』的结构是"整个词以错字形式打两遍 + 等量退格"，退格串后面跟的是**下一个词**的字，
                // 于是重打演出会念出下一个词的拼音（真机日志：块42 单位='那项'/naxiang，而这一串退格删的是'收紧'）。
                // 判据：退格串后面的字若不在本块（归属表给出），就改用退格串**前面**的 units 个字（=被重复的那个词）。
                int afterIndex = backspaceAt + units;
                int afterBlock = afterIndex >= 0 && afterIndex < planBlocks.Length ? planBlocks[afterIndex] : -1;
                if (afterBlock >= 0 && afterBlock != blockIndex0)
                {
                    StringBuilder before = new StringBuilder();
                    for (int i = backspaceAt - units; i < backspaceAt && i >= 0; i++)
                    {
                        Step s = plan[i];
                        if (s.Kind != StepKind.Char || string.IsNullOrEmpty(s.Text)) { before.Length = 0; break; }
                        before.Append(s.Text);
                    }
                    if (before.Length > 0)
                    {
                        unit.Length = 0;
                        unit.Append(before.ToString());
                        covered.Clear();
                        for (int i = backspaceAt - units; i < backspaceAt && i >= 0; i++) covered.Add(i);
                        Log.Info(string.Format("重打单位改用退格串前面的字：退格串后面的字属于第 {0} 段（不是这一串要重打的），改用第 {1} 段的字",
                            afterBlock + 1, blockIndex0 + 1));
                    }
                }
                if (unit.Length == 0) return true;      // 拿不到真值就不演，绝不给假拼音
                StringBuilder py = new StringBuilder();
                for (int i = 0; i < unit.Length; i++)
                {
                    string one = SafePinyin(unit[i]);
                    if (one.Length == 0) return true;   // 不知道拼音就不演
                    py.Append(one);
                }
                // 第三十一轮：块号取计划步归属表（调用方算好传入，见上面 blockIndex0），不再用会漂移的回放位置——
                // 这样"改错删除：第 N 段"与"重打演出：块N"的段号一致（真机日志里曾出现 43/42 不一致）。
                int blockIndex = blockIndex0;
                bool wholeWord = units > 1 || string.Equals(reason, "词组错词", StringComparison.Ordinal);
                Log.Info(string.Format("重打演出：块{0}｜单位='{1}'（{2} 字，{3}）｜原因={4}｜拼音='{5}'｜退格步 {6}~{7}｜覆盖计划下标={8}｜只在悬浮条，目标不受影响",
                    blockIndex + 1, unit.ToString(), unit.Length, wholeWord ? "整词重打" : "单字重打",
                    string.IsNullOrEmpty(reason) ? "(空)" : reason, py.ToString(), backspaceAt, backspaceAt + units - 1,
                    JoinInts(covered)));
                note = "重打'" + unit + "'/" + py;
                string text = py.ToString();
                // 问题2（真机）：重打字母阶段**默认模式也必须看得见**，且逐字母长出（PinyinLetterMs 节奏）——
                // 用户要求"删掉错字 → 逐字打出 dian 字母 → 然后选中 点 → 然后输出"。
                // 只显示**被重打单位**的拼音（单位='抖' → 只显示 dou，不是整词的 doudong）。
                // 删除阶段（"删除中：剩 N 字"帧）仍然只在排障模式可见：注入循环里的删除帧有 BarVerbose 门控，本函数不碰它。
                long t0Retype = DateTime.Now.Ticks;      // 第三十三轮：最小可见时长基准
                PhraseBlock retypeUnitBlk = new PhraseBlock();   // 第三十六轮：重打字母帧的候选也按"被重打单位"
                retypeUnitBlk.Text = unit.ToString();
                retypeUnitBlk.Pinyin = py.ToString();
                retypeUnitBlk.Chinese = true;
                retypeUnitBlk.Start = 0;
                BarStatus lastFrame = null;
                for (int i = 0; i < text.Length; i++)
                {
                    if (_stopRequested) return false;
                    BarStatus s = new BarStatus();
                    s.Visible = true;                                   // 重打字母阶段：必须可见（用户要看见字母一个字一个字长出来）
                    s.Anchor = AnchorService.AnchorPoint(anchor);
                    s.Trail = "";                                       // 问题1/2：Trail 一律空串，绝不显示灰色已确认汉字
                    s.Composing = text.Substring(0, i + 1);             // 逐字母长出：只含被重打单位的拼音
                    s.Suffix = "";
                    // 第三十六轮：候选必须按**被重打单位**的拼音生成（不能用整词块——否则 chu 会给出"一处"）
                    ApplyPrefixCandidates(s, retypeUnitBlk, text.Substring(0, i + 1));   // 候选跟着重打的前缀变
                    if (_cfg.Behavior.BarVerbose) s.Foot = "重打：" + unit;   // 脚注只在排障模式出现
                    SetBar(s);
                    lastFrame = s;
                    if (!SleepInterruptible(_cfg.Inject.PinyinLetterMs)) return false;
                }
                // 第三十三轮：最小可见时长（短单位也要看得清）+ 记入等待落屏队列 + **高亮被重打的那个单位**（不再高亮整词）
                int minRetype = _cfg.Inject.MinComposeVisibleMs;
                long visibleRetype = (DateTime.Now.Ticks - t0Retype) / TimeSpan.TicksPerMillisecond;
                if (minRetype > 0 && visibleRetype < minRetype)
                {
                    if (!SleepInterruptible((int)(minRetype - visibleRetype))) return false;
                    visibleRetype = (DateTime.Now.Ticks - t0Retype) / TimeSpan.TicksPerMillisecond;
                }
                Log.Info(string.Format("重打字母阶段可见时长：单位='{0}'｜帧={1}×{2}ms｜可见={3}ms（最小要求 {4}ms）｜覆盖计划下标={5}",
                    unit, text.Length, _cfg.Inject.PinyinLetterMs, visibleRetype, minRetype, JoinInts(covered)));
                RegisterComposedUnit(unit.ToString(), covered);            // 第三十五轮：重打路径也必须登记覆盖下标（否则落屏前会重播一遍）
                Log.Info(string.Format("重打路径已登记已组串：单位='{0}'｜覆盖计划下标={1}", unit, JoinInts(covered)));
                int hl = _cfg.Inject.SelectHighlightMs;
                if (hl > 0)
                {
                    BarStatus hu = new BarStatus();
                    hu.Visible = true;
                    hu.Anchor = AnchorService.AnchorPoint(anchor);
                    hu.Trail = "";
                    hu.Composing = py.ToString();
                    // 第三十六轮（用户最后一处）：候选行必须按**被重打单位**的拼音生成（chu → 出/处/除/初…），
                    // 不能用整词块（一处/yichu）的候选 —— 否则会出现"拼音只有 chu，候选却是 一处"。
                    PhraseBlock unitBlk = new PhraseBlock();
                    unitBlk.Text = unit.ToString();
                    unitBlk.Pinyin = py.ToString();
                    unitBlk.Chinese = true;
                    unitBlk.Start = 0;
                    ApplyCandidates(hu, unitBlk, true);
                    SetBar(hu);
                    Log.Info(string.Format("选词高亮（重打单位）：'{0}'（候选行第 1 个）高亮 {1}ms 后落屏｜候选行='{2}'", unit, hl, hu.Candidates));
                    AssertCandidatesMatchUnit(unit.ToString(), py.ToString(), hu, "重打单位");
                    if (!SleepInterruptible(hl)) return false;
                }
                Log.Info(string.Format("重打字母阶段：Visible={0}｜逐字母 Composing='{1}'（{2} 帧×{3}ms）｜只含被重打单位 '{4}'｜Trail='{5}'｜候选行='{6}'｜默认模式={7}",
                    lastFrame == null ? false : lastFrame.Visible, text, text.Length, _cfg.Inject.PinyinLetterMs, unit,
                    lastFrame == null ? "" : lastFrame.Trail, lastFrame == null ? "" : lastFrame.Candidates, !_cfg.Behavior.BarVerbose));
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("重打演出失败（不影响落屏，继续注入）", ex);
                return true;
            }
        }

        private int DelayForStep(Step step)
        {
            if (step == null) return 0;
            int commit = _cfg.Inject.CommitCharMs;
            if (commit < 5) commit = 5;
            if (commit > 200) commit = 200;

            if (step.Kind == StepKind.Char)
            {
                if (Injector.IsChineseChar(step.Text)) return commit;              // 汉字：块内快速落字
                return step.DelayMs > commit ? step.DelayMs : commit;              // 非汉字：用引擎给的延时，兜底不小于 CommitCharMs
            }
            if (step.Kind == StepKind.Backspace)
            {
                int del = _cfg.Inject.DeleteCharMs;      // 用户要求：删除慢一点、一个字一个字删
                if (del < 30) del = 30;
                if (del > 1000) del = 1000;
                return del;
            }
            return step.DelayMs;                                                   // 换行/停顿仍按引擎
        }

        // 把刷新到的插入符写进锚点并更新悬浮条位置（可单测：不依赖真实前台/真实插入符）
        internal void ApplyRefreshedCaret(AnchorInfo anchor, Rectangle caret)
        {
            anchor.HasCaret = true;
            anchor.CaretRect = caret;
            _anchorMissCount = 0;
            Point current = AnchorService.AnchorPoint(anchor);
            if (current != _barAnchorPoint)
            {
                Log.Info(string.Format("运行中锚点更新：({0},{1}) → ({2},{3})", _barAnchorPoint.X, _barAnchorPoint.Y, current.X, current.Y));
                _barAnchorPoint = current;
            }
        }

        private bool EnsureForeground(AnchorInfo anchor)
        {
            IntPtr target = anchor.Hwnd;
            if (target == IntPtr.Zero) return true;
            if (Native.GetForegroundWindow() == target)
            {
                _attention = false;
                return true;
            }
            if (string.Equals(_cfg.Behavior.FocusLostPolicy, "Abort", StringComparison.OrdinalIgnoreCase))
            {
                Log.Info("目标窗口失焦 → 按 Abort 策略中止");
                return false;
            }
            Log.Info("目标窗口失焦 → 暂停等待（托盘闪动提示）");
            ShowStatusBar("已暂停：目标窗口不在前台", "切回目标窗口自动继续 · F12 停止", AnchorService.AnchorPoint(anchor));
            _attention = true;
            while (!_stopRequested)
            {
                if (Native.GetForegroundWindow() == target)
                {
                    _attention = false;
                    return true;
                }
                Thread.Sleep(120);
            }
            _attention = false;
            return false;
        }

        private bool SleepInterruptible(int milliseconds)
        {
            if (milliseconds <= 0) milliseconds = 0;
            int waited = 0;
            while (waited < milliseconds)
            {
                if (_stopRequested) return false;
                int slice = Math.Min(50, milliseconds - waited);
                Thread.Sleep(slice);
                waited += slice;
            }
            return !_stopRequested;
        }

        // 修改A：收集"夹在本切片内两个 Char 之间"的 Sleep 计划下标（升序）。
        // 判据只看 Char：Backspace 不算 Char（错字纠正本来就要在两个字之间插退格），否则每条错字都会误报。
        // from 传"头部停顿已提前执行完"的 stepIndex，切片头部的 Sleep 不会被重复收集（否则会睡第二次）。
        internal static List<int> CollectIntraBlockSleeps(IList<Step> plan, int from, int toExclusive)
        {
            List<int> found = new List<int>();
            if (plan == null) return found;
            int last = Math.Min(toExclusive, plan.Count) - 1;      // 需要 plan[i+1]，止于 count-1
            for (int i = Math.Max(from, 1); i < last; i++)
            {
                if (plan[i] == null || plan[i].Kind != StepKind.Sleep) continue;
                if (plan[i - 1] == null || plan[i + 1] == null) continue;
                if (plan[i - 1].Kind != StepKind.Char || plan[i + 1].Kind != StepKind.Char) continue;
                found.Add(i);
            }
            return found;
        }

        // 修改A：在组串之前把上面收集到的块内停顿各睡一次（每个下标恰好一次）；sleep 返回 false 表示被打断。
        // 返回实际睡过的下标（升序）：运行时只用于记录，宿主取证也调这一段生产代码。
        internal static List<int> HoistIntraBlockSleepsBeforeCompose(IList<Step> plan, IList<int> hoisted, int blockNo, Func<int, bool> sleep)
        {
            List<int> slept = new List<int>();
            if (plan == null || hoisted == null) return slept;
            for (int h = 0; h < hoisted.Count; h++)
            {
                int i = hoisted[h];
                if (i < 0 || i >= plan.Count || plan[i] == null) continue;
                Log.Info(string.Format("块内停顿上提：Sleep {0}ms（计划下标={1}，夹在第 {2} 段的两个字符之间）已移到本缓冲组串前执行",
                    plan[i].DelayMs, i, blockNo));
                if (sleep != null && !sleep(plan[i].DelayMs)) break;
                slept.Add(i);
            }
            return slept;
        }

        private void FinishRun(string reason)
        {
            _finishReason = reason;
            _clickAnchor = null;
            _runAnchor = null;
            Log.Info("运行结束：" + reason);
            _pendingSilent = true;   // 由 UI 线程执行真正的静默切换
        }

        // 生效热键摘要：停止键被自动切换时必须一眼可见
        private string HotkeySummary()
        {
            if (_state == AppState.Silent) return "热键：已全部注销（静默待机中，双击托盘唤醒）";
            int stop = _hotkeys.EffectiveStopKey;
            string stopText = stop != 0 ? KeyTable.NameOf(stop) : "无";
            string extra = "";
            if (stop == 0) extra = "（候选全部失败，详见 error.log）";
            else if (stop != _cfg.Hotkeys.StopKey) extra = "（首选 " + KeyTable.NameOf(_cfg.Hotkeys.StopKey) + " 被占用，已自动切换）";
            return string.Format("热键：开始 {0} · 点击定位 {1} · 停止 {2}{3}",
                KeyTable.NameOf(_cfg.Hotkeys.StartKey), KeyTable.NameOf(_cfg.Hotkeys.LocateKey), stopText, extra);
        }

        private void OnWakeRequested()
        {
            try
            {
                Log.Info("收到第二实例的唤醒请求");
                Wake();
            }
            catch (Exception ex)
            {
                Log.Error("处理唤醒请求失败", ex);
            }
        }

        private void StopRun()
        {
            _stopRequested = true;
            if (_state == AppState.Locating || _countdownActive)
            {
                CancelLocate();
                return;
            }
            Thread worker = _worker;
            if (worker == null || !worker.IsAlive)
            {
                _pendingSilent = true;
                return;
            }
            ShowStatusBar("停止中：等待当前字符结束…", "F12", _barStatus.Anchor);
        }

        // ---- 悬浮条内容：对齐微软拼音候选窗，且任何一步都有可读内容（绝不空白）----

        // 像真输入法：不打字就不显示候选窗。BarVerbose=true 时才显示停顿/等待这类详细状态（排障）
        private void ShowStatusBar(string composing, string foot, Point anchor)
        {
            if (_cfg.Behavior.BarVerbose) SetBar(SingleBar(composing, foot, anchor));
            else SetBar(new BarStatus { Visible = false });
        }

        // 安全告警（完整性不足等）：不受 BarVerbose 影响，必须让用户看见
        private void ShowAlertBar(string composing, string foot, Point anchor)
        {
            SetBar(SingleBar(composing, foot, anchor));
        }

        // 悬浮条默认只放输入法该有的东西：上行=拼音串(当前音节高亮) + 下行=带序号候选。
        // 进度数字、状态词、告警一律不进悬浮条（BarVerbose=true 才恢复详细状态）。
        private void ShowStepBar(Step step, int done, int total, PinyinComposer composer, AnchorInfo anchor, bool fallbackUsed, bool chineseEmission)
        {
            bool verbose = _cfg.Behavior.BarVerbose;
            BarStatus s = new BarStatus();
            s.Visible = true;
            s.Anchor = AnchorService.AnchorPoint(anchor);
            if (verbose)
            {
                s.Progress = string.Format("{0}/{1}", done, total);
                s.Foot = (step.Reason ?? "") + (fallbackUsed ? " · 剪贴板兜底" : "");
            }

            if (step.Kind == StepKind.Char && !string.IsNullOrEmpty(step.Text))
            {
                if (chineseEmission)
                {
                    s.Trail = TrailFor(composer.Trail);   // 问题1：逐词模型下为空串
                    s.Composing = composer.Current;
                    s.Candidates = BuildCandidates(step.Text, 4);
                    s.Highlight = step.Text;
                    SetBar(s);
                    return;
                }
                // 标点/英文/数字：真输入法在中文态下也是直接上屏，不弹候选窗
                if (verbose) { s.Composing = step.Text; SetBar(s); }
                else SetBar(new BarStatus { Visible = false });
                return;
            }

            if (step.Kind == StepKind.Backspace)
            {
                composer.Pop();     // 真输入法：组串变短，而不是显示「退格修正」
                if (!composer.IsEmpty)
                {
                    s.Trail = TrailFor(composer.Trail);   // 问题1：逐词模型下为空串
                    s.Composing = composer.Current;
                    s.Candidates = BuildCandidates(composer.CurrentChar.ToString(), 4);
                    s.Highlight = composer.CurrentChar.ToString();
                    SetBar(s);
                    return;
                }
                if (verbose) { s.Composing = "退格修正"; SetBar(s); }
                else SetBar(new BarStatus { Visible = false });
                return;
            }

            if (verbose)
            {
                s.Composing = step.Kind == StepKind.LineBreak ? "换行" : ("停顿 " + step.DelayMs + "ms");
                SetBar(s);
            }
            else SetBar(new BarStatus { Visible = false });
        }

        // 完整性守卫：UIPI 拦截时 SendInput 仍返回成功（用户实测 16/16），只有 RID 比对才可靠
        private bool CheckIntegrityGuard(AnchorInfo anchor)
        {
            try
            {
                int selfRid = AppInfo.IntegrityRid;
                uint pid = Native.WindowProcessId(anchor.Hwnd);
                if (selfRid <= 0)
                {
                    Log.Info("完整性守卫：读不到自身完整性，跳过比对");
                    return true;
                }
                int openError;
                int targetRid = Native.ProcessIntegrityRid(pid, out openError);
                string message = null;
                if (targetRid > 0)
                {
                    Log.Info(string.Format("完整性守卫：我方={0} 目标(pid={1})={2}", Integrity.Describe(selfRid), pid, Integrity.Describe(targetRid)));
                    if (selfRid < targetRid)
                    {
                        message = string.Format("我方完整性={0}，目标={1}，Windows 会丢弃按键；请用 SimIme-Admin.exe 启动",
                            Integrity.Name(selfRid), Integrity.Name(targetRid));
                    }
                }
                else if (openError == Native.ERROR_ACCESS_DENIED)
                {
                    // 同级/更低完整性的目标一定读得到，所以「拒绝访问」几乎等于目标完整性更高（UIPI 一样丢弃按键）
                    message = string.Format("无法读取目标进程完整性(拒绝访问 win32=5)，我方={0}；目标极可能完整性更高，按键会被 Windows 丢弃；请用 SimIme-Admin.exe 启动",
                        Integrity.Name(selfRid));
                }
                else
                {
                    Log.Info(string.Format("完整性守卫：目标完整性未知(pid={0} win32={1})，跳过比对", pid, openError));
                    return true;
                }
                if (message == null) return true;

                Log.Error(message);
                NotifyAlert("无法输入：完整性不足", message, AnchorService.AnchorPoint(anchor));
                _attention = true;
                // 不假装在输入：保持告警直到用户按 F12（Abort/Pause 两种策略都不注入）
                while (!_stopRequested) Thread.Sleep(120);
                _attention = false;
                FinishRun("完整性不足，未做任何输入");
                return false;
            }
            catch (Exception ex)
            {
                Log.Error("完整性守卫检查异常（跳过该检查）", ex);
                return true;
            }
        }

        // 运行期告警：托盘气泡 + 主界面状态区 + 日志；悬浮条只在排障模式下占用
        private void NotifyAlert(string title, string message, Point anchor)
        {
            try
            {
                _form.SetHint(title + "：" + message);
                _finishReason = title;
                if (_tray != null && _tray.Visible) _tray.ShowBalloonTip(10000, title, message, ToolTipIcon.Warning);
            }
            catch (Exception ex) { Log.Error("显示告警失败", ex); }
            if (_cfg.Behavior.BarVerbose) SetBar(SingleBar(title, message, anchor));
            else SetBar(new BarStatus { Visible = false });
        }

        private static BarStatus SingleBar(string composing, string foot, Point anchor)
        {
            BarStatus s = new BarStatus();
            s.Visible = true;
            s.Composing = string.IsNullOrEmpty(composing) ? "等待…" : composing;
            s.Foot = foot ?? "";
            s.Anchor = anchor;
            return s;
        }

        private string SafePinyin(char c)
        {
            try
            {
                if (_table != null)
                {
                    string p = _table.GetPinyin(c);
                    if (!string.IsNullOrEmpty(p)) return p;
                }
            }
            catch (Exception ex)
            {
                Log.Error("取拼音失败", ex);
            }
            return "";
        }

        private string BuildCandidates(string current, int extra)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("1.").Append(current);
            int index = 2;
            try
            {
                if (_table != null && current.Length > 0)
                {
                    IList<string> homo = _table.HomophonesOf(current[0], extra);
                    if (homo != null)
                    {
                        for (int i = 0; i < homo.Count; i++)
                        {
                            if (string.IsNullOrEmpty(homo[i])) continue;
                            sb.Append(' ').Append(index).Append('.').Append(homo[i]);
                            index++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("取同音字候选失败", ex);
            }
            return sb.ToString();
        }

        private static string DescribeStepForLog(Step step, IPinyinTable table)
        {
            if (step == null) return "";
            string text = "";
            if (step.Kind == StepKind.Char && !string.IsNullOrEmpty(step.Text))
            {
                string pinyin = "";
                try { if (table != null) pinyin = table.GetPinyin(step.Text[0]) ?? ""; }
                catch { }
                text = "char='" + step.Text + "'" + (pinyin.Length > 0 ? "(" + pinyin + ")" : "");
            }
            else if (step.Kind == StepKind.LineBreak) text = "换行";
            else if (step.Kind == StepKind.Backspace) text = "退格";
            else if (step.Kind == StepKind.Sleep) text = "停顿";
            return text + (string.IsNullOrEmpty(step.Reason) ? "" : " reason=" + step.Reason);
        }

        private static IList<Step> FallbackPlan(string text)
        {
            List<Step> plan = new List<Step>();
            if (text == null) return plan;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r') continue;
                if (c == '\n') { plan.Add(new Step(StepKind.LineBreak, null, 60, "fallback-line")); continue; }
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    plan.Add(new Step(StepKind.Char, text.Substring(i, 2), 90, "fallback-surrogate"));
                    i++;
                    continue;
                }
                plan.Add(new Step(StepKind.Char, c.ToString(), 90, "fallback-char"));
            }
            return plan;
        }

        // ---------------- 静默 / 唤醒 / 退出 ----------------

        private void RequestSilent()
        {
            _stopRequested = true;
            Thread worker = _worker;
            if (worker != null && worker.IsAlive) ShowStatusBar("停止中：准备进入静默…", "F12", _barStatus.Anchor);
            else _pendingSilent = true;
        }

        private void EnterSilent()
        {
            try
            {
                _countdownActive = false;
                try { _mouseHook.Uninstall(); } catch (Exception ex) { Log.Error("静默时卸载鼠标钩子失败", ex); }
                _hotkeys.UnregisterAll();
                _attention = false;
                _barStatus = new BarStatus { Visible = false };
                if (_barShown) { _bar.HideBar(); _barShown = false; }
                _form.Hide();
                SetState(AppState.Silent);
                _tray.Visible = true;
                Log.Info("进入静默待机：全部热键已注销，仅托盘可唤醒");
            }
            catch (Exception ex)
            {
                Log.Error("进入静默失败", ex);
            }
        }

        private void Wake()
        {
            try
            {
                if (_exiting) return;
                _stopRequested = false;
                _finishReason = null;
                bool ok = _hotkeys.RegisterAll(_cfg);
                _barStatus = new BarStatus { Visible = false };
                if (_barShown) { _bar.HideBar(); _barShown = false; }
                SetState(AppState.Idle);
                _form.Show();
                _form.Activate();
                _form.SetHint(ok
                    ? "已唤醒：粘贴文本后点「确认开始」，再按 F9。"
                    : "已唤醒：部分热键注册失败（可能被占用），详见 error.log。");
                Log.Info("唤醒：热键重新注册 " + (ok ? "成功" : "部分失败"));
            }
            catch (Exception ex)
            {
                Log.Error("唤醒失败", ex);
            }
        }

        private void Shutdown()
        {
            if (_exiting) return;
            _exiting = true;
            _stopRequested = true;
            Log.Info("退出 SimIme");
            try { _uiTimer.Stop(); } catch (Exception ex) { Log.Error("停止 UI 定时器失败", ex); }
            try { _mouseHook.Uninstall(); } catch (Exception ex) { Log.Error("卸载鼠标钩子失败", ex); }
            try { _hotkeys.UnregisterAll(); } catch (Exception ex) { Log.Error("注销热键失败", ex); }
            try { _tray.Visible = false; _tray.Dispose(); } catch (Exception ex) { Log.Error("销毁托盘失败", ex); }
            try { _bar.HideBar(); _bar.Dispose(); } catch (Exception ex) { Log.Error("销毁悬浮条失败", ex); }
            try { _form.AllowClose = true; _form.Dispose(); } catch (Exception ex) { Log.Error("销毁主界面失败", ex); }
            try { _messageWindow.Dispose(); } catch (Exception ex) { Log.Error("销毁消息窗口失败", ex); }
            ExitThread();
        }

        // ---------------- UI 心跳（所有控件访问都在 UI 线程） ----------------

        private void OnUiTick(object sender, EventArgs e)
        {
            try
            {
                if (_dictBusy)
                {
                    string progress = DictUpdater.Progress;
                    if (!string.IsNullOrEmpty(progress)) _form.SetDictInfo("正在联网更新词典…" + progress);
                }
                if (_dictPendingText != null) ApplyDictUpdateResult();
                if (_countdownActive)
                {
                    double left = (_countdownEnd - DateTime.UtcNow).TotalSeconds;
                    if (left <= 0)
                    {
                        _countdownActive = false;
                        _hotkeys.UnregisterOne(HotkeyManager.IdCancel);
                        AnchorInfo pending = _clickAnchor;
                        BeginLockAndRun(pending);
                    }
                    else
                    {
                        BarStatus counting = _barStatus;
                        ShowStatusBar(string.Format("倒计时 {0:0.0} 秒后开始", left), "Esc / F12 取消", counting.Anchor);
                    }
                }

                if (_pendingSilent)
                {
                    _pendingSilent = false;
                    EnterSilent();
                }

                BarStatus status = _barStatus;
                if (status != null && status.Visible)
                {
                    // 跟随光标：运行期用运行线程刷新的最新锚点（只挪位置）
                    AnchorInfo live = _runAnchor;
                    if (live != null && live.HasCaret)
                    {
                        Point latest = AnchorService.AnchorPoint(live);
                        if (latest != status.Anchor) status.Anchor = latest;
                    }
                    _bar.ShowAt(status.Anchor, status);
                    _barShown = true;
                }
                else if (_barShown)
                {
                    _bar.HideBar();
                    _barShown = false;
                }

                if (_attention)
                {
                    _attentionTick++;
                    if (_attentionTick >= 4)
                    {
                        _attentionTick = 0;
                        _attentionPhase = !_attentionPhase;
                        _tray.Icon = _attentionPhase ? SystemIcons.Warning : SystemIcons.Application;
                    }
                }
                else if (_attentionPhase)
                {
                    _attentionPhase = false;
                    _tray.Icon = SystemIcons.Application;
                }

                string trayText = "SimIme · " + StateNames.Of(_state);
                if (trayText.Length > 60) trayText = trayText.Substring(0, 60);
                if (trayText != _lastTrayText)
                {
                    _tray.Text = trayText;
                    _lastTrayText = trayText;
                }

                string stateLine = "状态：" + StateNames.Of(_state);
                string reason = _finishReason;
                if (!string.IsNullOrEmpty(reason)) stateLine += "（" + reason + "）";
                if (stateLine != _lastStateLine)
                {
                    _form.SetStateLine(stateLine);
                    _lastStateLine = stateLine;
                }
                _form.SetHotkeyLine(HotkeySummary());
            }
            catch (Exception ex)
            {
                Log.Error("UI 心跳异常", ex);
            }
        }

        private void SetState(AppState state)
        {
            _state = state;
            Log.Info("状态 → " + StateNames.Of(state));
        }

        private void SetBar(BarStatus status)
        {
            _barStatus = status;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _uiTimer.Dispose(); } catch { }
                try { _mouseHook.Dispose(); } catch { }
                try { _tray.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
