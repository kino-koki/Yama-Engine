import re, os, shutil

ROOT=r"f:/LUCKY_OS/LuckyOS systems 1.0/userspace/other/Shanyu GamesEngine and Modeling/ShanyuEngine"
BACKUP=os.path.join(r"f:/LUCKY_OS/LuckyOS systems 1.0/userspace/other/Shanyu GamesEngine and Modeling","_dup_backup")
os.makedirs(BACKUP, exist_ok=True)

def find_class_ranges(txt):
    """Return list of (class_start_idx, class_end_idx, class_name) for classes implementing IEditorCommand."""
    res=[]
    for m in re.finditer(r'class\s+([A-Za-z_]\w*)\s*(?:<[^>]*>)?\s*:[^;{]*IEditorCommand\b', txt):
        # find the class opening brace
        i=txt.find('{', m.start())
        if i==-1: continue
        d=0; j=i
        while j<len(txt):
            c=txt[j]
            if c=='{': d+=1
            elif c=='}':
                d-=1
                if d==0: break
            j+=1
        res.append((m.start(), j, m.group(1)))
    return res

def has_member(body, pat):
    return re.search(pat, body) is not None

added_total=0
for dp,_,fs in os.walk(ROOT):
    for fn in fs:
        if not fn.endswith('.cs'): continue
        p=os.path.join(dp,fn)
        txt=open(p,encoding='utf-8',errors='ignore').read()
        if 'IEditorCommand' not in txt: continue
        ranges=find_class_ranges(txt)
        if not ranges: continue
        shutil.copy2(p, os.path.join(BACKUP, os.path.relpath(p,ROOT).replace('\\','_').replace('/','_')+".ied.bak"))
        # process from last to first to preserve indices
        ranges.sort(key=lambda x:-x[1])
        for start,end,name in ranges:
            body=txt[start:end]
            additions=[]
            if not has_member(body, r'string\s+Name\s*[{=>]'):
                if has_member(body, r'string\s+Description\s*[{=>]'):
                    additions.append('        public string Name => Description;')
                else:
                    additions.append('        public string Name => GetType().Name;')
            if not has_member(body, r'CanMergeWith\s*\('):
                additions.append('        public bool CanMergeWith(IEditorCommand next) => false;')
            if not has_member(body, r'void\s+Execute\s*\(\s*EditorWorkspace'):
                additions.append('        public void Execute(EditorWorkspace workspace) => Execute();')
            if not has_member(body, r'void\s+Undo\s*\(\s*EditorWorkspace'):
                additions.append('        public void Undo(EditorWorkspace workspace) => Undo();')
            if additions:
                ins = "\n".join(additions)
                # insert before the class closing brace (end points at '}')
                txt = txt[:end] + "\n" + ins + "\n" + txt[end:]
                added_total+=len(additions)
                print(f"{os.path.relpath(p,ROOT)} :: {name} added {len(additions)} members", flush=True)
        open(p,'w',encoding='utf-8').write(txt)
print(f"DONE added {added_total} members", flush=True)
