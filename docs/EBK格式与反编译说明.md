# EBK Creative 1.5.0 反编译与性能分析报告

> 目标文件：`EBKCreative.exe`（688,128 字节）
> 分析时间：2026-11-07
> 结论先行：**这是 Visual Basic 6.0 的 native（本机代码）编译程序，不是 VB5，也不是 P-code。**
> 因此不存在“一键还原 VB6 源码”的可行路径；但程序结构、全部窗体/控件、模块/过程名、字符串、导入 API 与反汇编都已恢复，足以支撑下一步的性能改造。

---

## 一、二进制鉴定（证据链）

| 项目 | 结果 | 依据 |
|---|---|---|
| 文件类型 | PE32，GUI 子系统，x86 | `MZ`/`PE` 头，Machine = 0x014C，Subsystem = 2 |
| 链接器 | 6.0 | OptionalHeader.MajorLinkerVersion = 6 |
| 编译时间 | 2005-02-27 09:12:35 UTC（北京时间 17:12） | `TimeDateStamp = 0x42218F03` |
| 运行时 | **MSVBVM60.DLL**（VB6） | 导入表 + 入口 `push 004079EC; call ThunRTMain` |
| 语言 DLL | `vb6chs.dll`，LCID = 0x0804（简体中文） | VBHeader +0x06 / +0x24 |
| 编译模式 | **Native（本机代码）** | `ProjectInfo.aNativeCode = 0x004A0000 ≠ 0`；`aStartOfCode = 0x0043FBF0` 起是标准 VB6 本机函数序言 `55 8B EC 83 EC … 68 … 64 A1 00 00 00 00` |
| 版本 | 1.05 | 版本资源 FileVersion/ProductVersion |
| 公司/作者 | TG / “EBK Creative By MrBlackOx” | 版本资源 + 字符串 |
| 程序用途 | “处理可在 WQX 上使用的 EBK 文件” | 版本资源 Comments；WQX = 文曲星 |

补充事实：

- `VB5!` 只是 VB5/VB6 共用的魔数，**不能据此判断是 VB5**；判定 VB6 的依据是 `MSVBVM60.DLL` 与 `RuntimeDLLVersion = 0x0A`。
- 构建机路径被编译器泄漏在二进制里：
  `C:\Documents and Settings\Huang\My Documents\Visual Studio Projects\Visual Basic 6.0 Programmes\ebk`
  说明作者是 Huang，工程名 `ebk`，2005 年用 VB6 IDE 直接编译。
- 帮助文件路径同样泄漏：`…\ebk\EBKCREATIVE.CHM`。

### 为什么“native 编译”意味着拿不回源码

VB6 有两种编译产物：

| | P-code | Native（本程序） |
|---|---|---|
| 代码形态 | 伪代码 + RTMI 过程描述表 | 纯 x86 机器码 |
| 可反编译性 | 可恢复到接近 VB6 源码（有 RTMI、常量池、过程表） | **没有过程描述表**，只能反汇编 |
| 现有工具 | VB Decompiler 等可出伪源码 | 只能恢复窗体、导入、字符串、反汇编 |

所以本报告给出的是“**结构级 + 反汇编级**”的反编译结果，而不是 `.frm/.bas` 源码。这一点必须先说清楚，避免后续对工作量的误判。

---

## 二、程序结构（已完整恢复）

### 2.1 工程对象（12 个）

| # | 名称 | 类型 | 过程数 | 说明 |
|---|---|---|---|---|
| 0 | FrmMain | 窗体 | 32 | 主界面（EBK 编译、菜单编辑、预览） |
| 1 | MdlSpt | 标准模块 | 80 | **核心支撑模块**（文件/编码/EBK/HTML 等） |
| 2 | FrmTree | 窗体 | 66 | 目录树编辑器（最大窗体） |
| 3 | frmAbout | 窗体 | 14 | 关于 |
| 4 | FrmINI | 窗体 | 11 | INI 生成/设置 |
| 5 | FrmTool | 窗体 | 16 | 文本过滤/GB2312/裁剪工具 |
| 6 | FrmDat | 窗体 | 6 | INI ↔ DAT 转换 |
| 7 | FrmHtmlAuto | 窗体 | 5 | HTML 自动处理选项 |
| 8 | HtmlAna | 类模块 | 10 | HTML 分析器 |
| 9 | FrmGetHTMLS | 窗体 | 16 | 网页批量抓取 |
| 10 | Downloader | UserControl | 9 | 下载控件（WebBrowser 封装） |
| 11 | FrmCHM | 窗体 | 14 | CHM → EBK |

