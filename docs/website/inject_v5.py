# -*- coding: utf-8 -*-
"""v5 注入（修正版）：山脊线 / 日落群山 / 设计语言展示区。"""
import os

HERO_RIDGE = ('<div class="hero-ridge" aria-hidden="true"><svg viewBox="0 0 1440 110" preserveAspectRatio="none" shape-rendering="crispEdges">'
 '<path d="M0 110 L0 78 L120 52 L240 74 L360 40 L480 66 L600 30 L720 58 L840 40 L960 66 L1080 34 L1200 60 L1320 46 L1440 70 L1440 110 Z" fill="#171310"/>'
 '</svg></div>')

SUNSET_RIDGES = ('<div class="sunset-ridges" aria-hidden="true"><svg viewBox="0 0 1440 170" preserveAspectRatio="none" shape-rendering="crispEdges">'
 '<path d="M0 118 L144 84 L288 110 L432 72 L576 102 L720 64 L864 96 L1008 70 L1152 100 L1296 78 L1440 108 L1440 170 L0 170 Z" fill="#FFD900"/>'
 '<path d="M0 136 L160 106 L320 130 L480 96 L640 124 L800 92 L960 120 L1120 96 L1280 124 L1440 102 L1440 170 L0 170 Z" fill="#FFAE00"/>'
 '<path d="M0 150 L200 122 L400 144 L600 114 L800 140 L1000 112 L1200 138 L1440 120 L1440 170 L0 170 Z" fill="#FF8205"/>'
 '<path d="M0 162 L240 140 L480 158 L720 134 L960 156 L1200 138 L1440 154 L1440 170 L0 170 Z" fill="#FA520F"/>'
 '</svg></div>')

SWATCHES = [
    ('#FFFAEB', '#171310', 'paper', '#FFFAEB'),
    ('#171310', '#FFFAEB', 'ink', '#171310'),
    ('#FA500F', '#ffffff', 'primary', '#FA500F'),
    ('#FF8205', '#ffffff', 'ochre', '#FF8205'),
    ('#FFD900', '#171310', 'sun-2', '#FFD900'),
    ('#FFAE00', '#ffffff', 'sun-3', '#FFAE00'),
    ('#FA520F', '#ffffff', 'sun-5', '#FA520F'),
    ('#E00400', '#ffffff', 'sun-6', '#E00400'),
]


