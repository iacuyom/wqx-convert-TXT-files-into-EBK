using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace EbkPacker
{
    class Entry
    {
        public byte[] Name;
        public byte[] Data;
        public Entry(byte[] name, byte[] data) { Name = name; Data = data; }
    }

    static class Ebk
    {
        const int HEADER = 15, REC = 14, TAB = 9;
        const int MAX16 = 0xFFFF;
        const int MAX24 = 0xFFFFFF;

        static void P16(byte[] b, int o, int v)
        {
            if (o < 0 || o + 1 >= b.Length) throw new ArgumentOutOfRangeException("o");
            if (v < 0 || v > MAX16)
                throw new Exception(string.Format(
                    "地址/大小 {0} 超出 16 位范围（{1}），EBK 目录区过大，请减小分本粒度（每本条目数）",
                    v, MAX16));
            b[o] = (byte)(v & 0xFF);
            b[o + 1] = (byte)((v >> 8) & 0xFF);
        }

        static void P24(byte[] b, int o, int v)
        {
            if (o < 0 || o + 2 >= b.Length) throw new ArgumentOutOfRangeException("o");
            if (v < 0 || v > MAX24)
                throw new Exception(string.Format(
                    "偏移/大小 {0} 超出 24 位范围（{1}），单个 EBK 超过约 16MB，请减小分本粒度或块大小",
                    v, MAX24));
            b[o] = (byte)(v & 0xFF);
            b[o + 1] = (byte)((v >> 8) & 0xFF);
            b[o + 2] = (byte)((v >> 16) & 0xFF);
        }

        public class Node
        {
            public string Name = "";
            public Node Parent;
            public List<Node> Dirs = new List<Node>();
            public List<Entry> Recs = new List<Entry>();
            public int Addr;
            public int Size;
        }

        public static byte[] Build(List<Entry> entries)
        {
            return BuildTree(entries, 255);
        }

        /// <summary>
        /// 一本书多章节：条目 ≤255 时全部挂在根目录；超过则按 255 一组分卷成"第N部分"。
        /// 单本条目数上限 255×255（根 + 子目录各 255），超出请减小分本粒度。
        /// </summary>
        public static byte[] BuildTree(List<Entry> entries, int maxPerDir)
        {
            int n = entries.Count;
            if (n == 0) throw new Exception("没有可写入的条目");
            var root = new Node();
            if (n <= maxPerDir)
            {
                root.Recs.AddRange(entries);
            }
            else
            {
                int chunks = (n + maxPerDir - 1) / maxPerDir;
                if (chunks > maxPerDir)
                    throw new Exception(string.Format(
                        "单本条目数 {0} 超过上限 {1}，请减小分本粒度（每本条目数）",
                        n, maxPerDir * maxPerDir));
                for (int s = 0; s < n; s += maxPerDir)
                {
                    var child = new Node { Name = "第" + ((s / maxPerDir) + 1) + "部分", Parent = root };
                    int e = Math.Min(s + maxPerDir, n);
                    for (int k = s; k < e; k++) child.Recs.Add(entries[k]);
                    root.Dirs.Add(child);
                }
            }

            var dirs = new List<Node>();
            Action<Node> walk = null;
            walk = d => { dirs.Add(d); foreach (var c in d.Dirs) walk(c); };
            walk(root);

            Encoding enc = Encoding.GetEncoding(936);
            int off = HEADER;
            foreach (var d in dirs)
            {
                int size = 1 + 2 + 2 * (d.Dirs.Count + d.Recs.Count);
                foreach (var c in d.Dirs) size += enc.GetByteCount(c.Name) + 1;
                foreach (var r in d.Recs) size += r.Name.Length + 1;
                d.Size = size;
                d.Addr = off;
                off += size;
            }
            int recStart = off;
            int tabStart = recStart + REC * n;
            int textStart = tabStart + TAB * n;

            int textSize = 0;
            foreach (var r in entries) textSize += r.Data.Length;

            long total = (long)textStart + textSize;
            if (total > MAX24)
                throw new Exception(string.Format(
                    "输出大小 {0} 字节超过 EBK 24 位偏移上限（{1} 字节 ≈ 16MB），请减小分本粒度或块大小",
                    total, MAX24));

            byte[] buf = new byte[total];

            // 头部布局：
            //   [0..2]   未知/保留
            //   [3..5]   24 位——原实现写的是 recStart + REC*(n-1) - 1；
            //            疑似"记录区结束地址"或"最后一条记录起始-1"，保留原值，
            //            建议对照实际读取器复核
            //   [10..12] 24 位——正文总大小
            //   [13..14] 16 位——记录区起始地址
            P24(buf, 3, recStart + REC * (n - 1) - 1);
            P24(buf, 10, textSize);
            P16(buf, 13, recStart);

            var addr = new Dictionary<Entry, int>();
            int ri = 0;
            foreach (var d in dirs)
                foreach (var r in d.Recs) { addr[r] = recStart + REC * ri; ri++; }

            foreach (var d in dirs)
            {
                int q = d.Addr;
                int cnt = d.Dirs.Count + d.Recs.Count;
                if (cnt > 255)
                    throw new Exception(string.Format("目录 '{0}' 条目数 {1} 超过 255",
                        string.IsNullOrEmpty(d.Name) ? "(根)" : d.Name, cnt));
                buf[q] = (byte)cnt;
                P16(buf, q + 1, d.Parent == null ? 0 : d.Parent.Addr);
                q += 3;
                foreach (var c in d.Dirs) { P16(buf, q, c.Addr); q += 2; }
                foreach (var r in d.Recs) { P16(buf, q, addr[r]); q += 2; }
                foreach (var c in d.Dirs) { byte[] nb = enc.GetBytes(c.Name); Buffer.BlockCopy(nb, 0, buf, q, nb.Length); q += nb.Length; buf[q] = 0xFF; q++; }
                foreach (var r in d.Recs) { Buffer.BlockCopy(r.Name, 0, buf, q, r.Name.Length); q += r.Name.Length; buf[q] = 0xFF; q++; }
            }

            int pos = textStart;
            int ti = 0;
            foreach (var d in dirs)
            {
                for (int i = 0; i < d.Recs.Count; i++)
                {
                    var r = d.Recs[i];
                    int a = addr[r];
                    buf[a] = 0;
                    P16(buf, a + 1, d.Addr);
                    buf[a + 3] = 1;
                    P24(buf, a + 4, pos);
                    buf[a + 7] = (byte)(i & 0xFF);
                    buf[a + 8] = (byte)((i >> 8) & 0xFF);
                    P24(buf, a + 9, pos + r.Data.Length);
                    buf[a + 12] = (byte)(i & 0xFF);
                    buf[a + 13] = (byte)((i >> 8) & 0xFF);

                    int t = tabStart + TAB * ti;
                    P24(buf, t + 1, pos);
                    P24(buf, t + 4, pos + r.Data.Length);
                    Buffer.BlockCopy(r.Data, 0, buf, pos, r.Data.Length);
                    pos += r.Data.Length;
                    ti++;
                }
            }
            return buf;
        }
    }

    static class Conv
    {
        public static readonly Regex ChapterRe = new Regex(
            @"^第[0-9一二三四五六七八九十百千零〇两]+[章节回卷篇][^\r\n]{0,40}",
            RegexOptions.Multiline | RegexOptions.Compiled);

        public static string DetectAndDecode(byte[] raw, out string enc)
        {
            if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
            { enc = "UTF-8 BOM"; return new UTF8Encoding(false).GetString(raw, 3, raw.Length - 3); }
            if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE)
            { enc = "UTF-16 LE"; return Encoding.Unicode.GetString(raw, 2, raw.Length - 2); }
            if (raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF)
            { enc = "UTF-16 BE"; return Encoding.BigEndianUnicode.GetString(raw, 2, raw.Length - 2); }
            try { enc = "UTF-8"; return new UTF8Encoding(false, true).GetString(raw); }
            catch { }
            try
            {
                Encoding g = Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                enc = "GBK"; return g.GetString(raw);
            }
            catch { }
            try
            {
                Encoding g = Encoding.GetEncoding(54936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                enc = "GB18030"; return g.GetString(raw);
            }
            catch { }
            enc = "GBK(替换)";
            return Encoding.GetEncoding(936).GetString(raw);
        }

        public static string Normalize(string text)
        {
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");
            text = text.Replace("\n", "\r\n");
            if (!text.EndsWith("\r\n")) text += "\r\n";
            return text;
        }

        /// <summary>
        /// 单 txt 转条目。按章节切分时，章节数不再硬性限 255，
        /// 交由 Ebk.BuildTree 自动分卷（每 255 一条子目录）；
        /// 仅当总大小超 16MB 时由 P24 抛错。
        /// </summary>
        public static List<Entry> ToEntries(string text, int blockBytes, bool byChapter, Encoding gbk)
        {
            var list = new List<Entry>();
            List<KeyValuePair<string, string>> chapters = null;
            if (byChapter) chapters = SplitChapters(text);
            if (chapters != null && chapters.Count > 0)
            {
                foreach (var kv in chapters)
                    list.Add(new Entry(EncName(kv.Key, gbk), gbk.GetBytes(kv.Value)));
                return list;
            }
            byte[] gb = gbk.GetBytes(text);
            foreach (var kv in SplitBlocks(gb, blockBytes, gbk))
                list.Add(new Entry(EncName(kv.Key, gbk), kv.Value));
            return list;
        }

        public static byte[] EncName(string s, Encoding gbk)
        {
            if (s.Length > 40) s = s.Substring(0, 40);
            byte[] b = gbk.GetBytes(s);
            if (b.Length <= 120) return b;
            // 按 GBK 双字节字符边界回退，避免砍断半个汉字
            int n = 120;
            int i = 0;
            while (i < n)
            {
                byte c = b[i];
                int step = (c >= 0x81 && c <= 0xFE) ? 2 : 1;
                if (i + step > n) { n = i; break; }
                i += step;
            }
            byte[] r = new byte[n];
            Array.Copy(b, r, n);
            return r;
        }

        public static List<KeyValuePair<string, string>> SplitChapters(string text)
        {
            MatchCollection ms = ChapterRe.Matches(text);
            if (ms.Count < 2) return null;
            var list = new List<KeyValuePair<string, string>>();
            if (ms[0].Index > 0)
            {
                string pre = text.Substring(0, ms[0].Index).Trim();
                if (pre.Length > 0) list.Add(new KeyValuePair<string, string>("前言", pre + "\r\n"));
            }
            for (int i = 0; i < ms.Count; i++)
            {
                int s = ms[i].Index;
                int e = (i + 1 < ms.Count) ? ms[i + 1].Index : text.Length;
                list.Add(new KeyValuePair<string, string>(ms[i].Value.Trim(), text.Substring(s, e - s)));
            }
            return list;
        }

        public static List<KeyValuePair<string, byte[]>> SplitBlocks(byte[] gb, int block, Encoding gbk)
        {
            int n = gb.Length;
            int step = Math.Max(1, block);
            List<int[]> ranges = null;
            for (int attempt = 0; attempt < 40; attempt++)
            {
                ranges = new List<int[]>();
                int start = 0;
                while (start < n)
                {
                    int end = Math.Min(start + step, n);
                    if (end < n)
                    {
                        int cut = LastIndex(gb, 0x0D, 0x0A, start + 1, end);
                        if (cut > start + step / 2) end = cut + 2;
                    }
                    ranges.Add(new int[] { start, end });
                    start = end;
                }
                if (ranges.Count <= 255) break;
                step = (int)(step * 1.3) + 1;
            }
            var list = new List<KeyValuePair<string, byte[]>>();
            for (int i = 0; i < ranges.Count; i++)
            {
                int s = ranges[i][0], e = ranges[i][1];
                byte[] d = new byte[e - s];
                Array.Copy(gb, s, d, 0, e - s);
                int le = Array.IndexOf(d, (byte)0x0D);
                if (le < 0) le = d.Length;
                string line = gbk.GetString(d, 0, le).Trim();
                if (line.Length == 0) line = "Page" + (i + 1);
                list.Add(new KeyValuePair<string, byte[]>(line, d));
            }
            return list;
        }

        static int LastIndex(byte[] b, byte a, byte c, int from, int to)
        {
            for (int i = to - 2; i >= from; i--)
                if (b[i] == a && b[i + 1] == c) return i;
            return -1;
        }
    }

    class NaturalComparer : IComparer<string>
    {
        public int Compare(string a, string b)
        {
            if (a == null && b == null) return 0;
            if (a == null) return -1;
            if (b == null) return 1;
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                bool da = char.IsDigit(a[i]);
                bool db = char.IsDigit(b[j]);
                if (da && db)
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    string na = a.Substring(si, i - si).TrimStart('0');
                    string nb = b.Substring(sj, j - sj).TrimStart('0');
                    if (na.Length != nb.Length) return na.Length - nb.Length;
                    int c = string.CompareOrdinal(na, nb);
                    if (c != 0) return c;
                }
                else
                {
                    int c = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                    if (c != 0) return c;
                    i++; j++;
                }
            }
            return (a.Length - i) - (b.Length - j);
        }
    }

    class ItemComparer : System.Collections.IComparer
    {
        readonly NaturalComparer nc = new NaturalComparer();
        public int Compare(object x, object y)
        {
            var a = x as ListViewItem;
            var b = y as ListViewItem;
            if (a == null || b == null) return 0;
            string pa = a.Tag as string ?? a.Text;
            string pb = b.Tag as string ?? b.Text;
            int c = nc.Compare(Path.GetFileName(pa), Path.GetFileName(pb));
            if (c != 0) return c;
            return nc.Compare(pa, pb);
        }
    }

    class MainForm : Form
    {
        TextBox txtOut;
        ListView list;
        Button btnStart, btnAdd, btnAddDir, btnRemove, btnClear;
        ProgressBar bar;
        Label lblStatus;
        TextBox log;
        CheckBox chkChapter, chkOverwrite, chkMerge, chkSplit;
        TextBox txtBook;
        NumericUpDown numPerBook;
        NumericUpDown numBlock;
        int done, failed;

        public MainForm()
        {
            ClientSize = new Size(940, 620);
            MinimumSize = new Size(780, 560);
            Text = "EBK 批量转换器 — txt → ebk";
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);
            AllowDrop = true;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(8, 6, 8, 6)
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 116));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 108));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));

            root.Controls.Add(BuildTop(), 0, 0);
            list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                AllowDrop = true,
                HideSelection = false
            };
            list.Columns.Add("文件", 370);
            list.Columns.Add("大小", 86, HorizontalAlignment.Right);
            list.Columns.Add("编码", 104);
            list.Columns.Add("状态", 330);
            list.ListViewItemSorter = new ItemComparer();
            root.Controls.Add(list, 0, 1);

            log = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(250, 250, 250),
                Font = new Font("Consolas", 8.5f)
            };
            root.Controls.Add(log, 0, 2);

            root.Controls.Add(BuildBottom(), 0, 3);
            Controls.Add(root);

            list.DragEnter += OnDragEnter;
            list.DragDrop += OnDragDrop;
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;

            txtOut.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "EBK输出");
        }

        Control BuildTop()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 3
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

            var lblOut = new Label { Text = "输出目录：", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            txtOut = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 5, 6, 5) };
            var btnOut = new Button { Text = "浏览...", Dock = DockStyle.Fill, Margin = new Padding(0, 3, 0, 3) };
            btnOut.Click += (s, e) =>
            {
                using (var d = new FolderBrowserDialog())
                {
                    d.Description = "选择 EBK 输出目录";
                    if (d.ShowDialog(this) == DialogResult.OK) txtOut.Text = d.SelectedPath;
                }
            };
            grid.Controls.Add(lblOut, 0, 0);
            grid.Controls.Add(txtOut, 1, 0);
            grid.Controls.Add(btnOut, 2, 0);

            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0) };
            chkChapter = new CheckBox { Text = "按章节标题切分（单 txt 多章也拆）", Checked = true, AutoSize = true, Margin = new Padding(0, 7, 14, 0) };
            var lblBlock = new Label { Text = "块大小(字节)：", AutoSize = true, Margin = new Padding(0, 9, 4, 0) };
            numBlock = new NumericUpDown { Width = 78, Minimum = 1000, Maximum = 60000, Value = 8000, Increment = 1000, Margin = new Padding(0, 5, 18, 0) };
            chkOverwrite = new CheckBox { Text = "覆盖同名文件", Checked = true, AutoSize = true, Margin = new Padding(0, 7, 0, 0) };
            flow.Controls.AddRange(new Control[] { chkChapter, lblBlock, numBlock, chkOverwrite });
            grid.Controls.Add(flow, 1, 1);
            grid.SetColumnSpan(flow, 2);

            var flow2 = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0) };
            chkMerge = new CheckBox { Text = "合并成 EBK", Checked = true, AutoSize = true, Margin = new Padding(0, 8, 12, 0) };
            var lblBook = new Label { Text = "书名：", AutoSize = true, Margin = new Padding(0, 10, 4, 0) };
            txtBook = new TextBox { Width = 150, Margin = new Padding(0, 6, 14, 0) };
            chkSplit = new CheckBox { Text = "每", AutoSize = true, Margin = new Padding(0, 8, 4, 0) };
            numPerBook = new NumericUpDown { Width = 72, Minimum = 1, Maximum = 65535, Value = 100, Increment = 10, Margin = new Padding(0, 5, 4, 0) };
            var lblPer = new Label { Text = "个章节条目一本", AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
            chkMerge.CheckedChanged += (s, e) =>
            {
                txtBook.Enabled = chkMerge.Checked;
                lblBook.Enabled = chkMerge.Checked;
                chkSplit.Enabled = chkMerge.Checked;
                numPerBook.Enabled = chkMerge.Checked && chkSplit.Checked;
                lblPer.Enabled = chkMerge.Checked;
            };
            chkSplit.CheckedChanged += (s, e) => { numPerBook.Enabled = chkMerge.Checked && chkSplit.Checked; };
            flow2.Controls.AddRange(new Control[] { chkMerge, lblBook, txtBook, chkSplit, numPerBook, lblPer });
            grid.Controls.Add(flow2, 1, 2);
            grid.SetColumnSpan(flow2, 2);

            panel.Controls.Add(grid);
            return panel;
        }

        Control BuildBottom()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));

            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0) };
            btnAdd = new Button { Text = "添加文件", Width = 96, Height = 30, Margin = new Padding(0, 4, 8, 0) };
            btnAddDir = new Button { Text = "添加文件夹", Width = 104, Height = 30, Margin = new Padding(0, 4, 8, 0) };
            btnRemove = new Button { Text = "移除选中", Width = 90, Height = 30, Margin = new Padding(0, 4, 8, 0) };
            btnClear = new Button { Text = "清空", Width = 70, Height = 30, Margin = new Padding(0, 4, 20, 0) };
            btnStart = new Button
            {
                Text = "开始转换",
                Width = 130,
                Height = 30,
                Margin = new Padding(0, 4, 20, 0),
                BackColor = Color.FromArgb(0, 120, 215),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnStart.FlatAppearance.BorderSize = 0;
            lblStatus = new Label { Text = "就绪。可把 txt 或文件夹直接拖进窗口。", AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
            flow.Controls.AddRange(new Control[] { btnAdd, btnAddDir, btnRemove, btnClear, btnStart, lblStatus });
            grid.Controls.Add(flow, 0, 0);

            bar = new ProgressBar { Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 2) };
            grid.Controls.Add(bar, 0, 1);

            panel.Controls.Add(grid);

            btnAdd.Click += (s, e) => AddFilesDialog();
            btnAddDir.Click += (s, e) => AddDirDialog();
            btnRemove.Click += (s, e) => RemoveSelected();
            btnClear.Click += (s, e) => { list.Items.Clear(); UpdateStatus(); };
            btnStart.Click += (s, e) => StartConvert();
            return panel;
        }

        void OnDragEnter(object s, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
        }

        void OnDragDrop(object s, DragEventArgs e)
        {
            string[] paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (paths == null) return;
            foreach (string p in paths) AddPath(p);
            SortList();
            UpdateStatus();
        }

        void AddFilesDialog()
        {
            using (var d = new OpenFileDialog())
            {
                d.Multiselect = true;
                d.Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*";
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    foreach (string f in d.FileNames) AddPath(f);
                    SortList();
                    UpdateStatus();
                }
            }
        }

        void AddDirDialog()
        {
            using (var d = new FolderBrowserDialog())
            {
                d.Description = "选择包含 txt 的文件夹";
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    AddPath(d.SelectedPath);
                    SortList();
                    UpdateStatus();
                }
            }
        }

        void AddPath(string p)
        {
            if (Directory.Exists(p))
            {
                string[] files;
                try { files = Directory.GetFiles(p, "*.txt", SearchOption.AllDirectories); }
                catch { files = new string[0]; }
                Array.Sort(files, new NaturalComparer());
                foreach (string f in files) AddFile(f);
            }
            else if (File.Exists(p) && p.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            {
                AddFile(p);
            }
        }

        void AddFile(string f)
        {
            string full = Path.GetFullPath(f);
            foreach (ListViewItem it in list.Items)
                if (string.Equals(it.Tag as string, full, StringComparison.OrdinalIgnoreCase)) return;
            var fi = new FileInfo(full);
            var item = new ListViewItem(fi.Name);
            item.SubItems.Add(FormatSize(fi.Length));
            item.SubItems.Add("待检测");
            item.SubItems.Add("等待");
            item.Tag = full;
            list.Items.Add(item);
        }

        void RemoveSelected()
        {
            var sel = new List<ListViewItem>();
            foreach (ListViewItem it in list.SelectedItems) sel.Add(it);
            foreach (var it in sel) list.Items.Remove(it);
            UpdateStatus();
        }

        void UpdateStatus()
        {
            lblStatus.Text = string.Format("共 {0} 个文件。拖拽 txt 或文件夹到窗口即可添加。", list.Items.Count);
        }

        void SortList()
        {
            if (list.Items.Count > 1) list.Sort();
        }

        public void Preload(string dir)
        {
            AddPath(dir);
            SortList();
            UpdateStatus();
        }

        static string FormatSize(long n)
        {
            if (n < 1024) return n + " B";
            if (n < 1024 * 1024) return (n / 1024.0).ToString("0.0") + " KB";
            return (n / 1024.0 / 1024.0).ToString("0.00") + " MB";
        }

        void Log(string s)
        {
            if (log.Lines.Length > 400)
            {
                string[] keep = new string[200];
                Array.Copy(log.Lines, log.Lines.Length - 200, keep, 0, 200);
                log.Lines = keep;
            }
            log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + s + "\r\n");
        }

        // 统一的跨线程回调：窗体已释放时静默忽略，避免 ObjectDisposedException
        void OnUI(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(a); } catch (ObjectDisposedException) { }
        }

        void StartConvert()
        {
            if (list.Items.Count == 0) { MessageBox.Show(this, "请先添加 txt 文件。", "提示"); return; }
            string outDir = txtOut.Text.Trim();
            if (outDir.Length == 0) { MessageBox.Show(this, "请先选择输出目录。", "提示"); return; }
            try { Directory.CreateDirectory(outDir); }
            catch (Exception ex) { MessageBox.Show(this, "无法创建输出目录：\r\n" + ex.Message, "错误"); return; }

            var files = new List<string>();
            foreach (ListViewItem it in list.Items) files.Add((string)it.Tag);

            // —— 关键：所有 UI 控件值都在主线程读取，后台线程不再触碰 ——
            int block = (int)numBlock.Value;
            bool byChapter = chkChapter.Checked;
            bool overwrite = chkOverwrite.Checked;
            bool merge = chkMerge.Checked;
            bool split = chkSplit.Checked;
            int perBook = (int)numPerBook.Value;
            string bookName = txtBook.Text.Trim();

            btnStart.Enabled = false;
            btnAdd.Enabled = btnAddDir.Enabled = btnRemove.Enabled = btnClear.Enabled = false;
            bar.Maximum = Math.Max(1, files.Count);
            bar.Value = 0;
            done = 0; failed = 0;
            Log((merge ? "开始合并 " : "开始转换 ") + files.Count + " 个文件 -> " + outDir);

            var t = new Thread(() =>
            {
                Encoding gbk = Encoding.GetEncoding(936);
                if (merge)
                {
                    try
                    {
                        // 1) 把所有文件的章节汇总成一个大条目列表
                        //    进度条以文件数为单位推进
                        var allEntries = Program.MergeEntries(files, byChapter, gbk,
                            (idx, enc) => OnUI(() =>
                            {
                                SetRow(idx, enc, "已读取");
                                bar.Value = Math.Min(bar.Maximum, bar.Value + 1);
                            }));

                        int total = allEntries.Count;
                        if (total == 0) throw new Exception("没有可合并的条目");

                        // 2) 按"每 N 个章节条目"分组；不勾选则整本输出
                        int groupSize = (split && perBook > 0 && perBook < total) ? perBook : total;
                        int groups = (total + groupSize - 1) / groupSize;

                        // 进度条扩到 files.Count + groups，分本阶段每组 +1
                        OnUI(() => { bar.Maximum = files.Count + groups; });

                        // 3) 书基本名
                        string baseName = bookName;
                        if (baseName.Length == 0)
                        {
                            try { baseName = new DirectoryInfo(Path.GetDirectoryName(files[0])).Name; }
                            catch { baseName = "book"; }
                        }
                        foreach (char c in Path.GetInvalidFileNameChars()) baseName = baseName.Replace(c, '_');
                        if (baseName.Trim().Length == 0) baseName = "book";

                        // 4) 逐本生成
                        var made = new List<string>();
                        int gi = 0;
                        for (int st = 0; st < total; st += groupSize)
                        {
                            gi++;
                            int cnt = Math.Min(groupSize, total - st);
                            var part = allEntries.GetRange(st, cnt);
                            byte[] ebk = Ebk.Build(part);

                            string name = groups > 1 ? (baseName + "_" + gi) : baseName;
                            string outp = Path.Combine(outDir, name + ".ebk");
                            if (!overwrite && File.Exists(outp))
                            {
                                int k = 1;
                                while (File.Exists(Path.Combine(outDir, name + "_" + k + ".ebk"))) k++;
                                outp = Path.Combine(outDir, name + "_" + k + ".ebk");
                            }
                            File.WriteAllBytes(outp, ebk);

                            string line = string.Format("{0}（{1} 条目，{2}）",
                                Path.GetFileName(outp), cnt, FormatSize(ebk.Length));
                            made.Add(line);
                            OnUI(() =>
                            {
                                bar.Value = Math.Min(bar.Maximum, bar.Value + 1);
                                Log(line);
                            });
                        }

                        done = made.Count;
                        string summary = string.Join("\r\n", made.ToArray());
                        OnUI(() =>
                        {
                            lblStatus.Text = string.Format("合并完成：{0} 本书", made.Count);
                            Log(string.Format("合并完成：{0} 本书", made.Count));
                            MessageBox.Show(this,
                                string.Format("合并完成，共 {0} 本书：\r\n{1}", made.Count, summary), "完成");
                        });
                    }
                    catch (Exception ex)
                    {
                        failed = 1;
                        string em = ex.Message;
                        OnUI(() =>
                        {
                            Log("合并失败：" + em);
                            MessageBox.Show(this, "合并失败：\r\n" + em, "错误");
                        });
                    }
                    finally
                    {
                        OnUI(() =>
                        {
                            btnStart.Enabled = true;
                            btnAdd.Enabled = btnAddDir.Enabled = btnRemove.Enabled = btnClear.Enabled = true;
                        });
                    }
                    return;
                }

                // —— 非合并模式：每个 txt 转一个 ebk，逻辑不变 ——
                for (int i = 0; i < files.Count; i++)
                {
                    string f = files[i];
                    int idx = i;
                    try
                    {
                        var r = Program.ConvertOne(f, outDir, block, byChapter, overwrite, gbk);
                        done++;
                        string msg = string.Format("{0}  [{1}] {2} 个条目 -> {3} ({4})",
                            Path.GetFileName(f), r.Enc, r.Count, Path.GetFileName(r.Output), FormatSize(r.Size));
                        string encName = r.Enc;
                        OnUI(() =>
                        {
                            SetRow(idx, encName, "完成");
                            bar.Value = Math.Min(bar.Maximum, bar.Value + 1);
                            Log(msg);
                        });
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        string em = ex.Message;
                        OnUI(() =>
                        {
                            SetRow(idx, "-", "失败: " + em);
                            bar.Value = Math.Min(bar.Maximum, bar.Value + 1);
                            Log("失败 " + Path.GetFileName(f) + " : " + em);
                        });
                    }
                }
                OnUI(() =>
                {
                    btnStart.Enabled = true;
                    btnAdd.Enabled = btnAddDir.Enabled = btnRemove.Enabled = btnClear.Enabled = true;
                    lblStatus.Text = string.Format("完成：成功 {0}，失败 {1}", done, failed);
                    Log(string.Format("结束：成功 {0}，失败 {1}", done, failed));
                    MessageBox.Show(this,
                        string.Format("转换完成。\r\n成功 {0} 个，失败 {1} 个。\r\n输出目录：{2}", done, failed, outDir), "完成");
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        void SetRow(int idx, string enc, string status)
        {
            if (idx < 0 || idx >= list.Items.Count) return;
            var it = list.Items[idx];
            if (enc != "-") it.SubItems[2].Text = enc;
            it.SubItems[3].Text = status;
            it.EnsureVisible();
        }
    }

    class ConvResult
    {
        public string Enc;
        public int Count;
        public string Output;
        public long Size;
    }

    static class Program
    {
        public static ConvResult ConvertOne(string f, string outDir, int block, bool byChapter, bool overwrite, Encoding gbk)
        {
            byte[] raw = File.ReadAllBytes(f);
            string enc;
            string text = Conv.Normalize(Conv.DetectAndDecode(raw, out enc));
            var entries = Conv.ToEntries(text, block, byChapter, gbk);
            byte[] ebk = Ebk.Build(entries);
            string baseName = Path.GetFileNameWithoutExtension(f);
            string outp = Path.Combine(outDir, baseName + ".ebk");
            if (!overwrite && File.Exists(outp))
            {
                int k = 1;
                while (File.Exists(Path.Combine(outDir, baseName + "_" + k + ".ebk"))) k++;
                outp = Path.Combine(outDir, baseName + "_" + k + ".ebk");
            }
            File.WriteAllBytes(outp, ebk);
            return new ConvResult { Enc = enc, Count = entries.Count, Output = outp, Size = ebk.Length };
        }

        /// <summary>
        /// 读取所有 txt 的章节条目（合并后由调用方再按"每 N 条目"分组）。
        /// 单章 >4MB 时自动分块，避免单个 entry 挤爆 16MB 上限。
        /// </summary>
        public static List<Entry> MergeEntries(List<string> files, bool byChapter, Encoding gbk, Action<int, string> progress)
        {
            var entries = new List<Entry>();
            const int MAX_ENTRY = 4 * 1024 * 1024;

            for (int i = 0; i < files.Count; i++)
            {
                string f = files[i];
                string enc;
                string text = Conv.Normalize(Conv.DetectAndDecode(File.ReadAllBytes(f), out enc));
                List<KeyValuePair<string, string>> chs = byChapter ? Conv.SplitChapters(text) : null;
                if (chs != null && chs.Count > 1)
                {
                    foreach (var kv in chs)
                        AddEntry(entries, kv.Key, gbk.GetBytes(kv.Value), gbk, MAX_ENTRY);
                }
                else
                {
                    string nm = Path.GetFileNameWithoutExtension(f);
                    if (string.IsNullOrEmpty(nm)) nm = "第" + (entries.Count + 1) + "章";
                    AddEntry(entries, nm, gbk.GetBytes(text), gbk, MAX_ENTRY);
                }
                if (progress != null) { try { progress(i, enc); } catch { } }
            }
            if (entries.Count == 0) throw new Exception("没有可合并的章节");
            return entries;
        }

        static void AddEntry(List<Entry> entries, string name, byte[] data, Encoding gbk, int maxBytes)
        {
            if (data.Length <= maxBytes)
            {
                entries.Add(new Entry(Conv.EncName(name, gbk), data));
                return;
            }
            int part = 1;
            foreach (var kv in Conv.SplitBlocks(data, maxBytes, gbk))
            {
                string nm = name + "_" + part;
                part++;
                entries.Add(new Entry(Conv.EncName(nm, gbk), kv.Value));
            }
        }

        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length >= 3 && args[0] == "--cli")
            {
                try
                {
                    Encoding gbk = Encoding.GetEncoding(936);
                    string enc;
                    string text = Conv.Normalize(Conv.DetectAndDecode(File.ReadAllBytes(args[1]), out enc));
                    var entries = Conv.ToEntries(text, 8000, true, gbk);
                    File.WriteAllBytes(args[2], Ebk.Build(entries));
                    Environment.Exit(0);
                }
                catch { Environment.Exit(1); }
            }
            if (args.Length >= 3 && args[0] == "--batch")
            {
                try
                {
                    string outDir = args[1];
                    Directory.CreateDirectory(outDir);
                    Encoding gbk = Encoding.GetEncoding(936);
                    var lines = new List<string>();
                    int ok = 0, bad = 0;
                    var inputs = new List<string>();
                    for (int i = 2; i < args.Length; i++) inputs.Add(args[i]);
                    inputs.Sort(new NaturalComparer());
                    for (int i = 0; i < inputs.Count; i++)
                    {
                        try
                        {
                            var r = ConvertOne(inputs[i], outDir, 8000, true, true, gbk);
                            lines.Add("OK\t" + r.Enc + "\t" + r.Count + "\t" + r.Size + "\t" + Path.GetFileName(r.Output));
                            ok++;
                        }
                        catch (Exception ex)
                        {
                            lines.Add("FAIL\t" + ex.Message + "\t" + Path.GetFileName(inputs[i]));
                            bad++;
                        }
                    }
                    File.WriteAllLines(Path.Combine(outDir, "_ebk_batch.log"), lines.ToArray(), Encoding.UTF8);
                    Environment.Exit(bad == 0 ? 0 : 2);
                }
                catch { Environment.Exit(1); }
            }
            if (args.Length >= 3 && args[0] == "--sort")
            {
                try
                {
                    var fs = new List<string>(Directory.GetFiles(args[2], "*.txt", SearchOption.TopDirectoryOnly));
                    fs.Sort(new NaturalComparer());
                    var names = new List<string>();
                    foreach (var f in fs) names.Add(Path.GetFileName(f));
                    File.WriteAllLines(args[1], names.ToArray(), Encoding.UTF8);
                    Environment.Exit(0);
                }
                catch { Environment.Exit(1); }
            }
            if (args.Length >= 4 && args[0] == "--mergegroup")
            {
                // --mergegroup <outdir> <per> <dir>
                // per 现在是"每 N 个章节条目一本"（不再是每 N 个文件一本）
                try
                {
                    string outDir = args[1];
                    int per = int.Parse(args[2]);
                    Directory.CreateDirectory(outDir);
                    var files = new List<string>(Directory.GetFiles(args[3], "*.txt", SearchOption.TopDirectoryOnly));
                    files.Sort(new NaturalComparer());
                    Encoding gbk = Encoding.GetEncoding(936);
                    string baseName = new DirectoryInfo(args[3]).Name;

                    var allEntries = MergeEntries(files, true, gbk, null);
                    int total = allEntries.Count;
                    int groupSize = (per > 0 && per < total) ? per : total;
                    int groups = (total + groupSize - 1) / groupSize;

                    int gi = 0;
                    for (int st = 0; st < total; st += groupSize)
                    {
                        gi++;
                        int cnt = Math.Min(groupSize, total - st);
                        var part = allEntries.GetRange(st, cnt);
                        string nm = groups > 1 ? (baseName + "_" + gi) : baseName;
                        File.WriteAllBytes(Path.Combine(outDir, nm + ".ebk"), Ebk.Build(part));
                    }
                    Environment.Exit(0);
                }
                catch { Environment.Exit(1); }
            }
            if (args.Length >= 3 && args[0] == "--mergedir")
            {
                try
                {
                    var files = new List<string>(Directory.GetFiles(args[2], "*.txt", SearchOption.TopDirectoryOnly));
                    files.Sort(new NaturalComparer());
                    Encoding gbk = Encoding.GetEncoding(936);
                    var entries = MergeEntries(files, true, gbk, null);
                    File.WriteAllBytes(args[1], Ebk.Build(entries));
                    Environment.Exit(0);
                }
                catch { Environment.Exit(1); }
            }
            if (args.Length >= 3 && args[0] == "--merge")
            {
                try
                {
                    var files = new List<string>();
                    for (int i = 2; i < args.Length; i++) files.Add(args[i]);
                    files.Sort(new NaturalComparer());
                    Encoding gbk = Encoding.GetEncoding(936);
                    var entries = MergeEntries(files, true, gbk, null);
                    File.WriteAllBytes(args[1], Ebk.Build(entries));
                    Environment.Exit(0);
                }
                catch { Environment.Exit(1); }
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var form = new MainForm();
            if (args.Length >= 2 && args[0] == "--open")
            {
                string dir = args[1];
                form.Load += (s, e) => form.Preload(dir);
            }
            Application.Run(form);
        }
    }
}