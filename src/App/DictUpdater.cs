using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SimIme.Engine;

namespace SimIme.App
{
    internal sealed class DictUpdateResult
    {
        internal bool Ok;
        internal string Message = "";
        internal int Count;
        internal long DownloadedBytes;
        internal long TarBytes;
        internal long PayloadBytes;
        internal string Url = "";
        internal string WrittenPath = "";
        internal long IndexMs;
        internal long DownloadMs;
        internal long ElapsedMs;
        internal bool RegisteredThisRun;
        internal string RegisterNote = "";
        internal string SourceKind = "";
    }

    // 联网更新外部词表：HTTP 拉 jieba 分发包 -> GZip -> 自写最小 TAR 解析 -> 只留 1~4 字纯中文词
    // -> 原子写入目标文件（先写 .tmp 再替换，失败不破坏已有文件）
    internal static class DictUpdater
    {
        private const int MaxEntries = 200000;
        private const int IndexTimeoutMs = 20000;      // 索引页
        private const int PackageTimeoutMs = 180000;   // 分发包（慢网络留足时间，配进度上报）
        internal static volatile string Progress = "";  // 供状态行轮询：已下载 X / Y 字节

        internal static DictUpdateResult Run(AppConfig cfg, string mirrorOverride, string outputOverride)
        {
            DictUpdateResult result = new DictUpdateResult();
            DateTime started = DateTime.Now;
            try
            {
                string mirror = string.IsNullOrEmpty(mirrorOverride) ? cfg.Behavior.DictMirror : mirrorOverride;
                string output = string.IsNullOrEmpty(outputOverride) ? cfg.Behavior.ExtraDictFile : outputOverride;
                if (string.IsNullOrEmpty(mirror))
                {
                    result.Message = "失败：尚未配置词典镜像地址（Behavior.DictMirror 为空）";
                    return result;
                }
                if (string.IsNullOrEmpty(output))
                {
                    result.Message = "失败：尚未配置外部词表输出路径（Behavior.ExtraDictFile 为空）";
                    return result;
                }
                if (!mirror.EndsWith("/", StringComparison.Ordinal)) mirror += "/";

                // ① 首选 pypinyin 的 wheel（ZIP，约 840KB，含 4.6 万条词组）；失败则**明确记录原因**并回落 jieba 源码包
                List<KeyValuePair<string, int>> words;
                string failureReason;
                words = TryFetchPypinyin(mirror, result, out failureReason);
                if (words != null)
                {
                    result.SourceKind = "pypinyin wheel";
                }
                else
                {
                    Log.Info("pypinyin 源不可用（" + failureReason + "），改用 jieba 源码包源");
                    words = TryFetchJieba(mirror, result, out failureReason);
                    if (words != null) result.SourceKind = "jieba 源码包";
                }
                if (words == null)
                {
                    result.Message = "失败：" + (failureReason == null ? "没有可用的词典数据源" : failureReason);
                    Log.Error(result.Message);
                    return result;
                }
                if (words.Count == 0)
                {
                    result.Message = "失败：解析出的 2~4 字纯中文词组为 0 条（数据源格式可能变了），未改动任何文件";
                    Log.Error(result.Message);
                    return result;
                }

                // ⑥ 原子写入
                try
                {
                    WriteAtomic(output, words);
                }
                catch (Exception ex)
                {
                    result.Message = "失败：写盘失败（" + output + "）：" + ex.GetType().Name + ": " + ex.Message
                        + "｜已有文件未被改动（先写 .tmp 再替换）";
                    Log.Error(result.Message, ex);
                    return result;
                }

                result.Ok = true;
                result.Count = words.Count;
                result.WrittenPath = output;
                // ⑦ 让本次运行即生效（A 的注册 API；未交付则明说重启后生效）
                string registerNote;
                result.RegisteredThisRun = TryRegister(output, out registerNote);
                result.RegisterNote = registerNote;
                result.Message = string.Format("成功：外部词表已更新 {0} 条（来源={1}：{2}），写入={3}",
                    words.Count, result.SourceKind, result.Url, output);
                if (!string.IsNullOrEmpty(registerNote)) result.Message += "｜" + registerNote;
                Log.Info(result.Message);
                return result;
            }
            catch (Exception ex)
            {
                result.Message = "失败：词典更新发生未预期异常：" + ex.GetType().Name + ": " + ex.Message;
                Log.Error(result.Message, ex);
                return result;
            }
            finally
            {
                Progress = "";
                result.ElapsedMs = (long)(DateTime.Now - started).TotalMilliseconds;
            }
        }

