import re, os, collections

ROOT = r"f:/LUCKY_OS/LuckyOS systems 1.0/userspace/other/Shanyu GamesEngine and Modeling/ShanyuEngine"
proj = r"f:/LUCKY_OS/LuckyOS systems 1.0/userspace/other/Shanyu GamesEngine and Modeling"

def read(p):
    return open(p,encoding='utf-8',errors='ignore').read()

def type_span_lines(path, name):
    """Return (start_line, end_line, member_count) of the first top-level def of `name` via brace matching."""
    txt=read(os.path.join(ROOT,path))
    lines=txt.split('\n')
    pat=re.compile(r'\b(class|struct|interface|enum|record)\s+'+re.escape(name)+r'\b')
    for i,l in enumerate(lines):
        m=pat.search(l)
        if not m: continue
        # ensure it's a real declaration (starts at proper place); skip if inside a comment heuristic
        # find brace open on this or following lines
        depth=0; started=False; end=i; j=i
        # accumulate from first '{'
        buf='\n'.join(lines[i:])
        # find first '{'
        k=buf.find('{')
        if k==-1:
            # enum or type with no body? enum always has {. Could be ';' (forward) - rare
            return (i+1, i+1, 0)
        # brace match
        d=0; inStr=False; inCh=False; esc=False
        p=k
        while p<len(buf):
            c=buf[p]
            if esc:
                esc=False; p+=1; continue
            if c=='\\': esc=True; p+=1; continue
            if c=='"' and not inCh: inStr=not inStr
            elif c=="'" and not inCh: inCh=not inCh
            elif not inStr and not inCh:
                if c=='{': d+=1
                elif c=='}':
                    d-=1
                    if d==0:
                        end=i+buf.count('\n',0,p)+1
                        break
            p+=1
        # count member-ish lines: lines with '(' or ';' or '=>' inside span excluding braces lines
        span=lines[i:end]
        mem=sum(1 for x in span if re.search(r'\(.*\)\s*{|=>|;\s*$', x.strip()) and not x.strip().startswith('//'))
        return (i+1, end, mem)
    return None

# Build duplicate map from CS0101
build=read(os.path.join(proj,"build_out.txt")).split('\n')
cs0101_types=collections.OrderedDict()
for ln in build:
    m=re.search(r'error CS0101: 命名空间[“"]([^”"]+)[”"]已经包含[“"]([^”"]+)[”"]的定义', ln)
    if m:
        cs0101_types.setdefault(m.group(2), set()).add(m.group(1))

# find defs
def find_defs(name):
    out=[]
    for dp,_,fs in os.walk(ROOT):
        for fn in fs:
            if not fn.endswith('.cs'): continue
            p=os.path.join(dp,fn)
            txt=read(p)
            if re.search(r'\b(class|struct|interface|enum|record)\s+'+re.escape(name)+r'\b', txt):
                out.append(os.path.relpath(p,ROOT).replace('\\','/'))
    return out

decisions=[]
for t in cs0101_types:
    defs=find_defs(t)
    if len(defs)<=1: continue
    scored=[]
    for f in defs:
        sp=type_span_lines(f,t)
        scored.append((f, sp[1]-sp[0] if sp else 0, sp[2] if sp else 0))
    scored.sort(key=lambda x:-(x[1]))
    keep=scored[0][0]
    decisions.append((t,keep,[s[0] for s in scored[1:]],scored))
    print(f"## {t}  defs={len(defs)}")
    for f,span,mem in scored:
        print(f"   [{'KEEP' if f==keep else 'rem '}] {f}  span={span}L mem≈{mem}")

with open(os.path.join(proj,"dup_decisions.txt"),"w",encoding="utf-8") as fo:
    for t,keep,remove,scored in decisions:
        fo.write(f"TYPE\t{t}\tKEEP\t{keep}\n")
        for r in remove:
            fo.write(f"TYPE\t{t}\tREMOVE\t{r}\n")
print(f"\n{len(decisions)} decisions written")
