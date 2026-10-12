using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;

namespace SimIme.App
{
    internal enum AppState
    {
        Idle = 0,
        Arming = 1,
        Locating = 2,
        Countdown = 3,
        Locked = 4,
        Running = 5,
        Silent = 6
    }

    internal static class StateNames
    {
        internal static string Of(AppState state)
        {
            switch (state)
            {
                case AppState.Idle: return "就绪";
                case AppState.Arming: return "已武装(等 F9)";
                case AppState.Locating: return "点击定位";
                case AppState.Countdown: return "倒计时";
                case AppState.Locked: return "已锁定";
                case AppState.Running: return "输入中";
                case AppState.Silent: return "静默待机";
                default: return "未知";
            }
        }
    }

    // 运行线程写入、UI 线程只读的不可变快照：避免任何跨线程控件访问
    // 字段语义对齐微软拼音候选窗：上行 = 已输入拼音(Trail 灰) + 当前音节(Composing 黑,下划线)，
    // 下行 = 候选列表(Candidates, Highlight 命中的候选高亮浅蓝)；无候选时下行显示 Foot 状态说明
    internal sealed class BarStatus
    {
        internal bool Visible;
        internal string Trail = "";
        internal string Composing = "";
        internal string Suffix = "";          // 组串缓冲里当前块之后的拼音（灰）
        internal string Candidates = "";
        internal string SelectedCandidate = "";   // 正在被"选词"高亮的那一个（浅蓝底加粗）
        internal string Highlight = "";
        internal string Progress = "";
        internal string Foot = "";
        internal Point Anchor;
    }

    // 完整性级别（UIPI 判据）：SendInput 在跨完整性时仍返回成功，只有 RID 比对才可靠
    internal static class Integrity
    {
        internal const int Low = 0x1000;
        internal const int Medium = 0x2000;
        internal const int High = 0x3000;
        internal const int SystemRid = 0x4000;

        internal static string Name(int rid)
        {
            if (rid <= 0) return "未知";
            if (rid < Medium) return "Low";
            if (rid < High) return "Medium";
            if (rid < SystemRid) return "High";
            return "System";
        }

        internal static string Describe(int rid)
        {
            if (rid <= 0) return "未知(0x0)";
            return string.Format("{0}(0x{1:X4})", Name(rid), rid);
        }
    }

    // 拼音组串器：模拟真输入法的组串——输入加音节，退格减音节；悬浮条只反映它
    internal sealed class PinyinComposer
    {
        private const int MaxSyllables = 6;
        private readonly List<char> _chars = new List<char>();
        private readonly List<string> _syllables = new List<string>();

        internal void Push(char c, string pinyin)
        {
            _chars.Add(c);
            _syllables.Add(string.IsNullOrEmpty(pinyin) ? c.ToString() : pinyin);
            while (_chars.Count > MaxSyllables)
            {
                _chars.RemoveAt(0);
                _syllables.RemoveAt(0);
            }
        }

        internal bool Pop()
        {
            if (_chars.Count == 0) return false;
            _chars.RemoveAt(_chars.Count - 1);
            _syllables.RemoveAt(_syllables.Count - 1);
            return true;
        }

        internal void Clear()
        {
            _chars.Clear();
            _syllables.Clear();
        }

        internal bool IsEmpty { get { return _syllables.Count == 0; } }

        internal char CurrentChar { get { return _chars.Count == 0 ? '\0' : _chars[_chars.Count - 1]; } }

        // 当前音节（最后一个），是悬浮条里高亮/画下划线的那段
        internal string Current
        {
            get { return _syllables.Count == 0 ? "" : _syllables[_syllables.Count - 1]; }
        }

        // 之前的音节串（灰色）
        internal string Trail
        {
            get
            {
                if (_syllables.Count <= 1) return "";
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < _syllables.Count - 1; i++) sb.Append(_syllables[i]);
                return sb.ToString();
            }
        }

