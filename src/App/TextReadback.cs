using System;
using System.Drawing;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Forms;

namespace SimIme.App
{
    internal sealed class ReadBackResult
    {
        internal string Text = "";
        internal string Method = "无";
        internal bool Valid;
    }

    // --verify 的回读：先定位「真正的文本载体」，再按 WM_GETTEXT(经典控件) → UIA → 剪贴板 依次尝试。
    // 决定性护栏：读回内容若等于窗口标题，一律判为无效读回（顶层窗口的 WM_GETTEXT 返回的就是标题）。
    internal static class TextReadback
    {
        internal static ReadBackResult Read(IntPtr target, AppConfig cfg)
        {
            ReadBackResult result = new ReadBackResult();
            string title = Native.GetWindowTitle(target);
            int timeout = cfg == null ? 800 : cfg.Anchor.UiaTimeoutMs;

            // 路径1：焦点控件 + 经典文本控件类名（Edit / RichEdit…）
            try
            {
                IntPtr focus = FocusedControl(target);
                string cls = focus == IntPtr.Zero ? "" : Native.GetWindowClass(focus);
                if (focus != IntPtr.Zero && IsClassicTextClass(cls))
                {
                    string text = Native.ReadWindowText(focus, 200000);
                    string method = "WM_GETTEXT(焦点控件 class=" + cls + ")";
                    if (IsUsable(text, title))
                    {
                        result.Text = text;
                        result.Method = method;
                        result.Valid = true;
                        Log.Info("--verify 回读：" + method + " 长度=" + text.Length);
                        return result;
                    }
                    Log.Info("--verify 回读：" + method + " 结果不可用（空值或等于窗口标题），转下一路径；长度=" + (text == null ? -1 : text.Length));
                }
                else if (focus != IntPtr.Zero)
                {
                    Log.Info("--verify 回读：焦点控件 class=" + cls + " 不是经典文本控件，跳过 WM_GETTEXT");
                }
                else
                {
                    Log.Info("--verify 回读：取不到焦点控件，跳过 WM_GETTEXT");
                }
            }
            catch (Exception ex)
            {
                Log.Error("--verify 回读路径1（WM_GETTEXT）异常", ex);
            }

            // 路径2：UIA（WinUI/UWP/WPF/Chromium 等现代控件）
            try
            {
                string detail;
                string text = TryUiaRead(target, timeout, out detail);
                string method = string.IsNullOrEmpty(detail) ? "UIA(TextPattern)" : "UIA" + detail;
                if (IsUsable(text, title))
                {
                    result.Text = text;
                    result.Method = method;
                    result.Valid = true;
                    Log.Info("--verify 回读：" + method + " 长度=" + text.Length);
                    return result;
                }
                Log.Info("--verify 回读：" + method + " 结果不可用（空值或等于窗口标题），转下一路径；长度=" + (text == null ? -1 : text.Length));
            }
            catch (Exception ex)
            {
                Log.Error("--verify 回读路径2（UIA）异常", ex);
            }

            // 路径3：剪贴板 Ctrl+A / Ctrl+C（最后兜底，必定会真的尝试）
            try
            {
                string text = ReadViaClipboard(target);
                string method = "剪贴板(Ctrl+A/Ctrl+C)";
                if (IsUsable(text, title))
                {
                    result.Text = text;
                    result.Method = method;
                    result.Valid = true;
                    Log.Info("--verify 回读：" + method + " 长度=" + text.Length);
                    return result;
                }
                Log.Info("--verify 回读：" + method + " 结果不可用（空值或等于窗口标题）；长度=" + (text == null ? -1 : text.Length));
            }
            catch (Exception ex)
            {
                Log.Error("--verify 回读路径3（剪贴板）异常", ex);
            }

            result.Method = "三条路径都未取到有效文本";
            return result;
        }

