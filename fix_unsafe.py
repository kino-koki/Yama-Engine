import re, os, shutil
ROOT=r"f:/LUCKY_OS/LuckyOS systems 1.0/userspace/other/Shanyu GamesEngine and Modeling/ShanyuEngine"
files=[
 "Vulkan/VulkanInterop.cs",
 "Platform/SDL2Window.cs",
 "Vulkan/VulkanMemoryAllocator.cs",
 "Rendering/MeshShaderRenderer.cs",
]
BACKUP=os.path.join(r"f:/LUCKY_OS/LuckyOS systems 1.0/userspace/other/Shanyu GamesEngine and Modeling","_dup_backup")
os.makedirs(BACKUP, exist_ok=True)
# declaration regex: optional modifiers then class/struct/interface/enum/record
decl=re.compile(r'^(?P<indent>\s*)(?P<mods>(?:(?:public|internal|protected|private|abstract|sealed|static|partial|unsafe|file|ref|readonly)\s+)*)(?P<kw>class|struct|interface|enum|record)\b')
for f in files:
    p=os.path.join(ROOT,f)
    lines=open(p,encoding='utf-8',errors='ignore').read().split('\n')
    out=[]
    for ln in lines:
        m=decl.match(ln)
        if m and m.group('kw') in ('class','struct') and 'unsafe' not in m.group('mods'):
            # insert unsafe after indent
            new=ln[:len(m.group('indent'))]+'unsafe '+ln[len(m.group('indent')):]
            out.append(new)
        else:
            out.append(ln)
    shutil.copy2(p, os.path.join(BACKUP, f.replace('/','_')+".unsafe.bak"))
    open(p,'w',encoding='utf-8').write('\n'.join(out))
    changed=sum(1 for a,b in zip(lines,out) if a!=b)
    print(f"unsafe added in {f}: {changed} declarations changed", flush=True)
print("DONE unsafe", flush=True)
