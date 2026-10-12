# SimIme 拟人化中文输入器 —— 设计与任务记录

## 1. 目标
后台托盘常驻（无任务栏图标）的拟人化中文输入器：在自身 UI 粘贴文本 → 确认 → 隐身 →
切到目标窗口按 F9 二次确认 → 锁定前台窗口与插入点 → 等待 → 逐字拟人化上屏 → 结束静默待机。

## 2. 已冻结决策（访谈结论）
| # | 决策 | 结论 |
|---|---|---|
| D1 | 输入法形态 | 自建输入法引擎 + 悬浮条，不注册系统 IME；拼音→汉字由本程序决定 |
| D2 | 上屏方式 | SendInput KEYEVENTF_UNICODE 逐字注入；可选剪贴板分段粘贴兜底 |
| D3 | 流程 | UI 粘贴→确认(隐身)→切目标窗口按 F9=二次确认→锁定→等待→开始 |
| D4 | 锚定 | 前台窗口(必需)+插入点(尽力)：Win32 caret → UIA TextPattern2 → 鼠标位置 |
| D5 | 停止/结束 | F12 停止；结束无前台提示→静默待机→热键注销→仅托盘唤醒 |
| D6 | 数据 | 全量汉字→拼音表内嵌；习惯库本地持久化；联网升级手动 |
| D7 | 交付 | .NET Framework 4.8 单文件 exe + config.json + README + 构建脚本 + 自检模式 |

## 3. 实测环境事实（证据）
- csc 4.0.30319 编译 WinForms exe 成功；exe 直接调用可运行（Start-Process 被沙箱拒绝）。
- 托盘 NotifyIcon 创建成功；RegisterHotKey(F9)=True；GetGUIThreadInfo 取插入符成功。


## 6. 决策记录与事故记录（调度者维护）

### 6.1 已裁决
| 事项 | 裁决 | 依据 |
|---|---|---|
| TextPattern2 | 不用；改 TextPattern.GetSelection() | .NET FX 4.8 托管 UIA 无该类型（csc 复现 CS0117/CS0246）；GetSelection 返回 TextPatternRange[]，GetBoundingRectangles 返回 System.Windows.Rect[]，需 WindowsBase.dll |
| 任务栏判定 | 无 WS_EX_TOOLWINDOW 且无属主窗口才算进任务栏 | WinForms 的 ShowInTaskbar=false 靠归属 parking window 实现，只看 TOOLWINDOW 会误判 |
| ong/eng 鼻音对 | 不纳入 | 中(zhong)≠真(zhen)，纳入会破坏「只差前后鼻音」断言且错字不像人话 |
| 长=chang | 维持 | 两读音都成立，错字都像人话；tools\pinyin_overrides.tsv 一行可改 |
| 长=chang 之外的 9 个多音字 | 必须修（的=de 了=le 着=zhe 还=hai 都=dou 什=shen 么=me 行=xing 地=de） | 原 pypinyin 单字首读音偏书面，会让「的」错打成「第/帝」，一眼假 |
| 停止热键 | 主键 F12 + 候选兜底，实际生效键必须可见 | 实测本机 F12 被第三方常驻占用，否则用户没有停止键 |
| PunctuationExtra | 要求真正生效（标点后停顿可叠加超出字延时上限） | 配置项不该无效；真人打完标点会停顿 |

