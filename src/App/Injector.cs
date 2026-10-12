using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using SimIme.Engine;

namespace SimIme.App
{
    // 一个字符步实际要发出的动作：先敲拼音预览，再退格这些字母，最后上屏真正的字符
    internal sealed class CharEmission
    {
        internal string Preview = "";
        internal int PreviewBackspaces;
        internal string Commit = "";
    }

    internal sealed class Injector
    {
        private readonly AppConfig _cfg;
        private IDataObject _savedClipboard;

        internal Injector(AppConfig cfg)
        {
            _cfg = cfg;
        }

        // ---------- 原语 ----------

        // KEYEVENTF_UNICODE 一次只发一个 UTF-16 码元：代理对(emoji 等)由两次事件组成，系统自行合成
        internal static bool SendUnicodeUnit(char unit)
        {
            try
            {
                Native.INPUT[] inputs = new Native.INPUT[2];
                inputs[0].type = Native.INPUT_KEYBOARD;
                inputs[0].u.ki.wVk = 0;
                inputs[0].u.ki.wScan = unit;
                inputs[0].u.ki.dwFlags = Native.KEYEVENTF_UNICODE;
                inputs[1] = inputs[0];
                inputs[1].u.ki.dwFlags = Native.KEYEVENTF_UNICODE | Native.KEYEVENTF_KEYUP;
                uint sent = Native.SendInput(2, inputs, Marshal.SizeOf(typeof(Native.INPUT)));
                if (sent == 2) return true;
                LogSendInputFailure("SendInput(Unicode)", sent);
                return false;
            }
            catch (Exception ex)
            {
                Log.Error("SendInput(Unicode) 异常", ex);
                return false;
            }
        }

        internal static bool TapVirtualKey(ushort vk)
        {
            try
            {
                Native.INPUT[] inputs = new Native.INPUT[2];
                inputs[0].type = Native.INPUT_KEYBOARD;
                inputs[0].u.ki.wVk = vk;
                inputs[0].u.ki.wScan = 0;
                inputs[0].u.ki.dwFlags = 0;
                inputs[1] = inputs[0];
                inputs[1].u.ki.dwFlags = Native.KEYEVENTF_KEYUP;
                uint sent = Native.SendInput(2, inputs, Marshal.SizeOf(typeof(Native.INPUT)));
                if (sent == 2) return true;
                LogSendInputFailure("SendInput(VK 0x" + vk.ToString("X2") + ")", sent);
                return false;
            }
            catch (Exception ex)
            {
                Log.Error("SendInput(VK) 异常", ex);
                return false;
            }
        }

        // SendInput 被拒绝时给出可执行的诊断：UIPI 跨权限注入是最常见原因（win32=5 ERROR_ACCESS_DENIED）
        internal static void LogSendInputFailure(string what, uint sent)
        {
            int err = Marshal.GetLastWin32Error();
            Log.Error(string.Format("{0} 返回 {1} win32={2}", what, sent, err));
            if (err == Native.ERROR_ACCESS_DENIED)
            {
                Log.Error("目标窗口可能以管理员权限运行，Windows 拒绝跨权限注入；请以管理员身份运行本程序，或改用剪贴板兜底（Inject.Method=Clipboard 或开启 Inject.ClipboardFallback）");
            }
            else if (sent == 0)
            {
                Log.Error("SendInput 被系统拒绝（可能目标进程权限更高或输入被拦截）；请以管理员身份运行本程序，或改用剪贴板兜底");
            }
        }

        internal static bool SendCtrlV()
        {
            return SendCtrlKey(Native.VK_V);
        }

