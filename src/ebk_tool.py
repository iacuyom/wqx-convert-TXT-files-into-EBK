#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
ebk_tool.py — 快速 EBK 转换核心（v1.0，已用真实样本逐字节校准）

用法:
  py -3 ebk_tool.py build  input.txt -o out.ebk [--html] [--block 8000]
  py -3 ebk_tool.py dump   out.ebk
  py -3 ebk_tool.py check  out.ebk
  py -3 ebk_tool.py bench  input.txt

格式（由 asd.ebk 逐字节校准）:
  文件头 15B:
    0..2    00 00 00
    3..5    last_record_addr - 1  (u24)
    6..9    00 00 00 00
    10..12  text_size (u24)
    13..14  first_record_addr (u16)
  目录:
    1B count | 2B parent(0) | 2B*count 记录地址 | count*(name + 0xFF)
  页记录 14B:
    1B 00 | 2B parent(=15) | 1B x(=1) | 3B start | 2B index | 3B end | 2B index
  索引表 9B/条:
    1B 00 | 3B start | 3B end | 2B 0000
  正文:
    所有条目按顺序连续存放（GB2312）
"""
import argparse
import html as _html
import os
import re
import struct
import sys
import time

HEADER = 15
REC = 14
TAB = 9


def u24(b, o): return b[o] | (b[o + 1] << 8) | (b[o + 2] << 16)
def p24(v): return bytes((v & 0xFF, (v >> 8) & 0xFF, (v >> 16) & 0xFF))


def detect_encoding(raw: bytes) -> str:
    if raw.startswith(b"\xef\xbb\xbf"):
        return "utf-8-sig"
    if raw.startswith(b"\xff\xfe"):
        return "utf-16-le"
    if raw.startswith(b"\xfe\xff"):
        return "utf-16-be"
    try:
        raw.decode("utf-8")
        return "utf-8"
    except UnicodeDecodeError:
        return "gb18030"


def decode_text(raw: bytes) -> str:
    t = raw.decode(detect_encoding(raw), errors="replace")
    return t.replace("\r\n", "\n").replace("\r", "\n")


def html_to_text(src: str) -> str:
    src = re.sub(r"(?is)<head\b.*?</head\s*>", "", src)
    src = re.sub(r"(?is)<script\b.*?</script\s*>", "", src)
    src = re.sub(r"(?is)<style\b.*?</style\s*>", "", src)
    src = re.sub(r"(?is)<!--.*?-->", "", src)
    src = re.sub(r"(?i)<br\s*/?>", "\n", src)
    src = re.sub(r"(?i)</(p|div|li|tr|h[1-6]|table|blockquote)\s*>", "\n", src)
    src = re.sub(r"(?s)<[^>]+>", "", src)
    src = _html.unescape(src)
    src = src.replace("\xa0", " ").replace("\u3000", "  ")
    src = re.sub(r"[ \t]+\n", "\n", src)
    src = re.sub(r"\n{3,}", "\n\n", src)
    return src.strip()


class Book:
    def __init__(self):
        self.names: list[bytes] = []
        self.texts: list[bytes] = []

    def add(self, name: str, data: bytes):
        self.names.append(name.encode("gb2312", "replace")[:120])
        self.texts.append(data)

    def to_bytes(self, x: int = 1) -> bytes:
        n = len(self.names)
        if n == 0:
            raise ValueError("empty book")
        if n > 255:
            raise ValueError("too many entries (>255)")
        dir_size = 1 + 2 + 2 * n + sum(len(s) + 1 for s in self.names)
        rec_start = HEADER + dir_size
        tab_start = rec_start + REC * n
        text_start = tab_start + TAB * n
        total = text_start + sum(len(t) for t in self.texts)
        buf = bytearray(total)

        buf[0:3] = b"\x00\x00\x00"
        buf[3:6] = p24(rec_start + REC * (n - 1) - 1)
        buf[6:10] = b"\x00\x00\x00\x00"
        buf[10:13] = p24(sum(len(t) for t in self.texts))
        struct.pack_into("<H", buf, 13, rec_start)

        q = HEADER
        buf[q] = n
        struct.pack_into("<H", buf, q + 1, 0)
        q += 3
        for i in range(n):
            struct.pack_into("<H", buf, q, rec_start + REC * i)
            q += 2
        for s in self.names:
            buf[q:q + len(s)] = s
            q += len(s)
            buf[q] = 0xFF
            q += 1

        pos = text_start
        for i in range(n):
            a = rec_start + REC * i
            data = self.texts[i]
            buf[a] = 0
            struct.pack_into("<H", buf, a + 1, HEADER)
            buf[a + 3] = x
            buf[a + 4:a + 7] = p24(pos)
            struct.pack_into("<H", buf, a + 7, i)
            buf[a + 9:a + 12] = p24(pos + len(data))
            struct.pack_into("<H", buf, a + 12, i)

            t = tab_start + TAB * i
            buf[t] = 0
            buf[t + 1:t + 4] = p24(pos)
            buf[t + 4:t + 7] = p24(pos + len(data))
            struct.pack_into("<H", buf, t + 7, 0)

            buf[pos:pos + len(data)] = data
            pos += len(data)
        return bytes(buf)


def parse(path: str):
    b = open(path, "rb").read()
    n = b[15]
    p = 18
    addrs = [struct.unpack_from("<H", b, p + 2 * i)[0] for i in range(n)]
    p += 2 * n
    names = []
    for _ in range(n):
        e = b.find(b"\xff", p)
        if e < 0:
            e = len(b)
        names.append(b[p:e])
        p = e + 1
    recs = []
    for a in addrs:
        recs.append(dict(addr=a, start=u24(b, a + 4), end=u24(b, a + 9),
                         i1=struct.unpack_from("<H", b, a + 7)[0],
                         i2=struct.unpack_from("<H", b, a + 12)[0], x=b[a + 3]))
    return b, names, recs


def walk(b, addr=15, prefix=""):
    """递归遍历目录，返回 [(path_name, record)]。"""
    n = b[addr]
    p = addr + 3
    addrs = [struct.unpack_from("<H", b, p + 2 * i)[0] for i in range(n)]
    p += 2 * n
    names = []
    for _ in range(n):
        e = b.find(b"\xff", p)
        if e < 0:
            e = len(b)
        names.append(b[p:e])
        p = e + 1
    out = []
    for a, nm in zip(addrs, names):
        label = (prefix + "/" + nm.decode("gb2312", "replace")) if prefix else nm.decode("gb2312", "replace")
        if b[a] == 0:
            out.append((label, dict(addr=a, start=u24(b, a + 4), end=u24(b, a + 9),
                                    i1=struct.unpack_from("<H", b, a + 7)[0],
                                    i2=struct.unpack_from("<H", b, a + 12)[0], x=b[a + 3])))
        else:
            out.extend(walk(b, a, label))
    return out


def dump(path: str):
    b = open(path, "rb").read()
    print(f"file      : {path} ({len(b):,} bytes)")
    print(f"header    : {b[0:3].hex(' ')} | max-1={u24(b,3):#x} | res={b[6:10].hex(' ')} | "
          f"text={u24(b,10):,} | min={struct.unpack_from('<H', b, 13)[0]:#x}")
    items = walk(b)
    print(f"entries   : {len(items)}")
    for label, r in items[:15]:
        txt = b[r["start"]:r["end"]]
        print(f"  {label[:36]!r:42} rec@{r['addr']:#06x} "
              f"[{r['start']:#06x}:{r['end']:#06x}] {len(txt):6}B x={r['x']}")
    if len(items) > 15:
        print(f"  ... {len(items)-15} more")


def check(path: str):
    b = open(path, "rb").read()
    items = walk(b)
    ok = True
    total = sum(r["end"] - r["start"] for _, r in items)
    hdr = u24(b, 10)
    print(f"textSize header={hdr:,} sum={total:,} {'OK' if hdr == total else 'MISMATCH'}")
    ok &= hdr == total
    ordered = sorted((r for _, r in items), key=lambda r: r["start"])
    for i in range(len(ordered) - 1):
        if ordered[i]["end"] != ordered[i + 1]["start"]:
            print(f"chain break at {i}: {ordered[i]['end']:#x} -> {ordered[i+1]['start']:#x}")
            ok = False
            break
    print("check:", "PASS" if ok else "FAIL")
    return ok


def split_gb(gb: bytes, block: int):
    """按 \\r\\n 边界把 GB2312 字节流切块，最多 255 块。"""
    n = len(gb)
    step = max(1, block)
    chunks = []
    for _ in range(40):
        chunks = []
        start = 0
        while start < n:
            end = min(start + step, n)
            if end < n:
                cut = gb.rfind(b"\r\n", start + 1, end)
                if cut > start + step // 2:
                    end = cut + 2
            chunks.append(gb[start:end])
            start = end
        if len(chunks) <= 255:
            break
        step = int(step * 1.3) + 1
    out = []
    for i, ch in enumerate(chunks, 1):
        line = ch.split(b"\r\n", 1)[0].decode("gb2312", "replace").strip()
        out.append(((line[:40] or f"Page{i}"), ch))
    return out


CHAPTER_RE = re.compile(r"(?m)^(第[0-9一二三四五六七八九十百千零〇两]+[章节回卷篇][^\r\n]{0,40})")


def split_chapters(text: str):
    """按“第N章/节/回/卷/篇”标题切分；找不到则返回 None。"""
    ms = list(CHAPTER_RE.finditer(text))
    if len(ms) < 2:
        return None
    parts = []
    if ms[0].start() > 0:
        pre = text[:ms[0].start()].strip()
        if pre:
            parts.append(("前言", pre + "\r\n"))
    for i, m in enumerate(ms):
        s = m.start()
        e = ms[i + 1].start() if i + 1 < len(ms) else len(text)
        parts.append((m.group(1).strip()[:40], text[s:e]))
    return parts


def prepare(path: str, from_html: bool, block: int):
    raw = open(path, "rb").read()
    text = decode_text(raw)
    if from_html or path.lower().endswith((".htm", ".html", ".shtml")):
        text = html_to_text(text)
    text = text.replace("\n", "\r\n")
    if not text.endswith("\r\n"):
        text += "\r\n"
    chapters = split_chapters(text)
    if chapters and len(chapters) <= 255:
        return raw, [(n, c.encode("gb2312", "replace")) for n, c in chapters]
    gb = text.encode("gb2312", "replace")
    return raw, split_gb(gb, block)


def build(input_path: str, output_path: str, block: int = 8000, from_html: bool = False):
    t0 = time.perf_counter()
    raw, chunks = prepare(input_path, from_html, block)
    book = Book()
    for name, data in chunks:
        book.add(name, data)
    blob = book.to_bytes()
    open(output_path, "wb").write(blob)
    dt = time.perf_counter() - t0
    print(f"input   : {len(raw):,} bytes")
    print(f"entries : {len(book.names)} (<= {block} B each)")
    print(f"output  : {output_path}  {len(blob):,} bytes")
    print(f"elapsed : {dt*1000:.1f} ms  ({len(raw)/1024/1024/dt:.1f} MB/s)")


def bench(input_path: str, rounds: int = 5):
    raw, chunks = prepare(input_path, False, 8000)
    times = []
    for _ in range(rounds):
        t0 = time.perf_counter()
        book = Book()
        for name, data in chunks:
            book.add(name, data)
        blob = book.to_bytes()
        times.append(time.perf_counter() - t0)
    best = min(times)
    print(f"size {len(raw):,} B -> {len(blob):,} B, {len(chunks)} entries")
    print(f"best {best*1000:.1f} ms, {len(raw)/1024/1024/best:.1f} MB/s")


def main():
    ap = argparse.ArgumentParser(description="fast EBK toolkit v1.0")
    sub = ap.add_subparsers(dest="cmd", required=True)
    b = sub.add_parser("build")
    b.add_argument("input"); b.add_argument("-o", "--output", required=True)
    b.add_argument("--block", type=int, default=8000)
    b.add_argument("--html", action="store_true")
    d = sub.add_parser("dump"); d.add_argument("input")
    c = sub.add_parser("check"); c.add_argument("input")
    bn = sub.add_parser("bench"); bn.add_argument("input"); bn.add_argument("--rounds", type=int, default=5)
    args = ap.parse_args()
    if args.cmd == "build":
        build(args.input, args.output, args.block, args.html)
    elif args.cmd == "dump":
        dump(args.input)
    elif args.cmd == "check":
        check(args.input)
    elif args.cmd == "bench":
        bench(args.input, args.rounds)


if __name__ == "__main__":
    main()
