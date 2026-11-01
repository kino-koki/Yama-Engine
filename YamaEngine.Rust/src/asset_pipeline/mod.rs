pub mod packer;
pub mod unpacker;
pub mod hasher;

// 资产条目
#[repr(C)]
pub struct AssetEntry {
    pub hash: u64,
    pub offset: u64,
    pub compressed_size: u64,
    pub uncompressed_size: u64,
}

// 资产包头部
#[repr(C)]
pub struct AssetPackageHeader {
    pub magic: [u8; 4],      // 'S' 'H' 'A' 'N'
    pub version: u32,
    pub entry_count: u32,
    pub entry_table_offset: u64,
    pub compression_type: u8, // 0=None, 1=LZ4, 2=LZMA
}