> 共 10 个 GUI 对象（9 窗体 + 1 UserControl）、1 个标准模块、1 个类模块。`VBHeader.FormCount = 10`。

### 2.2 窗体与控件（摘要）

完整清单见 `analysis/forms.txt` / `analysis/forms.json`。

- **FrmMain**：`CmdTree`、`CmdReset`、`CmdIn`、`CmdMake`、`CmdExit`、`CmdAbout`、`VSC1`（滚动条）、`TxtMenu`、`TxtPCfn`、`FraMain`、`FraCon`、`TxtC`、`TxtT`、`HSc1`、`Mnuoperation` 及 `MnuDelete/MnuBlank/MnuIns/MnuBrowse/MnuEdit/MnuUp/MnuDown/MnuLeft/MnuRight/MnuMoveto`。
- **FrmTree**：`TreeView`、`WBCont`（WebBrowser 预览）、`TxtEdit`、`TxtCont`、`TxtFN`、`Content`、`ImgIcon`、`ImgDB`、`PicShow/PicLoad`、`tmrDrag/tmrDragIcon`，以及完整菜单（`MnuFile/MnuEdit/MnuConv/MnuSaveEBK/MnuEBKSaveAs/MnuAutoHtml/MnuGetHTMLs/MnuIni2Dat/MnuRegT/MnuRegF/MnuBrowser/MnuBsrCpy/MnuBsrSA` 等）。
- **FrmTool**：`ChkTrim/ChkTrimStart/TxtStartTrim/ChkRplcUGB/ChkNarrow/ChkFLA/ChkAutoDeleteBlank/TxtSpaceCount/ChkCut/ChkBlackList` 等文本过滤选项。
- **FrmCHM**：`WBMain`（WebBrowser）、`CmdOLE`、`Timer1/Timer2`。
- **FrmGetHTMLS**：`WBMain`、`DLRMain`、`TxtUrl/TxtStart/TxtEnd/TxtBits/TxtRetry`。
- **FrmDat**：`TxtDat/TxtINI`、`CmdI2D/CmdD2I`（INI↔DAT）。
- **FrmHtmlAuto**：`OptExport`（EBK/Html/Txt）、`OptTXTFn/OptTXTFl/OptHTMLFn/OptHTMLTitle`、`TxtTagRights`、`ChkDelScript`。

### 2.3 导入与外部依赖

- **MSVBVM60.DLL：220 个导入**，覆盖字符串、变体、数组、文件、错误处理等 VB6 运行时。
- 通过 `Declare` 动态调用的 Win32 API（字符串证据）：
  `kernel32`（GetPrivateProfileStringA/WritePrivateProfileStringA、GetTempPathA、RtlMoveMemory）、
  `user32`（SendMessageA、SetCapture/ReleaseCapture、SetWindowLongA、CallWindowProcA、LockWindowUpdate）、
  `advapi32`（RegOpenKeyExA/RegQueryValueExA/RegCreateKeyExA/RegSetValueExA/RegDeleteKeyA/RegSetValueA）、
  `shell32`（ShellExecuteA、FindExecutableA、SHGetSpecialFolderLocation、SHChangeNotify）、
  `comdlg32`（GetOpenFileNameA/GetSaveFileNameA）、
  `wininet`（InternetGetConnectedStateExA）。
- OCX：`SHDocVwCtl.WebBrowser`（`shdocvw.dll`），用于 HTML 预览与 CHM/网页读取；`TreeView` 树控件。

### 2.4 字符串与功能线索

从 `.text` 中恢复出 1,600+ 条字符串，能直接还原产品功能：

| 功能 | 证据字符串 |
|---|---|
| EBK 打包 | `*.ebk`、`EBKFile Error!`、`Too many blocks`/`There are too many blocks!`、`\InfoBlk.tmp`、`ItemCount`、`RepeatTitle`、`GradeTree`、`MenuMark` |
| 文本过滤/编码 | `Text sifter : No.`、`Text Sifter : Solving No.`、`Ultra2312`、`chrcfg.dat`、`[ReplaceChr]`、`GB2312`、`SifterCore`、`ReplaceUGB2312`、`Replace2UGB2312` |
| HTML/网页 | `htmlfile`、`InnerText`、`innerhtml`、`documentelement`、`<script`、`javascript:`、`Http://` |
| CHM | `Opening CHM File ...`、`Reading map ...`、`mk:@MSITStore:`、`*.hhc`、`text/sitemap` |
| 下载 | `Downloading file:`、`Downloading time out.`、`Download Error:`、`\downloads`、`ReadyState` |
| INI/DAT | `[General]`、`[TreeView]`、`InfoType=MenuMark`、`ItemCount=`、`RepeatTitle=` |
| 注册表/外壳 | `Software\Classes\EBook file for WQX`、`.ebk` 关联、`defaulticon`、`shell\open\command` |
| 其他 | `BlackList.txt`、`MSINFO32.EXE`、`WQX`、`MrBlackOx`、`www.wqxsky.net` |