        // ---------------- 数据源 ①：pypinyin wheel（ZIP，首选） ----------------

        private static List<KeyValuePair<string, int>> TryFetchPypinyin(string mirror, DictUpdateResult result, out string failureReason)
        {
            failureReason = null;
            string indexUrl = mirror + "pypinyin/";
            DateTime t0 = DateTime.Now;
            string indexText;
            try
            {
                indexText = HttpGetText(indexUrl);
            }
            catch (Exception ex)
            {
                failureReason = "无法访问索引页 " + indexUrl + "（" + DescribeHttpError(ex) + "）";
                return null;
            }
            result.IndexMs = (long)(DateTime.Now - t0).TotalMilliseconds;
            Log.Info(string.Format("词典更新：索引页 {0} 用时 {1}ms（{2} 字节）", indexUrl, result.IndexMs, indexText.Length));

            string wheelUrl = FindPackageUrl(indexText, indexUrl, "pypinyin-", new string[] { ".whl" });
            if (wheelUrl == null)
            {
                failureReason = "索引页里找不到 pypinyin 的 .whl 包（" + indexUrl + "）";
                return null;
            }
            byte[] wheel;
            DateTime t1 = DateTime.Now;
            try
            {
                wheel = HttpGetBytes(wheelUrl, PackageTimeoutMs, out result.DownloadedBytes);
            }
            catch (Exception ex)
            {
                failureReason = "下载 " + wheelUrl + " 失败（" + DescribeHttpError(ex) + "）";
                return null;
            }
            result.DownloadMs = (long)(DateTime.Now - t1).TotalMilliseconds;
            result.Url = wheelUrl;
            Log.Info(string.Format("词典更新：已下载 {0} 字节（用时 {1}ms）：{2}", result.DownloadedBytes, result.DownloadMs, wheelUrl));

            byte[] jsonBytes;
            string entryName;
            try
            {
                // 0.55 是 phrases_dict.json，0.9.5 是 phrases_dict.py：按文件名片段找，跨版本都能命中
                jsonBytes = ExtractZipEntryByFragment(wheel, "phrases_dict", out entryName);
            }
            catch (Exception ex)
            {
                failureReason = "ZIP 解析失败：" + ex.Message;
                return null;
            }
            if (jsonBytes == null)
            {
                failureReason = "wheel 里找不到 phrases_dict（条目清单见 error.log）";
                try
                {
                    List<string> names = ListZipEntries(wheel, 20);
                    for (int i = 0; i < names.Count; i++) Log.Info("wheel 条目：" + names[i]);
                }
                catch { }
                return null;
            }
            result.PayloadBytes = jsonBytes.Length;
            Log.Info(string.Format("词典更新：wheel 内取到 {0}（{1} 字节）", entryName, jsonBytes.Length));

            List<KeyValuePair<string, int>> words;
            try
            {
                words = ParsePypinyinJson(jsonBytes);
            }
            catch (Exception ex)
            {
                failureReason = "短语表解析失败：" + ex.Message;
                return null;
            }
            Log.Info(string.Format("词典更新：pypinyin 短语表解出 {0} 条 2~4 字纯中文词组", words.Count));
            return words;
        }

        // pypinyin/phrases_dict.json：顶层键即词组。该源没有频次，用**按词长的固定名义频次**（写明且稳定）
        internal static int NominalFreq(int length)
        {
            if (length <= 2) return 2000;
            if (length == 3) return 1500;
            return 1000;   // 4 字
        }

        internal static List<KeyValuePair<string, int>> ParsePypinyinJson(byte[] jsonBytes)
        {
            string text = Encoding.UTF8.GetString(jsonBytes);
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            List<KeyValuePair<string, int>> words = new List<KeyValuePair<string, int>>();
            // pypinyin 0.55 是 phrases_dict.json（双引号键）；0.9.5 是 phrases_dict.py（单引号键）——两种都吃
            Regex rx = new Regex("(['\"])([^'\"]{1,12})\\1\\s*:", RegexOptions.Compiled);
            MatchCollection matches = rx.Matches(text);
            for (int i = 0; i < matches.Count; i++)
            {
                string word = matches[i].Groups[2].Value;
                if (word.Length < 2 || word.Length > 4) continue;
                if (!IsAllHan(word)) continue;
                if (!seen.Add(word)) continue;
                words.Add(new KeyValuePair<string, int>(word, NominalFreq(word.Length)));
            }
            SortWords(words);
            if (words.Count > MaxEntries) words.RemoveRange(MaxEntries, words.Count - MaxEntries);
            return words;
        }