        // 决定性护栏：读回内容等于窗口标题（或去掉前后空白后相等）即判无效
        internal static bool IsTitleEcho(string text, string windowTitle)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(windowTitle)) return false;
            string a = text.Trim();
            string b = windowTitle.Trim();
            if (a.Length == 0) return false;
            return string.Equals(a, b, StringComparison.Ordinal);
        }

        internal static bool IsUsable(string text, string windowTitle)
        {
            if (string.IsNullOrEmpty(text)) return false;
            if (IsTitleEcho(text, windowTitle)) return false;
            return true;
        }

        // 焦点控件：优先 GetGUIThreadInfo(hwndFocus)，退 AttachThreadInput + GetFocus
        internal static IntPtr FocusedControl(IntPtr target)
        {
            try
            {
                uint pid;
                int tid = Native.GetWindowThreadProcessId(target, out pid);
                if (tid != 0)
                {
                    Native.GUITHREADINFO info = new Native.GUITHREADINFO();
                    info.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.GUITHREADINFO));
                    if (Native.GetGUIThreadInfo(tid, ref info) && info.hwndFocus != IntPtr.Zero) return info.hwndFocus;
                }
                int ourTid = Native.GetCurrentThreadId();
                if (tid != 0 && ourTid != tid && Native.AttachThreadInput(ourTid, tid, true))
                {
                    try
                    {
                        IntPtr focus = Native.GetFocus();
                        if (focus != IntPtr.Zero) return focus;
                    }
                    finally
                    {
                        Native.AttachThreadInput(ourTid, tid, false);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("取焦点控件失败", ex);
            }
            return IntPtr.Zero;
        }

        // 经典文本控件：WinForms 的 TextBox 类名形如 WindowsForms10.EDIT.app.0.xxx
        internal static bool IsClassicTextClass(string className)
        {
            if (string.IsNullOrEmpty(className)) return false;
            string c = className.ToUpperInvariant();
            if (c.StartsWith("EDIT", StringComparison.Ordinal)) return true;
            if (c.StartsWith("RICHEDIT", StringComparison.Ordinal)) return true;
            if (c.IndexOf(".EDIT.", StringComparison.Ordinal) >= 0) return true;
            if (c.IndexOf(".RICHEDIT", StringComparison.Ordinal) >= 0) return true;
            if (c.StartsWith("TEXTBOX", StringComparison.Ordinal)) return true;
            return false;
        }

        internal static string TryUiaRead(IntPtr target, int timeoutMs, out string detail)
        {
            detail = "";
            if (timeoutMs < 100) timeoutMs = 100;
            string localText = null;
            string localDetail = "";
            ManualResetEvent done = new ManualResetEvent(false);
            Thread worker = new Thread(delegate()
            {
                try
                {
                    localText = UiaReadWorker(target, out localDetail);
                }
                catch (Exception ex)
                {
                    Log.Error("UIA 回读失败", ex);
                }
                finally
                {
                    try { done.Set(); } catch { }
                }
            });
            worker.IsBackground = true;
            worker.Name = "SimIme.UiaRead";
            try { worker.SetApartmentState(ApartmentState.MTA); } catch { }
            try { worker.Start(); }
            catch (Exception ex)
            {
                Log.Error("UIA 回读线程启动失败", ex);
                return null;
            }
            bool finished = false;
            try { finished = done.WaitOne(timeoutMs); } catch { }
            if (!finished)
            {
                detail = "(超时)";
                Log.Info("UIA 回读超时（" + timeoutMs + "ms）");
                return null;
            }
            detail = localDetail;
            return localText;
        }

        private static string UiaReadWorker(IntPtr target, out string detail)
        {
            detail = "";
            AutomationElement focused = null;
            try { focused = AutomationElement.FocusedElement; }
            catch (Exception ex) { Log.Info("UIA FocusedElement 不可用：" + ex.Message); }
            string text = TryReadElement(focused, out detail);
            if (!string.IsNullOrEmpty(text)) return text;

            AutomationElement root = null;
            try { if (target != IntPtr.Zero) root = AutomationElement.FromHandle(target); }
            catch (Exception ex) { Log.Info("UIA FromHandle 不可用：" + ex.Message); }
            if (root == null) return null;
            text = TryReadElement(root, out detail);
            if (!string.IsNullOrEmpty(text)) return text;
            try
            {
                Condition cond = new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true);
                AutomationElement hit = root.FindFirst(TreeScope.Subtree, cond);
                if (hit != null)
                {
                    text = TryReadElement(hit, out detail);
                    if (!string.IsNullOrEmpty(text)) return text;
                }
                Condition cond2 = new PropertyCondition(AutomationElement.IsValuePatternAvailableProperty, true);
                AutomationElement hit2 = root.FindFirst(TreeScope.Subtree, cond2);
                if (hit2 != null)
                {
                    text = TryReadElement(hit2, out detail);
                    if (!string.IsNullOrEmpty(text)) return text;
                }
            }
            catch (Exception ex)
            {
                detail = "(子树搜索失败)";
                Log.Info("UIA 回读子树搜索失败：" + ex.Message);
            }
            return null;
        }

        private static string TryReadElement(AutomationElement element, out string detail)
        {
            detail = "";
            if (element == null) return null;
            try
            {
                object patternObject = null;
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out patternObject))
                {
                    ValuePattern value = patternObject as ValuePattern;
                    if (value != null)
                    {
                        string v = value.Current.Value;
                        if (!string.IsNullOrEmpty(v)) { detail = "(ValuePattern)"; return v; }
                    }
                }
            }
            catch (Exception ex)
            {
                detail = "(ValuePattern:" + ex.GetType().Name + ")";
            }
            try
            {
                object patternObject = null;
                if (element.TryGetCurrentPattern(TextPattern.Pattern, out patternObject))
                {
                    TextPattern tp = patternObject as TextPattern;
                    if (tp != null)
                    {
                        TextPatternRange range = tp.DocumentRange;
                        if (range != null)
                        {
                            string v = range.GetText(200000);
                            if (!string.IsNullOrEmpty(v)) { detail = "(TextPattern)"; return v; }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                detail = "(TextPattern:" + ex.GetType().Name + ")";
            }
            return null;
        }

        // 剪贴板兜底：哨兵串区分「复制真的发生」，结束时无条件还原用户剪贴板
        internal static string ReadViaClipboard(IntPtr target)
        {
            IDataObject saved = null;
            try
            {
                try { saved = Clipboard.GetDataObject(); }
                catch (Exception ex) { Log.Info("--verify 读取原剪贴板失败：" + ex.Message); }
                string sentinel = "<<SimImeVerify-" + Guid.NewGuid().ToString("N") + ">>";
                Injector.SetClipboardText(sentinel, 3, 50);
                if (!Injector.SendCtrlKey(Native.VK_A)) return null;
                Thread.Sleep(150);
                if (!Injector.SendCtrlKey(Native.VK_C)) return null;
                Thread.Sleep(300);
                string got = null;
                try { if (Clipboard.ContainsText()) got = Clipboard.GetText(); }
                catch (Exception ex) { Log.Info("--verify 读取剪贴板失败：" + ex.Message); }
                if (got != null && string.Equals(got, sentinel, StringComparison.Ordinal)) return null;   // 复制没发生
                return got;
            }
            catch (Exception ex)
            {
                Log.Error("--verify 剪贴板回读失败", ex);
                return null;
            }
            finally
            {
                if (saved != null)
                {
                    try { Clipboard.SetDataObject(saved, true, 3, 60); }
                    catch (Exception ex) { Log.Error("--verify 还原剪贴板失败", ex); }
                }
            }
        }
    }
}
