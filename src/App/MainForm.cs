using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace SimIme.App
{
    internal sealed class FieldSpec
    {
        internal string Label;
        internal string Path;
        internal string Kind;      // int | bool | choice | key | keys | text | button | info
        internal decimal Min;
        internal decimal Max;
        internal string[] Options;
    }

    internal sealed class MainForm : Form
    {
        private readonly AppConfig _cfg;
        private readonly TextBox _input;
        private readonly Label _stateLine;
        private readonly Label _hintLine;
        private readonly Label _hotkeyLine;
        private readonly Label _adminLine;
        private Label _userWordLabel;
        private Label _dictLabel;
        private Button _dictButton;
        private bool _dictBusy;

        internal event Action ConfirmRequested;
        internal event Action LocateRequested;
        internal event Action StopRequested;
        internal event Action HideRequested;
        internal event Action ExitRequested;
        internal event Action ElevateRequested;
        internal event Action DictUpdateRequested;

        internal bool AllowClose;

        internal MainForm(AppConfig cfg)
        {
            _cfg = cfg;
            Text = "SimIme 拟人化中文输入器";
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(780, 640);
            MinimumSize = new Size(640, 480);
            Font = new Font("Microsoft YaHei UI", 9f);

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 4;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 158));

            Label inputLabel = new Label();
            inputLabel.Text = "① 粘贴要输入的文本（支持多行 / 中英文 / 标点）：";
            inputLabel.Dock = DockStyle.Fill;
            inputLabel.TextAlign = ContentAlignment.MiddleLeft;

            _input = new TextBox();
            _input.Multiline = true;
            _input.ScrollBars = ScrollBars.Vertical;
            _input.AcceptsReturn = true;
            _input.WordWrap = true;
            _input.Dock = DockStyle.Fill;
            _input.Font = new Font("Microsoft YaHei UI", 10f);

            TabControl tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            tabs.TabPages.Add(BuildTab("基本", BasicFields()));
            tabs.TabPages.Add(BuildTab("节奏", RhythmFields()));
            tabs.TabPages.Add(BuildTab("错字", TypoFields()));

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Fill;

            Button btnConfirm = MakeButton("② 确认开始", 12, 8);
            btnConfirm.Click += delegate { Raise(ConfirmRequested); };
            Button btnLocate = MakeButton("点击定位开始", 156, 8);
            btnLocate.Click += delegate { Raise(LocateRequested); };
            Button btnStop = MakeButton("停止 (F12)", 300, 8);
            btnStop.Click += delegate { Raise(StopRequested); };
            Button btnHide = MakeButton("隐藏到托盘", 444, 8);
            btnHide.Click += delegate { Raise(HideRequested); };
            Button btnExit = MakeButton("退出", 588, 8);
            btnExit.Click += delegate { Raise(ExitRequested); };

            bottom.Controls.Add(btnConfirm);
            bottom.Controls.Add(btnLocate);
            bottom.Controls.Add(btnStop);
            bottom.Controls.Add(btnHide);
            bottom.Controls.Add(btnExit);

            // 提权重启：Low 完整性下这是唯一能救回用户的入口
            Button btnElevate = MakeButton("以管理员身份重启", 600, 89);
            btnElevate.Size = new Size(140, 24);
            btnElevate.Click += delegate { Raise(ElevateRequested); };
            bottom.Controls.Add(btnElevate);

            // 当前生效热键（尤其停止键）必须一眼可见：注册失败/自动切换都在这里体现
            _hotkeyLine = new Label();
            _hotkeyLine.AutoSize = false;
            _hotkeyLine.Location = new Point(12, 48);
            _hotkeyLine.Size = new Size(740, 20);
            _hotkeyLine.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            _hotkeyLine.ForeColor = Color.FromArgb(20, 80, 20);
            _hotkeyLine.Text = "热键：初始化中…";

            _stateLine = new Label();
            _stateLine.AutoSize = false;
            _stateLine.Location = new Point(12, 70);
            _stateLine.Size = new Size(740, 20);
            _stateLine.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            _stateLine.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            _stateLine.Text = "就绪";

            _hintLine = new Label();
            _hintLine.AutoSize = false;
            // 权限状态：提权与否直接决定能否注入管理员窗口，必须在界面上常驻可见
            _adminLine = new Label();
            _adminLine.AutoSize = false;
            _adminLine.Location = new Point(12, 92);
            _adminLine.Size = new Size(580, 20);
            _adminLine.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            _adminLine.ForeColor = Color.FromArgb(120, 60, 0);
            _adminLine.Text = AppInfo.PermissionLine();
            bool lowIntegrity = AppInfo.IntegrityRid > 0 && AppInfo.IntegrityRid < Integrity.Medium;
            _adminLine.ForeColor = lowIntegrity ? Color.Red : Color.FromArgb(120, 60, 0);
            if (lowIntegrity) _adminLine.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);

            _hintLine.Location = new Point(12, 114);
            _hintLine.Size = new Size(740, 36);
            _hintLine.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            _hintLine.ForeColor = Color.FromArgb(70, 70, 70);
            _hintLine.Text = "流程：粘贴文本 → 确认开始（窗口隐身）→ 切到目标窗口按 F9 → 锁定插入点 → 等待 → 逐字输入 → 结束后静默待机。";

            bottom.Controls.Add(_hotkeyLine);
            bottom.Controls.Add(_adminLine);
            bottom.Controls.Add(_stateLine);
            bottom.Controls.Add(_hintLine);

            root.Controls.Add(inputLabel, 0, 0);
            root.Controls.Add(_input, 0, 1);
            root.Controls.Add(tabs, 0, 2);
            root.Controls.Add(bottom, 0, 3);
            Controls.Add(root);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW;   // 不进任务栏，也不进 Alt-Tab
                return cp;
            }
        }

        private static void Raise(Action handler)
        {
            if (handler != null) handler();
        }

        private Button MakeButton(string text, int x, int y)
        {
            Button button = new Button();
            button.Text = text;
            button.Location = new Point(x, y);
            button.Size = new Size(138, 34);
            button.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            return button;
        }

        private TabPage BuildTab(string title, List<FieldSpec> specs)
        {
            TabPage page = new TabPage(title);
            Panel scroll = new Panel();
            scroll.Dock = DockStyle.Fill;
            scroll.AutoScroll = true;

            TableLayoutPanel grid = new TableLayoutPanel();
            grid.ColumnCount = 2;
            grid.Dock = DockStyle.Top;
            grid.AutoSize = true;
            grid.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));

            for (int i = 0; i < specs.Count; i++)
            {
                grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
                AddRow(grid, i, specs[i]);
            }
            scroll.Controls.Add(grid);
            page.Controls.Add(scroll);
            return page;
        }

        private void AddRow(TableLayoutPanel grid, int row, FieldSpec spec)
        {
            Label label = new Label();
            label.Text = spec.Label;
            label.AutoSize = false;
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.Margin = new Padding(3, 0, 3, 0);

            Control editor = CreateEditor(spec);
            editor.Dock = DockStyle.Fill;
            editor.Margin = new Padding(3, 4, 3, 4);

            grid.Controls.Add(label, 0, row);
            grid.Controls.Add(editor, 1, row);
        }

        private Control CreateEditor(FieldSpec spec)
        {
            if (spec.Kind == "info")
            {
                Label info = new Label();
                info.Text = "（读取中…）";
                info.TextAlign = ContentAlignment.MiddleLeft;
                info.ForeColor = Color.FromArgb(20, 80, 20);
                if (spec.Path == "info.dict") _dictLabel = info;
                else _userWordLabel = info;
                return info;
            }
            if (spec.Kind == "text")
            {
                TextBox box = new TextBox();
                box.Text = (FieldAccess.Get(_cfg, spec.Path) as string) ?? "";
                box.TextChanged += delegate { FieldAccess.Set(_cfg, spec.Path, box.Text); };
                return box;
            }
            if (spec.Kind == "button")
            {
                Button button = new Button();
                button.Text = spec.Label;
                button.UseVisualStyleBackColor = true;
                button.Click += delegate
                {
                    Action handler = DictUpdateRequested;
                    if (handler != null) handler();
                };
                _dictButton = button;
                return button;
            }
            if (spec.Kind == "bool")
            {
                CheckBox box = new CheckBox();
                object value = FieldAccess.Get(_cfg, spec.Path);
                box.Checked = (value is bool) && (bool)value;
                box.CheckedChanged += delegate { FieldAccess.Set(_cfg, spec.Path, box.Checked); };
                return box;
            }
            if (spec.Kind == "choice")
            {
                ComboBox combo = new ComboBox();
                combo.DropDownStyle = ComboBoxStyle.DropDownList;
                combo.Items.AddRange(spec.Options);
                string current = FieldAccess.Get(_cfg, spec.Path) as string;
                int index = 0;
                for (int i = 0; i < spec.Options.Length; i++)
                {
                    if (string.Equals(spec.Options[i], current, StringComparison.OrdinalIgnoreCase)) index = i;
                }
                combo.SelectedIndex = index;
                combo.SelectedIndexChanged += delegate
                {
                    string picked = combo.SelectedItem as string;
                    if (picked != null) FieldAccess.Set(_cfg, spec.Path, picked);
                };
                return combo;
            }
            if (spec.Kind == "keys")
            {
                TextBox box = new TextBox();
                box.Text = JoinKeyNames(FieldAccess.Get(_cfg, spec.Path));
                box.TextChanged += delegate
                {
                    int[] parsed = ParseKeyNames(box.Text);
                    if (parsed != null && parsed.Length > 0) FieldAccess.Set(_cfg, spec.Path, parsed);
                };
                return box;
            }
            if (spec.Kind == "key")
            {
                ComboBox combo = new ComboBox();
                combo.DropDownStyle = ComboBoxStyle.DropDownList;
                string[] names = KeyTable.AllNames();
                combo.Items.AddRange(names);
                object value = FieldAccess.Get(_cfg, spec.Path);
                int vk = 0;
                if (value != null) vk = Convert.ToInt32(value);
                string name = KeyTable.NameOf(vk);
                int index = 0;
                for (int i = 0; i < names.Length; i++) if (names[i] == name) index = i;
                combo.SelectedIndex = index;
                combo.SelectedIndexChanged += delegate
                {
                    string picked = combo.SelectedItem as string;
                    if (picked != null) FieldAccess.Set(_cfg, spec.Path, KeyTable.CodeOf(picked, 0x78));
                };
                return combo;
            }

            NumericUpDown number = new NumericUpDown();
            number.Maximum = spec.Max;
            number.Minimum = spec.Min;
            decimal current2 = spec.Min;
            try
            {
                object raw = FieldAccess.Get(_cfg, spec.Path);
                if (raw != null) current2 = Convert.ToDecimal(raw);
            }
            catch
            {
                current2 = spec.Min;
            }
            if (current2 < spec.Min) current2 = spec.Min;
            if (current2 > spec.Max) current2 = spec.Max;
            number.Value = current2;
            number.ValueChanged += delegate { FieldAccess.Set(_cfg, spec.Path, (int)number.Value); };
            return number;
        }

        private static FieldSpec Num(string label, string path, int min, int max)
        {
            FieldSpec spec = new FieldSpec();
            spec.Label = label;
            spec.Path = path;
            spec.Kind = "int";
            spec.Min = min;
            spec.Max = max;
            return spec;
        }

        private static FieldSpec Flag(string label, string path)
        {
            FieldSpec spec = new FieldSpec();
            spec.Label = label;
            spec.Path = path;
            spec.Kind = "bool";
            return spec;
        }

        private static FieldSpec Choice(string label, string path, string[] options)
        {
            FieldSpec spec = new FieldSpec();
            spec.Label = label;
            spec.Path = path;
            spec.Kind = "choice";
            spec.Options = options;
            return spec;
        }

        private static FieldSpec Key(string label, string path)
        {
            FieldSpec spec = new FieldSpec();
            spec.Label = label;
            spec.Path = path;
            spec.Kind = "key";
            return spec;
        }

        private static FieldSpec Info(string label, string tag)
        {
            FieldSpec spec = new FieldSpec();
            spec.Label = label;
            spec.Path = "info." + tag;
            spec.Kind = "info";
            return spec;
        }

        private static FieldSpec TextRow(string label, string path)
        {
            FieldSpec spec = new FieldSpec();
            spec.Label = label;
            spec.Path = path;
            spec.Kind = "text";
            return spec;
        }

        private static FieldSpec Button(string label)
        {
            FieldSpec spec = new FieldSpec();
            spec.Label = label;
            spec.Path = "";
            spec.Kind = "button";
            return spec;
        }

        // 外部词表状态行 / 更新按钮状态（都由后台线程的结果经 UI 线程刷新）
        internal void SetDictInfo(string text)
        {
            try
            {
                if (_dictLabel != null && _dictLabel.Text != text) _dictLabel.Text = text;
            }
            catch (Exception ex)
            {
                Log.Error("更新外部词表状态失败", ex);
            }
        }

        internal void SetDictBusy(bool busy, string buttonText)
        {
            try
            {
                _dictBusy = busy;
                if (_dictButton != null)
                {
                    _dictButton.Enabled = !busy;
                    if (!string.IsNullOrEmpty(buttonText)) _dictButton.Text = buttonText;
                }
            }
            catch (Exception ex)
            {
                Log.Error("更新词典按钮状态失败", ex);
            }
        }

        internal void SetUserWordInfo(string text)
        {
            try
            {
                if (_userWordLabel != null && _userWordLabel.Text != text) _userWordLabel.Text = text;
            }
            catch (Exception ex)
            {
                Log.Error("更新用户词表状态失败", ex);
            }
        }

        private static FieldSpec Keys(string label, string path)
        {
            FieldSpec spec = new FieldSpec();
            spec.Label = label;
            spec.Path = path;
            spec.Kind = "keys";
            return spec;
        }

        internal static string JoinKeyNames(object value)
        {
            int[] keys = value as int[];
            if (keys == null || keys.Length == 0) return "";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < keys.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(KeyTable.NameOf(keys[i]));
            }
            return sb.ToString();
        }

        // 解析逗号/分号/空格分隔的键名；认不出或全空的输入返回 null（保持原值不动）
        internal static int[] ParseKeyNames(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string[] parts = text.Split(new char[] { ',', '，', ';', '；', ' ', '\t' });
            List<int> keys = new List<int>();
            for (int i = 0; i < parts.Length; i++)
            {
                string name = parts[i].Trim();
                if (name.Length == 0) continue;
                int vk = KeyTable.CodeOf(name, 0);
                if (vk < 0x08) continue;
                if (!keys.Contains(vk)) keys.Add(vk);
            }
            if (keys.Count == 0) return null;
            return keys.ToArray();
        }

        private static List<FieldSpec> BasicFields()
        {
            List<FieldSpec> list = new List<FieldSpec>();
            list.Add(Key("开始 / 二次确认热键", "Hotkeys.StartKey"));
            list.Add(Key("点击定位热键", "Hotkeys.LocateKey"));
            list.Add(Key("停止热键（首选）", "Hotkeys.StopKey"));
            list.Add(Keys("停止键候选(按序回退)", "Hotkeys.StopKeyCandidates"));
            list.Add(Num("启动等待 StartupDelayMs", "Engine.Rhythm.StartupDelayMs", 0, 120000));
            list.Add(Num("点击定位倒计时(ms)", "Behavior.ClickLocateCountdownMs", 0, 30000));
            list.Add(Choice("目标窗口失焦策略", "Behavior.FocusLostPolicy", new string[] { "Pause", "Abort" }));
            list.Add(Choice("注入方式", "Inject.Method", new string[] { "SendInput", "Clipboard" }));
            list.Add(Flag("SendInput 失败时剪贴板兜底", "Inject.ClipboardFallback"));
            list.Add(Flag("结束后还原原剪贴板", "Inject.RestoreClipboard"));
            list.Add(Num("剪贴板粘贴等待(ms)", "Inject.ClipboardPasteDelayMs", 0, 5000));
            list.Add(Choice("锚定方式", "Anchor.Method", new string[] { "Auto", "Caret", "Uia", "Mouse" }));
            list.Add(Num("UIA 锚定超时(ms)", "Anchor.UiaTimeoutMs", 100, 10000));
            list.Add(Num("随机种子(0=每次随机)", "Engine.RandomSeed", 0, 2000000000));
            list.Add(Flag("学习用户词组(写 habits.dat)", "Behavior.LearnUserWords"));
            list.Add(Info("用户词表", "words"));
            list.Add(TextRow("词典镜像地址", "Behavior.DictMirror"));
            list.Add(TextRow("外部词表路径", "Behavior.ExtraDictFile"));
            list.Add(Button("联网更新词典"));
            list.Add(Info("外部词表", "dict"));
            list.Add(Flag("写运行日志 error.log", "Behavior.LogEnabled"));
            list.Add(Num("悬浮条偏移 X", "Behavior.BarOffsetX", -400, 400));
            list.Add(Num("悬浮条偏移 Y", "Behavior.BarOffsetY", -400, 400));
            return list;
        }

        private static List<FieldSpec> RhythmFields()
        {
            List<FieldSpec> list = new List<FieldSpec>();
            list.Add(Num("单字间隔下限(ms)", "Engine.Rhythm.CharDelayMinMs", 1, 10000));
            list.Add(Num("单字间隔上限(ms)", "Engine.Rhythm.CharDelayMaxMs", 1, 20000));
            list.Add(Num("速度波动(%)", "Engine.Rhythm.SpeedVariancePct", 0, 100));
            list.Add(Num("连打突发概率(%)", "Engine.Rhythm.BurstChancePct", 0, 100));
            list.Add(Num("思考停顿下限(ms)", "Engine.Rhythm.ThinkPauseMinMs", 0, 60000));
            list.Add(Num("思考停顿上限(ms)", "Engine.Rhythm.ThinkPauseMaxMs", 0, 120000));
            list.Add(Num("思考间隔字数下限", "Engine.Rhythm.ThinkEveryMinChars", 1, 500));
            list.Add(Num("思考间隔字数上限", "Engine.Rhythm.ThinkEveryMaxChars", 1, 1000));
            list.Add(Num("长休息下限(ms)", "Engine.Rhythm.RestPauseMinMs", 0, 300000));
            list.Add(Num("长休息上限(ms)", "Engine.Rhythm.RestPauseMaxMs", 0, 600000));
            list.Add(Num("长休息间隔句子下限", "Engine.Rhythm.RestEveryMinSentences", 1, 500));
            list.Add(Num("长休息间隔句子上限", "Engine.Rhythm.RestEveryMaxSentences", 1, 1000));
            list.Add(Num("标点额外停顿下限(ms)", "Engine.Rhythm.PunctuationExtraMinMs", 0, 10000));
            list.Add(Num("标点额外停顿上限(ms)", "Engine.Rhythm.PunctuationExtraMaxMs", 0, 20000));
            return list;
        }

        private static List<FieldSpec> TypoFields()
        {
            List<FieldSpec> list = new List<FieldSpec>();
            list.Add(Flag("启用错字模拟", "Engine.Typo.Enabled"));
            list.Add(Num("同音字错误率(%)", "Engine.Typo.HomophonePct", 0, 100));
            list.Add(Num("前后鼻音错误率(%)", "Engine.Typo.NasalPct", 0, 100));
            list.Add(Num("打头重打概率(%)", "Engine.Typo.HeadRedoPct", 0, 100));
            list.Add(Num("漏字概率(%)", "Engine.Typo.OmissionPct", 0, 100));
            list.Add(Num("叠词概率(%)", "Engine.Typo.RepeatWordPct", 0, 100));
            list.Add(Num("每句错字上限", "Engine.Typo.MaxTyposPerSentence", 0, 20));
            return list;
        }

        internal string InputText
        {
            get { return _input.Text; }
        }

        internal void ApplyToConfig()
        {
            _cfg.Normalize();
        }

        internal void SetStateLine(string text)
        {
            try
            {
                if (_stateLine.Text != text) _stateLine.Text = text;
            }
            catch (Exception ex)
            {
                Log.Error("更新状态行失败", ex);
            }
        }

        internal void SetHotkeyLine(string text)
        {
            try
            {
                if (_hotkeyLine.Text != text) _hotkeyLine.Text = text;
            }
            catch (Exception ex)
            {
                Log.Error("更新热键行失败", ex);
            }
        }

        internal void SetAdminLine(string text)
        {
            try
            {
                if (_adminLine.Text != text) _adminLine.Text = text;
            }
            catch (Exception ex)
            {
                Log.Error("更新权限行失败", ex);
            }
        }

        internal void SetHint(string text)
        {
            try
            {
                if (_hintLine.Text != text) _hintLine.Text = text;
            }
            catch (Exception ex)
            {
                Log.Error("更新提示行失败", ex);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 关闭按钮 = 收起到托盘；真退出只走托盘菜单
            if (!AllowClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }
    }
}
