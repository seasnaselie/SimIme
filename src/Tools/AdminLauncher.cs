// SimIme 管理员启动器（C 组交付物）
// 作用：用一种权限动作把同目录的 SimIme.exe 以管理员身份拉起，用于向“以管理员运行的目标窗口”注入。
// 编译：csc /target:winexe /win32manifest:src\Tools\SimIme-Admin.manifest src\Tools\AdminLauncher.cs
// 说明：本文件保持 C# 5 语法（csc 4.0.30319）；必须保存为 UTF-8 带 BOM。
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows.Forms;

namespace SimIme.Tools
{
    internal static class AdminLauncher
    {
        private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;
        private const int ERROR_CANCELLED = 1223;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(uint dwProcessId);

        [STAThread]
        private static int Main(string[] args)
        {
            string dir = BaseDir();
            string target = Path.Combine(dir, "SimIme.exe");
            if (!File.Exists(target))
            {
                ReportFailure("同目录找不到 SimIme.exe", target + Environment.NewLine + "请把 SimIme-Admin.exe 与 SimIme.exe 放在同一目录后重试。");
                return 2;
            }

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(target);
                psi.WorkingDirectory = dir;
                psi.UseShellExecute = true;
                // 本程序自身由 requireAdministrator 清单提权；已提权时不再二次索要 UAC。
                if (!IsElevated()) psi.Verb = "runas";
                Process.Start(psi);
                return 0;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                if (ex.NativeErrorCode == ERROR_CANCELLED)
                {
                    ReportFailure("已取消 UAC 提权，SimIme 未启动。", "需要管理员权限才能注入高权限窗口；如需普通模式请直接双击 SimIme.exe。");
                    return 3;
                }
                ReportFailure("启动失败：" + ex.Message, null);
                return 4;
            }
            catch (Exception ex)
            {
                ReportFailure("启动失败：" + ex.Message, null);
                return 4;
            }
        }

        private static string BaseDir()
        {
            try
            {
                string loc = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(loc)) return Path.GetDirectoryName(loc);
            }
            catch (Exception) { }
            return Environment.CurrentDirectory;
        }

        private static bool IsElevated()
        {
            try
            {
                WindowsIdentity id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception) { return false; }
        }

        private static void ReportFailure(string head, string detail)
        {
            string text = detail == null ? head : head + Environment.NewLine + detail;
            bool console = false;
            try { console = AttachConsole(ATTACH_PARENT_PROCESS); } catch (Exception) { }
            if (console)
            {
                try { Console.Error.WriteLine("SimIme-Admin: " + text.Replace(Environment.NewLine, " ")); } catch (Exception) { }
                return;
            }
            try { MessageBox.Show(text, "SimIme-Admin", MessageBoxButtons.OK, MessageBoxIcon.Warning); } catch (Exception) { }
        }
    }
}