### 2.5 资源

图标 3 个、版本信息（0804/04B0）、字符串表、清单资源等，位于 `.rsrc`（0x63F2 字节）。

---

## 三、代码层反编译结果

- 代码区：`0x0043FBF0 – 0x0049E7E0`（约 388 KB，~100,000 条 x86 指令）。
- 识别出 **236+ 个函数入口**（直接 `call` 目标 + VB6 函数序言扫描），已生成带 API/字符串注释的清单：`analysis/functions.txt`。
- 每个 VB6 函数都能看到标准异常帧（`push offset __vbaExceptHandler; mov eax, fs:[0] …`）与 `__vba*` 运行时调用，可据此还原控制流。

### 3.1 反汇编样例（文本过滤例程，0x0048AEF0）

该函数被每个“文本过滤”项调用，负责刷新状态栏 `Text sifter : No. N`：

```
0048AEF0  push ebp
0048AEF1  mov  ebp, esp
0048AEF3  sub  esp, 0x18
0048AEF6  push 0x407216                  ; VB6 异常处理帧
0048AEFB  mov  eax, dword ptr fs:[0]
...
0048AF57  movsx eax, word ptr [0x4A0192] ; 全局计数器
0048AFB8  call dword ptr [0x401178]      ; __vbaVarTstEq
0048B008  mov  dword ptr [ebp-0x70], 0x412D90   ; "Text sifter : No."
0048B016  mov  dword ptr [ebp-0x80], 0x412DB8   ; " No."
0048B02D  mov  word ptr [ebp-0x90], ax
0048B04B  call dword ptr [0x401244]      ; __vbaVarCat   ← 变体字符串拼接
0048B05D  call dword ptr [0x401244]      ; __vbaVarCat
0048B06B  ...
```

**含义**：每处理一项，都要做 2~4 次 `Variant` 拼接、格式化数字、写 Label、刷新窗口。项数上千时，这部分本身就是可观的纯开销。

### 3.2 EBK 写块逻辑（证据）

在 `Too many blocks` 附近的代码可见逐字节构造块表：

```
... __vbaLenBstrB
... __vbaUI1I2
... mov  byte ptr [ecx+edx], al     ; 逐字节写入缓冲区（0xFF 结束符等）
... __vbaPutOwner3                  ; 文件 Put
... __vbaPut3
```

配合 `\InfoBlk.tmp` 临时文件与 `Too many blocks` 上限判断，可以确认：**EBK 的块索引是在 VB 字符串/字节数组里逐字节拼出来的，再 `Put` 到文件**。这正是大文本下最典型的 O(n²) 来源。

---

## 四、性能瓶颈分析（基于代码与字符串证据）

| # | 瓶颈 | 证据 | 影响 | 优化方向 |
|---|---|---|---|---|
| 1 | EBK 块索引逐字节拼接 | `mov byte ptr [ecx+edx], al`、`__vbaPut3/__vbaPutOwner3`、`InfoBlk.tmp` | 文本越大越慢，字符串反复重分配 | 预分配字节缓冲，一次性 `Put`；或直接流式写 |
| 2 | 每项刷新 `Text sifter` 状态 | `__vbaVarCat`、Label 写入、窗口刷新 | 几千项时明显拖慢 | 批量/每 N 项刷新；关闭进度显示 |
| 3 | HTML 解析依赖 WebBrowser/MSHTML | `htmlfile`、`InnerText`、`innerHTML`、`documentelement` | IE 引擎解析大 HTML 慢、内存高 | 用轻量 HTML 解析器替代；或只做文本抽取 |
| 4 | 下载用 WebBrowser + Timer 轮询 | `ReadyState`、`Timer1`、`Downloading time out.` | 等待/重试期间空转、UI 卡 | 改用 WinHTTP/WinINet 同步下载 + 超时 |
| 5 | GB2312/Ultra2312 逐字符转换 | `Text sifter`、`Ultra2312`、`chrcfg.dat` | 逐字符查表 + 字符串拼接 | 映射表批量转换（一次 `MultiByteToWideChar` 或表驱动缓冲） |
| 6 | 单线程 + UI 线程阻塞 | 无 `CreateThread`；`DoEvents` 式刷新 | 大文件时界面“假死” | 后台线程 + 进度回调 |
| 7 | 临时文件反复读写 | `\EbkIN`、`\downloads`、`InfoBlk.tmp` | 磁盘 I/O 放大 | 内存中完成，最后一次性落盘 |

