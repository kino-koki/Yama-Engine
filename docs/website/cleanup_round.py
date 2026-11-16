# -*- coding: utf-8 -*-
"""清理脚本：删除全站搜索模块与 Cookie 提示（按钮/层/JS/CSS）。"""
import os
import re

REMOVALS = {
    'search btn': (r'\n      <button class="search-btn" aria-label="[^"]*" aria-haspopup="dialog">\s*<svg[^>]*>.*?</svg>\s*</button>', ''),
    'search overlay': (r'\n<div class="search-overlay" id="search-overlay"[\s\S]*?</div>\n(?=</body>)', '\n'),
    'search js': (r"\n  // ── 全站搜索 ──[\s\S]*?sInput\.addEventListener\('input',\(\)=>\{[\s\S]*?\}\);\n  \}\n", '\n'),
    'cookie div': (r'\n<div class="cookie" role="dialog" aria-label="[^"]*" hidden>\s*<p>.*?</p>\s*<button class="btn btn-primary cookie-ok">[^<]*</button>\s*</div>\n(?=</body>)', '\n'),
    'cookie js': (r"\n  // Cookie notice[\s\S]*?ok\.addEventListener\('click',\(\)=>\{[\s\S]*?\}\);\n  \}\n", '\n'),
    'cookie js2': (r"\n  // Cookie 提示[\s\S]*?ok\.addEventListener\('click',\(\)=>\{[\s\S]*?\}\);\n  \}\n", '\n'),
}

CSS_REMOVALS = [
    (r'\n/\* ── 全站搜索（Mistral 编辑风）── \*/[\s\S]*?@media\(max-width:600px\)\{\.search-overlay\{[^}]*\}\.search-input\{[^}]*\}\}\n', '\n'),
    (r'\n/\* Cookie 诚实提示 \*/[\s\S]*?\.cookie-ok\{[^}]*\}\n', '\n'),
    (r'\.cookie\{[^}]*\}\n', ''),
    (r'\.cookie p\{[^}]*\}\n', ''),
    (r'\.cookie \.btn\{[^}]*\}\n', ''),
    (r'\.cookie-ok\{[^}]*\}\n', ''),
]

for f in sorted(x for x in os.listdir('.') if x.endswith('.html')):
    s = open(f, encoding='utf-8').read()
    orig = s
    for name, (pat, rep) in REMOVALS.items():
        s = re.sub(pat, rep, s)
    if s != orig:
        open(f, 'w', encoding='utf-8').write(s)
        print('cleaned:', f)

c = open('assets/style.css', encoding='utf-8').read()
for pat, rep in CSS_REMOVALS:
    c = re.sub(pat, rep, c)
open('assets/style.css', 'w', encoding='utf-8').write(c)
print('css cleaned:', len(c), 'bytes')
