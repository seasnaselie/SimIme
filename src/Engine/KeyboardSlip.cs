// SimIme 引擎：QWERTY 邻键错拼（组串/显示层用，不影响最终落屏文本）
using System;
using System.Collections.Generic;
using System.Text;

namespace SimIme.Engine
{
    internal static class KeyboardSlip
    {
        // 键盘几何：三行按真实交错的横坐标排布，距离 <= Threshold 视为邻键（同一行左右各一键 = 1.0）
        private static readonly string[] Rows = new string[] { "qwertyuiop", "asdfghjkl", "zxcvbnm" };
        private static readonly double[] RowOffsets = new double[] { 0.0, 0.25, 0.75 };
        private const double Threshold = 1.75;

        private static readonly Dictionary<char, string> Cache = new Dictionary<char, string>();
        private static readonly object SyncRoot = new object();

        // 返回小写字母的邻键字母串（按距离由近到远）；非字母或无邻键返回空串
        public static string NeighborsOf(char key)
        {
            char k = char.ToLowerInvariant(key);
            if (k < 'a' || k > 'z') return "";
            lock (SyncRoot)
            {
                string cached;
                if (Cache.TryGetValue(k, out cached)) return cached;
                string list = ComputeNeighbors(k);
                Cache[k] = list;
                return list;
            }
        }

        private static string ComputeNeighbors(char k)
        {
            int kr = -1, kc = -1;
            for (int r = 0; r < Rows.Length && kr < 0; r++)
            {
                int idx = Rows[r].IndexOf(k);
                if (idx >= 0) { kr = r; kc = idx; }
            }
            if (kr < 0) return "";
            double kx = RowOffsets[kr] + kc;
            List<char> keys = new List<char>();
            List<double> dists = new List<double>();
            for (int r = 0; r < Rows.Length; r++)
            {
                for (int c = 0; c < Rows[r].Length; c++)
                {
                    char other = Rows[r][c];
                    if (other == k) continue;
                    double dx = (RowOffsets[r] + c) - kx;
                    double dy = r - kr;
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    if (d > Threshold) continue;
                    int at = keys.Count;
                    for (int i = 0; i < dists.Count; i++)
                    {
                        if (d < dists[i]) { at = i; break; }
                    }
                    keys.Insert(at, other);
                    dists.Insert(at, d);
                }
            }
            StringBuilder sb = new StringBuilder(keys.Count);
            for (int i = 0; i < keys.Count; i++) sb.Append(keys[i]);
            return sb.ToString();
        }

        // 按概率 pct 做一次邻键错拼：无错/不可错时原样返回
        public static string Apply(string pinyin, int pct, Random rnd)
        {
            if (string.IsNullOrEmpty(pinyin) || rnd == null) return pinyin;
            if (pct <= 0) return pinyin;
            if (pct < 100 && rnd.Next(100) >= pct) return pinyin;

            List<int> positions = new List<int>();
            for (int i = 0; i < pinyin.Length; i++)
            {
                if (NeighborsOf(pinyin[i]).Length > 0) positions.Add(i);
            }
            if (positions.Count == 0) return pinyin;

            int pos = positions[rnd.Next(positions.Count)];
            string nb = NeighborsOf(pinyin[pos]);
            char rep = nb[rnd.Next(nb.Length)];
            char[] chars = pinyin.ToCharArray();
            chars[pos] = rep;
            return new string(chars);
        }
    }
}
