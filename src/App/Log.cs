using System;
using System.IO;
using System.Text;

namespace SimIme.App
{
    // 极简文件日志：默认静默；崩溃兜底始终写盘；绝不弹任何前台对话框。
    internal static class Log
    {
        private static readonly object Sync = new object();
        internal static string FilePath = "error.log";
        internal static bool Enabled = false;
        internal static long MaxBytes = 5L * 1024 * 1024;   // 超过就轮转成 error.log.1（不无限增长）
        private static int _writes;

        static Log()
        {
            try
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                if (string.IsNullOrEmpty(dir)) dir = ".";
                FilePath = Path.Combine(dir, "error.log");
            }
            catch
            {
                FilePath = "error.log";
            }
        }

        internal static void Info(string message)
        {
            MaybeRotate();
            if (Enabled) WriteLine("INFO ", message, null);
        }

        // 运行期诊断默认开启，日志会持续增长：启动时超限就先轮转一个备份
        internal static void RotateIfLarge(long maxBytes)
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                FileInfo info = new FileInfo(FilePath);
                if (info.Length <= maxBytes) return;
                string backup = FilePath + ".1";
                if (File.Exists(backup)) File.Delete(backup);
                File.Move(FilePath, backup);
            }
            catch
            {
                // 轮转失败不影响运行
            }
        }

        private static void MaybeRotate()
        {
            if (++_writes % 500 != 0) return;   // 每 500 行检查一次，开销可忽略
            RotateIfLarge(MaxBytes);
        }

        internal static void Error(string message)
        {
            WriteLine("ERROR", message, null);
        }

        internal static void Error(string message, Exception ex)
        {
            WriteLine("ERROR", message, ex);
        }

        private static void WriteLine(string level, string message, Exception ex)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                sb.Append(' ');
                sb.Append(level);
                sb.Append(' ');
                sb.Append(message);
                if (ex != null)
                {
                    sb.AppendLine();
                    sb.Append(ex.ToString());
                }
                sb.AppendLine();
                lock (Sync)
                {
                    File.AppendAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
                }
            }
            catch
            {
                // 日志写入失败绝不升级为异常
            }
        }
    }
}