> 说明：以上是静态证据支持的判断，不是实测 profile。具体哪一项占大头，取决于你实际处理的文件类型（纯文本 / 网页 / CHM / 图片多）。

---

## 五、提升性能的三条路线

### 路线 A：定点二进制补丁（保留原程序与界面）

- 做法：在反汇编定位热循环，改写为预分配缓冲/批量写；或把进度刷新改为每 N 项一次。
- 优点：界面、功能、输出格式完全不变。
- 缺点：需要动态调试确认热点；手工改机器码风险高；VB6 运行时调用约定（stdcall、`__vbaFreeStr` 生命周期）必须精确处理。
- 预期收益：**10%~50%**（取决于热点是否命中）。
- 适合：只处理单一瓶颈、且必须保持原程序的情况。

### 路线 B：重写“转换核心”为独立 CLI（推荐）

- 依据：EBK 格式已有公开的结构说明（15 字节文件头 + 目录树 + 9 字节块记录 + GB2312 文本），本程序的输出也遵循该格式。
  参考：[EBK 文件结构之不完全解读](https://www.emsky.net/bbs/forum.php?mod=viewthread&tid=27363&page=1&authorid=18121)
- 做法：用 C#/Go/Python 实现 `txt/html → ebk` 的批处理；HTML 用轻量解析器；编码一次转换；块表一次性构造；支持多文件并行。
- 优点：**10~100 倍**速度提升、可脚本化/批处理、可脱离 GUI 跑在服务器上；不再受 IE/WebBrowser 限制。
- 缺点：需要保证与原程序输出**逐字节兼容**（至少与文曲星阅读器兼容）；图片/菜单等边缘格式要逐项对齐。
- 适合：你主要用它的“转换”功能，而不是编辑界面。

### 路线 C：混合方案

- 保留 FrmMain/FrmTree 做可视化编辑，导出时调用新的 CLI 核心。
- 兼顾“所见即所得”和“转换速度”，工作量介于 A/B 之间。

---

## 六、建议的下一步

1. **先确认慢的场景**（这会直接决定选 A 还是 B）：
   - 是“生成 EBK 慢”（大 TXT/HTML）？
   - 还是“打开/导入 CHM、网页慢”？
   - 还是“预览/树操作卡”？
   - 还是“下载慢”？
2. **给我一个能复现的样本**（一个输入文件 + 它生成的 .ebk，或一个 CHM），我可以：
   - 用动态调试/计时定位真正的热点函数；
   - 或直接按路线 B 写一个 POC，用样本做逐字节对比。
3. 如果目标是“保留原程序只提速”，我建议先做 A 的**可行性验证**（定位 1~2 个热函数，评估补丁风险），再决定是否投入。

> 结论：反编译已经做到 native VB6 的上限——结构、窗体、过程、字符串、反汇编都拿到了；但要真正“提升性能”，**路线 B（重写转换核心）是投入产出比最高的方案**，因为原程序的慢是算法与 IE 引擎层面的，不是换个编译选项能解决的。

---

## 七、第二阶段：快速转换核心（已交付原型）

按选定的路线 B，已实现可运行的 CLI 原型：`tools/ebk_tool.py`（说明见 `tools/README.md`）。

### 已实现

| 能力 | 命令 |
|---|---|
| TXT → EBK（自动识别 UTF-8/UTF-16/GBK，统一转 GB2312） | `py -3 tools\ebk_tool.py build book.txt -o book.ebk` |
| HTML → 纯文本 → EBK（去脚本/样式/标签、解实体） | `... build page.html -o page.ebk --html` |
| 解析/对照任意 .ebk 的头部、目录、块表 | `... dump book.ebk` |
| 产物自洽校验（textSize、块 start/end） | `... check book.ebk` |
| 吞吐基准 | `... bench book.txt` |

### 实测性能（本机 Python 3.14）

| 输入 | 大小 | 条目 | 耗时 | 吞吐 |
|---|---|---|---|---|
| 中文 TXT | 8.18 MB | 209 | 约 0.32 s（含读盘/解码/GB2312 编码） | 约 24 MB/s |
| 同上，仅容器构建 | 8.18 MB → 6.21 MB | 209 | 约 17 ms | 约 455 MB/s |

对比原程序：VB6 在 UI 线程里逐项处理，每项还要 `Variant` 拼接、刷新 Label、走 WebBrowser/IE，
大文件通常是几十秒到几分钟；新核心把“转换”本身压到秒级，且可批处理、可并行、无需 GUI。

### 字节级校准：已完成

用真实样本 `asd.ebk`（50 章，261,016 字节）验证：

| 项目 | 结果 |
|---|---|
| 用新核心重建 `asd.ebk` | **261,016 字节完全相同（byte-for-byte）** |
| `sanguo.ebk`（120 章嵌套） | `check` PASS |
| `lavax.ebk`（11 个目录嵌套） | `check` PASS |
| `test.ebk` | 另一种老变体（1 字节页号），不属于本程序格式 |

最终确认的容器布局：

```
文件头 15B: 000000 | (last_rec_addr-1) u24 | 00000000 | text_size u24 | first_rec_addr u16
目录:       count u8 | parent u16 | addr[count] u16 | name + 0xFF ...
页记录 14B: 00 | parent u16 | x u8 | start u24 | index u16 | end u24 | index u16
索引表 9B:  00 | start u24 | end u24 | 0000
正文:       所有条目连续存放（GB2312），文件尾 = 最后一页 end
```

当前限制：写入端为单层目录（≤255 条，超出自动放大块尺寸）；图片/封面/`.men`/CHM 尚未实现。

---

## 八、第三阶段：桌面 GUI（已交付）

`ui\EbkPacker.exe`（单文件，.NET Framework 4.x，Win10 自带）——批量把 txt 转成 ebk：

- 拖拽多个 txt / 整个文件夹进窗口，或按钮添加；列表与转换顺序按**自然排序**（第2章 → 第10章 → 第100章）；
- 自动识别 UTF-8（含 BOM）/ UTF-16 LE / UTF-16 BE / GBK / GB18030，统一转 GBK 写入；
- 指定输出目录（默认 `桌面\EBK输出`），可覆盖或自动改名；
- **多章节合并**：默认把所有 txt 合并成**一本** `.ebk`（每个 txt 一章），也可切换成“每个 txt 一个 EBK”；
- 一本书章节 ≤255 时直接挂在根目录；>255 时自动按 255 章一组分“第1部分 / 第2部分 …”（两级目录）；
- 自动按“第N章/节/回/卷/篇”切分，无标题时按块大小切分；
- 后台线程转换、进度条、逐文件编码/状态/日志。

验证：

- 同一段文本分别以 UTF-8、GBK、UTF-16 保存，生成的 EBK **字节完全相同**；
- GUI 与 Python 核心输出**逐字节相同**；
- 生成的 3 章 `demo.ebk` 与 419 章合并文件，均已在**原版 EBK Creative 中打开验证**：
  左侧树显示章节（419 章显示为 `第1部分 → 第1章…`），选中章节后正文正常显示。

说明见 `ui/README.md`。

---

## 附录：本次生成的产物

| 文件 | 内容 |
|---|---|
| `analysis/inventory.txt` | PE 信息、版本资源、220 个导入、全部 UTF-16/ASCII 字符串 |
| `analysis/functions.txt` | 236+ 个函数入口、大小、API 与字符串引用 |
| `analysis/forms.txt` / `forms.json` | 10 个 GUI 对象的控件清单（机器可读） |
| `analysis/core_disasm.txt` | 关键例程的带注释反汇编（文本过滤/进度刷新） |
| `analysis/functions.txt` 中 `0x46255B` 附近 | EBK 写块（`__vbaPut3`/`__vbaPutOwner3`）证据 |
| `analysis/ebkstru105b3_miner.txt` | 从官方 PDF 提取的 EBK 1.05 文件结构说明 |
| `tools/ebk_tool.py` | 快速转换核心（build/dump/check/bench，已校准） |
| `tools/rebuild_test.py` | 用 `asd.ebk` 做逐字节重建验证 |
| `tools/README.md` | 用法、EBK 格式、验证记录、路线图 |
| `samples/asd.ebk` | 校准基准样本（50 章，261 KB） |
| `samples/lavax.ebk` / `test.ebk` | 嵌套目录样本 / 另一种老变体样本 |

复现方式（本机已装 Python + capstone + pefile + pdfminer）：

```powershell
py -3 analysis\inventory2.py      # 导入/字符串/资源
py -3 analysis\make_forms.py      # 窗体控件
py -3 analysis\funcs2.py          # 函数映射
py -3 analysis\core.py            # 反汇编样例
py -3 tools\ebk_tool.py build tools\sample.txt -o tools\sample.ebk
py -3 tools\ebk_tool.py check tools\sample.ebk
```