        internal static bool SendCtrlKey(ushort vk)
        {
            try
            {
                Native.INPUT[] inputs = new Native.INPUT[4];
                inputs[0].type = Native.INPUT_KEYBOARD;
                inputs[0].u.ki.wVk = Native.VK_CONTROL;
                inputs[1] = inputs[0];
                inputs[1].u.ki.wVk = vk;
                inputs[2] = inputs[1];
                inputs[2].u.ki.dwFlags = Native.KEYEVENTF_KEYUP;
                inputs[3] = inputs[0];
                inputs[3].u.ki.dwFlags = Native.KEYEVENTF_KEYUP;
                uint sent = Native.SendInput(4, inputs, Marshal.SizeOf(typeof(Native.INPUT)));
                if (sent == 4) return true;
                LogSendInputFailure("SendInput(Ctrl+0x" + vk.ToString("X2") + ")", sent);
                return false;
            }
            catch (Exception ex)
            {
                Log.Error("SendInput(Ctrl+V) 异常", ex);
                return false;
            }
        }

        // ---------- 按步骤执行 ----------

        internal bool ApplyStep(Step step)
        {
            return ApplyStep(step, null);
        }

        // chinesePreview：汉字步要「先敲的拼音字母」（调用方用 IPinyinTable 取；null/空=不模拟）
        internal bool ApplyStep(Step step, string chinesePreview)
        {
            if (step == null) return true;
            switch (step.Kind)
            {
                case StepKind.Char:
                    CharEmission emission = PlanEmission(step, chinesePreview, _cfg.Inject.ImeSimulation);
                    if (emission.Preview.Length > 0 && !EmitImePreview(emission)) return false;
                    if (UseClipboardForChars())
                    {
                        bool pasted = PasteTextViaClipboard(emission.Commit);
                        if (pasted) _charsSent += emission.Commit == null ? 0 : emission.Commit.Length;
                        return pasted;
                    }
                    bool sentChars = SendText(emission.Commit);
                    if (sentChars) _charsSent += emission.Commit == null ? 0 : emission.Commit.Length;
                    return sentChars;
                case StepKind.LineBreak:
                    return TapVirtualKey(Native.VK_RETURN);
                case StepKind.Backspace:
                    bool backOk = TapVirtualKey(Native.VK_BACK);
                    if (backOk) _backspacesSent++;
                    return backOk;
                case StepKind.Sleep:
                    return true;
                default:
                    return true;
            }
        }

        // 本次注入统计（生产可见）：默认模式下 Letters 必须恒为 0 —— 目标收不到任何字母
        private int _lettersSent;
        private int _charsSent;
        private int _backspacesSent;

        internal int LettersSent { get { return _lettersSent; } }
        internal int CharsSent { get { return _charsSent; } }
        internal int BackspacesSent { get { return _backspacesSent; } }

        internal string DescribeCounters()
        {
            return string.Format("本次注入统计：字母={0} 汉字/字符={1} 退格={2}", _lettersSent, _charsSent, _backspacesSent);
        }

        // 组串阶段原子动作（PinyinInTarget=true 时才真的发给目标）
        internal bool SendLetter(char letter)
        {
            bool ok = SendUnicodeUnit(letter);
            if (ok) _lettersSent++;
            return ok;
        }

        internal bool SendBackspace()
        {
            bool ok = TapVirtualKey(Native.VK_BACK);
            if (ok) _backspacesSent++;
            return ok;
        }

        internal static bool IsChineseChar(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length != 1) return false;
            char c = text[0];
            return c >= 0x4E00 && c <= 0x9FFF;
        }

        // 纯函数：给定步骤与拼音，算出「预览字母 / 退格数 / 最终上屏文本」——回放测试与运行期共用同一份逻辑
        internal static CharEmission PlanEmission(Step step, string chinesePreview, bool imeSimulation)
        {
            CharEmission emission = new CharEmission();
            if (step == null || step.Kind != StepKind.Char || string.IsNullOrEmpty(step.Text)) return emission;
            emission.Commit = step.Text;
            if (imeSimulation && IsChineseChar(step.Text) && !string.IsNullOrEmpty(chinesePreview))
            {
                emission.Preview = chinesePreview;
            }
            emission.PreviewBackspaces = emission.Preview.Length;
            return emission;
        }

