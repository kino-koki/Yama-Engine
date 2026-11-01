import re, os, collections, shutil

ROOT = r"f:/LUCKY_OS/LuckyOS systems 1.0/userspace/other/Yama GamesEngine and Modeling/YamaEngine"
proj = r"f:/LUCKY_OS/LuckyOS systems 1.0/userspace/other/Yama GamesEngine and Modeling"

def read(p): return open(p,encoding='utf-8',errors='ignore').read()

# 1) single walk: index every top-level type declaration -> files
decl_re = re.compile(r'\b(class|struct|interface|enum|record)\s+([A-Za-z_][A-Za-z0-9_]*)\b')
typemap = collections.defaultdict(list)   # name -> list of (relpath)
for dp,_,fs in os.walk(ROOT):
    for fn in fs:
        if not fn.endswith('.cs'): continue
        rel=os.path.relpath(os.path.join(dp,fn),ROOT).replace('\\','/')
        txt=read(os.path.join(ROOT,rel))
        for m in decl_re.finditer(txt):
            typemap[m.group(2)].append(rel)

# 2) CS0101 duplicate types
build=read(os.path.join(proj,"build_out.txt")).split('\n')
cs0101=collections.OrderedDict()
for ln in build:
    m=re.search(r'error CS0101: 命名空间[“"]([^”"]+)[”"]已经包含[“"]([^”"]+)[”"]的定义', ln)
    if m: cs0101.setdefault(m.group(2),0)

def type_span(lines, idx):
    buf='\n'.join(lines[idx:])
    k=buf.find('{')
    if k==-1: return idx, idx
    d=0; inS=inC=esc=False; p=k
    while p<len(buf):
        c=buf[p]
        if esc: esc=False; p+=1; continue
        if c=='\\': esc=True; p+=1; continue
        if c=='"' and not inC: inS=not inS
        elif c=="'" and not inS: inC=not inC
        elif not inS and not inC:
            if c=='{': d+=1
            elif c=='}':
                d-=1
                if d==0:
                    return idx, idx+buf.count('\n',0,p)
        p+=1
    return idx, idx

# 3) choose keep (largest span) per duplicate type; collect file->types to strip
file_strip=collections.defaultdict(set)
keep_map={}
for t in cs0101:
    defs=typemap.get(t)
    if not defs or len(defs)<=1: continue
    best=None; bestspan=-1
    for f in defs:
        lines=read(os.path.join(ROOT,f)).split('\n')
        found=None
        for i,l in enumerate(lines):
            if re.search(r'\b(class|struct|interface|enum|record)\s+'+re.escape(t)+r'\b', l):
                s,e=type_span(lines,i); found=(e-s); break
        if found is not None and found>bestspan:
            bestspan=found; best=f
    keep_map[t]=best
    for f in defs:
        if f!=best: file_strip[f].add(t)

# 4) strip duplicate type blocks from each file
BACKUP=os.path.join(proj,"_dup_backup")
os.makedirs(BACKUP, exist_ok=True)
total=0; report=[]
for f,types in file_strip.items():
    path=os.path.join(ROOT,f)
    lines=read(path).split('\n')
    ranges=[]
    for t in types:
        i=0
        while i<len(lines):
            if re.search(r'\b(class|struct|interface|enum|record)\s+'+re.escape(t)+r'\b', lines[i]):
                st=lines[i].lstrip()
                if st.startswith('//') or st.startswith('*'):
                    i+=1; continue
                s,e=type_span(lines,i)
                if e>s: ranges.append((s,e))
                i=e+1
            else:
                i+=1
    if not ranges: continue
    ranges.sort(reverse=True)
    for s,e in ranges:
        del lines[s:e+1]; total+=1
    shutil.copy2(path, os.path.join(BACKUP, f.replace('/','_')+".bak"))
    with open(path,'w',encoding='utf-8') as fo:
        fo.write('\n'.join(lines))
    report.append((f, sorted(types)))
    print(f"stripped {f}: {sorted(types)}", flush=True)

with open(os.path.join(proj,"dup_report.txt"),"w",encoding="utf-8") as fo:
    for t,keep in keep_map.items():
        removes=[f for f,ts in report if t in ts]
        fo.write(f"TYPE {t}: KEEP {keep} | REMOVE-BLOCK {removes}\n")
print(f"DONE total_blocks_removed={total} files={len(report)}", flush=True)
