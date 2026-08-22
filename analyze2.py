import re, os, collections

ROOT = r"f:/LUCKY_OS/LuckyOS systems 1.0/userspace/other/Shanyu GamesEngine and Modeling"
log = open(os.path.join(ROOT, "build_out.txt"), encoding="utf-8", errors="ignore").read().splitlines()
pat = re.compile(r'ShanyuEngine\\(.+?)\((?P<line>\d+),(?P<col>\d+)\): error (?P<code>CS\d+): (?P<msg>.*)')
errors = []
for ln in log:
    m = pat.search(ln)
    if m:
        errors.append((m.group(1).replace('\\','/'), int(m.group('line')), m.group('code'), m.group('msg')))

perfile = collections.Counter(e[0] for e in errors)
print("=== per-file code breakdown (top 45 files) ===")
for f,_ in perfile.most_common(45):
    codes = collections.Counter(e[2] for e in errors if e[0]==f)
    cd = " ".join(f"{c}:{n}" for c,n in codes.most_common())
    print(f"\n{f}  (total {sum(codes.values())})\n   {cd}")
