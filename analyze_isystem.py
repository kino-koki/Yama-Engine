import re,os,collections
ROOT="."
impl=[]
for dp,_,fs in os.walk(ROOT):
    for fn in fs:
        if not fn.endswith('.cs'): continue
        p=os.path.join(dp,fn)
        t=open(p,encoding='utf-8',errors='ignore').read()
        for m in re.finditer(r'class\s+([A-Za-z_]\w*)[^{]*?:\s*[^ {]*ISystem\b', t, re.S):
            impl.append((p,m.group(1)))
print("ISystem implementers:",len(impl))
cnt=collections.Counter()
two_param=[]
for p,cls in impl:
    t=open(p,encoding='utf-8',errors='ignore').read()
    ups=re.findall(r'(?:public|internal|protected|private|override|sealed\s+override|virtual)?\s*(?:override\s+)?(?:void|int|bool|float|double|string|void)\s+Update\s*\(([^)]*)\)', t)
    # classify
    has_two=False
    for u in ups:
        s=u.strip()
        if ',' in s:   # has comma => 2+ params
            has_two=True
            two_param.append((cls,os.path.relpath(p,ROOT),s))
    cnt['2param' if has_two else '1param(approx)']+=1
print("distribution:",dict(cnt))
print("classes with 2-param Update:")
for c,f,s in two_param:
    print(f"  {c} [{f}] Update({s})")
# Check SystemGroup.Update call
ecs=open(os.path.join(ROOT,"ECS/EntityComponentSystem.cs"),encoding='utf-8',errors='ignore').read()
for m in re.finditer(r'system\.Update\([^)]*\)', ecs):
    print("SystemGroup call:",m.group(0))
PY