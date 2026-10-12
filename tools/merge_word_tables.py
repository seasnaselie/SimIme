# -*- coding: utf-8 -*-
"""把 add.tsv 合并进 base.tsv（同格式：词<TAB>频次），按频次降序、cap 截断，输出 out.tsv。

用法: py -3 tools/merge_word_tables.py <base.tsv> <add.tsv> <out.tsv> [cap]
同名取较大频次；base 不存在时等价于只写 add。先 .tmp 再原子替换。
"""
import sys
import os


def main():
    if len(sys.argv) < 4:
        print('usage: merge_word_tables.py <base.tsv> <add.tsv> <out.tsv> [cap]')
        return 2
    base = sys.argv[1]
    add = sys.argv[2]
    out = sys.argv[3]
    cap = int(sys.argv[4]) if len(sys.argv) > 4 else 120000

    words = {}

    def load(p):
        n = 0
        if not os.path.exists(p):
            return n
        for line in open(p, 'r', encoding='utf-8'):
            i = line.find(chr(9))
            if i <= 0:
                continue
            w = line[:i]
            v = line[i + 1:].strip()
            try:
                f = int(v)
            except ValueError:
                continue
            if f <= 0 or len(w) < 1:
                continue
            n += 1
            old = words.get(w)
            if old is None or f > old:
                words[w] = f
        return n

    nb = load(base)
    na = load(add)
    rows = sorted(words.items(), key=lambda kv: (-kv[1], kv[0]))[:cap]
    tmp = out + '.tmp'
    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    with open(tmp, 'w', encoding='utf-8', newline=chr(10)) as fh:
        for w, f in rows:
            fh.write(w + chr(9) + str(f) + chr(10))
    os.replace(tmp, out)
    print('baseRows=%d addRows=%d mergedDistinct=%d rows=%d bytes=%d'
          % (nb, na, len(words), len(rows), os.path.getsize(out)))
    return 0


sys.exit(main())
