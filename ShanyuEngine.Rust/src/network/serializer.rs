/// 二进制序列化（VarInt + ZigZag编码）。
/// 
/// 内存安全：所有操作检查缓冲区边界，使用 Rust 的切片安全保证。

pub struct BinarySerializer {
    buffer: Vec<u8>,
    position: usize,
}

impl BinarySerializer {
    pub fn new() -> Self {
        Self { buffer: Vec::new(), position: 0 }
    }
    
    pub fn with_capacity(capacity: usize) -> Self {
        Self { buffer: Vec::with_capacity(capacity), position: 0 }
    }
    
    pub fn write_u8(&mut self, value: u8) {
        self.buffer.push(value);
        self.position += 1;
    }
    
    pub fn write_u16(&mut self, value: u16) {
        self.buffer.extend_from_slice(&value.to_le_bytes());
        self.position += 2;
    }
    
    pub fn write_u32(&mut self, value: u32) {
        self.buffer.extend_from_slice(&value.to_le_bytes());
        self.position += 4;
    }
    
    pub fn write_u64(&mut self, value: u64) {
        self.buffer.extend_from_slice(&value.to_le_bytes());
        self.position += 8;
    }
    
    /// VarInt编码（Google Protocol Buffers风格）
    pub fn write_varint(&mut self, value: u64) {
        let mut v = value;
        while v >= 0x80 {
            self.buffer.push((v as u8) | 0x80);
            v >>= 7;
        }
        self.buffer.push(v as u8);
    }
    
    /// ZigZag编码（有符号→无符号，适合VarInt）
    pub fn write_zigzag(&mut self, value: i64) {
        let encoded = ((value << 1) ^ (value >> 63)) as u64;
        self.write_varint(encoded);
    }
    
    pub fn write_f32(&mut self, value: f32) {
        self.buffer.extend_from_slice(&value.to_le_bytes());
        self.position += 4;
    }
    
    pub fn write_f64(&mut self, value: f64) {
        self.buffer.extend_from_slice(&value.to_le_bytes());
        self.position += 8;
    }
    
    pub fn write_bytes(&mut self, data: &[u8]) {
        self.write_varint(data.len() as u64);
        self.buffer.extend_from_slice(data);
    }
    
    pub fn write_string(&mut self, s: &str) {
        self.write_bytes(s.as_bytes());
    }
    
    pub fn into_vec(self) -> Vec<u8> {
        self.buffer
    }
    
    pub fn as_slice(&self) -> &[u8] {
        &self.buffer
    }
}

pub struct BinaryDeserializer<'a> {
    data: &'a [u8],
    position: usize,
}

impl<'a> BinaryDeserializer<'a> {
    pub fn new(data: &'a [u8]) -> Self {
        Self { data, position: 0 }
    }
    
    pub fn read_u8(&mut self) -> Option<u8> {
        if self.position >= self.data.len() { return None; }
        let v = self.data[self.position];
        self.position += 1;
        Some(v)
    }
    
    pub fn read_u32(&mut self) -> Option<u32> {
        if self.position + 4 > self.data.len() { return None; }
        let v = u32::from_le_bytes([
            self.data[self.position],
            self.data[self.position + 1],
            self.data[self.position + 2],
            self.data[self.position + 3],
        ]);
        self.position += 4;
        Some(v)
    }
    
    pub fn read_varint(&mut self) -> Option<u64> {
        let mut result: u64 = 0;
        let mut shift = 0;
        loop {
            if self.position >= self.data.len() { return None; }
            let byte = self.data[self.position];
            self.position += 1;
            result |= ((byte & 0x7F) as u64) << shift;
            if (byte & 0x80) == 0 { break; }
            shift += 7;
            if shift >= 64 { return None; } // 溢出保护
        }
        Some(result)
    }
    
    pub fn read_zigzag(&mut self) -> Option<i64> {
        let encoded = self.read_varint()?;
        let result = ((encoded >> 1) as i64) ^ -((encoded & 1) as i64);
        Some(result)
    }
    
    pub fn read_bytes(&mut self) -> Option<Vec<u8>> {
        let len = self.read_varint()? as usize;
        if self.position + len > self.data.len() { return None; }
        let v = self.data[self.position..self.position + len].to_vec();
        self.position += len;
        Some(v)
    }
    
    pub fn read_string(&mut self) -> Option<String> {
        let bytes = self.read_bytes()?;
        String::from_utf8(bytes).ok()
    }
    
    pub fn position(&self) -> usize { self.position }
    pub fn len(&self) -> usize { self.data.len() }
    pub fn is_empty(&self) -> bool { self.position >= self.data.len() }
}