        // 真输入法观感：逐字母敲拼音 -> 停顿 -> 逐字母退格（随后由调用方上屏汉字）
        private bool EmitImePreview(CharEmission emission)
        {
            Log.Info(string.Format("IME 预览：键入拼音 '{0}'（{1} 个字母）→ 停顿 {2}ms → 退格 {3} 次 → 上屏 '{4}'",
                emission.Preview, emission.Preview.Length, _cfg.Inject.ImePreviewMs, emission.PreviewBackspaces, emission.Commit));
            int sent = 0;
            for (int i = 0; i < emission.Preview.Length; i++)
            {
                if (!SendUnicodeUnit(emission.Preview[i]))
                {
                    CleanupPreview(sent);
                    return false;
                }
                sent++;
            }
            int wait = _cfg.Inject.ImePreviewMs;
            if (wait > 0) Thread.Sleep(wait);
            for (int i = 0; i < sent; i++)
            {
                if (!TapVirtualKey(Native.VK_BACK))
                {
                    CleanupPreview(sent - i);
                    return false;
                }
            }
            return true;
        }

        // 失败清理：把已经敲出去但还没删掉的预览字母退格删净，避免污染目标文本
        private void CleanupPreview(int remainingLetters)
        {
            for (int i = 0; i < remainingLetters; i++)
            {
                if (!TapVirtualKey(Native.VK_BACK)) break;
            }
            Log.Error("拼音预览中途失败，已尝试回滚 " + remainingLetters + " 个字母（见上一条 SendInput 日志）");
        }

        // 一个 Step 的 Text 可能是 1~2 个 UTF-16 码元（代理对），逐码元发出
        internal static bool SendText(string text)
        {
            if (string.IsNullOrEmpty(text)) return true;
            for (int i = 0; i < text.Length; i++)
            {
                if (!SendUnicodeUnit(text[i])) return false;
            }
            return true;
        }

        internal bool UseClipboardForChars()
        {
            return string.Equals(_cfg.Inject.Method, "Clipboard", StringComparison.OrdinalIgnoreCase);
        }

        // ---------- 剪贴板兜底 ----------

        internal void SaveClipboard()
        {
            if (_savedClipboard != null) return;
            try
            {
                if (Clipboard.ContainsText() || Clipboard.ContainsImage() || Clipboard.ContainsData(DataFormats.FileDrop))
                {
                    _savedClipboard = Clipboard.GetDataObject();
                }
                else
                {
                    _savedClipboard = Clipboard.GetDataObject();
                }
            }
            catch (Exception ex)
            {
                Log.Info("读取原剪贴板失败：" + ex.Message);
                _savedClipboard = null;
            }
        }

        internal void RestoreClipboard()
        {
            if (!_cfg.Inject.RestoreClipboard) return;
            if (_savedClipboard == null) return;
            try
            {
                Clipboard.SetDataObject(_savedClipboard, true, 3, 60);
            }
            catch (Exception ex)
            {
                Log.Error("恢复原剪贴板失败", ex);
            }
            finally
            {
                _savedClipboard = null;
            }
        }

        internal bool PasteTextViaClipboard(string text)
        {
            if (string.IsNullOrEmpty(text)) return true;
            try
            {
                SetClipboardText(text, 3, 50);
                int wait = _cfg.Inject.ClipboardPasteDelayMs;
                if (wait > 0) Thread.Sleep(wait);
                return SendCtrlV();
            }
            catch (Exception ex)
            {
                Log.Error("剪贴板粘贴失败", ex);
                return false;
            }
        }

        internal static void SetClipboardText(string text, int retries, int delayMs)
        {
            Exception last = null;
            for (int i = 0; i < retries; i++)
            {
                try
                {
                    Clipboard.SetDataObject(text, true, 2, 40);
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    Thread.Sleep(delayMs);
                }
            }
            if (last != null) throw last;
        }
    }
}
