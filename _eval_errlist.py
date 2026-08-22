import re, collections, os
base = os.path.dirname(os.path.abspath(__file__))
per_file = collections.defaultdict(list)
for line in open(os.path.join(base, 'eval_build_engine.txt'), encoding='utf-8', errors='replace'):
    if ' error CS' not in line:
        continue
    m = re.search(r'^(.*?)\((\d+),(\d+)\): error (CS\d+): (.*)$', line.strip())
    if not m:
        continue
    path, ln, col, code, msg = m.groups()
    mm = re.search(r'ShanyuEngine\\(.+)$', path)
    rel = mm.group(1) if mm else path
    # 简化消息：去掉项目后缀
    msg = re.sub(r'\s*\[F:.*$', '', msg)
    per_file[rel].append((int(ln), code, msg[:120]))

out = open(os.path.join(base, '_eval_errors_grouped.txt'), 'w', encoding='utf-8')
for f in sorted(per_file, key=lambda k: -len(per_file[k])):
    errs = per_file[f]
    out.write(f"\n### {f} ({len(errs)} errors)\n")
    for ln, code, msg in errs:
        out.write(f"  L{ln} {code} {msg}\n")
out.close()
print('written _eval_errors_grouped.txt,', sum(len(v) for v in per_file.values()), 'errors,', len(per_file), 'files')
