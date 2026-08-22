import re, os, collections

ROOT = r"f:/LUCKY_OS/LuckyOS systems 1.0/userspace/other/Shanyu GamesEngine and Modeling"
log = open(os.path.join(ROOT, "build_out.txt"), encoding="utf-8", errors="ignore").read().splitlines()
# CS0101: 命名空间'NS'已经包含'TYPE'的定义
# CS0104: 类型'TYPE'在'A'和'B'之间不明确
# CS0111: 类型'TYPE'已定义了一个名为'M'... ; or 已定义了一个名为'M'的具有相同参数类型的成员
pat = re.compile(r'ShanyuEngine\\(.+?)\((?P<line>\d+),(?P<col>\d+)\): error (?P<code>CS\d+): (?P<msg>.*)')
errs=[]
for ln in log:
    m=pat.search(ln)
    if m: errs.append((m.group(1).replace('\\','/'), int(m.group('line')), m.group('code'), m.group('msg')))

cs0101=collections.defaultdict(set)   # type -> set(files)
for f,line,code,msg in errs:
    if code=='CS0101':
        m=re.search(r'已经包含[‘\']([^’\']+)[’\']的定义', msg)
        if m: cs0101[m.group(1)].add(f)
cs0104=collections.defaultdict(set)
for f,line,code,msg in errs:
    if code=='CS0104':
        m=re.search(r'类型[‘\']([^’\']+)[’\']在[‘\']([^’\']+)[’\']和[‘\']([^’\']+)[’\']之间不明确', msg)
        if m:
            cs0104[m.group(1)].add(f)

print("=== CS0101 duplicate NAMESPACE types (count of files each defined in) ===")
for t,fs in sorted(cs0101.items(), key=lambda kv:-len(kv[1])):
    print(f"{len(fs):3d}  {t}  -> {sorted(fs)}")

print("\n=== CS0104 ambiguous types ===")
for t,fs in sorted(cs0104.items(), key=lambda kv:-len(kv[1])):
    print(f"{len(fs):3d}  {t}  -> {sorted(fs)}")
