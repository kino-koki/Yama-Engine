# -*- coding: utf-8 -*-
"""清理脚本 v2：贪婪移除残留的搜索层与 Cookie 提示（含之间所有内容）。"""
import os
import re

PATTERNS = [
    (r'\n<div class="search-overlay" id="search-overlay"[\s\S]*</body>', '\n</body>'),
    (r'\n<div class="cookie" role="dialog"[\s\S]*</body>', '\n</body>'),
]

for f in sorted(x for x in os.listdir('.') if x.endswith('.html')):
    s = open(f, encoding='utf-8').read()
    orig = s
    for pat, rep in PATTERNS:
        s = re.sub(pat, rep, s)
    if s != orig:
        open(f, 'w', encoding='utf-8').write(s)
        print('cleaned:', f)

c = open('assets/style.css', encoding='utf-8').read()
for pat, rep in [
    (r'\n/\* ── 全站搜索（Mistral 编辑风）── \*/[\s\S]*?@media\(max-width:600px\)\{[^}]*\}[^}]*\}\}\n', '\n'),
    (r'\n/\* Cookie 诚实提示 \*/[\s\S]*?@media\(max-width:600px\)\{[^}]*\}\n', '\n'),
]:
    c2 = re.sub(pat, rep, c)
    if c2 != c:
        c = c2
        print('css block removed')
open('assets/style.css', 'w', encoding='utf-8').write(c)
print('css bytes:', len(c))