        // ---------------- 数据源 ②：jieba tar.gz（回落） ----------------

        private static List<KeyValuePair<string, int>> TryFetchJieba(string mirror, DictUpdateResult result, out string failureReason)
        {
            failureReason = null;
            string indexUrl = mirror + "jieba/";
            string indexText;
            try
            {
                indexText = HttpGetText(indexUrl);
            }
            catch (Exception ex)
            {
                failureReason = "无法访问索引页 " + indexUrl + "（" + DescribeHttpError(ex) + "）";
                return null;
            }
            string packageUrl = FindPackageUrl(indexText, indexUrl, "jieba-", new string[] { ".tar.gz" });
            if (packageUrl == null)
            {
                failureReason = "索引页里找不到 jieba 的 tar.gz 包（" + indexUrl + "）";
                return null;
            }
            // jieba 源码包 18MB，按本机实测吞吐(~25KB/s)要十几分钟，注定超时：先 HEAD 看体积，过大直接跳过
            long headSize = HttpHeadContentLength(packageUrl);
            if (headSize > 4L * 1024 * 1024)
            {
                failureReason = string.Format("jieba 源码包 {0} 字节（约 {1} MB），按本机实测吞吐会超时，已跳过",
                    headSize, (headSize / 1048576.0).ToString("0.0"));
                Log.Info("词典更新：" + failureReason);
                return null;
            }
            byte[] package;
            try
            {
                package = HttpGetBytes(packageUrl, PackageTimeoutMs, out result.DownloadedBytes);
            }
            catch (Exception ex)
            {
                failureReason = "下载 " + packageUrl + " 失败（" + DescribeHttpError(ex) + "）";
                return null;
            }
            result.Url = packageUrl;
            byte[] tar;
            try { tar = Gunzip(package); result.TarBytes = tar.Length; }
            catch (Exception ex) { failureReason = "GZip 解压失败：" + ex.Message; return null; }

            byte[] dictBytes;
            string entryName;
            try { dictBytes = ExtractTarEntry(tar, "/dict.txt", out entryName); }
            catch (Exception ex) { failureReason = "TAR 解析失败：" + ex.Message; return null; }
            if (dictBytes == null) { failureReason = "TAR 里找不到 jieba/dict.txt"; return null; }
            result.PayloadBytes = dictBytes.Length;
            Log.Info(string.Format("词典更新（回落 jieba）：取到 {0}（{1} 字节）", entryName, dictBytes.Length));
            try { return ParseDict(dictBytes); }
            catch (Exception ex) { failureReason = "词表解析异常：" + ex.Message; return null; }
        }

