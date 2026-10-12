using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace SimIme.App
{
    // --notify：托盘气泡（首选，本机最可靠）+ Windows 通知中心 Toast（尽力而为）
    // 两者都不弹前台对话框、不抢焦点；退出码 0=至少一种成功
    internal static class Notifier
    {
        private const int BalloonMs = 6000;

        internal static int Run(string title, string body)
        {
            ConsoleBridge.Attach();
            StringBuilder mirror = new StringBuilder();
            Log.Enabled = true;
            try
            {
                if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(body))
                {
                    ConsoleBridge.WriteLine(mirror, "用法：SimIme.exe --notify \"标题\" \"正文\"");
                    ConsoleBridge.WriteLine(mirror, "  例：SimIme.exe --notify \"SimIme 验收邀请\" \"第十轮已交付，请到真机验收\"");
                    ConsoleBridge.WriteLine(mirror, "退出码：0=至少一种通知成功；2=两种都失败；1=参数缺失");
                    Log.Info("--notify 参数缺失，已打印用法");
                    return 1;
                }

                ConsoleBridge.WriteLine(mirror, "SimIme --notify");
                ConsoleBridge.WriteLine(mirror, "标题=" + title);
                ConsoleBridge.WriteLine(mirror, "正文=" + body);

                string balloonReason;
                bool balloonOk = ShowBalloon(title, body, out balloonReason);
                ConsoleBridge.WriteLine(mirror, (balloonOk ? "托盘气泡：成功" : "托盘气泡：失败") + "｜" + balloonReason);
                Log.Info("--notify 托盘气泡：" + (balloonOk ? "成功" : "失败") + "｜" + balloonReason);

                string toastReason;
                bool toastOk = ShowToast(title, body, out toastReason);
                ConsoleBridge.WriteLine(mirror, (toastOk ? "Windows Toast：成功" : "Windows Toast：失败") + "｜" + toastReason);
                Log.Info("--notify Windows Toast：" + (toastOk ? "成功" : "失败") + "｜" + toastReason);

                int code = (balloonOk || toastOk) ? 0 : 2;
                ConsoleBridge.WriteLine(mirror, "退出码=" + code + "（0=至少一种成功；2=两种都失败）");
                Log.Info("--notify 完成，退出码=" + code);
                return code;
            }
            catch (Exception ex)
            {
                Log.Error("--notify 异常", ex);
                ConsoleBridge.WriteLine(mirror, "--notify 异常：" + ex.Message);
                return 2;
            }
            finally
            {
                try { File.WriteAllText(AppPaths.SelfTestLogPath, mirror.ToString(), new UTF8Encoding(true)); } catch { }
            }
        }

        // 托盘气泡：需要托盘图标存在 + 短暂消息泵，否则气泡不会真的弹出来
        internal static bool ShowBalloon(string title, string body, out string reason)
        {
            NotifyIcon icon = null;
            reason = "";
            try
            {
                icon = new NotifyIcon();
                icon.Icon = SystemIcons.Information;
                icon.Text = "SimIme";
                icon.Visible = true;                      // 必须为 true 才有托盘图标（气泡的载体）
                if (!icon.Visible)
                {
                    reason = "托盘图标未能创建（Visible=false）";
                    return false;
                }
                icon.ShowBalloonTip(BalloonMs, title, body, ToolTipIcon.Info);
                // 给外壳一点时间把气泡显示出来（同时跑消息泵，让 NotifyIcon 的消息被处理）
                DateTime deadline = DateTime.Now.AddMilliseconds(2500);
                while (DateTime.Now < deadline)
                {
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(50);
                }
                reason = "已调用 ShowBalloonTip(" + BalloonMs + "ms) 并等待 2500ms，图标已创建";
                return true;
            }
            catch (Exception ex)
            {
                reason = ex.GetType().Name + ": " + ex.Message;
                Log.Error("托盘气泡失败", ex);
                return false;
            }
            finally
            {
                try
                {
                    if (icon != null)
                    {
                        icon.Visible = false;
                        icon.Dispose();
                    }
                }
                catch { }
            }
        }

        // Windows 通知中心 Toast：经 powershell.exe 调 WinRT（尽力而为，失败只记日志）
        internal static bool ShowToast(string title, string body, out string reason)
        {
            reason = "";
            string scriptPath = Path.Combine(Path.GetTempPath(), "simime-toast-" + Guid.NewGuid().ToString("N") + ".ps1");
            string resultPath = scriptPath + ".log";
            try
            {
                string ps = "$ErrorActionPreference='Stop'" + Environment.NewLine
                    + "try {" + Environment.NewLine
                    + "  [void][Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType=WindowsRuntime]" + Environment.NewLine
                    + "  [void][Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType=WindowsRuntime]" + Environment.NewLine
                    + "  $t = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02)" + Environment.NewLine
                    + "  $x = $t.GetElementsByTagName('text')" + Environment.NewLine
                    + "  $x.Item(0).AppendChild($t.CreateTextNode('" + EscapePs(title) + "')) | Out-Null" + Environment.NewLine
                    + "  $x.Item(1).AppendChild($t.CreateTextNode('" + EscapePs(body) + "')) | Out-Null" + Environment.NewLine
                    + "  $n = [Windows.UI.Notifications.ToastNotification]::new($t)" + Environment.NewLine
                    + "  [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('SimIme').Show($n)" + Environment.NewLine
                    + "  'OK' | Out-File -FilePath '" + resultPath + "' -Encoding utf8" + Environment.NewLine
                    + "} catch { ('ERR ' + $_.Exception.GetType().Name + ': ' + $_.Exception.Message) | Out-File -FilePath '" + resultPath + "' -Encoding utf8; exit 3 }" + Environment.NewLine;
                File.WriteAllText(scriptPath, ps, new UTF8Encoding(false));

                string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
                if (!File.Exists(powershell)) powershell = "powershell.exe";
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = powershell;
                psi.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + scriptPath + "\"";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    if (p == null)
                    {
                        reason = "无法启动 powershell.exe";
                        return false;
                    }
                    if (!p.WaitForExit(20000))
                    {
                        try { p.Kill(); } catch { }
                        reason = "powershell.exe 20 秒未退出（Toast 调用未返回）";
                        return false;
                    }
                    string detail = File.Exists(resultPath) ? File.ReadAllText(resultPath, Encoding.UTF8).Trim() : "";
                    if (p.ExitCode == 0 && detail.StartsWith("OK", StringComparison.Ordinal))
                    {
                        reason = "已通过 WinRT ToastNotificationManager 发出（AUMID=SimIme）";
                        return true;
                    }
                    reason = "powershell 退出码=" + p.ExitCode + "｜" + (detail.Length > 0 ? detail : "无输出文件内容");
                    return false;
                }
            }
            catch (Exception ex)
            {
                reason = ex.GetType().Name + ": " + ex.Message;
                Log.Info("Windows Toast 不可用（不影响托盘气泡）：" + reason);
                return false;
            }
            finally
            {
                try { if (File.Exists(scriptPath)) File.Delete(scriptPath); } catch { }
                try { if (File.Exists(resultPath)) File.Delete(resultPath); } catch { }
            }
        }

        private static string EscapePs(string text)
        {
            if (text == null) return "";
            return text.Replace("'", "''").Replace("\r", " ").Replace("\n", " ");
        }
    }
}
