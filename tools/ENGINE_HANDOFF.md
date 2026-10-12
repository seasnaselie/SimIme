# SimIme 引擎（A 交付）—— 接口与构建说明

> 归属：src/Engine/**、src/Assets/**、tools/**（EngineTypes.cs 只读，未改动，仅新增文件）

## 1. 公开 API（签名冻结，反射可调用）

    namespace SimIme.Engine {
      public static class EngineFactory {
        public static IPinyinTable CreatePinyinTable();                                   // 0 参
        public static IHabitStore CreateHabitStore(string path);
        public static IList<Step> BuildPlan(string text, EngineConfig cfg, IPinyinTable table, IHabitStore habits);
        public static string VerifyReplay(string text, EngineConfig cfg);
        // --- 第七轮新增（词组级工作流），既有 4 个签名未改动 ---
        public static IList<string> SplitPhrases(string text, EngineConfig cfg);   // 词组切分
        public static string MakeKeyboardSlip(string pinyin, EngineConfig cfg);    // 邻键错拼（显示层）
        public static string MakeKeyboardSlip(string pinyin, int randomSeed);      // 同上，显式种子且必定尝试一次
        public static string NeighborKeysOf(char key);                             // QWERTY 邻键数据
        // --- 第八轮：用户词表 ---
        public static IList<string> SplitPhrases(string text, EngineConfig cfg, IHabitStore habits); // 用户词组参与切分
        // --- 第九轮：外部词表叠加 ---
        public static string SetExtraWordFile(string tsvPath);   // 注册/注销外部词表，返回状态字符串
        public static string ExtraWordStatus { get; }            // 最近一次注册结果（App 可直接读出来记日志）
        public static System.Action<string> LogSink;             // 可选日志回调
      }
    }

    IHabitStore 新增成员（第八轮，既有 5 个方法签名未动）：
        IList<string> UserWords(int max);   // 按次数降序返回学到的 2~4 字用户词组（只含出现 >= 2 次的）
        int UserWordCount(string word);     // 该用户词组的出现次数；不是用户词组返回 0

实现文件：EngineFactory.cs（门面 + VerifyReplay）、TypingPlanner.cs（计划生成）、
PinyinTable.cs、HabitStore.cs、CommonWordIndex.cs、AssetLoader.cs。
Contract 类型全部来自只读的 EngineTypes.cs。

## 2. 构建（C 的 build.ps1 请照抄资源三行）

    csc /nologo /target:library /codepage:65001 /optimize+ /out:SimIme.Engine.dll
        /resource:src\Assets\pinyin_full.tsv,SimIme.Assets.pinyin_full.tsv
        /resource:src\Assets\char_freq.tsv,SimIme.Assets.char_freq.tsv
        /resource:src\Assets\common_words.tsv,SimIme.Assets.common_words.tsv
        src\Engine\*.cs

现成脚本：tools\build_engine.ps1（自动补 .cs 的 UTF-8 BOM，再调用 csc）。
- 所有 .cs 必须是 UTF-8 带 BOM + /codepage:65001，否则中文字面量乱码。
- 裸命令 csc /target:library /codepage:65001 src\Engine\*.cs 能编译通过（实测 exit 0，25600 字节），
  但产物没有嵌入资源，运行期 CreatePinyinTable() 返回 IsReady=false。要么加 /resource，要么把三个 .tsv 放到 exe 同目录。

## 3. 数据（src\Assets，全部由 tools\gen_pinyin_data.py 生成，可重跑）

| 文件 | 行数 | 字节 | 格式 | 用途 |
|---|---|---|---|---|
| pinyin_full.tsv | 20901 | 169193 | 字 TAB 无调小写拼音 | 全量表（资源名 SimIme.Assets.pinyin_full.tsv） |
| char_freq.tsv | 6707 | 56719 | 字 TAB 常用度分值 | 常用字白名单+排序（GB2312 ∩ jieba 语料），错字候选只从这里出 |
| common_words.tsv | 120000 | 1390008 | 词 TAB 语料词频（1~4 字） | 词组切分词典 + 词组错词优先挑真词 + 习惯权重回退 + 句首重打素材 |

读音来源（现代文本常用读音，非书面/文言）：
- 基底：pypinyin 0.55.0 的 pinyin_dict 首读音（语料序；的=de 了=le 着=zhe 还=hai 都=dou 什=shen 么=me 行=xíng），缺字用 0.9.5 补位；
- 覆盖：tools\pinyin_overrides.tsv（字 TAB 读音，可改；含 24 条，改完重跑 gen_pinyin_data.py 即可，不动代码）；
- 已实测否决「词表投票」当主数据源：词表里 的 以 目的/的确 为主 -> 投票 71% 判 di，与真实文本相反（都 88% 判 du、地 100% 判 di 同理），故只用它做诊断。

重新生成：py -3 tools\gen_pinyin_data.py（依赖 _devA\pkg 下的 pypinyin/jieba 解包目录；HTTP 镜像 aliyun）。

## 4. 资源找不到时的三条加载路径（均为实测）

1. 嵌入资源精确名 -> OK（3 个资源，CharCount=20901）
2. 无资源 + exe/dll 同目录有同名 .tsv -> OK（IsReady=True 20901；查找顺序：程序集目录 -> AppDomain.BaseDirectory -> 当前工作目录）
3. 都没有 -> IsReady=false 空表；BuildPlan 仍可工作（只剩 句首重打/漏字回填/重复输入 三类错字，无同音/鼻音）

## 5. BuildPlan 行为契约（B 需要知道的）

- Step.Kind：Char（Text=该字，可能是代理对）/ Backspace（Text=null）/ LineBreak（Text=换行符）/ Sleep（Text=null）。
- 不含启动等待；调用方自行 Sleep(cfg.Rhythm.StartupDelayMs)。
- 行结束符归一化：BuildPlan 入口把 CRLF / 单独 CR / 单独 LF 统一成 LF，各对应恰好一个 LineBreak 步，不会产生 Char(CR)。
  即计划的落屏等价物 = 把入参行结束符归一化为 LF 后的文本；除行结束符外的所有字符一律原样输出（其它控制符不动）。
- 不变量：把 Char 追加、LineBreak 追加换行、Backspace 删除末字符，最终文本 === 归一化后的入参原文；退格永不落空（无下溢）。
- VerifyReplay 的比对基准同样是归一化后的文本，报告含 lineEndingsNormalized / rawInputMatches 两行可核对。
- Reason 取值：null（正常字）/ 思考停顿 / 休息 / 换行 / 同音字错字 / 鼻音错字 / 句首重打 / 漏字回填 / 重复输入 / 词组错词。

### 5.1 词组级工作流（第七轮新增）

- SplitPhrases(text, cfg)：按词组切块。硬契约——各块顺序拼接逐字等于 NormalizeLineEndings(text)；空串返回空列表；同 RandomSeed 可复现。
  切分规则：标点各自成块；换行各自成块；ASCII 字母数字连续段成一块（<=16）；空格段成一块（<=3）；汉字按 common_words.tsv 词典取"以该字开头的最长词（2~4 字）"，
  词典里**没有任何 >=2 字词**匹配时：默认只取 **1 个字**（EngineConfig.PhraseJitterMaxChars = 1），因此不会出现「去公 / 园散步」这类被随机窗口切出的非词块。
- EngineConfig.PhraseJitterMaxChars（第十七轮新增，钳 1~4，默认 1）：>1 时按种子在 1~该值内抖动块长，用于"随手分块"的观感，但会造出非词块。
  设 4 可**逐字段复现**第十七轮之前的行为（旧基线 sha256 b54f705b...，实测由 tools/selfcheck_engine6.ps1 守护）；默认 1 是本轮**有意的行为变更**（新基线 sha256 b139ba93...）。
  注意：抖动只影响"词典无匹配"的兜底；最长匹配优先级（用户词组 > 外部词表 > 内置词典）与命中时的行为完全不变。
- 词组级同音错词：TypoConfig.WordHomophonePct（默认 2，0=关闭）。命中时把一个块整体打成同音错词 -> 退格整块字数 -> 再打正确块，Reason = 词组错词；
  错词的每个字都取自 char_freq.tsv 常用字池且与原字同音，并优先拼成**真实存在的同音词**（如 星星 -> 猩猩 / 行刑），找不到真词时每字随机取同音字。
  与其它错字一样受 MaxTyposPerSentence 约束；块长为 1 时不触发（那种情况由逐字同音错字覆盖）。
- 键盘邻键错拼：TypoConfig.KeyboardSlipPct（默认 3，0=关闭）。MakeKeyboardSlip 只在拼音串上改**一个**字母为其 QWERTY 邻键；
  邻键由键盘几何（三行交错，距离 <= 1.75 键距）算出：x -> zcsd、n -> bmhj（见 NeighborKeysOf）。
  无错（概率未命中 / 空串 / null / 无字母）时返回原串。**这是组串/显示层的错，BuildPlan 不调用它，落屏文本不受影响。**
- DelayMs 区间（均已断言）：普通字的字延时在 [CharDelayMinMs, CharDelayMaxMs]；Backspace/LineBreak 同区间。
  标点与句末 = 字延时 + PunctuationExtra 叠加：字延时仍在 [CharDelayMinMs, CharDelayMaxMs]、额外量在
  [PunctuationExtraMinMs, PunctuationExtraMaxMs]，故标点步落在
  [CharDelayMinMs + PunctuationExtraMinMs, CharDelayMaxMs + PunctuationExtraMaxMs]。
  Sleep(思考停顿) 在 [ThinkPauseMinMs, ThinkPauseMaxMs]；Sleep(休息) 在 [RestPauseMinMs, RestPauseMaxMs]；全部 >= 0。
- 概率口径：同音/鼻音/漏字/重复 = 每个汉字位置判定；句首重打 = 每个句首判定；均受 MaxTyposPerSentence（每句上限，句首重置）约束。
- RandomSeed != 0 时逐字段可复现；RandomSeed = 0 时每次不同（TickCount 混合自增序号，避免同毫秒重复）。

### 5.2 用户词表（第八轮）

- HabitStore.ObserveText 除原有 1~2 元词频外，额外统计 **2~4 字连续汉字段落的 n-gram 计数**（标点/空白/ASCII 不参与）。
  出现 1 次先入暂存（上限 20000 条，满了清空），出现 >= 2 次才升级为「用户词组」；用户词组上限 **5000 条**，超出按次数淘汰最小的（同次数淘汰序数最小的，确定性）。
- 查询：UserWords(max) / UserWordCount(word)；WordWeight 对用户词组返回 1.5*ln(1+次数)（上限 4），原有词频语义不变。
- 持久化：Save/Load 仍走同一个文件（habits.dat），词组以 phrase TAB 词 TAB 次数 行保存（最多 5000 条），加载后 UserWords 立即可用。
- EngineConfig.UserWordMinCount（默认 3）：只有次数达到该阈值的用户词组才参与切分。
- SplitPhrases(text, cfg, habits)：层级 **习惯库用户词组 > 外部词表 > 内置词典**；用户词组取最长匹配，其中 4 字用户词组若与它的 3 字前缀同频（= 前缀永远跟着同一个字出现）则不单独成块、退回 3 字。
  未传 habits / 阈值不满足 / habits 为 null 时，结果与两参重载**逐字段一致**（两参重载不依赖习惯库）。

### 5.3 外部词表叠加（第九轮）

- SetExtraWordFile(path)：把一份与内置 common_words.tsv **同格式**（词 TAB 频次，1~4 字中文）的词表叠加进切分词典，进程内全局生效；传 null/空串注销。
  返回状态字符串，同时通过 LogSink（若已设置）回调一行日志；App 也可随时读 ExtraWordStatus。
- 层级：**习惯库用户词组 > 外部词表 > 内置词典**；外部词表与内置词典一样参与最长匹配（不消耗随机数）。
- 上限 200000 条（超出截断并在状态里写明截断条数）；只索引 2~4 字中文词。
- 容错：文件不存在 / 解析失败 / 无可用词 → 状态里写明原因并**退回只用内置词典**，绝不抛异常、不影响落屏。
- 未注册（或已注销）时，两参/三参 SplitPhrases 与注册前**逐字段一致**（有基线 sha256 断言守护）。

## 6. 自检

    powershell -File tools\build_engine.ps1        # 编译 + BOM 归一化
    & tools\selfcheck_engine.ps1                   # 35 条（契约/表/回放/同音/鼻音/种子/延时/习惯库）
    & tools\selfcheck_engine2.ps1                  # 23 条（多音字 T1/T2、停顿间隔、快打段、边界、错字上限、五类错字、种子 0）

两份脚本默认读取 _devA\SimIme.Engine.dll，可用 -Dll / -Root 覆盖。

注意：自检会把 DLL 载入当前 PowerShell 进程，进程退出前该文件被锁；
因此「先自检、后编译」必须分成两个进程（否则 csc 报 CS0016 无法写入输出文件）。
自检脚本每次以独立 pwsh 进程运行即可，例如： powershell -NoProfile -File tools\selfcheck_engine.ps1

## 7. 已知边界（未做/存疑）

- 引擎纯逻辑，不含 UI/Win32；GUI 真机验收不在本模块范围。
- 鼻音错字只覆盖「去掉词尾 ng 后相同」的对（an/ang、en/eng、in/ing、ian/iang、uan/uang）；任务书里举的 ong/eng 不在此列
  —— 因为 o 与 e 元音不同，归一化后不相等，会破坏「只差前后鼻音」的可判定性。如需强制加入，改 PinyinTable.NormalizeNasal 一处。
- 与 B 的注入层尚未联调（Step 语义以本文档第 5 节为准）。

## 8. 维护脚本：tools/fetch_dict.ps1（手动运行，不进入产品运行期）

    powershell -File tools/fetch_dict.ps1 [-Source pypinyin|jieba] [-Mode auto|merge|replace] [-Out <目标词表>] [-Mirror <HTTP 镜像>] [-Proxy <代理>] [-Cap 120000] [-DictFile <本地 dict.txt>]

- 源：默认 -Source pypinyin（轮子，约 840KB，实测 840203 字节，解出 phrases_dict.json 2,545,585 字节 -> 46111 条 2~4 字词组，写入 725,968 字节）；
  -Source jieba（源码包约 19MB，解出 dict.txt 5,071,852 字节 -> 120000 条语料词表）作为显式备用。
- **重要**：pypinyin 的 phrases_dict 是「注音消歧词表」而非通用词表（同包 phrases_dict.json 与 phrases_dict.py 内容完全一致，均 47111 键、46111 条 2~4 字中文）：
  它含 十二个/我们/散步/你好，但**不含** 星星/太阳/明天/但是/生日快乐 这类常用词。因此单独替换会把常用词弄丢。
- Mode：auto（默认）= pypinyin 源合并、jieba 源替换；merge = 与现有表求并集（同名取较大频次）后按频次降序截断到 Cap；replace = 只用该源重建。
  实测（以真实表副本为基）：merge 时 baseRows=120000 addRows=46111 mergedDistinct=142673 -> rows=120000 bytes=1,574,205（新增约 2.27 万条，常用词全部保留）。
- 失败演练：包名不存在 -> 索引页 404 -> 退出码 10 且目标词表字节不变；坏 -DictFile -> 9；过小 dict.txt -> 15；坏镜像 -> 10。所有演练后 src/Assets/common_words.tsv 的 sha256 均未变化。
- 并发安全：每次运行使用唯一工作目录 _devA/dictfx/run-<时间戳>-<pid>；2 小时前的残留目录会被尽力清理（失败不致命）。
- 护栏：下载后校验存在且 >= 10000 字节；dict.txt（含 -DictFile）校验 >= 100000 字节；先写临时文件，全部成功才替换目标；
  任何失败都打印原因并以非零退出（10 索引页 / 11 无包 / 12 版本解析 / 13 下载或大小 / 14 解包 / 15 dict.txt 或 -DictFile 不合法 / 16 缺脚本 / 17 重建失败 / 19 新表过小 / 20 替换失败），**绝不改动现有词表**。
- 改完内置词表必须重新编译 exe 才能生效（词表是嵌入资源）。
- 已验证：tools/rebuild_common_words.py 与 tools/gen_pinyin_data.py 对同一份 jieba dict.txt 产出的 common_words.tsv **逐字节相同**（sha256 一致），两条路径可互为校验。
- 控制台与重定向日志都用 UTF-8（脚本开头设置 Console.OutputEncoding 与 PYTHONIOENCODING），中文不会乱码；-Proxy 省略时走系统代理。
