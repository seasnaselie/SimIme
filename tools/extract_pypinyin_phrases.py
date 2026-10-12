# -*- coding: utf-8 -*-
"""从 pypinyin 的 phrases_dict.json 提取 2~4 字中文词组，写成 common_words.tsv 格式（词<TAB>频次）。

用法: py -3 tools/extract_pypinyin_phrases.py <phrases_dict.json> <pinyin_full.tsv> <out.tsv> [cap]
phrases_dict 没有语料频次，这里用「序号倒数」合成频次（只用于排序与权重回退，位次越靠前值越大）。
只写 out.tsv（先 .tmp 再原子替换）；校验不过以非零退出，不产出半成品。
"""
import sys
import os
import json


def main():
    if len(sys.argv) < 4:
        print('usage: extract_pypinyin_phrases.py <phrases_dict.json> <pinyin_full.tsv> <out.tsv> [cap]')
        return 2
    src = sys.argv[1]
    pin = sys.argv[2]
    out = sys.argv[3]
    cap = int(sys.argv[4]) if len(sys.argv) > 4 else 120000
    if not os.path.exists(src):
        print('FAIL: phrases_dict.json 不存在: ' + src)
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
        print('FAIL: pinyin_full.tsv 只解析出 %d 个字' % len(chars))
        return 3

    data = json.load(open(src, 'r', encoding='utf-8'))
    if not isinstance(data, dict):
        print('FAIL: phrases_dict.json 结构不是字典')
        return 3

    words = []
    seen = set()
    for k in data.keys():
        if not (2 <= len(k) <= 4):
            continue
        if not all(0x4E00 <= ord(c) <= 0x9FA5 for c in k):
            continue
        if not all(c in chars for c in k):
            continue
        if k in seen:
            continue
        seen.add(k)
        words.append(k)
    if len(words) < 1000:
        print('FAIL: 只提取到 %d 条 2~4 字词组，拒绝写入' % len(words))
        return 4

    words = words[:cap]
    n = len(words)
    tmp = out + '.tmp'
    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    with open(tmp, 'w', encoding='utf-8', newline=chr(10)) as fh:
        for i in range(n):
            fh.write(words[i] + chr(9) + str(n - i) + chr(10))
    os.replace(tmp, out)

    probes = ['十二个', '我们', '散步', '你好']
    hit = '/'.join([w for w in probes if w in seen])
    common = ['星星', '太阳', '明天', '但是', '生日快乐', '天上有']
    miss = '/'.join([w for w in common if w not in seen])
    print('jsonKeys=%d phrases=%d rows=%d bytes=%d 抽检命中=%s'
          % (len(data), len(seen), n, os.path.getsize(out), hit))
    print('注意: phrases_dict 是「注音消歧词表」而非通用词表（json 与同包 phrases_dict.py 完全一致）；'
          '常用词缺失抽检 -> ' + (miss if miss else '(无)'))
    print('建议: 用 -Mode merge 与现有词表合并（默认 auto 对 pypinyin 即合并），避免把 星星/太阳 这类词弄丢')
    return 0


sys.exit(main())
