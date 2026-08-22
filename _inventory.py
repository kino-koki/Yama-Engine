#!/usr/bin/env python3
import re, os, collections

ROOT = r"F:\LUCKY_OS\LuckyOS systems 1.0\userspace\other\Shanyu GamesEngine and Modeling"
os.chdir(ROOT)

# Run build and capture errors
import subprocess
env = dict(os.environ)
env["DOTNET"] = r"C:\Users\28726\.dotnet\dotnet.exe"
res = subprocess.run(
    [env["DOTNET"], "build", "ShanyuEngine/ShanyuEngine.csproj", "-v", "q", "--nologo"],
    capture_output=True, text=True, env=env)
lines = (res.stdout + res.stderr).splitlines()

err_re = re.compile(r"^(?P<path>[^(]+)\((?P<line>\d+),\d+\):\s*error\s+(?P<code>CS\d+):\s*(?P<msg>.*)$")
errs = []
for ln in lines:
    m = err_re.match(ln.strip())
    if m:
        errs.append(m.groupdict())

print(f"TOTAL ERROR OCCURRENCES: {len(errs)}")

# Unique (path, code, msg-without-specific-id)
# For CS0246 extract missing type name
def key_of(e):
    if e["code"] == "CS0246":
        mm = re.search(r"类型或命名空间名“([^”]+)”", e["msg"])
        if mm: return ("CS0246", mm.group(1))
    if e["code"] in ("CS0111","CS0101","CS0102"):
        mm = re.search(r"“([^”]+)”", e["msg"])
        if mm: return (e["code"], mm.group(1))
    return (e["code"], e["msg"][:60])

uniq = collections.Counter()
for e in errs:
    uniq[key_of(e)] += 1

print("\n=== UNIQUE (code, target) counts ===")
for (code, tgt), c in uniq.most_common(60):
    print(f"{c:4d}  {code}  {tgt}")

# File distribution
byfile = collections.Counter(e["path"] for e in errs)
print("\n=== TOP FILES BY ERROR COUNT ===")
for f, c in byfile.most_common(30):
    print(f"{c:4d}  {f}")