        // 自写最小 ZIP 解析（wheel 就是 zip）：走中央目录，支持 stored(0) 与 deflate(8)
        internal static byte[] ExtractZipEntry(byte[] zip, string nameSuffix, out string foundName)
        {
            foundName = null;
            int eocd = -1;
            int floor = Math.Max(0, zip.Length - 22 - 65536);
            for (int i = zip.Length - 22; i >= floor; i--)
            {
                if (zip[i] == 0x50 && zip[i + 1] == 0x4B && zip[i + 2] == 0x05 && zip[i + 3] == 0x06) { eocd = i; break; }
            }
            if (eocd < 0) throw new InvalidDataException("不是有效的 ZIP（找不到中央目录）");
            int count = ReadU16(zip, eocd + 10);
            long cdOffset = ReadU32(zip, eocd + 16);
            int p = (int)cdOffset;
            for (int i = 0; i < count && p + 46 <= zip.Length; i++)
            {
                if (!(zip[p] == 0x50 && zip[p + 1] == 0x4B && zip[p + 2] == 0x01 && zip[p + 3] == 0x02))
                {
                    throw new InvalidDataException("中央目录项签名不对（偏移 " + p + "）");
                }
                int method = ReadU16(zip, p + 10);
                long compSize = ReadU32(zip, p + 20);
                long uncompSize = ReadU32(zip, p + 24);
                int nameLen = ReadU16(zip, p + 28);
                int extraLen = ReadU16(zip, p + 30);
                int commentLen = ReadU16(zip, p + 32);
                long localOffset = ReadU32(zip, p + 42);
                string name = Encoding.UTF8.GetString(zip, p + 46, nameLen);
                if (MatchesEntryName(name, nameSuffix))
                {
                    int lp = (int)localOffset;
                    if (!(zip[lp] == 0x50 && zip[lp + 1] == 0x4B && zip[lp + 2] == 0x03 && zip[lp + 3] == 0x04))
                    {
                        throw new InvalidDataException("局部文件头签名不对：" + name);
                    }
                    int lNameLen = ReadU16(zip, lp + 26);
                    int lExtraLen = ReadU16(zip, lp + 28);
                    int dataStart = lp + 30 + lNameLen + lExtraLen;
                    byte[] compressed = new byte[compSize];
                    Array.Copy(zip, dataStart, compressed, 0, (int)compSize);
                    foundName = name;
                    if (method == 0) return compressed;
                    if (method != 8) throw new InvalidDataException("不支持的压缩方式：" + method);
                    using (MemoryStream input = new MemoryStream(compressed))
                    using (DeflateStream deflate = new DeflateStream(input, CompressionMode.Decompress))
                    using (MemoryStream output = new MemoryStream())
                    {
                        byte[] chunk = new byte[81920];
                        int read;
                        while ((read = deflate.Read(chunk, 0, chunk.Length)) > 0) output.Write(chunk, 0, read);
                        byte[] data = output.ToArray();
                        if (uncompSize > 0 && data.Length != uncompSize)
                        {
                            Log.Info("注意：解出字节数与中央目录不一致（" + data.Length + " vs " + uncompSize + "）");
                        }
                        return data;
                    }
                }
                p += 46 + nameLen + extraLen + commentLen;
            }
            return null;
        }

        // ZIP 条目清单（诊断用）：名字 + 原始/压缩大小
        internal static List<string> ListZipEntries(byte[] zip, int max)
        {
            List<string> names = new List<string>();
            int eocd = -1;
            int floor = Math.Max(0, zip.Length - 22 - 65536);
            for (int i = zip.Length - 22; i >= floor; i--)
            {
                if (zip[i] == 0x50 && zip[i + 1] == 0x4B && zip[i + 2] == 0x05 && zip[i + 3] == 0x06) { eocd = i; break; }
            }
            if (eocd < 0) { names.Add("(不是有效的 ZIP)"); return names; }
            int count = ReadU16(zip, eocd + 10);
            int p = (int)ReadU32(zip, eocd + 16);
            for (int i = 0; i < count && p + 46 <= zip.Length && names.Count < max; i++)
            {
                if (!(zip[p] == 0x50 && zip[p + 1] == 0x4B && zip[p + 2] == 0x01 && zip[p + 3] == 0x02)) break;
                int method = ReadU16(zip, p + 10);
                long compSize = ReadU32(zip, p + 20);
                long uncompSize = ReadU32(zip, p + 24);
                int nameLen = ReadU16(zip, p + 28);
                int extraLen = ReadU16(zip, p + 30);
                int commentLen = ReadU16(zip, p + 32);
                string name = Encoding.UTF8.GetString(zip, p + 46, nameLen);
                names.Add(string.Format("{0}  [压缩={1} 原始={2} 方式={3}]", name, compSize, uncompSize, method));
                p += 46 + nameLen + extraLen + commentLen;
            }
            names.Add("(共 " + count + " 个条目)");
            return names;
        }

        // 按"文件名里含某片段"找第一个条目（跨版本更稳：phrases_dict.json / phrases_dict.py 都能命中）
        internal static byte[] ExtractZipEntryByFragment(byte[] zip, string fragment, out string foundName)
        {
            foundName = null;
            int eocd = -1;
            int floor = Math.Max(0, zip.Length - 22 - 65536);
            for (int i = zip.Length - 22; i >= floor; i--)
            {
                if (zip[i] == 0x50 && zip[i + 1] == 0x4B && zip[i + 2] == 0x05 && zip[i + 3] == 0x06) { eocd = i; break; }
            }
            if (eocd < 0) throw new InvalidDataException("不是有效的 ZIP（找不到中央目录）");
            int count = ReadU16(zip, eocd + 10);
            int p = (int)ReadU32(zip, eocd + 16);
            for (int i = 0; i < count && p + 46 <= zip.Length; i++)
            {
                if (!(zip[p] == 0x50 && zip[p + 1] == 0x4B && zip[p + 2] == 0x01 && zip[p + 3] == 0x02)) break;
                int nameLen = ReadU16(zip, p + 28);
                int extraLen = ReadU16(zip, p + 30);
                int commentLen = ReadU16(zip, p + 32);
                string name = Encoding.UTF8.GetString(zip, p + 46, nameLen);
                if (name.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return ExtractZipEntry(zip, name, out foundName);
                }
                p += 46 + nameLen + extraLen + commentLen;
            }
            return null;
        }

