use std::io::{Write, Cursor};

/// 创建资产包（内存安全版本）。
/// 输入: 资产数据数组 (hash, data)
/// 输出: 打包后的字节数组
pub fn create_package(entries: &[(u64, &[u8])], compression: u8) -> Vec<u8> {
    let mut output = Vec::new();
    
    // 1. 写入头部
    let header = super::AssetPackageHeader {
        magic: *b"SHAN",
        version: 1,
        entry_count: entries.len() as u32,
        entry_table_offset: 32, // 头部大小
        compression_type: compression,
    };
    output.extend_from_slice(&header.magic);
    output.extend_from_slice(&header.version.to_le_bytes());
    output.extend_from_slice(&header.entry_count.to_le_bytes());
    output.extend_from_slice(&header.entry_table_offset.to_le_bytes());
    output.extend_from_slice(&header.compression_type.to_le_bytes());
    output.extend_from_slice(&[0u8; 7]); // padding
    
    // 2. 计算条目表偏移
    let entry_table_size = entries.len() * std::mem::size_of::<super::AssetEntry>();
    let data_offset = 32 + entry_table_size;
    
    // 3. 写入条目表 + 数据
    let mut current_offset = data_offset as u64;
    for (hash, data) in entries {
        let compressed = if compression == 1 {
            lz4_compress(data) // 简化版
        } else {
            data.to_vec()
        };
        
        let entry = super::AssetEntry {
            hash: *hash,
            offset: current_offset,
            compressed_size: compressed.len() as u64,
            uncompressed_size: data.len() as u64,
        };
        
        // 写入条目
        output.extend_from_slice(&entry.hash.to_le_bytes());
        output.extend_from_slice(&entry.offset.to_le_bytes());
        output.extend_from_slice(&entry.compressed_size.to_le_bytes());
        output.extend_from_slice(&entry.uncompressed_size.to_le_bytes());
        
        // 数据在后面写入
        current_offset += compressed.len() as u64;
    }
    
    // 4. 写入数据区
    for (_, data) in entries {
        let compressed = if compression == 1 {
            lz4_compress(data)
        } else {
            data.to_vec()
        };
        output.extend_from_slice(&compressed);
    }
    
    output
}

fn lz4_compress(data: &[u8]) -> Vec<u8> {
    // 简化版LZ4：仅做RLE（Run Length Encoding）作为演示
    // 实际生产环境应使用lz4 crate
    let mut result = Vec::new();
    if data.is_empty() { return result; }
    
    let mut i = 0;
    while i < data.len() {
        let literal_start = i;
        while i < data.len() && i - literal_start < 15 {
            i += 1;
        }
        let literal_len = i - literal_start;
        result.push(literal_len as u8);
        result.extend_from_slice(&data[literal_start..i]);
    }
    result
}
