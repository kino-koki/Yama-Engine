use super::{PacketHeader, NetworkError, crc};
use super::serializer::{BinarySerializer, BinaryDeserializer};

/// 构建网络数据包。
/// 
/// 内存安全：自动计算CRC32校验和，验证头部和载荷边界。
pub fn build_packet(packet_type: u16, sequence_id: u32, payload: &[u8]) -> Vec<u8> {
    let mut serializer = BinarySerializer::with_capacity(12 + payload.len() + 4);
    
    // 计算校验和（头部+载荷）
    let mut header_serializer = BinarySerializer::with_capacity(12);
    header_serializer.write_u16(0x5348); // magic 'SH'
    header_serializer.write_u16(packet_type);
    header_serializer.write_u32(sequence_id);
    header_serializer.write_u32(payload.len() as u32);
    let header_bytes = header_serializer.into_vec();
    
    let checksum = crc::crc32_combine(&header_bytes, payload);
    
    // 写入完整头部（含校验和）
    serializer.write_u16(0x5348);
    serializer.write_u16(packet_type);
    serializer.write_u32(sequence_id);
    serializer.write_u32(payload.len() as u32);
    serializer.write_u32(checksum);
    
    // 写入载荷
    serializer.buffer.extend_from_slice(payload);
    
    serializer.into_vec()
}

/// 解析网络数据包。
/// 
/// 返回 (packet_type, sequence_id, payload) 或错误码。
pub fn parse_packet(data: &[u8]) -> Result<(u16, u32, Vec<u8>), NetworkError> {
    if data.len() < 16 {
        return Err(NetworkError::InvalidHeader);
    }
    
    let mut deserializer = BinaryDeserializer::new(data);
    
    let magic = deserializer.read_u16().ok_or(NetworkError::InvalidHeader)?;
    if magic != 0x5348 {
        return Err(NetworkError::InvalidHeader);
    }
    
    let packet_type = deserializer.read_u16().ok_or(NetworkError::InvalidHeader)?;
    let sequence_id = deserializer.read_u32().ok_or(NetworkError::InvalidHeader)?;
    let payload_len = deserializer.read_u32().ok_or(NetworkError::InvalidHeader)?;
    let checksum = deserializer.read_u32().ok_or(NetworkError::InvalidHeader)?;
    
    if payload_len as usize > data.len() - 16 {
        return Err(NetworkError::PayloadTooLarge);
    }
    
    let payload = &data[16..16 + payload_len as usize];
    
    // 验证校验和
    let header = &data[0..12];
    let expected_checksum = crc::crc32_combine(header, payload);
    if checksum != expected_checksum {
        return Err(NetworkError::ChecksumMismatch);
    }
    
    Ok((packet_type, sequence_id, payload.to_vec()))
}
