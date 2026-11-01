using System.Runtime.InteropServices;

namespace YamaEngine.NativeInterop
{
    /// <summary>
    /// Rust 安全层 P/Invoke 封装。
    /// </summary>
    public static class RustInterop
    {
        private const string DllName = "yama_engine_rust";
        
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr yama_rust_create_package(
            ulong[] entriesHashes,
            IntPtr[] entriesData,
            ulong[] entriesSizes,
            ulong entryCount,
            byte compression,
            out ulong outSize);
        
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void yama_rust_free(IntPtr ptr, ulong len);
        
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int yama_rust_serialize_varint(
            ulong value, byte[] outBuffer, ulong maxLen);
        
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int yama_rust_deserialize_varint(
            byte[] data, ulong len, out ulong outValue);
        
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ulong yama_rust_fnv1a_hash(byte[] data, ulong len);
        
        /// <summary>
        /// 使用 Rust 计算 FNV-1a 哈希（带C#回退）。
        /// </summary>
        public static ulong HashFNV1a(byte[] data)
        {
            try
            {
                return yama_rust_fnv1a_hash(data, (ulong)data.Length);
            }
            catch (DllNotFoundException)
            {
                // C# 回退
                return HashFNV1aFallback(data);
            }
        }
        
        private static ulong HashFNV1aFallback(byte[] data)
        {
            const ulong FNV_OFFSET_BASIS = 0xcbf29ce484222325;
            const ulong FNV_PRIME = 0x100000001b3;
            ulong hash = FNV_OFFSET_BASIS;
            foreach (var b in data)
            {
                hash ^= b;
                hash *= FNV_PRIME;
            }
            return hash;
        }
    }
}