        internal string All
        {
            get
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < _syllables.Count; i++) sb.Append(_syllables[i]);
                return sb.ToString();
            }
        }
    }

    internal static class AppInfo
    {
        internal static string Version
        {
            get
            {
                try
                {
                    // 没写 AssemblyInfo 时 ProductVersion 是 0.0.0.0：补上 exe 的构建时间，日志里才认得出是哪一版
                    string version = Application.ProductVersion + " / file " + Assembly.GetExecutingAssembly().GetName().Version;
                    string path = Application.ExecutablePath;
                    if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                    {
                        version += "｜构建=" + System.IO.File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss");
                    }
                    return version;
                }
                catch
                {
                    return "未知";
                }
            }
        }

        internal static bool IsElevated
        {
            get
            {
                try
                {
                    WindowsIdentity identity = WindowsIdentity.GetCurrent();
                    WindowsPrincipal principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
                catch (Exception ex)
                {
                    Log.Error("探测管理员权限失败", ex);
                    return false;
                }
            }
        }

        private static int _integrityRid = -2;

        // 进程生命周期内不变，缓存一次
        internal static int IntegrityRid
        {
            get
            {
                if (_integrityRid == -2) _integrityRid = Native.CurrentIntegrityRid();
                return _integrityRid;
            }
        }

        internal static bool IsElevatedToken
        {
            get { return Native.CurrentTokenElevated(); }
        }

        // 主界面「权限：」行文案：Low 必须显眼告警（用户实测就是 Low 导致注入被静默丢弃）
        internal static string PermissionLine()
        {
            int rid = IntegrityRid;
            if (rid > 0 && rid < Integrity.Medium)
            {
                return "权限：低完整性 Low —— 无法向普通程序写入按键（Windows UIPI 限制），请改用 SimIme-Admin.exe 启动";
            }
            if (IsElevatedToken || rid >= Integrity.High)
            {
                return "权限：管理员 " + Integrity.Name(rid) + "（已提权，可向管理员窗口注入）";
            }
            return "权限：普通用户 " + Integrity.Describe(rid) + "（可注入同级或更低完整性窗口；目标是管理员程序时点右侧「以管理员身份重启」）";
        }

        internal static string RuntimeInfo
        {
            get
            {
                try
                {
                    return ".NET CLR " + typeof(object).Assembly.ImageRuntimeVersion + " / " + (IntPtr.Size == 8 ? "x64" : "x86");
                }
                catch
                {
                    return "未知运行时";
                }
            }
        }

        internal static string OsInfo
        {
            get
            {
                try { return Environment.OSVersion.VersionString; }
                catch { return "未知系统"; }
            }
        }

        internal static string DpiInfo
        {
            get
            {
                try
                {
                    return Native.GetSystemDpiX() + "x" + Native.GetSystemDpiY();
                }
                catch
                {
                    return "?";
                }
            }
        }
    }

    internal static class KeyTable
    {
        private static readonly string[] Names = new string[]
        {
            "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12",
            "Insert", "Delete", "Home", "End", "PageUp", "PageDown", "Pause", "ScrollLock", "NumLock", "Tab", "Backspace"
        };

        private static readonly int[] Codes = new int[]
        {
            0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x7B,
            0x2D, 0x2E, 0x24, 0x23, 0x21, 0x22, 0x13, 0x91, 0x90, 0x09, 0x08
        };

        internal static string[] AllNames()
        {
            return (string[])Names.Clone();
        }

        internal static string NameOf(int vk)
        {
            for (int i = 0; i < Codes.Length; i++) if (Codes[i] == vk) return Names[i];
            return string.Format("0x{0:X2}", vk);
        }

        internal static int CodeOf(string name, int fallback)
        {
            if (!string.IsNullOrEmpty(name))
            {
                for (int i = 0; i < Names.Length; i++)
                {
                    if (string.Equals(Names[i], name, StringComparison.OrdinalIgnoreCase)) return Codes[i];
                }
            }
            return fallback;
        }
    }

    // 直接以 public 字段路径读写配置：一个控件表驱动全部参数，避免手写三十份重复代码
    internal static class FieldAccess
    {
        internal static object Get(object root, string path)
        {
            try
            {
                object cur = root;
                string[] parts = path.Split('.');
                for (int i = 0; i < parts.Length; i++)
                {
                    if (cur == null) return null;
                    FieldInfo fi = cur.GetType().GetField(parts[i], BindingFlags.Public | BindingFlags.Instance);
                    if (fi == null) return null;
                    cur = fi.GetValue(cur);
                }
                return cur;
            }
            catch (Exception ex)
            {
                Log.Error("读取配置字段失败：" + path, ex);
                return null;
            }
        }

        internal static void Set(object root, string path, object value)
        {
            try
            {
                object cur = root;
                string[] parts = path.Split('.');
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    FieldInfo fi = cur.GetType().GetField(parts[i], BindingFlags.Public | BindingFlags.Instance);
                    if (fi == null) return;
                    cur = fi.GetValue(cur);
                    if (cur == null) return;
                }
                FieldInfo last = cur.GetType().GetField(parts[parts.Length - 1], BindingFlags.Public | BindingFlags.Instance);
                if (last == null) return;
                last.SetValue(cur, Convert.ChangeType(value, last.FieldType));
            }
            catch (Exception ex)
            {
                Log.Error("写入配置字段失败：" + path, ex);
            }
        }
    }

    internal static class WindowFinder
    {
        // 按标题片段查找可见的顶层窗口（--selftest 用）
        internal static IntPtr FindByTitle(string fragment)
        {
            IntPtr found = IntPtr.Zero;
            try
            {
                Native.EnumWindowsProc proc = delegate(IntPtr hwnd, IntPtr lparam)
                {
                    if (!Native.IsWindowVisible(hwnd)) return true;
                    string title = Native.GetWindowTitle(hwnd);
                    if (title.Length == 0) return true;
                    if (title.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        found = hwnd;
                        return false;
                    }
                    return true;
                };
                Native.EnumWindows(proc, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                Log.Error("按标题查找窗口失败", ex);
            }
            return found;
        }
    }

    internal static class ConsoleBridge
    {
        // winexe 默认没有 stdout；附着到父控制台后再绑定，--printconfig/--selftest 才能被看到
        internal static bool Attach()
        {
            try
            {
                if (!Native.AttachConsole(-1)) return false;
                System.IO.StreamWriter so = new System.IO.StreamWriter(Console.OpenStandardOutput());
                so.AutoFlush = true;
                Console.SetOut(so);
                System.IO.StreamWriter se = new System.IO.StreamWriter(Console.OpenStandardError());
                se.AutoFlush = true;
                Console.SetError(se);
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static void WriteLine(StringBuilder mirror, string text)
        {
            if (mirror != null) mirror.AppendLine(text);
            try { Console.WriteLine(text); } catch { }
        }
    }
}
