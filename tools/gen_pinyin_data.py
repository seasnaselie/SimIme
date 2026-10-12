# -*- coding: utf-8 -*-
"""SimIme 拼音/资产生成器（可重跑）

数据源（HTTP 镜像下载，见 _devA/pkg）：
  pypinyin 0.55.0  pinyin_dict.py   语料序读音（首选，的=de 了=le 行=xíng）
  pypinyin 0.9.5   pinyin_dict.py   兜底（0.55 缺字时补位）
  jieba            dict.txt         词频（常用度 / 常用字筛选）
输出（src/Assets）：
  pinyin_full.tsv   字<TAB>无调小写拼音
  char_freq.tsv     字<TAB>常用度分值（GB2312 ∩ 语料频次 的常用字，错字候选只用这些字）
  common_words.tsv  词<TAB>语料词频（1~2 字词）
"""
import importlib.util, collections, unicodedata, re, sys

BASE = 'D:/deepseek/SimIme'
PP_NEW = BASE + '/_devA/pkg/x55/pypinyin/'
PP_OLD = BASE + '/_devA/pkg/x/pypinyin/'
JIEBA = BASE + '/_devA/pkg/jieba_x/jieba-0.42/jieba/dict.txt'
OVERRIDES = BASE + '/tools/pinyin_overrides.tsv'
OUT = BASE + '/src/Assets'
TAB = chr(9)
LF = chr(10)
MAX_WORDS = 120000
BASIC_LO, BASIC_HI = 0x4E00, 0x9FA5
DIAERESIS = 0x308
PY_RE = re.compile('^[a-z\u00fc]+$')

report = []
def say(s):
    report.append(s)


def strip_tone(s):
    d = unicodedata.normalize('NFD', s)
    keep = []
    for ch in d:
        if unicodedata.combining(ch) and ord(ch) != DIAERESIS:
            continue
        keep.append(ch)
    return unicodedata.normalize('NFC', ''.join(keep))


def load_mod(path, name):
    spec = importlib.util.spec_from_file_location(name, path)
    m = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(m)
    return m


def first_reading(d, cp):
    raw = d.get(cp)
    if not raw:
        return None
    plain = strip_tone(raw.split(',')[0].strip()).lower()
    return plain if PY_RE.match(plain) else None


def all_readings(d, cp):
    raw = d.get(cp)
    if not raw:
        return []
    out = []
    for part in raw.split(','):
        p = strip_tone(part.strip()).lower()
        if PY_RE.match(p):
            out.append(p)
    return out


def gb2312_hanzi():
    s = set()
    for b1 in range(0xB0, 0xF8):
        for b2 in range(0xA1, 0xFF):
            try:
                s.add(bytes([b1, b2]).decode('gb2312'))
            except UnicodeDecodeError:
                pass
    return s


GB = gb2312_hanzi()
say('gb2312 hanzi set size = ' + str(len(GB)))

pd_new = load_mod(PP_NEW + 'pinyin_dict.py', 'pdnew').pinyin_dict
pd_old = load_mod(PP_OLD + 'pinyin_dict.py', 'pdold').pinyin_dict
say('pypinyin 0.55 entries = ' + str(len(pd_new)) + ' | 0.9.5 entries = ' + str(len(pd_old)))

# ---------- 覆盖表 ----------
override = {}
for line in open(OVERRIDES, 'r', encoding='utf-8'):
    line = line.strip()
    if not line or line.startswith('#'):
        continue
    parts = line.split(TAB)
    if len(parts) < 2:
        parts = line.split()
    if len(parts) < 2 or len(parts[0]) != 1:
        continue
    ch, py = parts[0], parts[1].strip().lower()
    if PY_RE.match(py):
        override[ch] = py
say('override entries = ' + str(len(override)))

# ---------- 基础表：0.55 优先，0.9.5 兜底，覆盖表最高 ----------
table = {}
src_new = src_old = src_ovr = 0
changed_vs_old = 0
for cp in range(BASIC_LO, BASIC_HI + 1):
    ch = chr(cp)
    old = first_reading(pd_old, cp)
    new = first_reading(pd_new, cp)
    pick = new if new else old
    if not pick:
        continue
    if ch in override:
        if pick != override[ch]:
            src_ovr += 1
        pick = override[ch]
    elif new:
        src_new += 1
    else:
        src_old += 1
    table[cp] = pick
    if old and old != pick:
        changed_vs_old += 1

say('pinyin_full rows = ' + str(len(table)))
say('  from 0.55 = ' + str(src_new) + ' | 0.9.5 fallback = ' + str(src_old) + ' | override = ' + str(src_ovr))
say('  readings changed vs old 0.9.5 table = ' + str(changed_vs_old))
missing = [c for c in range(BASIC_LO, BASIC_HI + 1) if c not in table]
say('  missing chars = ' + str(len(missing)) + ' ' + repr([hex(c) for c in missing[:8]]))

