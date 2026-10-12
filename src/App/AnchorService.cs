using System;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Forms;

namespace SimIme.App
{
    internal sealed class AnchorInfo
    {
        internal IntPtr Hwnd = IntPtr.Zero;          // 注入目标（顶层窗口）
        internal IntPtr FocusedHwnd = IntPtr.Zero;   // 焦点控件
        internal bool HasCaret;
        internal Rectangle CaretRect;
        internal Point MousePos;
        internal string Method = "None";

        internal string Describe()
        {
            return string.Format("hwnd=0x{0:X} focus=0x{1:X} method={2} caret={3} mouse=({4},{5})",
                Hwnd.ToInt64(), FocusedHwnd.ToInt64(), Method,
                HasCaret ? string.Format("({0},{1},{2},{3})", CaretRect.X, CaretRect.Y, CaretRect.Width, CaretRect.Height) : "无",
                MousePos.X, MousePos.Y);
        }
    }

    internal static class AnchorService
    {
        // L1 Win32 GetGUIThreadInfo 插入符 -> L2 UI Automation -> L3 热键时刻鼠标位置
        internal static AnchorInfo Resolve(AppConfig cfg)
        {
            AnchorInfo a = new AnchorInfo();
            a.MousePos = Native.CursorPosition();
            a.Hwnd = Native.GetForegroundWindow();
            string method = cfg.Anchor.Method;
            try
            {
                if (method == "Auto" || method == "Caret") TryWin32Caret(a);

                if (!a.HasCaret && (method == "Auto" || method == "Uia"))
                {
                    Rectangle rect;
                    IntPtr focus;
                    string how;
                    if (TryUiaCaret(a.Hwnd, cfg.Anchor.UiaTimeoutMs, out rect, out focus, out how))
                    {
                        a.HasCaret = true;
                        a.CaretRect = rect;
                        a.Method = "Uia(" + how + ")";
                        if (focus != IntPtr.Zero) a.FocusedHwnd = focus;
                    }
                }

                if (!a.HasCaret) a.Method = "Mouse";
            }
            catch (Exception ex)
            {
                Log.Error("锚定过程异常（已降级为鼠标位置）", ex);
                if (!a.HasCaret) a.Method = "Mouse";
            }
            if (a.FocusedHwnd == IntPtr.Zero) a.FocusedHwnd = a.Hwnd;
            Log.Info("锚定结果：" + a.Describe());
            return a;
        }

        // 点击定位模式：以落点所在顶层窗口为目标，落点即锚点
        internal static AnchorInfo FromClick(Point screenPoint)
        {
            AnchorInfo a = new AnchorInfo();
            a.MousePos = screenPoint;
            a.HasCaret = true;
            a.CaretRect = new Rectangle(screenPoint.X, screenPoint.Y, 2, 2);
            a.Method = "Click";
            try
            {
                IntPtr hit = Native.WindowFromPoint(new Native.POINT(screenPoint.X, screenPoint.Y));
                IntPtr root = IntPtr.Zero;
                if (hit != IntPtr.Zero) root = Native.GetAncestor(hit, Native.GA_ROOT);
                a.Hwnd = root != IntPtr.Zero ? root : hit;
                a.FocusedHwnd = hit;
            }
            catch (Exception ex)
            {
                Log.Error("点击定位取窗口失败", ex);
            }
            if (a.Hwnd == IntPtr.Zero) a.Hwnd = Native.GetForegroundWindow();
            Log.Info("点击锚定：" + a.Describe() + " title=" + Native.GetWindowTitle(a.Hwnd));
            return a;
        }

        // 悬浮条贴靠矩形：优先插入符，其次目标窗口客户区左上，最后鼠标点
        internal static Rectangle BarAnchorRect(AnchorInfo a)
        {
            if (a == null) return new Rectangle(0, 0, 1, 1);
            if (a.HasCaret) return a.CaretRect;
            if (a.Hwnd != IntPtr.Zero)
            {
                Native.RECT cr;
                if (Native.GetClientRect(a.Hwnd, out cr))
                {
                    Native.POINT origin = new Native.POINT(0, 0);
                    if (Native.ClientToScreen(a.Hwnd, ref origin))
                    {
                        return new Rectangle(origin.X, origin.Y, Math.Max(1, cr.Right), Math.Max(1, cr.Bottom));
                    }
                }
            }
            return new Rectangle(a.MousePos.X, a.MousePos.Y, 1, 1);
        }