        // 条目匹配：完整路径相同，或**文件名部分**相同（wheel 里路径层级可能变）
        internal static bool MatchesEntryName(string entryName, string wanted)
        {
            if (string.Equals(entryName, wanted, StringComparison.OrdinalIgnoreCase)) return true;
            int slash = wanted.LastIndexOf('/');
            string file = slash >= 0 ? wanted.Substring(slash + 1) : wanted;
            int e = entryName.LastIndexOf('/');
            string entryFile = e >= 0 ? entryName.Substring(e + 1) : entryName;
            return string.Equals(entryFile, file, StringComparison.OrdinalIgnoreCase);
        }

        private static int ReadU16(byte[] b, int o) { return b[o] | (b[o + 1] << 8); }

        private static long ReadU32(byte[] b, int o)
        {
            return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        }

        private static void SortWords(List<KeyValuePair<string, int>> words)
        {
            words.Sort(delegate(KeyValuePair<string, int> a, KeyValuePair<string, int> b)
            {
                if (b.Value != a.Value) return b.Value.CompareTo(a.Value);
                return string.CompareOrdinal(a.Key, b.Key);
            });
        }

        // 精确调用 A 的注册 API：public static string SetExtraWordFile(string tsvPath)
        // 取不到就写明确告警，绝不静默回退到别的猜测名字
        internal static bool TryRegister(string path, out string note)
        {
            note = "";
            MethodInfo method = null;
            try
            {
                method = typeof(EngineFactory).GetMethod("SetExtraWordFile",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null, new Type[] { typeof(string) }, null);
            }
            catch (Exception ex)
            {
                Log.Error("查找 EngineFactory.SetExtraWordFile 失败", ex);
            }
            if (method == null)
            {
                note = "警告：引擎未提供 EngineFactory.SetExtraWordFile(string)，已写入，重启后生效";
                Log.Error(note);
                return false;
            }
            try
            {
                object returned = method.Invoke(null, new object[] { path });
                string text = returned as string;
                note = "引擎注册结果：" + (text == null ? "(null)" : text);
                Log.Info(note);
                string status = ExtraWordStatusText();
                if (status != null) Log.Info("引擎外部词表状态（ExtraWordStatus）：" + status);
                return true;
            }
            catch (Exception ex)
            {
                note = "调用 SetExtraWordFile 失败（不影响已写入的文件）";
                Log.Error(note, ex);
                return false;
            }
        }

        internal static string ExtraWordStatusText()
        {
            try
            {
                PropertyInfo p = typeof(EngineFactory).GetProperty("ExtraWordStatus",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (p == null) return null;
                object v = p.GetValue(null, null);
                return v as string;
            }
            catch (Exception ex)
            {
                Log.Error("读取 EngineFactory.ExtraWordStatus 失败", ex);
                return null;
            }
        }

        internal static bool TryRegisterForSelfTest(string path, out string note)
        {
            return TryRegister(path, out note);
        }

        // 状态文本：条数 / 时间 / 路径
        internal static string Describe(AppConfig cfg)
        {
            try
            {
                string path = cfg.Behavior.ExtraDictFile;
                if (string.IsNullOrEmpty(path)) return "外部词表：未配置路径";
                if (!File.Exists(path)) return "外部词表：未安装（点击「联网更新词典」下载）｜路径=" + path;
                int count = 0;
                using (StreamReader reader = new StreamReader(path, Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.Length > 0) count++;
                    }
                }
                FileInfo info = new FileInfo(path);
                return string.Format("外部词表：{0} 条（更新于 {1}，{2} 字节）｜路径={3}",
                    count, info.LastWriteTime.ToString("yyyy-MM-dd HH:mm"), info.Length, path);
            }
            catch (Exception ex)
            {
                Log.Error("读取外部词表状态失败", ex);
                return "外部词表：状态读取失败（详见 error.log）";
            }
        }

