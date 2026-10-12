using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SimIme.App
{
    // 隐藏消息窗口：只用来收 WM_HOTKEY。永不显示、不进任务栏、不进 Alt-Tab。
    internal sealed class MessageWindow : Form
    {
        internal event Action<int> HotkeyMessage;
        internal event Action WakeRequested;

        internal MessageWindow()
        {
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            Size = new Size(1, 1);
            Text = Native.HiddenWindowTitle;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(false); // 该窗口永不可见
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.ForceToolWindowStyle(Handle);
        }

        internal void EnsureHandle()
        {
            IntPtr h = Handle;
            if (h == IntPtr.Zero) throw new InvalidOperationException("消息窗口句柄创建失败");
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_APP_WAKE)
            {
                try
                {
                    Action handler = WakeRequested;
                    if (handler != null) handler();
                }
                catch (Exception ex)
                {
                    Log.Error("处理唤醒消息失败", ex);
                }
                return;
            }
            if (m.Msg == Native.WM_HOTKEY)
            {
                try
                {
                    Action<int> handler = HotkeyMessage;
                    if (handler != null) handler(m.WParam.ToInt32());
                }
                catch (Exception ex)
                {
                    Log.Error("处理 WM_HOTKEY 失败", ex);
                }
                return;
            }
            base.WndProc(ref m);
        }
    }

    internal sealed class HotkeyManager
    {
        internal const int IdStart = 0xA001;
        internal const int IdLocate = 0xA002;
        internal const int IdStop = 0xA003;
        internal const int IdCancel = 0xA004;

        private readonly MessageWindow _window;
        private readonly HashSet<int> _registered = new HashSet<int>();

        internal event Action<string> ActionPressed;

        internal HotkeyManager(MessageWindow window)
        {
            _window = window;
            _window.HotkeyMessage += OnHotkeyMessage;
        }

        // 实际生效的停止键（0 = 未注册成功）；界面与日志都以此为准
        internal int EffectiveStopKey { get { return _stopKey; } }
        private int _stopKey;

        internal bool RegisterAll(AppConfig cfg)
        {
            UnregisterAll();
            bool ok = true;
            ok &= RegisterOne("Start", IdStart, cfg.Hotkeys.StartKey);
            ok &= RegisterOne("Locate", IdLocate, cfg.Hotkeys.LocateKey);
            bool stopOk = RegisterStopWithFallback(cfg);
            return ok && stopOk;
        }

        // 中途停止是硬需求：首选键被占用就按候选表顺序回退，绝不让用户没有停止键
        private bool RegisterStopWithFallback(AppConfig cfg)
        {
            List<int> order = BuildStopOrder(cfg);
            for (int i = 0; i < order.Count; i++)
            {
                int vk = order[i];
                if (RegisterOne(i == 0 ? "Stop" : "Stop候选" + i, IdStop, vk))
                {
                    _stopKey = vk;
                    if (i > 0)
                    {
                        // 用 Error 级别：默认关闭 INFO 时这行仍无条件写盘，用户必须知道停止键变了
                        Log.Error(string.Format(
                            "{0} 被占用，停止键自动切到 {1}（主界面「停止热键/停止键候选」可改回；候选顺序见 config.json 的 StopKeyCandidates）",
                            KeyTable.NameOf(cfg.Hotkeys.StopKey), KeyTable.NameOf(vk)));
                    }
                    else
                    {
                        Log.Info("停止热键注册为 " + KeyTable.NameOf(vk));
                    }
                    return true;
                }
            }
            Log.Error("停止热键全部候选均注册失败，程序照常运行但当前没有停止键；请在主界面「基本」页改选停止热键或候选列表");
            return false;
        }

        // 候选顺序 = 首选 + 候选表，去掉重复与已被 Start/Locate 占用的键
        private static List<int> BuildStopOrder(AppConfig cfg)
        {
            List<int> order = new List<int>();
            AddStopKey(order, cfg.Hotkeys.StopKey);
            int[] candidates = cfg.Hotkeys.StopKeyCandidates;
            if (candidates != null)
            {
                for (int i = 0; i < candidates.Length; i++) AddStopKey(order, candidates[i]);
            }
            order.Remove(cfg.Hotkeys.StartKey);
            order.Remove(cfg.Hotkeys.LocateKey);
            if (order.Count == 0)
            {
                int[] fallback = AppConfig.DefaultStopCandidates;
                for (int i = 0; i < fallback.Length; i++) AddStopKey(order, fallback[i]);
                order.Remove(cfg.Hotkeys.StartKey);
                order.Remove(cfg.Hotkeys.LocateKey);
            }
            if (order.Count == 0) AddStopKey(order, cfg.Hotkeys.StopKey);
            return order;
        }

        private static void AddStopKey(List<int> list, int vk)
        {
            if (vk < 0x08 || vk > 0xFE) return;
            if (!list.Contains(vk)) list.Add(vk);
        }

        internal bool RegisterOne(string name, int id, int vk)
        {
            try
            {
                _window.EnsureHandle();
                if (Native.RegisterHotKey(_window.Handle, id, Native.MOD_NOREPEAT, vk))
                {
                    _registered.Add(id);
                    Log.Info(string.Format("热键已注册 {0} id=0x{1:X} vk=0x{2:X2}", name, id, vk));
                    return true;
                }
                int err = Marshal.GetLastWin32Error();
                Log.Error(string.Format("热键注册失败 {0} vk=0x{1:X2} win32={2}", name, vk, err));
            }
            catch (Exception ex)
            {
                Log.Error("热键注册异常：" + name, ex);
            }
            return false;
        }

        internal void UnregisterOne(int id)
        {
            if (!_registered.Contains(id)) return;
            try
            {
                Native.UnregisterHotKey(_window.Handle, id);
            }
            catch (Exception ex)
            {
                Log.Error("注销热键失败 id=" + id, ex);
            }
            _registered.Remove(id);
        }

        internal void UnregisterAll()
        {
            int[] ids = new int[_registered.Count];
            _registered.CopyTo(ids);
            for (int i = 0; i < ids.Length; i++) UnregisterOne(ids[i]);
            _registered.Clear();
            _stopKey = 0;
        }

        private void OnHotkeyMessage(int id)
        {
            string name = null;
            if (id == IdStart) name = "Start";
            else if (id == IdLocate) name = "Locate";
            else if (id == IdStop) name = "Stop";
            else if (id == IdCancel) name = "Cancel";
            if (name == null) return;
            try
            {
                Action<string> handler = ActionPressed;
                if (handler != null) handler(name);
            }
            catch (Exception ex)
            {
                Log.Error("热键回调失败：" + name, ex);
            }
        }
    }

    // 低级鼠标钩子：点击定位模式捕获下一次左键按下
    internal sealed class LowLevelMouseHook : IDisposable
    {
        private Native.LowLevelMouseProc _proc;
        private IntPtr _hook = IntPtr.Zero;

        internal event Action<Point> LeftButtonDown;

        internal bool Installed { get { return _hook != IntPtr.Zero; } }

        internal bool Install()
        {
            if (_hook != IntPtr.Zero) return true;
            try
            {
                _proc = HookProc;
                IntPtr module = Native.GetModuleHandleW(null);
                _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _proc, module, 0);
                if (_hook == IntPtr.Zero)
                {
                    Log.Error("安装鼠标钩子失败 win32=" + Marshal.GetLastWin32Error());
                    _proc = null;
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("安装鼠标钩子异常", ex);
                return false;
            }
        }

        internal void Uninstall()
        {
            try
            {
                if (_hook != IntPtr.Zero)
                {
                    Native.UnhookWindowsHookEx(_hook);
                    _hook = IntPtr.Zero;
                }
            }
            catch (Exception ex)
            {
                Log.Error("卸载鼠标钩子失败", ex);
            }
            finally
            {
                _proc = null;
            }
        }

        private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0 && wParam.ToInt32() == Native.WM_LBUTTONDOWN)
                {
                    Native.MSLLHOOKSTRUCT data = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                    Action<Point> handler = LeftButtonDown;
                    if (handler != null) handler(new Point(data.pt.X, data.pt.Y));
                }
            }
            catch (Exception ex)
            {
                Log.Error("鼠标钩子回调失败", ex);
            }
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            Uninstall();
        }
    }
}
