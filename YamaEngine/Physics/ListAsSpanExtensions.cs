using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace YamaEngine.Physics
{
    // ============================================================================================
    //  ListAsSpanExtensions.cs
    //
    //  net8 的 BCL 并没有给 List<T> 提供 AsSpan 扩展方法（BCL 只有 string/数组/Memory 的
    //  AsSpan），导致网上常见的 list.AsSpan() 写法编译不过。这里补一个，直接用
    //  CollectionsMarshal 拿到 List 内部数组的 Span，读写直达底层存储，窄相位/物理世界大量
    //  用到索引器/ref 语义的代码就不用改了。
    //
    //  注意：返回的 Span 的生命期内不要增删 List 元素，否则内部数组换了位置会有未定义行为。
    // ============================================================================================

    /// <summary>
    /// List&lt;T&gt; 的 AsSpan 扩展。把 List 当作 Span 访问（读写直达内部数组）。
    /// </summary>
    internal static class ListAsSpanExtensions
    {
        /// <summary>把 List 直接当作 Span 访问（需确保使用期内不增删元素）。</summary>
        public static Span<T> AsSpan<T>(this List<T> list)
        {
            return CollectionsMarshal.AsSpan(list);
        }
    }
}