        // 本机必须经系统代理出网：失败提示里带上代理配置，用户才知道该看哪里
        internal static string SystemProxyInfo()
        {
            string effective = EffectiveProxyInfo();
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
                {
                    if (key == null) return "（读不到系统代理设置）";
                    object enable = key.GetValue("ProxyEnable");
                    object server = key.GetValue("ProxyServer");
                    object pac = key.GetValue("AutoConfigURL");
                    if (pac != null && pac.ToString().Length > 0) return "本机使用自动配置脚本 " + pac + effective;
                    bool on = enable != null && Convert.ToInt32(enable) != 0;
                    if (on && server != null) return "本机经系统代理 " + server + " 访问外网，请确认代理在运行" + effective;
                    return "注册表 ProxyEnable=0；" + effective;
                }
            }
            catch (Exception ex)
            {
                return "（读取系统代理失败：" + ex.Message + "）";
            }
        }

        // .NET 实际会用的代理（比只看注册表更可信）
        internal static string EffectiveProxyInfo()
        {
            try
            {
                IWebProxy proxy = WebRequest.DefaultWebProxy;
                if (proxy == null) return "（.NET 未配置代理）";
                Uri probe = new Uri("http://mirrors.aliyun.com/");
                Uri via = proxy.GetProxy(probe);
                if (via == null || via == probe) return ".NET 实际=直连（未使用代理）";
                return ".NET 实际使用的代理=" + via.Host + ":" + via.Port;
            }
            catch (Exception ex)
            {
                return "（读取 .NET 代理失败：" + ex.Message + "）";
            }
        }

        internal static string DescribeHttpError(Exception ex)
        {
            string proxy = SystemProxyInfo();
            TimeoutException timeout = ex as TimeoutException;
            if (timeout != null) return timeout.Message + "｜" + proxy;
            WebException we = ex as WebException;
            if (we != null)
            {
                string detail = (we.Status == WebExceptionStatus.ProtocolError && we.Response != null)
                    ? "HTTP " + (int)((HttpWebResponse)we.Response).StatusCode
                    : we.Status.ToString();
                return "网络请求失败：" + detail + "：" + we.Message + "｜" + proxy;
            }
            return ex.GetType().Name + ": " + ex.Message + "｜" + proxy;
        }

