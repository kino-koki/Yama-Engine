import re, os, collections

ROOT = r"f:/LUCKY_OS/LuckyOS systems 1.0/userspace/other/Yama GamesEngine and Modeling/YamaEngine"
files = []
for dp,_,fs in os.walk(ROOT):
    for fn in fs:
        if fn.endswith('.cs'):
            files.append(os.path.join(dp,fn).replace('\\','/'))

# type declaration regex (top-level and nested). Capture kind and name (with generic arity base)
decl_re = re.compile(r'\b(class|struct|interface|enum|record)\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?:<[^>]*>)?')
# namespace
def namespaces_of(path):
    txt=open(path,encoding='utf-8',errors='ignore').read()
    # crude: find namespace blocks
    ns=set()
    for m in re.finditer(r'\bnamespace\s+([A-Za-z0-9_.]+)', txt):
        ns.add(m.group(1))
    return ns or {'<global>'}

type_files = collections.defaultdict(list)
for path in files:
    try:
        txt=open(path,encoding='utf-8',errors='ignore').read()
    except: continue
    for m in decl_re.finditer(txt):
        name=m.group(2)
        type_files[name].append(path.replace(ROOT+'/',''))

# reference count: occurrences of name in other files (whole word)
refcount=collections.Counter()
for path in files:
    try:
        txt=open(path,encoding='utf-8',errors='ignore').read()
    except: continue
    # count word occurrences
    for w in re.findall(r'[A-Za-z_][A-Za-z0-9_]*', txt):
        refcount[w]+=1

print("=== DUPLICATE TYPE DEFINITIONS (defined in >1 file) ===")
dups={t:fs for t,fs in type_files.items() if len(fs)>1}
for t in sorted(dups, key=lambda x:-len(dups[x])):
    fs=dups[t]
    info=[]
    for f in fs:
        lines=sum(1 for _ in open(os.path.join(ROOT,f),encoding='utf-8',errors='ignore'))
        r=refcount.get(t,0)
        info.append(f"{f}[L{lines},ref{r}]")
    print(f"\n## {t}  (refs={refcount.get(t,0)})  files={len(fs)}")
    for s in info: print("   ", s)
print(f"\nTOTAL duplicate types: {len(dups)}")
