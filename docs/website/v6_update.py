# -*- coding: utf-8 -*-
"""v6：引言带改为山雨欲来 + 版权改为青奈科技 + 子页删图改文字说明 + 下线画廊页。"""
import os
import re
import shutil

# ── 1. 引言带替换 ──
BAND_ZH_OLD = '''<div class="q">苟<span class="em">日新</span>，日日新，<span class="em">又日新</span>。</div>
  <span class="src">《礼记 · 大学》引 汤之《盘铭》 — 商汤科技取名之源</span>
  <p class="note">引擎按天迭代，代码按天刷新。这句刻在商汤名字里的古训，也是这个仓库的工作方式：每天让一个子系统比昨天更真实一点。</p>'''
BAND_ZH_NEW = '''<div class="q">山雨欲来<span class="em">风满楼</span>。</div>
  <span class="src">唐 · 许浑《咸阳城东楼》</span>
  <p class="note">编译错误已经清零，好比山雨将至；而真正的 GPU 端到端验证还没有跑通 —— 风尚未满楼。这是引擎此刻的真实状态，也是下一阶段要集中攻克的事。</p>'''

BAND_EN_OLD = '''<div class="q">苟<span class="em">日新</span>，日日新，<span class="em">又日新</span>。</div>
  <span class="src">Tang of Shang, from the Bronze Basin Inscription — the origin of SenseTime's name</span>
  <p class="note">"If you can renew yourself one day, do so day after day, and let there be daily renewal." The engine iterates daily; so does the code. Every subsystem gets a little more real than yesterday.</p>'''
BAND_EN_NEW = '''<div class="q">"A storm is coming — <span class="em">the wind fills the tower</span>."</div>
  <span class="src">Xu Hun, "East Tower of Xianyang City" · Tang dynasty</span>
  <p class="note">Compile errors are at zero — the mountain rain is near — but the real GPU end-to-end validation hasn't run yet. Closing that gap is exactly what Phase 1 is about.</p>'''

# ── 2. 版权 ──
COPY_ZH_OLD = '<div class="foot-copy">© 2026 Yama Engine · MIT License</div>'
COPY_ZH_NEW = '<div class="foot-copy">版权所有 © 2026 六安市青奈科技有限责任公司 · MIT License</div>'
COPY_EN_OLD = '<div class="foot-copy">© 2026 Yama Engine · MIT License</div>'
COPY_EN_NEW = '<div class="foot-copy">© 2026 Lu\u2019an Qingnai Technology Co., Ltd. · MIT License</div>'

# ── 3. 子页删图清单 ──
SUB_PAGES = ['why', 'capabilities', 'compare', 'scenarios', 'architecture',
             'status', 'get-started', 'faq']

RIDGE = ('\n  <div class="hero-ridge" aria-hidden="true"><svg viewBox="0 0 1440 110" preserveAspectRatio="none" shape-rendering="crispEdges">'
         '<defs><linearGradient id="ridgeGrad" x1="0" y1="0" x2="0" y2="1">'
         '<stop offset="0" stop-color="#FFAE00"/><stop offset="1" stop-color="#FA500F"/>'
         '</linearGradient></defs>'
         '<path d="M0 110 L0 78 L120 52 L240 74 L360 40 L480 66 L600 30 L720 58 L840 40 L960 66 L1080 34 L1200 60 L1320 46 L1440 70 L1440 110 Z" fill="url(#ridgeGrad)"/>'
         '</svg></div>')


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


for f in sorted(x for x in os.listdir('.') if x.endswith('.html')):
    s = open(f, encoding='utf-8').read()
    orig = s
    is_en = '-en' in f

    # 引言带
    if '苟<span class="em">日新</span>' in s:
        s = s.replace(BAND_ZH_OLD, BAND_ZH_NEW, 1)
        s = s.replace(BAND_EN_OLD, BAND_EN_NEW, 1)

    # 版权
    if is_en:
        s = s.replace(COPY_EN_OLD, COPY_EN_NEW, 1)
    else:
        s = s.replace(COPY_ZH_OLD, COPY_ZH_NEW, 1)

    # 子页删图（首页/诗页保留），改为墨色 hero + 日落山脊收边
    base = f.replace('-en.html', '').replace('.html', '')
    if base in SUB_PAGES:
        s = re.sub(r'\s*<div class="hero-bg hero-bg-sub"[\s\S]*?</div>', '', s, count=1)
        s = re.sub(r'\s*<link rel="preload" as="image" href="assets/heroes/' + base + r'\.webp">', '', s)
        s = s.replace('<header class="page-hero" id="main">', '<header class="page-hero no-hero-img" id="main">', 1)
        s = re.sub(r'(<meta property="og:image" content=")assets/og/' + base + r'\.jpg(">)',
                   r'\1assets/og/index.jpg\2', s)
        if 'hero-ridge' not in s:
            s = s.replace('</header>', RIDGE + '\n</header>', 1)

    if s != orig:
        open(f, 'w', encoding='utf-8').write(s)
        print('updated:', f)

# ── 4. 下线画廊页并清理引用 ──
if os.path.exists('heroes.html'):
    os.remove('heroes.html')
    print('heroes.html removed')
for f in sorted(x for x in os.listdir('.') if x.endswith('.html')):
    s = open(f, encoding='utf-8').read()
    s2 = re.sub(r'\s*<a href="heroes\.html">[^<]*</a>', '', s)
    if s2 != s:
        open(f, 'w', encoding='utf-8').write(s2)
        print('gallery link removed:', f)
x = open('sitemap.xml', encoding='utf-8').read()
x2 = re.sub(r'\s*<url><loc>heroes\.html</loc>[\s\S]*?</url>', '', x)
if x2 != x:
    open('sitemap.xml', 'w', encoding='utf-8').write(x2)
    print('sitemap entry removed')

# ── 5. 删除不再使用的图片资源 ──
for base in SUB_PAGES:
    for ext in ('png', 'webp'):
        p = 'assets/heroes/' + base + '.' + ext
        if os.path.exists(p):
            os.remove(p)
            print('deleted:', p)
for name in SUB_PAGES:
    p = 'assets/og/' + name + '.jpg'
    if os.path.exists(p):
        os.remove(p)
if os.path.exists('assets/gallery'):
    shutil.rmtree('assets/gallery')
    print('assets/gallery removed')

# ── 6. 无图 hero 的样式：墨色底、加高下边距给山脊留位 ──
c = open('assets/style.css', encoding='utf-8').read()
if 'no-hero-img' not in c:
    c += """
/* ── v6：无图页首（纯文字 + 日落山脊收边）── */
.page-hero.no-hero-img{padding-bottom:150px;background:var(--ink)}
.page-hero.no-hero-img .hero-ridge svg{height:80px}
"""
    open('assets/style.css', 'w', encoding='utf-8').write(c)
    print('no-hero-img css added')