        // HEAD 拿 Content-Length（拿不到返回 -1）
        internal static long HttpHeadContentLength(string url)
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "HEAD";
                request.Timeout = IndexTimeoutMs;
                request.UserAgent = "SimIme/1.0 (+dict update)";
                IAsyncResult pending = request.BeginGetResponse(null, null);
                if (!pending.AsyncWaitHandle.WaitOne(IndexTimeoutMs))
                {
                    try { request.Abort(); } catch { }
                    return -1;
                }
                using (HttpWebResponse response = (HttpWebResponse)request.EndGetResponse(pending))
                {
                    return response.ContentLength;
                }
            }
            catch (Exception ex)
            {
                Log.Info("HEAD 取体积失败（继续按原流程）：" + ex.Message);
                return -1;
            }
        }

        internal static string HttpGetText(string url)
        {
            long size;
            byte[] data = HttpGetBytes(url, IndexTimeoutMs, out size);
            return Encoding.UTF8.GetString(data);
        }

        // 整段 HTTP 都用 BeginGetResponse + 有界等待包住：
        // 网络被黑洞时（连 DNS/TCP 都不回）也能在超时后中止并给出可操作提示，绝不无限挂住
        internal static byte[] HttpGetBytes(string url, int timeoutMs, out long size)
        {
            size = 0;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;
            request.UserAgent = "SimIme/1.0 (+dict update)";
            request.AllowAutoRedirect = true;
            // 不设 Proxy：走 .NET 默认（即系统代理）。本机必须经代理才能出外网，设成 null 会直接失败。

            IAsyncResult pending = request.BeginGetResponse(null, null);
            if (!pending.AsyncWaitHandle.WaitOne(timeoutMs))
            {
                try { request.Abort(); } catch { }
                throw new TimeoutException("超过 " + timeoutMs + "ms 无响应（网络被阻断或镜像地址不可达）");
            }
            using (HttpWebResponse response = (HttpWebResponse)request.EndGetResponse(pending))
            using (Stream stream = response.GetResponseStream())
            using (MemoryStream buffer = new MemoryStream())
            {
                long total = response.ContentLength;
                byte[] chunk = new byte[65536];
                int read;
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (true)
                {
                    if (DateTime.UtcNow > deadline) throw new TimeoutException("下载超时（超过 " + timeoutMs + "ms）");
                    read = stream.Read(chunk, 0, chunk.Length);
                    if (read <= 0) break;
                    buffer.Write(chunk, 0, read);
                    Progress = string.Format("已下载 {0} / {1} 字节", buffer.Length, total > 0 ? total.ToString() : "?");
                }
                size = buffer.Length;
                return buffer.ToArray();
            }
        }

        // 索引页里找 jieba-*.tar.gz；优先版本号最大的
        internal static string FindPackageUrl(string indexHtml, string baseUrl, string namePrefix, string[] suffixes)
        {
            if (string.IsNullOrEmpty(indexHtml)) return null;
            string bestName = null;
            string bestUrl = null;
            int i = 0;
            while (i < indexHtml.Length)
            {
                int hrefAt = indexHtml.IndexOf("href=\"", i, StringComparison.OrdinalIgnoreCase);
                if (hrefAt < 0) break;
                int start = hrefAt + 6;
                int end = indexHtml.IndexOf('"', start);
                if (end < 0) break;
                string href = indexHtml.Substring(start, end - start);
                i = end + 1;
                int q = href.IndexOf('?');
                if (q >= 0) href = href.Substring(0, q);
                int hash = href.IndexOf('#');
                if (hash >= 0) href = href.Substring(0, hash);
                if (href.Length == 0) continue;
                string file = href;
                int slash = file.LastIndexOf('/');
                if (slash >= 0) file = file.Substring(slash + 1);
                if (file.IndexOf(namePrefix, StringComparison.OrdinalIgnoreCase) != 0) continue;
                bool suffixOk = false;
                for (int s = 0; s < suffixes.Length; s++)
                {
                    if (file.EndsWith(suffixes[s], StringComparison.OrdinalIgnoreCase)) { suffixOk = true; break; }
                }
                if (!suffixOk) continue;
                if (bestName == null || CompareVersionedName(file, bestName) > 0)
                {
                    bestName = file;
                    bestUrl = href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? href : baseUrl + href;
                }
            }
            return bestUrl;
        }

        // 按**数值化版本**比较：字符串序会把 pypinyin-0.9.5 误判为比 0.55.0 新（实测踩过）
        private static int CompareVersionedName(string a, string b)
        {
            int[] va = ExtractVersionNumbers(a);
            int[] vb = ExtractVersionNumbers(b);
            int n = Math.Max(va.Length, vb.Length);
            for (int i = 0; i < n; i++)
            {
                int x = i < va.Length ? va[i] : 0;
                int y = i < vb.Length ? vb[i] : 0;
                if (x != y) return x < y ? -1 : 1;
            }
            return string.CompareOrdinal(a, b);
        }

        // 从 "pypinyin-0.55.0-py2.py3-none-any.whl" 里取出版本数字段 [0,55,0]
        internal static int[] ExtractVersionNumbers(string name)
        {
            List<int> parts = new List<int>();
            if (string.IsNullOrEmpty(name)) return parts.ToArray();
            int i = 0;
            while (i < name.Length && !(name[i] >= '0' && name[i] <= '9')) i++;   // 跳过包名前缀
            while (i < name.Length)
            {
                if (name[i] >= '0' && name[i] <= '9')
                {
                    int start = i;
                    while (i < name.Length && name[i] >= '0' && name[i] <= '9') i++;
                    int value;
                    if (int.TryParse(name.Substring(start, i - start), out value)) parts.Add(value);
                    else parts.Add(0);
                }
                else if (name[i] == '.')
                {
                    i++;
                }
                else
                {
                    break;   // 版本段结束（遇到 -py2 / .tar.gz 之类）
                }
            }
            return parts.ToArray();
        }

        internal static byte[] Gunzip(byte[] gz)
        {
            using (MemoryStream input = new MemoryStream(gz))
            using (GZipStream gzip = new GZipStream(input, CompressionMode.Decompress))
            using (MemoryStream output = new MemoryStream())
            {
                byte[] chunk = new byte[81920];
                int read;
                while ((read = gzip.Read(chunk, 0, chunk.Length)) > 0)
                {
                    output.Write(chunk, 0, read);
                }
                return output.ToArray();
            }
        }

        // 自写最小 TAR 解析：512 字节块头，name@0(100) size@124(12,八进制) type@156
        internal static byte[] ExtractTarEntry(byte[] tar, string nameSuffix, out string foundName)
        {
            foundName = null;
            int offset = 0;
            while (offset + 512 <= tar.Length)
            {
                bool allZero = true;
                for (int i = 0; i < 512; i++)
                {
                    if (tar[offset + i] != 0) { allZero = false; break; }
                }
                if (allZero) break;

                string name = ReadAscii(tar, offset, 100);
                long size = ReadOctal(tar, offset + 124, 12);
                byte type = tar[offset + 156];
                int dataStart = offset + 512;

                bool isFile = (type == 0 || type == (byte)'0');
                if (isFile && name.EndsWith(nameSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    if (size < 0 || dataStart + size > tar.Length)
                    {
                        throw new InvalidDataException("TAR 条目长度越界：" + name + " size=" + size);
                    }
                    byte[] data = new byte[size];
                    Array.Copy(tar, dataStart, data, 0, (int)size);
                    foundName = name;
                    return data;
                }
                long padded = ((size + 511) / 512) * 512;
                offset = dataStart + (int)padded;
            }
            return null;
        }

        private static string ReadAscii(byte[] buffer, int offset, int max)
        {
            int len = 0;
            while (len < max && buffer[offset + len] != 0) len++;
            return Encoding.UTF8.GetString(buffer, offset, len);
        }

        private static long ReadOctal(byte[] buffer, int offset, int max)
        {
            long value = 0;
            int i = offset;
            int end = offset + max;
            while (i < end && (buffer[i] == ' ' || buffer[i] == 0)) i++;
            while (i < end)
            {
                byte b = buffer[i];
                if (b < '0' || b > '7') break;
                value = value * 8 + (b - '0');
                i++;
            }
            return value;
        }

        // jieba/dict.txt：每行「词 频次 [词性]」，只留 1~4 字纯中文，按频次降序，上限 200000
        internal static List<KeyValuePair<string, int>> ParseDict(byte[] dictBytes)
        {
            List<KeyValuePair<string, int>> words = new List<KeyValuePair<string, int>>();
            using (StreamReader reader = new StreamReader(new MemoryStream(dictBytes), Encoding.UTF8))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    int space = line.IndexOfAny(new char[] { ' ', '\t' });
                    if (space <= 0) continue;
                    string word = line.Substring(0, space);
                    if (word.Length < 1 || word.Length > 4) continue;
                    if (!IsAllHan(word)) continue;
                    int freq = 0;
                    int at = space;
                    while (at < line.Length && (line[at] == ' ' || line[at] == '\t')) at++;
                    int numEnd = at;
                    while (numEnd < line.Length && line[numEnd] >= '0' && line[numEnd] <= '9') numEnd++;
                    if (numEnd == at) continue;
                    if (!int.TryParse(line.Substring(at, numEnd - at), NumberStyles.Integer, CultureInfo.InvariantCulture, out freq)) continue;
                    words.Add(new KeyValuePair<string, int>(word, freq));
                }
            }
            words.Sort(delegate(KeyValuePair<string, int> a, KeyValuePair<string, int> b)
            {
                if (b.Value != a.Value) return b.Value.CompareTo(a.Value);
                return string.CompareOrdinal(a.Key, b.Key);
            });
            if (words.Count > MaxEntries) words.RemoveRange(MaxEntries, words.Count - MaxEntries);
            return words;
        }

        internal static bool IsAllHan(string word)
        {
            for (int i = 0; i < word.Length; i++)
            {
                char c = word[i];
                if (c < 0x4E00 || c > 0x9FFF) return false;
            }
            return word.Length > 0;
        }

        // 原子写入：先写 .tmp，成功后再替换目标；任何一步失败都不动已有文件
        internal static void WriteAtomic(string path, List<KeyValuePair<string, int>> words)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                throw new DirectoryNotFoundException("目录不存在：" + dir);
            }
            string tmp = path + ".tmp";
            using (StreamWriter writer = new StreamWriter(tmp, false, new UTF8Encoding(false)))
            {
                for (int i = 0; i < words.Count; i++)
                {
                    writer.Write(words[i].Key);
                    writer.Write('\t');
                    writer.Write(words[i].Value);
                    writer.Write("\n");
                }
            }
            try
            {
                if (File.Exists(path))
                {
                    File.Replace(tmp, path, null);
                }
                else
                {
                    File.Move(tmp, path);
                }
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
        }
    }
}
