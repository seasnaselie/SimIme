using System;
using System.Threading;
using System.Windows.Forms;

namespace SimIme.App
{
    internal static class Program
    {
        [STAThread]
        internal static int Main(string[] args)
        {
            Log.FilePath = AppPaths.ErrorLogPath;
            try { Native.SetProcessDPIAware(); }
            catch (Exception ex) { Log.Error("设置 DPI 感知失败", ex); }

            // 任何异常都不得弹前台对话框：只写 error.log
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandled;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += OnUiUnhandled;

            Log.RotateIfLarge(Log.MaxBytes);      // 诊断日志默认开启，超 5MB 轮转为 error.log.1（运行期每 500 行也会检查）
            AppConfig cfg = AppConfig.Load(AppPaths.ConfigPath);
            Log.Enabled = cfg.Behavior.LogEnabled;
            Log.Info("启动：args=" + string.Join(" ", args) + " dir=" + AppPaths.BaseDir);

            if (Arg.Has(args, "--printconfig")) return SelfTest.RunPrintConfig(cfg);
            if (Arg.Has(args, "--selftest")) return SelfTest.RunSelfTest(cfg, Arg.Value(args, "--selftest"));
            if (Arg.Has(args, "--bartest-all")) return SelfTest.RunBarTest(cfg, Arg.Value(args, "--bartest-all"), true);
            if (Arg.Has(args, "--bartest")) return SelfTest.RunBarTest(cfg, Arg.Value(args, "--bartest"), false);
            if (Arg.Has(args, "--iltest")) return SelfTest.RunIlTest(cfg);
            if (Arg.Has(args, "--words")) return SelfTest.RunWords(cfg);
            if (Arg.Has(args, "--notify")) return Notifier.Run(Arg.ValueAfter(args, "--notify", 1), Arg.ValueAfter(args, "--notify", 2));
            if (Arg.Has(args, "--updatedict"))
            {
                return SelfTest.RunUpdateDict(cfg, Arg.Value(args, "--mirror"), Arg.Value(args, "--dictout"));
            }
            if (Arg.Has(args, "--verify")) return SelfTest.RunVerify(cfg, Arg.Value(args, "--verify"), Arg.ValueAfter(args, "--verify", 2));

            try
            {
                using (SingleInstance guard = new SingleInstance())
                {
                    if (!guard.Acquire())
                    {
                        bool woken = SingleInstance.SignalExisting();
                        Log.Info("已有实例在运行，本实例退出；唤醒请求已发送 = " + woken);
                        return 0;   // 静默退出，无任何前台提示
                    }
                    SimImeContext context = new SimImeContext(cfg);
                    Application.Run(context);   // ApplicationContext 驱动：绝不 Application.Run(new Form(...))
                }
                return 0;
            }
            catch (Exception ex)
            {
                Log.Error("主循环异常退出", ex);
                return 1;
            }
        }

        private static void OnDomainUnhandled(object sender, UnhandledExceptionEventArgs e)
        {
            try { Log.Error("未处理异常（AppDomain）", e.ExceptionObject as Exception); }
            catch { }
        }

        private static void OnUiUnhandled(object sender, ThreadExceptionEventArgs e)
        {
            try { Log.Error("未处理异常（UI 线程）", e.Exception); }
            catch { }
        }
    }

    internal static class Arg
    {
        internal static bool Has(string[] args, string flag)
        {
            if (args == null) return false;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // 取 flag 之后第 offset 个参数（offset=1 即紧随其后的那个）
        internal static string ValueAfter(string[] args, string flag, int offset)
        {
            if (args == null) return null;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                {
                    int k = i + offset;
                    if (k >= 0 && k < args.Length) return args[k];
                    return null;
                }
            }
            return null;
        }

        // --selftest "标题片段" / --selftest=标题片段 两种写法都支持
        internal static string Value(string[] args, string flag)
        {
            if (args == null) return null;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length && args[i + 1].Length > 0 && args[i + 1][0] != '-') return args[i + 1];
                    return null;
                }
                string prefix = flag + "=";
                if (a != null && a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return a.Substring(prefix.Length);
            }
            return null;
        }
    }
}
