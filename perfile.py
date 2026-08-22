import re,collections,os
ROOT=r"f:/LUCKY_OS/LuckyOS systems 1.0/userspace/other/Shanyu GamesEngine and Modeling"
log=open(os.path.join(ROOT,"build_out.txt"),encoding="utf-8",errors="ignore").read().splitlines()
pat=re.compile(r'ShanyuEngine[\\/](.+?)\((?P<line>\d+),.*?error (?P<code>CS\d+)')
c=collections.defaultdict(collections.Counter)
for ln in log:
    m=pat.search(ln)
    if m:
        f=m.group(1).replace('\\','/')
        c[m.group('code')][f]+=1
for code in ("CS0214","CS0708","CS0104","CS0111","CS0101","CS0535","CS0115","CS0534","CS9244","CS8340","CS0051","CS0509","CS0102","CS1073","CS0103"):
    tot=sum(c[code].values())
    if tot==0: continue
    print(f"\n=== {code} ({tot}) ===")
    for f,n in c[code].most_common():
        print(f"  {n:3d} {f}")
