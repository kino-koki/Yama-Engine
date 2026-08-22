import re, os, collections

ROOT = r"f:/LUCKY_OS/LuckyOS systems 1.0/userspace/other/Shanyu GamesEngine and Modeling"
log = open(os.path.join(ROOT, "build_out.txt"), encoding="utf-8", errors="ignore").read().splitlines()

# error line: ...\ShanyuEngine\PATH\file.cs(line,col): error CSxxxx: message
pat = re.compile(r'ShanyuEngine\\(.+?)\((?P<line>\d+),(?P<col>\d+)\): error (?P<code>CS\d+): (?P<msg>.*)')
errors = []
for ln in log:
    m = pat.search(ln)
    if m:
        errors.append((m.group(1).replace('\\','/'), int(m.group('line')), m.group('code'), m.group('msg')))

print("TOTAL error lines:", len(errors))

# per file
perfile = collections.Counter(e[0] for e in errors)
print("\n=== errors per file (top 50) ===")
for f,c in perfile.most_common(50):
    print(f"{c:4d}  {f}")

# CS0101/CS0111: duplicate member within a type. Extract type name from msg
def type_of_msg(msg):
    # CS0101: 类型“T”已经包含一个名为“M”的定义
    # CS0111: 类型“T”已经定义了名为“M”的成员 / 已定义了一个名为“M”的具有相同参数类型的成员
    m = re.search(r'类型[“\"]([^”\"]+)[”\"]', msg)
    return m.group(1) if m else None

# CS0104: 类型“X”在“A”和“B”之间不明确
# CS0102: 类型“T”已经包含“M”的定义 (nested type)

dup_member = collections.defaultdict(set)   # type -> set of files
for f,line,code,msg in errors:
    if code in ("CS0101","CS0111","CS0102"):
        t = type_of_msg(msg)
        if t:
            dup_member[t].add(f)

print("\n=== types with duplicate-member errors (CS0101/0111/0102), #files involved ===")
for t,fs in sorted(dup_member.items(), key=lambda kv:-len(kv[1])):
    print(f"{len(fs):3d}  {t}  ->  {sorted(fs)}")

# CS0104 ambiguous
amb = collections.Counter()
for f,line,code,msg in errors:
    if code=="CS0104":
        m = re.search(r'类型[“\"]([^”\"]+)[”\"]', msg)
        if m: amb[m.group(1)]+=1
print("\n=== CS0104 ambiguous types ===")
for t,c in amb.most_common():
    print(f"{c:3d}  {t}")
