示例文件说明

demo.txt  —— 三章中文示例输入（UTF-8）
demo.ebk  —— 由 demo.txt 生成的 EBK（3 个条目，948 字节）
asd.ebk   —— 真实样本（50 章，261 KB），用于验证：用本工具重建后逐字节相同

验证命令：
  python ..\src\ebk_tool.py check asd.ebk
  python ..\src\ebk_tool.py dump  asd.ebk