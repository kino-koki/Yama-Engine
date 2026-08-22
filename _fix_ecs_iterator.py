# ComponentChunkIterator.cs: 用自定义委托替换 Action<..., Span<T>>（CS9244）
import os, re

base = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'ShanyuEngine', 'ECS')
p = os.path.join(base, 'ComponentChunkIterator.cs')
text = open(p, encoding='utf-8').read()

delegates = '''
// ──────────────────────────────────────────────────────────────────────────────
// 区块迭代委托（Span<T> 不能作为 Action<> 的泛型参数，故使用自定义委托）
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>单组件迭代委托。</summary>
public delegate void ChunkAction<T1>(ArchetypeChunk chunk, Span<T1> c1);
/// <summary>双组件迭代委托。</summary>
public delegate void ChunkAction<T1, T2>(ArchetypeChunk chunk, Span<T1> c1, Span<T2> c2);
/// <summary>三组件迭代委托。</summary>
public delegate void ChunkAction<T1, T2, T3>(ArchetypeChunk chunk, Span<T1> c1, Span<T2> c2, Span<T3> c3);
/// <summary>四组件迭代委托。</summary>
public delegate void ChunkAction<T1, T2, T3, T4>(ArchetypeChunk chunk, Span<T1> c1, Span<T2> c2, Span<T3> c3, Span<T4> c4);
/// <summary>实体+单组件迭代委托。</summary>
public delegate void ChunkEntityAction<T1>(ArchetypeChunk chunk, Span<EntityReal> entities, Span<T1> c1);
/// <summary>实体+双组件迭代委托。</summary>
public delegate void ChunkEntityAction<T1, T2>(ArchetypeChunk chunk, Span<EntityReal> entities, Span<T1> c1, Span<T2> c2);
/// <summary>实体+三组件迭代委托。</summary>
public delegate void ChunkEntityAction<T1, T2, T3>(ArchetypeChunk chunk, Span<EntityReal> entities, Span<T1> c1, Span<T2> c2, Span<T3> c3);
/// <summary>带索引区间的组件迭代委托。</summary>
public delegate void ChunkIndexAction<T1>(ArchetypeChunk chunk, int start, int count, Span<T1> c1);

'''

# 插入到命名空间声明之后（找第一个 namespace 行后的第一个类型声明之前）
m = re.search(r'^namespace .+$', text, flags=re.M)
assert m, 'namespace not found'
insert_at = text.index('\n', m.end()) + 1
text = text[:insert_at] + delegates + text[insert_at:]

replacements = [
    (r'Action<ArchetypeChunk, Span<EntityReal>, Span<T1>, Span<T2>, Span<T3>>', 'ChunkEntityAction<T1, T2, T3>'),
    (r'Action<ArchetypeChunk, Span<EntityReal>, Span<T1>, Span<T2>>', 'ChunkEntityAction<T1, T2>'),
    (r'Action<ArchetypeChunk, Span<EntityReal>, Span<T1>>', 'ChunkEntityAction<T1>'),
    (r'Action<ArchetypeChunk, Span<T1>, Span<T2>, Span<T3>, Span<T4>>', 'ChunkAction<T1, T2, T3, T4>'),
    (r'Action<ArchetypeChunk, Span<T1>, Span<T2>, Span<T3>>', 'ChunkAction<T1, T2, T3>'),
    (r'Action<ArchetypeChunk, Span<T1>, Span<T2>>', 'ChunkAction<T1, T2>'),
    (r'Action<ArchetypeChunk, Span<T1>>', 'ChunkAction<T1>'),
    (r'Action<ArchetypeChunk, int, int, Span<T1>>', 'ChunkIndexAction<T1>'),
]
for pat, rep in replacements:
    text = re.sub(pat, lambda m: rep, text)

open(p, 'w', encoding='utf-8', newline='').write(text)
print('ComponentChunkIterator.cs done')
