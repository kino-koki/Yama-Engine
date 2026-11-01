use std::slice;

/// 从资产包中提取单个条目。
/// 
/// # Safety
/// - `package_data` 必须指向有效的资产包数据
/// - `package_len` 必须是有效长度
/// - `out_size` 必须指向有效的 usize 内存
#[no_mangle]
pub unsafe extern "C" fn yama_rust_unpack_entry(
    package_data: *const u8,
    package_len: usize,
    entry_hash: u64,
    out_size: *mut usize,
) -> *mut u8 {
    let data = slice::from_raw_parts(package_data, package_len);
    
    // 验证头部
    if data.len() < 32 {
        return std::ptr::null_mut();
    }
    
    if &data[0..4] != b"SHAN" {
        return std::ptr::null_mut();
    }
    
    let entry_count = u32::from_le_bytes([data[4], data[5], data[6], data[7]]) as usize;
    let entry_table_offset = u32::from_le_bytes([data[8], data[9], data[10], data[11]]) as usize;
    let compression_type = data[12];
    
    // 读取条目表
    let entry_size = std::mem::size_of::<super::AssetEntry>();
    for i in 0..entry_count {
        let offset = entry_table_offset + i * entry_size;
        if offset + entry_size > data.len() {
            return std::ptr::null_mut();
        }
        
        let hash = u64::from_le_bytes([
            data[offset], data[offset + 1], data[offset + 2], data[offset + 3],
            data[offset + 4], data[offset + 5], data[offset + 6], data[offset + 7],
        ]);
        
        if hash == entry_hash {
            let data_offset = u64::from_le_bytes([
                data[offset + 8], data[offset + 9], data[offset + 10], data[offset + 11],
                data[offset + 12], data[offset + 13], data[offset + 14], data[offset + 15],
            ]) as usize;
            let compressed_size = u64::from_le_bytes([
                data[offset + 16], data[offset + 17], data[offset + 18], data[offset + 19],
                data[offset + 20], data[offset + 21], data[offset + 22], data[offset + 23],
            ]) as usize;
            let uncompressed_size = u64::from_le_bytes([
                data[offset + 24], data[offset + 25], data[offset + 26], data[offset + 27],
                data[offset + 28], data[offset + 29], data[offset + 30], data[offset + 31],
            ]) as usize;
            
            if data_offset + compressed_size > data.len() {
                return std::ptr::null_mut();
            }
            
            let compressed = &data[data_offset..data_offset + compressed_size];
            
            let result = if compression_type == 1 {
                lz4_decompress(compressed, uncompressed_size)
            } else {
                compressed.to_vec()
            };
            
            let mut boxed = result.into_boxed_slice();
            *out_size = boxed.len();
            let ptr = boxed.as_mut_ptr();
            Box::into_raw(boxed);
            return ptr;
        }
    }
    
    std::ptr::null_mut()
}

fn lz4_decompress(data: &[u8], expected_len: usize) -> Vec<u8> {
    // 简化版LZ4解压（对应packer中的简化压缩）
    let mut result = Vec::with_capacity(expected_len);
    let mut i = 0;
    while i < data.len() && result.len() < expected_len {
        let literal_len = data[i] as usize;
        i += 1;
        if i + literal_len > data.len() {
            break;
        }
        result.extend_from_slice(&data[i..i + literal_len]);
        i += literal_len;
    }
    result.truncate(expected_len);
    result
}
