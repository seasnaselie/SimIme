using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace SimIme.App
{
    internal static class Native
    {
        internal const int WM_HOTKEY = 0x0312;
        internal const int WM_LBUTTONDOWN = 0x0201;
        internal const int WM_APP_WAKE = 0x8001;          // 单实例第二启动时用来唤醒已有实例
        internal const string HiddenWindowTitle = "SimIme.MessageWindow";   // 单实例唤醒时按标题定位隐藏窗口
        internal const int MOD_NOREPEAT = 0x4000;
        internal const int GWL_EXSTYLE = -20;
        internal const int WS_EX_TOOLWINDOW = 0x00000080;
        internal const int WS_EX_TOPMOST = 0x00000008;
        internal const int WS_EX_NOACTIVATE = 0x08000000;
        internal const int WS_EX_TRANSPARENT = 0x00000020;
        internal const uint SWP_NOSIZE = 0x0001;
        internal const uint SWP_NOMOVE = 0x0002;
        internal const uint SWP_NOACTIVATE = 0x0010;
        internal const uint SWP_SHOWWINDOW = 0x0040;
        internal const int SW_HIDE = 0;
        internal const int SW_SHOWNOACTIVATE = 4;
        internal const uint KEYEVENTF_KEYUP = 0x0002;
        internal const uint KEYEVENTF_UNICODE = 0x0004;
        internal const ushort VK_BACK = 0x08;
        internal const ushort VK_RETURN = 0x0D;
        internal const ushort VK_CONTROL = 0x11;
        internal const ushort VK_ESCAPE = 0x1B;
        internal const ushort VK_V = 0x56;
        internal const ushort VK_A = 0x41;
        internal const ushort VK_C = 0x43;
        internal const int WH_MOUSE_LL = 14;
        internal const int GA_ROOT = 2;
        internal const int INPUT_KEYBOARD = 1;

        internal static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            internal int X;
            internal int Y;
            internal POINT(int x, int y) { X = x; Y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
            internal Rectangle ToRectangle() { return Rectangle.FromLTRB(Left, Top, Right, Bottom); }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MOUSEINPUT
        {
            internal int dx;
            internal int dy;
            internal uint mouseData;
            internal uint dwFlags;
            internal uint time;
            internal IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct KEYBDINPUT
        {
            internal ushort wVk;
            internal ushort wScan;
            internal uint dwFlags;
            internal uint time;
            internal IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct HARDWAREINPUT
        {
            internal uint uMsg;
            internal ushort wParamL;
            internal ushort wParamH;
        }

        [StructLayout(LayoutKind.Explicit)]
        internal struct INPUTUNION
        {
            [FieldOffset(0)] internal MOUSEINPUT mi;
            [FieldOffset(0)] internal KEYBDINPUT ki;
            [FieldOffset(0)] internal HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct INPUT
        {
            internal int type;
            internal INPUTUNION u;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct GUITHREADINFO
        {
            internal int cbSize;
            internal int flags;
            internal IntPtr hwndActive;
            internal IntPtr hwndFocus;
            internal IntPtr hwndCapture;
            internal IntPtr hwndMenuOwner;
            internal IntPtr hwndMoveSize;
            internal IntPtr hwndCaret;
            internal RECT rcCaret;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MSLLHOOKSTRUCT
        {
            internal POINT pt;
            internal uint mouseData;
            internal uint flags;
            internal uint time;
            internal IntPtr dwExtraInfo;
        }

        internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
        internal delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern IntPtr GetFocus();

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool AttachThreadInput(int attach, int attachTo, bool attachFlag);

        [DllImport("kernel32.dll")]
        internal static extern int GetCurrentThreadId();

        [DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool GetGUIThreadInfo(int idThread, ref GUITHREADINFO info);

        [DllImport("user32.dll")]
        internal static extern int GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [DllImport("user32.dll")]
        internal static extern bool GetCursorPos(out POINT p);

        [DllImport("user32.dll")]
        internal static extern IntPtr WindowFromPoint(POINT p);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetAncestor(IntPtr hWnd, int flags);

        [DllImport("user32.dll")]
        internal static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern bool GetClientRect(IntPtr hWnd, out RECT r);

        [DllImport("user32.dll")]
        internal static extern bool GetWindowRect(IntPtr hWnd, out RECT r);

        [DllImport("user32.dll")]
        internal static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);

        [DllImport("user32.dll")]
        internal static extern bool ShowWindow(IntPtr hWnd, int cmd);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowTextLengthW(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetClassNameW(IntPtr hWnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint SendInput(uint count, INPUT[] inputs, int size);

        [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
        internal static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc proc, IntPtr hMod, uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        internal static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
        internal static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        internal const int WM_GETTEXT = 0x000D;
        internal const int WM_GETTEXTLENGTH = 0x000E;
        internal const uint SMTO_ABORTIFHUNG = 0x0002;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, StringBuilder lParam, uint flags, uint timeout, out IntPtr result);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

        // 跨进程回读标准编辑控件文本：WM_GETTEXT 由系统负责跨进程编组；用 SendMessageTimeout 防目标无响应
        internal static string ReadWindowText(IntPtr hwnd, int maxChars)
        {
            try
            {
                if (hwnd == IntPtr.Zero) return null;
                IntPtr result;
                IntPtr ok = SendMessageTimeout(hwnd, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 1500, out result);
                if (ok == IntPtr.Zero) return null;
                int length = result.ToInt32();
                if (length < 0) return null;
                if (length > maxChars) length = maxChars;
                StringBuilder buffer = new StringBuilder(length + 2);
                IntPtr ok2 = SendMessageTimeout(hwnd, WM_GETTEXT, (IntPtr)buffer.Capacity, buffer, SMTO_ABORTIFHUNG, 1500, out result);
                if (ok2 == IntPtr.Zero) return null;
                return buffer.ToString();
            }
            catch (Exception ex)
            {
                Log.Error("WM_GETTEXT 回读失败", ex);
                return null;
            }
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        internal static extern int GetWindowLong(IntPtr hWnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        internal static extern int SetWindowLong(IntPtr hWnd, int index, int value);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr GetModuleHandleW(string moduleName);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool AttachConsole(int processId);

        [DllImport("user32.dll")]
        internal static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        internal static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        internal static extern int GetDeviceCaps(IntPtr hdc, int index);

        internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        internal const uint TOKEN_QUERY = 0x0008;
        internal const int TokenElevation = 20;
        internal const int TokenIntegrityLevel = 25;

        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CloseHandle(IntPtr handle);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int infoLength, out int returnLength);

        // TOKEN_MANDATORY_LABEL 偏移 0 是 SID 指针；RID 不是指针目标而是 SID 的最后一个子权威：
        // SID 头 = Revision(1) + SubAuthorityCount(1) + IdentifierAuthority(6) = 8 字节，之后每 4 字节一个子权威
        internal static int ReadIntegrityRid(IntPtr token)
        {
            IntPtr buffer = IntPtr.Zero;
            try
            {
                int size = 0;
                GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out size);
                if (size <= 0) size = 256;
                buffer = Marshal.AllocHGlobal(size);
                int returned = 0;
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, size, out returned)) return 0;
                IntPtr sid = Marshal.ReadIntPtr(buffer, 0);
                if (sid == IntPtr.Zero) return 0;
                int count = Marshal.ReadByte(sid, 1);
                if (count <= 0) return 0;
                return Marshal.ReadInt32(sid, 8 + (count - 1) * 4);
            }
            catch (Exception ex)
            {
                Log.Error("读取完整性级别(RID)失败", ex);
                return 0;
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            }
        }

        internal static int CurrentIntegrityRid()
        {
            IntPtr token = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out token)) return 0;
                return ReadIntegrityRid(token);
            }
            catch (Exception ex)
            {
                Log.Error("读取自身完整性失败", ex);
                return 0;
            }
            finally
            {
                if (token != IntPtr.Zero) CloseHandle(token);
            }
        }

        // 取不到就返回 0，由调用方跳过检查（绝不因此报错或阻断）
        internal static int ProcessIntegrityRid(uint processId)
        {
            int ignored;
            return ProcessIntegrityRid(processId, out ignored);
        }

        // win32Error 供调用方区分「拒绝访问」与「进程已退出」：
        // 同级或更低完整性的目标一定能打开，所以 ACCESS_DENIED 基本可断定目标完整性更高（UIPI 会丢弃按键）
        internal static int ProcessIntegrityRid(uint processId, out int win32Error)
        {
            win32Error = 0;
            IntPtr process = IntPtr.Zero;
            IntPtr token = IntPtr.Zero;
            try
            {
                if (processId == 0) return 0;
                process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
                if (process == IntPtr.Zero) { win32Error = Marshal.GetLastWin32Error(); return 0; }
                if (!OpenProcessToken(process, TOKEN_QUERY, out token)) { win32Error = Marshal.GetLastWin32Error(); return 0; }
                return ReadIntegrityRid(token);
            }
            catch
            {
                return 0;
            }
            finally
            {
                if (token != IntPtr.Zero) CloseHandle(token);
                if (process != IntPtr.Zero) CloseHandle(process);
            }
        }

        internal static bool CurrentTokenElevated()
        {
            IntPtr token = IntPtr.Zero;
            IntPtr buffer = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out token)) return false;
                int size = Marshal.SizeOf(typeof(int));
                buffer = Marshal.AllocHGlobal(size);
                int returned = 0;
                if (!GetTokenInformation(token, TokenElevation, buffer, size, out returned)) return false;
                return Marshal.ReadInt32(buffer, 0) != 0;
            }
            catch (Exception ex)
            {
                Log.Error("读取提权状态失败", ex);
                return false;
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                if (token != IntPtr.Zero) CloseHandle(token);
            }
        }

        internal static uint WindowProcessId(IntPtr hwnd)
        {
            try
            {
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                return pid;
            }
            catch
            {
                return 0;
            }
        }

        internal const int LOGPIXELSX = 88;
        internal const int LOGPIXELSY = 90;
        internal const int ERROR_ACCESS_DENIED = 5;

        // 系统 DPI（启动诊断用）
        internal static int GetSystemDpiX() { return ReadDpi(LOGPIXELSX); }
        internal static int GetSystemDpiY() { return ReadDpi(LOGPIXELSY); }

        private static int ReadDpi(int index)
        {
            IntPtr hdc = IntPtr.Zero;
            try
            {
                hdc = GetDC(IntPtr.Zero);
                if (hdc == IntPtr.Zero) return 0;
                return GetDeviceCaps(hdc, index);
            }
            catch
            {
                return 0;
            }
            finally
            {
                if (hdc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hdc);
            }
        }

        internal static string GetWindowTitle(IntPtr hwnd)
        {
            try
            {
                int len = GetWindowTextLengthW(hwnd);
                if (len <= 0) return "";
                StringBuilder sb = new StringBuilder(len + 2);
                GetWindowTextW(hwnd, sb, sb.Capacity);
                return sb.ToString();
            }
            catch
            {
                return "";
            }
        }

        internal static string GetWindowClass(IntPtr hwnd)
        {
            try
            {
                StringBuilder sb = new StringBuilder(256);
                GetClassNameW(hwnd, sb, sb.Capacity);
                return sb.ToString();
            }
            catch
            {
                return "";
            }
        }

        internal static Point CursorPosition()
        {
            POINT p;
            if (GetCursorPos(out p)) return new Point(p.X, p.Y);
            return Point.Empty;
        }

        internal static void ForceToolWindowStyle(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero) return;
                int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            }
            catch (Exception ex)
            {
                Log.Error("设置窗口扩展样式失败", ex);
            }
        }
    }
}