        internal static Point AnchorPoint(AnchorInfo a)
        {
            Rectangle r = BarAnchorRect(a);
            return new Point(r.Left, r.Top);
        }

        private static void TryWin32Caret(AnchorInfo a)
        {
            IntPtr target = a.Hwnd;
            if (target == IntPtr.Zero) return;
            uint pid;
            int tid = Native.GetWindowThreadProcessId(target, out pid);
            if (tid == 0) return;
            Native.GUITHREADINFO info = new Native.GUITHREADINFO();
            info.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.GUITHREADINFO));
            if (!Native.GetGUIThreadInfo(tid, ref info)) return;
            if (info.hwndFocus != IntPtr.Zero) a.FocusedHwnd = info.hwndFocus;
            if (info.hwndCaret == IntPtr.Zero) return;
            Rectangle raw = info.rcCaret.ToRectangle();
            Rectangle screen = ClientCaretToScreen(info.hwndCaret, raw);
            Log.Info(string.Format("caret 坐标判定（单位 client→screen）：raw=({0},{1},{2},{3})[client] 采用=({4},{5},{6},{7})[screen] hwndCaret=0x{8:X}",
                raw.X, raw.Y, raw.Width, raw.Height,
                screen.X, screen.Y, screen.Width, screen.Height, info.hwndCaret.ToInt64()));
            if (screen.IsEmpty)
            {
                Log.Error("插入符坐标转换失败（ClientToScreen 失败），本次回退鼠标位置");
                return;
            }
            a.HasCaret = true;
            a.CaretRect = screen;
            a.Method = "Win32Caret";
        }

        // 运行期跟随光标用：只走 Win32 路径（GetGUIThreadInfo → rcCaret → ClientToScreen），开销极小，
        // 不做 UIA。取不到就返回 false，由调用方保持上一次位置（绝不跳到 0,0）。
        internal static bool TryRefreshCaret(IntPtr targetHwnd, out Rectangle caret)
        {
            caret = Rectangle.Empty;
            try
            {
                IntPtr hwnd = targetHwnd;
                if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) hwnd = Native.GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return false;
                uint pid;
                int tid = Native.GetWindowThreadProcessId(hwnd, out pid);
                if (tid == 0) return false;
                Native.GUITHREADINFO info = new Native.GUITHREADINFO();
                info.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.GUITHREADINFO));
                if (!Native.GetGUIThreadInfo(tid, ref info)) return false;
                if (info.hwndCaret == IntPtr.Zero) return false;
                Rectangle screen = ClientCaretToScreen(info.hwndCaret, info.rcCaret.ToRectangle());
                if (screen.IsEmpty) return false;
                caret = screen;
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("刷新插入符位置失败（保持上一次位置）", ex);
                return false;
            }
        }

        // GetGUIThreadInfo 的 rcCaret 恒为 hwndCaret 的客户区坐标（没有二义性），
        // 必须经 ClientToScreen 转成屏幕坐标才能用来摆悬浮条；转换失败返回 Empty 由调用方回退。
        internal static Rectangle ClientCaretToScreen(IntPtr hwndCaret, Rectangle clientRect)
        {
            try
            {
                if (hwndCaret == IntPtr.Zero) return Rectangle.Empty;
                Native.POINT point = new Native.POINT(clientRect.Left, clientRect.Top);
                if (!Native.ClientToScreen(hwndCaret, ref point)) return Rectangle.Empty;
                int width = clientRect.Width < 1 ? 1 : clientRect.Width;
                int height = clientRect.Height < 1 ? 16 : clientRect.Height;
                return new Rectangle(point.X, point.Y, width, height);
            }
            catch (Exception ex)
            {
                Log.Error("插入符客户区→屏幕坐标转换异常", ex);
                return Rectangle.Empty;
            }
        }

        // UIA 可能阻塞（跨进程 COM），必须放后台线程并设超时；超时则放弃该次锚定
        private static bool TryUiaCaret(IntPtr hwnd, int timeoutMs, out Rectangle rect, out IntPtr focusHwnd, out string how)
        {
            rect = Rectangle.Empty;
            focusHwnd = IntPtr.Zero;
            how = "";
            if (timeoutMs < 50) timeoutMs = 50;

            Rectangle localRect = Rectangle.Empty;
            IntPtr localFocus = IntPtr.Zero;
            string localHow = "";
            bool ok = false;
            ManualResetEvent done = new ManualResetEvent(false);

            Thread worker = new Thread(delegate()
            {
                try
                {
                    ok = UiaWorker(hwnd, timeoutMs, out localRect, out localFocus, out localHow);
                }
                catch (Exception ex)
                {
                    Log.Error("UIA 锚定失败", ex);
                }
                finally
                {
                    try { done.Set(); } catch { }
                }
            });
            worker.IsBackground = true;
            worker.Name = "SimIme.UiaAnchor";
            try { worker.SetApartmentState(ApartmentState.MTA); } catch { }
            try { worker.Start(); } catch (Exception ex) { Log.Error("UIA 线程启动失败", ex); return false; }

            bool finished = false;
            try { finished = done.WaitOne(timeoutMs); } catch { }
            if (!finished)
            {
                Log.Info("UIA 锚定超时（" + timeoutMs + "ms），放弃本次 UIA 路径");
                return false;
            }
            rect = localRect;
            focusHwnd = localFocus;
            how = localHow;
            return ok;
        }

        // Chromium 首次 UIA 文本查询常常拿不到选区：在预算内重试 2~3 次，再退元素外接矩形，最后才由调用方退鼠标位置
        private static bool UiaWorker(IntPtr hwnd, int timeoutMs, out Rectangle rect, out IntPtr focusHwnd, out string how)
        {
            rect = Rectangle.Empty;
            focusHwnd = IntPtr.Zero;
            how = "";
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(100, timeoutMs - 60));
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0)
                {
                    if (DateTime.UtcNow >= deadline) break;
                    Thread.Sleep(180);
                }
                Rectangle hitRect;
                IntPtr hitFocus;
                string hitHow;
                if (TryTextCaretOnce(hwnd, out hitRect, out hitFocus, out hitHow))
                {
                    rect = hitRect;
                    focusHwnd = hitFocus;
                    how = hitHow;
                    Log.Info(string.Format("UIA 文本插入符命中：第 {0} 次尝试，方式 {1}，矩形=({2},{3},{4},{5})",
                        attempt + 1, hitHow, hitRect.X, hitRect.Y, hitRect.Width, hitRect.Height));
                    return true;
                }
                Log.Info(string.Format("UIA 第 {0} 次尝试未取到文本插入符", attempt + 1));
            }

            if (TryElementRectOnce(hwnd, out rect, out focusHwnd))
            {
                how = "ElementRect";
                Log.Info(string.Format("UIA 只取到元素外接矩形（未拿到插入符），矩形=({0},{1},{2},{3})", rect.X, rect.Y, rect.Width, rect.Height));
                return true;
            }
            return false;
        }

        // 只做「文本插入符」：TextPattern2(若存在) -> TextPattern.GetSelection；不含元素矩形兜底
        private static bool TryTextCaretOnce(IntPtr hwnd, out Rectangle rect, out IntPtr focusHwnd, out string how)
        {
            rect = Rectangle.Empty;
            focusHwnd = IntPtr.Zero;
            how = "";
            Rectangle found;

            // 1) 有键盘焦点的元素（Chrome/Electron/UWP 的文档或编辑元素就是它）
            AutomationElement focused = null;
            try { focused = AutomationElement.FocusedElement; }
            catch (Exception ex) { Log.Info("UIA FocusedElement 不可用：" + ex.Message); }
            if (focused != null && TryCaretFromElement(focused, out found, out how))
            {
                rect = found;
                focusHwnd = SafeNativeHandle(focused);
                return true;
            }

            // 1b) Chromium 的 TextPattern 常挂在 Document 祖先元素上：从焦点元素向上逐级试探（限 8 层）
            if (focused != null)
            {
                AutomationElement node = focused;
                TreeWalker walker = TreeWalker.ControlViewWalker;
                for (int depth = 0; depth < 8 && node != null; depth++)
                {
                    AutomationElement parent = null;
                    try { parent = walker.GetParent(node); }
                    catch (Exception ex) { Log.Info("UIA 取父元素失败：" + ex.Message); break; }
                    if (parent == null) break;
                    if (TryCaretFromElement(parent, out found, out how))
                    {
                        rect = found;
                        focusHwnd = SafeNativeHandle(parent);
                        return true;
                    }
                    node = parent;
                }
            }

            // 2) 前台窗口元素本身 + 受限子树搜索
            AutomationElement root = null;
            try { if (hwnd != IntPtr.Zero) root = AutomationElement.FromHandle(hwnd); }
            catch (Exception ex) { Log.Info("UIA FromHandle 不可用：" + ex.Message); }
            if (root != null)
            {
                if (TryCaretFromElement(root, out found, out how))
                {
                    rect = found;
                    return true;
                }
                try
                {
                    Condition cond = new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true);
                    AutomationElement hit = root.FindFirst(TreeScope.Subtree, cond);
                    if (hit != null && TryCaretFromElement(hit, out found, out how))
                    {
                        rect = found;
                        focusHwnd = SafeNativeHandle(hit);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Log.Info("UIA 子树搜索失败：" + ex.Message);
                }
            }

            return false;
        }

        // 元素外接矩形兜底：焦点元素优先，其次前台窗口元素
        private static bool TryElementRectOnce(IntPtr hwnd, out Rectangle rect, out IntPtr focusHwnd)
        {
            rect = Rectangle.Empty;
            focusHwnd = IntPtr.Zero;
            Rectangle found;
            try
            {
                AutomationElement focused = AutomationElement.FocusedElement;
                if (focused != null && TryElementRect(focused, out found))
                {
                    rect = found;
                    focusHwnd = SafeNativeHandle(focused);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Info("UIA 焦点元素矩形不可用：" + ex.Message);
            }
            try
            {
                if (hwnd == IntPtr.Zero) return false;
                AutomationElement root = AutomationElement.FromHandle(hwnd);
                if (root != null && TryElementRect(root, out found))
                {
                    rect = found;
                    focusHwnd = SafeNativeHandle(root);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Info("UIA 窗口元素矩形不可用：" + ex.Message);
            }
            return false;
        }

        private static bool TryCaretFromElement(AutomationElement element, out Rectangle rect, out string how)
        {
            rect = Rectangle.Empty;
            how = "";
            if (element == null) return false;
            if (TryTextPattern2Caret(element, out rect)) { how = "TextPattern2"; return true; }
            if (TryTextSelectionCaret(element, out rect)) { how = "TextSelection"; return true; }
            return false;
        }

        // 契约要求优先走 TextPattern2.GetCaretRange。实测本机 .NET Framework 4.8 的
        // UIAutomationClient.dll（v4.8 参考程序集与 GAC v4.0_4.0.0.0 均已核对）不含 TextPattern2 类型，
        // 故这里用反射探测：运行时存在就走它，不存在就静默落到 TextPattern.GetSelection。
        private static bool TryTextPattern2Caret(AutomationElement element, out Rectangle rect)
        {
            rect = Rectangle.Empty;
            try
            {
                Type pattern2Type = Type.GetType("System.Windows.Automation.TextPattern2, UIAutomationClient", false);
                if (pattern2Type == null) return false;

                PropertyInfo patternProperty = pattern2Type.GetProperty("Pattern", BindingFlags.Public | BindingFlags.Static);
                if (patternProperty == null) return false;
                object patternId = patternProperty.GetValue(null, null);
                if (patternId == null) return false;

                MethodInfo tryGet = typeof(AutomationElement).GetMethod("TryGetCurrentPattern",
                    new Type[] { typeof(AutomationPattern), typeof(object).MakeByRefType() });
                if (tryGet == null) return false;
                object[] patternArgs = new object[] { patternId, null };
                if (!(bool)tryGet.Invoke(element, patternArgs)) return false;
                object pattern = patternArgs[1];
                if (pattern == null) return false;

                MethodInfo getCaret = pattern2Type.GetMethod("GetCaretRange", new Type[] { typeof(bool).MakeByRefType() });
                if (getCaret == null) return false;
                object[] caretArgs = new object[] { false };
                object range = getCaret.Invoke(pattern, caretArgs);
                if (range == null) return false;

                MethodInfo getRects = range.GetType().GetMethod("GetBoundingRectangles");
                if (getRects == null) return false;
                return PickRect((double[])getRects.Invoke(range, null), out rect);
            }
            catch (Exception ex)
            {
                Log.Info("UIA TextPattern2(反射) 不可用：" + ex.Message);
                return false;
            }
        }

        private static bool TryTextSelectionCaret(AutomationElement element, out Rectangle rect)
        {
            rect = Rectangle.Empty;
            try
            {
                object patternObject = null;
                if (!element.TryGetCurrentPattern(TextPattern.Pattern, out patternObject)) return false;
                TextPattern pattern = patternObject as TextPattern;
                if (pattern == null) return false;

                TextPatternRange[] ranges = null;
                try { ranges = pattern.GetSelection(); }
                catch (InvalidOperationException) { return false; }   // 提供程序不支持选择
                if (ranges == null || ranges.Length == 0) return false;

                for (int i = 0; i < ranges.Length; i++)
                {
                    System.Windows.Rect[] bounds = null;
                    try { bounds = ranges[i].GetBoundingRectangles(); }
                    catch (Exception ex) { Log.Info("UIA GetBoundingRectangles 失败：" + ex.Message); continue; }
                    if (PickRect(bounds, out rect)) return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Log.Info("UIA GetSelection 失败：" + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private static bool TryElementRect(AutomationElement element, out Rectangle rect)
        {
            rect = Rectangle.Empty;
            try
            {
                System.Windows.Rect bounds = element.Current.BoundingRectangle;
                if (bounds.IsEmpty) return false;
                if (bounds.Width <= 0 || bounds.Height <= 0) return false;
                Rectangle candidate = new Rectangle(
                    (int)Math.Round(bounds.X), (int)Math.Round(bounds.Y),
                    (int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height));
                if (!SystemInformation.VirtualScreen.IntersectsWith(candidate)) return false;
                rect = candidate;
                return true;
            }
            catch (Exception ex)
            {
                Log.Info("UIA BoundingRectangle 失败：" + ex.Message);
                return false;
            }
        }

        // 托管 TextPatternRange.GetBoundingRectangles() 返回 Rect[]（屏幕坐标）；取第一个高度有效的
        private static bool PickRect(System.Windows.Rect[] bounds, out Rectangle rect)
        {
            rect = Rectangle.Empty;
            if (bounds == null) return false;
            for (int i = 0; i < bounds.Length; i++)
            {
                System.Windows.Rect b = bounds[i];
                if (b.IsEmpty || b.Height <= 0) continue;
                Rectangle candidate = new Rectangle(
                    (int)Math.Round(b.X), (int)Math.Round(b.Y),
                    Math.Max((int)Math.Round(b.Width), 1), (int)Math.Round(b.Height));
                if (!SystemInformation.VirtualScreen.IntersectsWith(candidate)) continue;
                rect = candidate;
                return true;
            }
            return false;
        }

        // COM TextPattern2 反射路径返回的是裸数组 [x1,y1,w1,h1, x2,y2,...]（屏幕坐标）
        private static bool PickRect(double[] bounds, out Rectangle rect)
        {
            rect = Rectangle.Empty;
            if (bounds == null || bounds.Length < 4) return false;
            for (int i = 0; i + 3 < bounds.Length; i += 4)
            {
                int x = (int)Math.Round(bounds[i]);
                int y = (int)Math.Round(bounds[i + 1]);
                int w = (int)Math.Round(bounds[i + 2]);
                int h = (int)Math.Round(bounds[i + 3]);
                if (h <= 0) continue;
                if (!SystemInformation.VirtualScreen.IntersectsWith(new Rectangle(x, y, Math.Max(w, 1), h))) continue;
                rect = new Rectangle(x, y, Math.Max(w, 1), h);
                return true;
            }
            return false;
        }

        private static IntPtr SafeNativeHandle(AutomationElement element)
        {
            try
            {
                int h = element.Current.NativeWindowHandle;
                if (h == 0) return IntPtr.Zero;
                return new IntPtr(h);
            }
            catch
            {
                return IntPtr.Zero;
            }
        }
    }
}
