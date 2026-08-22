import re, collections, os
base = os.path.dirname(os.path.abspath(__file__))
files = collections.Counter()
dirs = collections.Counter()
for line in open(os.path.join(base, 'eval_build_engine.txt'), encoding='utf-8', errors='replace'):
    if ' error CS' not in line:
        continue
    p = line.split('(')[0]
    m = re.search(r'ShanyuEngine\\(.+)$', p)
    rel = m.group(1).strip() if m else p.strip()
    files[rel] += 1
    top = rel.split('\\')[0] if '\\' in rel else '(root)'
    dirs[top] += 1
print('受影响文件总数:', len(files))
print('\n-- 错误最多的文件 --')
for f, c in files.most_common(12):
    print(c, f)
print('\n-- 按目录分布 --')
for d, c in dirs.most_common(15):
    print(c, d)
