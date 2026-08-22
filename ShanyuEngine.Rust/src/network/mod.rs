pub mod serializer;
pub mod packet;
pub mod crc;

// 网络包头部
#[repr(C)]
pub struct PacketHeader {
    pub magic: u16,       // 0x5348 ('S' 'H')
    pub packet_type: u16,
    pub sequence_id: u32,
    pub payload_len: u32,
    pub checksum: u32,
}

// 网络错误码
#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum NetworkError {
    Ok = 0,
    InvalidHeader = 1,
    ChecksumMismatch = 2,
    PayloadTooLarge = 3,
    DeserializeFailed = 4,
    BufferOverflow = 5,
}
