import re, os, collections

ROOT = r"f:/LUCKY_OS/LuckyOS systems 1.0/userspace/other/Shanyu GamesEngine and Modeling/ShanyuEngine"
proj = r"f:/LUCKY_OS/LuckyOS systems 1.0/userspace/other/Shanyu GamesEngine and Modeling"
build = open(os.path.join(proj,"build_out.txt"),encoding="utf-8",errors="ignore").read().splitlines()

# CS0101: 命名空间“NS”已经包含“TYPE”的定义
cs0101_types=collections.OrderedDict()
for ln in build:
    m=re.search(r'error CS0101: 命名空间[“"]([^”"]+)[”"]已经包含[“"]([^”"]+)[”"]的定义', ln)
    if m:
        cs0101_types.setdefault(m.group(2), set()).add(m.group(1))

# For each type, find ALL files that DECLARE it (class/struct/interface/enum/record NAME)
def find_defs(name):
    out=[]
    for dp,_,fs in os.walk(ROOT):
        for fn in fs:
            if not fn.endswith('.cs'): continue
            p=os.path.join(dp,fn)
            try: txt=open(p,encoding='utf-8',errors='ignore').read()
            except: continue
            # declaration: keyword + name + optional generics, with name exactly
            if re.search(r'\b(class|struct|interface|enum|record)\s+'+re.escape(name)+r'\b', txt):
                rel=os.path.relpath(p,ROOT).replace('\\','/')
                lines=txt.count('\n')+1
                out.append((rel,lines))
    return out

# reference count of name across all cs (external usage signal)
allref=collections.Counter()
for dp,_,fs in os.walk(ROOT):
    for fn in fs:
        if not fn.endswith('.cs'): continue
        try: txt=open(os.path.join(dp,fn),encoding='utf-8',errors='ignore').read()
        except: continue
        for w in re.findall(r'[A-Za-z_][A-Za-z0-9_]*', txt):
            allref[w]+=1

print(f"Distinct CS0101 duplicate types: {len(cs0101_types)}")
decisions=[]
for t in cs0101_types:
    defs=find_defs(t)
    if len(defs)<=1:
        # only one defining file but CS0101? maybe nested/partial; skip for now
        continue
    # pick keep = largest by lines
    defs_sorted=sorted(defs, key=lambda x:-x[1])
    keep=defs_sorted[0][0]
    remove=[d[0] for d in defs_sorted[1:]]
    decisions.append((t, keep, remove, defs_sorted))
    print(f"\n## {t}  (global refs={allref.get(t,0)})  defs={len(defs)}")
    for rel,ln in defs_sorted:
        tag = "KEEP" if rel==keep else "remove"
        print(f"   [{tag:5s}] {rel}  L{ln}")

# write decisions to file for application
with open(os.path.join(proj,"dup_decisions.txt"),"w",encoding="utf-8") as f:
    for t,keep,remove,defs_sorted in decisions:
        f.write(f"TYPE\t{t}\tKEEP\t{keep}\n")
        for r in remove:
            f.write(f"TYPE\t{t}\tREMOVE\t{r}\n")
print(f"\nWrote {len(decisions)} type decisions to dup_decisions.txt")