def dl_section(lang):
    zh = lang == 'zh'
    def t(z, e):
        return z if zh else e
    h2 = t('融合不是拼接，<br>沉淀出<span class="accent">自己的语言</span>。',
           'Fusion is not collage —<br>it settles into <span class="accent">a language of our own</span>.')
    p = t('来自 Mistral 的编辑排版与纸面质感，来自 SenseNova 的模型卡片与引言带，最终沉淀为 Yama 自己的三种母题：<strong>日落光谱、群山脊线、工程方角</strong>。这套语言只属于这个项目。',
          'Editorial typography from Mistral, model cards and quote bands from SenseNova — settled into three motifs of Yama\u2019s own: <strong>sunset spectrum, mountain ridgelines, and engineering squares</strong>. This language belongs to this project alone.')
    n = t('设计语言<br>设计语言', 'Design Language')
    sw = ''
    for bg, fg, k, hexv in SWATCHES:
        sw += ('<div class="swatch"><div class="chip" style="background:' + bg + ';color:' + fg +
               '"><span>' + k + '</span><span>' + hexv + '</span></div><span class="nm">' + k + '</span></div>')
    types = [
        ('Display · Archivo 800', 'display', t('山雨欲来 Aa', 'Ridgeline Aa'),
         t('大标题 / 数字 / 强调 — 紧排、负字距、方正有力', 'Headlines / numbers / emphasis — tight, negative tracking, square and strong.')),
        ('Body · Instrument Sans', 'body', t('正文与叙述 Aa', 'Narrative Aa'),
         t('段落 / 说明 — 中性、易读、不抢戏', 'Paragraphs / explanations — neutral, readable, quiet.')),
        ('Mono · JetBrains Mono', 'mono', 'MEASURE / 01',
         t('标注 / 眉标 / 数据 — 工程图纸的口吻', 'Labels / eyebrows / data — the voice of an engineering drawing.')),
        ('Serif · Noto Serif SC', 'cn', t('诗与古文', 'Poetry'),
         t('引文 / 诗页 — 连接千年前的那个傍晚', 'Quotes / poem page — connecting to that evening a thousand years ago.')),
    ]
    types_html = ''.join('<div class="dl-type"><span class="nm">' + nm + '</span><div class="spec ' + cls + '">' + spec +
                          '</div><div class="use">' + use + '</div></div>' for nm, cls, spec, use in types)
    sun_svg = ('<svg viewBox="0 0 44 44" fill="none" shape-rendering="crispEdges">'
               '<rect x="19" y="2" width="6" height="6" fill="#FFD900"/><rect x="19" y="8" width="6" height="6" fill="#FFAE00"/>'
               '<rect x="13" y="8" width="6" height="6" fill="#FF8205"/><rect x="25" y="8" width="6" height="6" fill="#FF8205"/>'
               '<rect x="8" y="14" width="6" height="6" fill="#FA520F"/><rect x="30" y="14" width="6" height="6" fill="#FA520F"/>'
               '<rect x="14" y="14" width="16" height="16" fill="#FA500F"/><rect x="2" y="19" width="6" height="6" fill="#E00400"/>'
               '<rect x="36" y="19" width="6" height="6" fill="#E00400"/><rect x="8" y="24" width="6" height="6" fill="#FF8205"/>'
               '<rect x="30" y="24" width="6" height="6" fill="#FF8205"/><rect x="13" y="30" width="6" height="6" fill="#FFAE00"/>'
               '<rect x="25" y="30" width="6" height="6" fill="#FFAE00"/><rect x="19" y="36" width="6" height="6" fill="#FFD900"/></svg>')
    motifs = [
        (t('Motif 01 · 像素太阳', 'Motif 01 · Pixel Sun'),
         '<div class="m-demo-sun">' + sun_svg + '</div>',
         t('方块拼出的太阳，缓慢自转。数字世界的光，和天空中的光同源。',
           'A sun assembled from squares, rotating slowly. The light of the digital world shares a source with the sky.')),
        (t('Motif 02 · 光谱带', 'Motif 02 · Spectrum Band'),
         '<div class="m-demo-band"><i style="background:#FFF0C2"></i><i style="background:#FFD900"></i><i style="background:#FFAE00"></i><i style="background:#FF8205"></i><i style="background:#FA520F"></i><i style="background:#E00400"></i></div>',
         t('六段等宽的日落光谱，是整套色彩系统的秩序来源，也是页脚的收束。',
           'Six equal bands of sunset — the order behind the entire color system, and the closing note of the footer.')),
        (t('Motif 03 · 群山脊线', 'Motif 03 · Ridgeline'),
         '<svg class="m-demo-ridge" viewBox="0 0 300 64" preserveAspectRatio="none" shape-rendering="crispEdges"><path d="M0 64 L0 46 L50 30 L100 44 L150 18 L200 38 L250 24 L300 40 L300 64 Z" fill="#171310"/></svg>',
         t('取自"嵯峨"的山势。章节之间的过渡不是一条直线，而是一道山脊。',
           'Taken from the mountain in Yama\u2019s name. The transition between sections is not a straight line — it is a ridge.')),
        (t('Motif 04 · 工程方角', 'Motif 04 · Engineering Square'),
         '<div class="m-demo-sq"><i></i><i></i><i></i><i></i></div>',
         t('没有圆角。卡片、按钮、标签全是直角 — 像图纸上的标注，清楚、肯定、不含糊。',
           'No rounded corners. Cards, buttons and tags are all right angles — like annotations on a drawing: clear, definite, unambiguous.')),
    ]
    motifs_html = ''.join('<div class="dl-motif"><div class="demo">' + demo + '</div><h4>' + h + '</h4><p>' + p + '</p></div>' for h, demo, p in motifs)
    return ('\n<section class="dl">\n  <div class="wrap">\n    <div class="sec-head reveal">\n      <div class="sec-index">\n'
            '        <span class="n">DL</span>\n        <span class="t">' + n + '</span>\n      </div>\n      <div>\n        <h2>' + h2 +
            '</h2>\n        <p>' + p + '</p>\n      </div>\n    </div>\n    <div class="dl-swatches reveal">' + sw +
            '</div>\n    <div class="dl-types reveal">' + types_html + '</div>\n    <div class="dl-motifs reveal">' + motifs_html +
            '</div>\n  </div>\n</section>\n')


for f in ['index.html', 'index-en.html']:
    s = open(f, encoding='utf-8').read()
    if 'class="dl"' in s:
        print('dl exists:', f)
        continue
    lang = 'en' if '-en' in f else 'zh'
    dl = dl_section(lang)
    anchor = '<!-- ══════════ 《盘铭》引言带'
    if anchor not in s:
        anchor = '<!-- ══════════ Quote band'
    assert anchor in s, f + ' quote band anchor'
    s = s.replace(anchor, dl + '\n' + anchor, 1)
    hr_anchor = 'decoding="async"></div>'
    assert hr_anchor in s, f + ' hero anchor'
    s = s.replace(hr_anchor, hr_anchor + '\n' + HERO_RIDGE, 1)
    footer_anchor = '<footer>'
    assert footer_anchor in s, f + ' footer anchor'
    s = s.replace(footer_anchor, SUNSET_RIDGES + '\n' + footer_anchor, 1)
    open(f, 'w', encoding='utf-8').write(s)
    print('v5 injected:', f)

for f in sorted(os.listdir('.')):
    if not f.endswith('.html') or f in ('index.html', 'index-en.html', '404.html', 'heroes.html'):
        continue
    s = open(f, encoding='utf-8').read()
    if 'sunset-ridges' in s:
        continue
    anchor = '<footer>'
    assert anchor in s, f + ' footer anchor'
    s = s.replace(anchor, SUNSET_RIDGES + '\n<footer>', 1)
    open(f, 'w', encoding='utf-8').write(s)
    print('sunset ridges:', f)