# ---------- T1 断言 ----------
T1 = [('的','de'),('了','le'),('着','zhe'),('还','hai'),('都','dou'),('什','shen'),('么','me'),('行','xing'),('地','de')]
t1_bad = 0
t1_lines = []
for ch, want in T1:
    got = table.get(ord(ch), '<none>')
    ok = (got == want)
    if not ok:
        t1_bad += 1
    t1_lines.append('  T1 %s %s expect=%s got=%s' % ('OK ' if ok else 'FAIL', ch, want, got))
say('T1 (多音字现代读音) bad = ' + str(t1_bad))
for l in t1_lines:
    say(l)

# ---------- 语料频次 ----------
char_score = collections.Counter()
word_freq = {}
lines_n = 0
for line in open(JIEBA, 'r', encoding='utf-8'):
    parts = line.split()
    if len(parts) < 2:
        continue
    w = parts[0]
    try:
        f = int(parts[1])
    except ValueError:
        continue
    lines_n += 1
    if not (1 <= len(w) <= 4):
        continue
    if not all(BASIC_LO <= ord(c) <= BASIC_HI for c in w):
        continue
    if w not in word_freq or word_freq[w] < f:
        word_freq[w] = f
    for ch in set(w):
        char_score[ch] += f
say('jieba lines = ' + str(lines_n) + ' | cjk words(1..4) = ' + str(len(word_freq)))

# 只用 phrases_dict 给"jieba 语料里查不到的字"补一点常用度分数；
# 不再把 phrases_dict 的词塞进 word_freq，保证 common_words.tsv 与 tools/rebuild_common_words.py 的产物逐字节一致
ph = load_mod(PP_OLD + 'phrases_dict.py', 'phd').phrases_dict
bumped = 0
for w in ph:
    if not (1 <= len(w) <= 4) or not all(BASIC_LO <= ord(c) <= BASIC_HI for c in w):
        continue
    for ch in set(w):
        if char_score[ch] == 0:
            char_score[ch] += 5
            bumped += 1
say('phrases char-score bumps = ' + str(bumped))

common = []
for cp in sorted(table):
    ch = chr(cp)
    sc = char_score.get(ch, 0)
    if sc <= 0 or ch not in GB:
        continue
    common.append((cp, sc))
common.sort(key=lambda t: (-t[1], t[0]))
say('common chars = ' + str(len(common)))

words = []
for w, f in word_freq.items():
    if len(w) > 4:
        continue
    if not all(ord(ch) in table for ch in w):
        continue
    words.append((w, f))
words.sort(key=lambda t: (-t[1], t[0]))
words = words[:MAX_WORDS]
say('common words = ' + str(len(words)))

# ---------- 诊断：最高频 200 字里还有哪些多音字，字典取了哪个读音 ----------
say('')
say('--- 高频 200 字中的多音字（dict055 首读音；人工抽查用）---')
poly = 0
for cp, sc in common[:200]:
    ch = chr(cp)
    rs = all_readings(pd_new, cp)
    if len(set(rs)) < 2:
        continue
    poly += 1
    pin = ' [override]' if ch in override else ''
    say('  ' + ch + ' -> ' + table[cp] + '   all=' + ','.join(rs) + pin)
say('polyphone chars in top200 = ' + str(poly))

def write_tsv(path, rows):
    with open(path, 'w', encoding='utf-8', newline=LF) as fh:
        for a, b in rows:
            fh.write(str(a) + TAB + str(b) + LF)
    import os
    return os.path.getsize(path)

s1 = write_tsv(OUT + '/pinyin_full.tsv', [(chr(cp), table[cp]) for cp in sorted(table)])
s2 = write_tsv(OUT + '/char_freq.tsv', [(chr(cp), sc) for cp, sc in common])
s3 = write_tsv(OUT + '/common_words.tsv', words)
say('')
say('bytes pinyin_full.tsv = ' + str(s1))
say('bytes char_freq.tsv = ' + str(s2))
say('bytes common_words.tsv = ' + str(s3))
say('probe rows: 的=' + table.get(0x7684) + ' 地=' + table.get(0x5730) + ' 得=' + table.get(0x5F97)
    + ' 长=' + table.get(0x957F) + ' 女=' + table.get(0x5973) + ' 绿=' + table.get(0x7EFF))

txt = LF.join(report)
open(BASE + '/_devA/gen_report.txt', 'w', encoding='utf-8', newline=LF).write(txt)
sys.stdout.write(txt)
