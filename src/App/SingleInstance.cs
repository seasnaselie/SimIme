using System;
using System.Threading;

namespace SimIme.App
{
    // 单点启动：第二个实例不弹任何提示，只唤醒已有实例后静默退出
    internal sealed class SingleInstance : IDisposable
    {
        private const string MutexName = "Local\\SimIme.SingleInstance";
        private Mutex _mutex;
        private bool _owned;

        // 提权重启时必须先释放，否则新的 SimIme-Admin.exe 会把自己当成「已有实例」而退出
        private static SingleInstance _current;

        internal static void ReleaseCurrent()
        {
            SingleInstance guard = _current;
            if (guard != null) guard.Dispose();
        }

        internal static bool Reacquire()
        {
            SingleInstance guard = new SingleInstance();
            if (guard.Acquire()) return true;
            guard.Dispose();
            return false;
        }

        internal bool Acquire()
        {
            try
            {
                bool createdNew;
                // 不要求初始拥有：用 WaitOne(0) 接管，才能区分「别的实例正在跑」与
                // 「上一个实例被 Stop-Process 强杀后留下的互斥体」——后者必须能正常启动
                _mutex = new Mutex(false, MutexName, out createdNew);
                try
                {
                    if (!_mutex.WaitOne(0)) return false;   // 另一个实例正在持有
                    _owned = true;
                    _current = this;
                    if (!createdNew) Log.Info("接管已存在的单实例互斥体（上一个实例已退出）");
                    return true;
                }
                catch (AbandonedMutexException)
                {
                    _owned = true;
                    Log.Info("上一个实例是被强制结束的（互斥体已弃用），本实例接管并继续启动");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Error("单实例互斥体创建失败（继续启动）", ex);
                return true;
            }
        }

        internal static bool SignalExisting()
        {
            try
            {
                IntPtr hwnd = Native.FindWindow(null, Native.HiddenWindowTitle);
                if (hwnd == IntPtr.Zero) return false;
                return Native.PostMessage(hwnd, Native.WM_APP_WAKE, IntPtr.Zero, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                Log.Error("唤醒已有实例失败", ex);
                return false;
            }
        }

        public void Dispose()
        {
            try
            {
                if (_mutex != null)
                {
                    if (_owned) _mutex.ReleaseMutex();
                    _mutex.Close();
                }
            }
            catch (Exception ex)
            {
                Log.Error("释放单实例互斥体失败", ex);
            }
            finally
            {
                if (object.ReferenceEquals(_current, this)) _current = null;
                _mutex = null;
                _owned = false;
            }
        }
    }
}
