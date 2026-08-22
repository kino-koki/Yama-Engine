use std::ffi::{c_char, c_void, CStr, CString};
use std::slice;

/// 创建资产包
/// 
/// # Safety
/// - `entries_data` 必须指向 `entry_count` 个连续的数据指针
/// - `entries_sizes` 必须指向 `entry_count` 个连续的大小值
/// - `entries_hashes` 必须指向 `entry_count` 个连续的哈希值
/// - `out_size` 必须指向有效的 usize 内存
#[no_mangle]
pub unsafe extern "C" fn shanyu_rust_create_package(
    entries_hashes: *const u64,
    entries_data: *const *const u8,
    entries_sizes: *const usize,
    entry_count: usize,
    compression: u8,
    out_size: *mut usize,
) -> *mut u8 {
    let hashes = slice::from_raw_parts(entries_hashes, entry_count);
    let data_ptrs = slice::from_raw_parts(entries_data, entry_count);
    let sizes = slice::from_raw_parts(entries_sizes, entry_count);
    
    let mut entries: Vec<(u64, Vec<u8>)> = Vec::with_capacity(entry_count);
    for i in 0..entry_count {
        let data = slice::from_raw_parts(data_ptrs[i], sizes[i]).to_vec();
        entries.push((hashes[i], data));
    }
    
    let slices: Vec<(u64, &[u8])> = entries.iter()
        .map(|(h, d)| (*h, d.as_slice()))
        .collect();
    
    let package = crate::asset_pipeline::packer::create_package(&slices, compression);
    
    let mut boxed = package.into_boxed_slice();
    *out_size = boxed.len();
    let ptr = boxed.as_mut_ptr();
    Box::into_raw(boxed); // 转移所有权给C#
    ptr
}

/// 释放 Rust 分配的内存
#[no_mangle]
pub unsafe extern "C" fn shanyu_rust_free(ptr: *mut u8, len: usize) {
    if !ptr.is_null() {
        let _ = Box::from_raw(slice::from_raw_parts_mut(ptr, len));
    }
}

/// 序列化 VarInt
#[no_mangle]
pub extern "C" fn shanyu_rust_serialize_varint(
    value: u64,
    out_buffer: *mut u8,
    max_len: usize,
) -> usize {
    let mut serializer = crate::network::serializer::BinarySerializer::new();
    serializer.write_varint(value);
    let data = serializer.into_vec();
    let len = data.len().min(max_len);
    unsafe {
        std::ptr::copy_nonoverlapping(data.as_ptr(), out_buffer, len);
    }
    len
}

/// 反序列化 VarInt
#[no_mangle]
pub extern "C" fn shanyu_rust_deserialize_varint(
    data: *const u8,
    len: usize,
    out_value: *mut u64,
) -> i32 {
    unsafe {
        let slice = std::slice::from_raw_parts(data, len);
        let mut deserializer = crate::network::serializer::BinaryDeserializer::new(slice);
        match deserializer.read_varint() {
            Some(v) => {
                *out_value = v;
                deserializer.position() as i32
            }
            None => -1,
        }
    }
}

/// 计算 FNV-1a 哈希
#[no_mangle]
pub extern "C" fn shanyu_rust_fnv1a_hash(data: *const u8, len: usize) -> u64 {
    use crate::asset_pipeline::hasher;
    unsafe {
        let slice = std::slice::from_raw_parts(data, len);
        hasher::fnv1a_64(slice)
    }
}
