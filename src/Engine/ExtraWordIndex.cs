// SimIme 引擎：外部词表（点一下联网更新词典的落地方式）
// 格式与内置 common_words.tsv 一致："词<TAB>频次"，1~4 字中文；层级：习惯库用户词组 > 外部词表 > 内置词典
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SimIme.Engine
{
    internal static class ExtraWordIndex
    {
        public const int MaxRows = 200000;
        public const int MinBlockLen = 2;
        public const int MaxBlockLen = 4;

        private static readonly object SyncRoot = new object();
        private static Dictionary<string, int> _words;
        private static string _status = "(未注册外部词表)";

        public static string Status
        {
            get { lock (SyncRoot) { return _status; } }
        }

        // null 表示未注册（此时切分与注册前逐字段一致）
        public static Dictionary<string, int> Words
        {
            get { lock (SyncRoot) { return _words; } }
        }

        // 任何异常都转成状态字符串，绝不抛出；失败时退回只用内置词典
        public static string SetFile(string path)
        {
            lock (SyncRoot)
            {
                if (string.IsNullOrEmpty(path))
                {
                    _words = null;
                    _status = "外部词表：已注销，只用内置词典";
                    return _status;
                }
                try
                {
                    if (!File.Exists(path))
                    {
                        _words = null;
                        _status = "外部词表：文件不存在，退回只用内置词典 -> " + path;
                        return _status;
                    }
                    string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                    int rows = 0;
                    int kept = 0;
                    int dropped = 0;
                    Dictionary<string, int> map = new Dictionary<string, int>(StringComparer.Ordinal);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i];
                        if (line.Length == 0) continue;
                        int tab = line.IndexOf('\t');
                        if (tab <= 0) continue;
                        string w = line.Substring(0, tab);
                        int f;
                        if (!int.TryParse(line.Substring(tab + 1).Trim(), out f) || f <= 0) continue;
                        rows++;
                        if (w.Length < MinBlockLen || w.Length > MaxBlockLen) continue;
                        bool cjk = true;
                        for (int k = 0; k < w.Length; k++)
                        {
                            if (!HabitStore.IsCjk(w[k])) { cjk = false; break; }
                        }
                        if (!cjk) continue;
                        int old;
                        if (map.TryGetValue(w, out old))
                        {
                            if (f > old) map[w] = f;
                            continue;
                        }
                        if (map.Count >= MaxRows)
                        {
                            dropped++;
                            continue;
                        }
                        map[w] = f;
                        kept++;
                    }
                    if (map.Count == 0)
                    {
                        _words = null;
                        _status = "外部词表：解析后没有可用的 2~4 字中文词（含频次的有效行 " + rows + "），退回只用内置词典 -> " + path;
                        return _status;
                    }
                    _words = map;
                    _status = "外部词表：已加载 " + kept + " 条 2~4 字词（含频次的有效行 " + rows
                        + (dropped > 0 ? "，超出上限 " + MaxRows + " 已截断 " + dropped + " 条" : "")
                        + "）-> " + path;
                    return _status;
                }
                catch (Exception ex)
                {
                    _words = null;
                    _status = "外部词表：读取/解析失败，退回只用内置词典 -> " + ex.GetType().Name + ": " + ex.Message;
                    return _status;
                }
            }
        }
    }
}
