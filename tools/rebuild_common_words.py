# -*- coding: utf-8 -*-
"""由 jieba dict.txt 重新生成 common_words.tsv（保持 "词<TAB>词频"、1~4 字中文词格式）

用法: py -3 tools/rebuild_common_words.py <jieba_dict.txt> <pinyin_full.tsv> <out.tsv> [cap]
只写 out.tsv（先写 out.tsv.tmp 再原子替换）；任何校验不过都以非零退出，不产出半成品。
"""
import sys
import os


def main():
    if len(sys.argv) < 4:
        print('usage: rebuild_common_words.py <jieba_dict.txt> <pinyin_full.tsv> <out.tsv> [cap]')
        return 2
    src = sys.argv[1]
    pin = sys.argv[2]
    out = sys.argv[3]
    cap = int(sys.argv[4]) if len(sys.argv) > 4 else 120000
    if not os.path.exists(src):
        print('FAIL: jieba 词表不存在: ' + src)
        return 3
    if not os.path.exists(pin):
        print('FAIL: pinyin_full.tsv 不存在: ' + pin)
        return 3

    chars = set()
    for line in open(pin, 'r', encoding='utf-8'):
        i = line.find(chr(9))
        if i > 0:
            chars.add(line[0])
    if len(chars) < 10000:
        print('FAIL: pinyin_full.tsv 只解析出 %d 个字，拒绝继续' % len(chars))
        return 3
    print('pinyin_full chars = %d' % len(chars))

    words = {}
    scanned = 0
    kept = 0
    for line in open(src, 'r', encoding='utf-8'):
        parts = line.split()
        if len(parts) < 2:
            continue
        scanned += 1
        w = parts[0]
        if not (1 <= len(w) <= 4):
            continue
        if not all(0x4E00 <= ord(c) <= 0x9FA5 for c in w):
            continue
        if not all(c in chars for c in w):
            continue
        try:
            f = int(parts[1])
        except ValueError:
            continue
        if f <= 0:
            continue
        kept += 1
        if w not in words or words[w] < f:
            words[w] = f

    rows = sorted(words.items(), key=lambda kv: (-kv[1], kv[0]))[:cap]
    if len(rows) < 1000:
        print('FAIL: 过滤后只剩 %d 条，拒绝写入' % len(rows))
        return 4

    tmp = out + '.tmp'
    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    with open(tmp, 'w', encoding='utf-8', newline=chr(10)) as fh:
        for w, f in rows:
            fh.write(w + chr(9) + str(f) + chr(10))
    os.replace(tmp, out)
    print('scanned=%d keptRaw=%d rows=%d bytes=%d minFreq=%d maxFreq=%d'
          % (scanned, kept, len(rows), os.path.getsize(out), rows[-1][1], rows[0][1]))
    return 0


sys.exit(main())
