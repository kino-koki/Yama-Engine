/// CRC32 计算（IEEE 802.3 标准多项式）。
/// 
/// 纯Rust实现，无查表，适合小型嵌入式场景。
/// 生产环境可启用查表优化。

const CRC32_POLYNOMIAL: u32 = 0xEDB88320;

/// 计算单字节的CRC32步进。
fn crc32_step(crc: u32, byte: u8) -> u32 {
    let mut crc = crc ^ (byte as u32);
    for _ in 0..8 {
        if crc & 1 != 0 {
            crc = (crc >> 1) ^ CRC32_POLYNOMIAL;
        } else {
            crc >>= 1;
        }
    }
    crc
}

/// 计算数据块的CRC32。
pub fn crc32(data: &[u8]) -> u32 {
    let mut crc: u32 = 0xFFFFFFFF;
    for byte in data {
        crc = crc32_step(crc, *byte);
    }
    crc ^ 0xFFFFFFFF
}

/// 组合计算两个数据块的CRC32（header + payload）。
pub fn crc32_combine(header: &[u8], payload: &[u8]) -> u32 {
    let mut crc: u32 = 0xFFFFFFFF;
    for byte in header {
        crc = crc32_step(crc, *byte);
    }
    for byte in payload {
        crc = crc32_step(crc, *byte);
    }
    crc ^ 0xFFFFFFFF
}

/// 查表版CRC32初始化（如需高性能可调用）。
#[allow(dead_code)]
pub fn crc32_table() -> [u32; 256] {
    let mut table = [0u32; 256];
    for i in 0..256 {
        let mut crc = i as u32;
        for _ in 0..8 {
            if crc & 1 != 0 {
                crc = (crc >> 1) ^ CRC32_POLYNOMIAL;
            } else {
                crc >>= 1;
            }
        }
        table[i] = crc;
    }
    table
}