### 6.2 事故记录（含我自己仪器的缺陷）
1. **沙箱 ACL 阻断**：run_code 在启动阶段失败（grantWrite(<项目目录>) Win32 5）→ 用 diagnose-windows-sandbox-acl 修复（writeOwner false→true 已验证）。
2. **我的探针缺陷①（键类型）**：用 [char] 查 PowerShell 哈希表的 [string] 键 → 22 个常用字全 MISSING。判别证据：同一次运行内 ContainsKey([char])=False / ContainsKey([string])=True。**A 曾归因为编码故障，已更正为探针缺陷。**
3. **我的探针缺陷②（任务栏规则）**：只看 WS_EX_TOOLWINDOW → 把 ShowInTaskbar=false 的窗口误判为会进任务栏。用双控制组标定后修正（托盘替身 False / 普通窗口 True）。
4. **我的探针缺陷③（F 键解析）**：只认识 F8/F9/F12，其余抛 FormatException → 输出为空被读成「无结果」。已改为 F1–F24 通用解析。
5. **我的工具缺陷（argv 偏移）**：sendhotkey 把 a[1] 当标题（应为 a[0]）→ FindWindow 找 "A003" 失败，静默测试假阴性。
6. **验收门假通过（G8）**：原判据只看「候选被占用」，而 F12 是被**第三方**占用的 → 误判通过。已改为「启动前基线为 True 且启动后为 False」才计为本程序占用。
7. **子代理真实缺陷（外部链路抓出）**：
   - 隐藏消息窗自定义 CreateParams.ClassName → WinForms RegisterClass 抛「窗口类名无效」→ 主循环死、进程 500ms 内以退出码 0 静默退出（旧产物复现，error.log 有栈）。修复：去掉自定义类名，改用窗口标题查找。
   - common_words.tsv 按单字格式解析导致 2 字词被截断 → 词权重恒为 0、句首重打无素材。
   - 句首重打片段跨句号；RandomSeed=0 同毫秒重复计划。
8. **环境事实（非缺陷）**：本机 F12 被第三方常驻占用（SimIme 未运行时独立抢注亦失败）；沙箱内进程无法取得前台（SetForegroundWindow 失败、Alt 解锁无效），故真机注入必须由用户现场验收。

### 6.2.1 第六轮新增事故（调度者自身）
9. **门禁 G14 被"构建清空 dist"打脸**：build.ps1 会清空 dist（含 config.json），而我的 G14 直接读 config.json 断言默认值 → 必然 FAIL。改为先跑 `--printconfig` 生成再断言。
10. **用错仪器查 CLI 开关**：我用 ASCII 扫描 exe 找 `--verify` 判为"未实现"，实际 .NET 程序集里字符串字面量是 **UTF-16** 存储（只有元数据字段名如 ImeSimulation 是 UTF-8）→ 误判。改用源码核对。
11. **B 的 CRLF 残留观察不可复现**：我独立穷举 900 个计划（6 种含 CRLF/CR/LF 的文本 × 150 种子、错字上限拉到 5）→ 含 `Char(CR)` 的计划数 = 0，判定为对修复前产物的过时观察，未派单。
12. **构建与运行必须串行**：三轮出现"残留 SimIme 实例锁 dist → build.ps1 exit 2 且把 dist 清成半成品"。门禁已在预构建阶段强制终止实例并等待退出。

### 6.4 第七轮：词组级输入法工作流（用户新需求）

用户原话要点：① 拼音先出、再变文字；② **必须词组输入、长句拆分**；③ 例：tianshangyou→天上有；
tianshangyoushierge → 先确认 天上有（剩 shierge）→ 再确认 十二个；④ 可插入随机错误（xingxing 错成 猩猩 再改）；
⑤ 可加**键盘相邻键打错**；⑥ 若「字母进目标」难做好，**允许只在悬浮框内组串**。

**决策（调度者）**：
- **默认「悬浮框内组串」**（Inject.PinyinInTarget=false）：拼音只在悬浮条上显示，目标窗口只收到**词组级的汉字提交**。
  理由：用户机上曾实测「字母进目标 + 退格清理不干净」导致字母残留（当时是逐字爆发式发送），
  而落屏 === 原文是本项目的最高优先级；用户已明确允许这个折中。
- **保留字母进目标能力**（PinyinInTarget=true）供需要「目标窗口里真的先出现字母」的场景，文档标注风险。
- **词组切分必须是对原文的精确划分**（各块拼接 == 归一化后原文），这样「逐块提交」天然保证落屏 === 原文。
- 错误类型扩充：词组级同音错词（猩猩/星星）+ 键盘相邻键错拼（显示层，或进目标模式下真实敲错再退格改正）。

### 6.3 用户现场验收（本沙箱无法覆盖）
见 README.md「现场验收清单」；调度者另在 _verify\final_acceptance.ps1 提供 10 道外部验收门（G1–G10）。
