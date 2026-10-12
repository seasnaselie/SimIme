// SimIme 引擎：嵌入资源加载（C# 5 / .NET Framework 4.8）
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace SimIme.Engine
{
    internal static class AssetLoader
    {
        // 先按资源名精确匹配 -> 再按文件名后缀匹配 -> 再回退到 exe 同目录同名文件
        public static string[] ReadLines(string resourceName, string fileName)
        {
            string text = TryReadResource(resourceName, fileName);
            if (text == null) text = TryReadFile(fileName);
            if (text == null) return new string[0];
            return SplitLines(text);
        }

        private static string TryReadResource(string resourceName, string fileName)
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                string[] names = asm.GetManifestResourceNames();
                string exact = null;
                string suffix = null;
                for (int i = 0; i < names.Length; i++)
                {
                    if (string.Equals(names[i], resourceName, StringComparison.OrdinalIgnoreCase))
                    {
                        exact = names[i];
                        break;
                    }
                    if (suffix == null && names[i].Length > fileName.Length &&
                        names[i].EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
                    {
                        suffix = names[i];
                    }
                }
                string pick = exact != null ? exact : suffix;
                if (pick == null) return null;
                using (Stream s = asm.GetManifestResourceStream(pick))
                {
                    if (s == null) return null;
                    using (StreamReader r = new StreamReader(s, new UTF8Encoding(false), true))
                    {
                        return r.ReadToEnd();
                    }
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        // 依次尝试：程序集所在目录 -> 应用基目录（exe 同目录）-> 当前工作目录
        private static string TryReadFile(string fileName)
        {
            List<string> cands = new List<string>();
            try
            {
                string loc = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(loc))
                {
                    string dir = Path.GetDirectoryName(loc);
                    if (!string.IsNullOrEmpty(dir)) cands.Add(Path.Combine(dir, fileName));
                }
            }
            catch (Exception) { }
            try { cands.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName)); }
            catch (Exception) { }
            try { cands.Add(Path.Combine(Environment.CurrentDirectory, fileName)); }
            catch (Exception) { }
            for (int i = 0; i < cands.Count; i++)
            {
                try
                {
                    if (File.Exists(cands[i])) return File.ReadAllText(cands[i], Encoding.UTF8);
                }
                catch (Exception) { }
            }
            return null;
        }

        public static string[] SplitLines(string text)
        {
            List<string> list = new List<string>();
            if (text == null) return list.ToArray();
            int start = 0;
            int n = text.Length;
            for (int i = 0; i <= n; i++)
            {
                if (i == n || text[i] == '\n')
                {
                    int end = i;
                    if (end > start && text[end - 1] == '\r') end--;
                    if (end > start) list.Add(text.Substring(start, end - start));
                    start = i + 1;
                }
            }
            return list.ToArray();
        }

        // 解析 "字<TAB>值" 形式的一行
        public static bool TrySplitPair(string line, out char key, out string value)
        {
            key = '\0';
            value = null;
            if (line == null || line.Length < 3) return false;
            int tab = line.IndexOf('\t');
            if (tab <= 0) return false;
            string v = line.Substring(tab + 1).Trim();
            if (v.Length == 0) return false;
            key = line[0];
            value = v;
            return true;
        }
    }
}
