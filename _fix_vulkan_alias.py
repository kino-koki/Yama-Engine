# 把 "using Vulkan = Silk.NET.Vulkan;" 移到每个 Vulkan 文件 using 块的最前面
import os, re, glob

base = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'ShanyuEngine', 'Vulkan')
alias = 'using Vulkan = Silk.NET.Vulkan;'
changed = []
for path in glob.glob(os.path.join(base, '*.cs')):
    text = open(path, encoding='utf-8').read()
    if alias not in text:
        continue
    lines = text.split('\n')
    # 找到别名行并移除
    idx = next(i for i, l in enumerate(lines) if l.strip() == alias)
    # 找到第一个 using 行
    first_using = next((i for i, l in enumerate(lines) if l.strip().startswith('using ')), None)
    if first_using is None or idx == first_using:
        continue
    lines.pop(idx)
    first_using = next(i for i, l in enumerate(lines) if l.strip().startswith('using '))
    lines.insert(first_using, alias)
    open(path, 'w', encoding='utf-8', newline='').write('\n'.join(lines))
    changed.append(os.path.basename(path))
print('moved alias in:', changed)
