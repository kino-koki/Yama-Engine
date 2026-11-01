/// FNV-1a 64位哈希
pub fn fnv1a_64(data: &[u8]) -> u64 {
    const FNV_OFFSET_BASIS: u64 = 0xcbf29ce484222325;
    const FNV_PRIME: u64 = 0x100000001b3;
    
    let mut hash = FNV_OFFSET_BASIS;
    for byte in data {
        hash ^= *byte as u64;
        hash = hash.wrapping_mul(FNV_PRIME);
    }
    hash
}

/// xxHash 32位（简化版，基于FNV的变体）
pub fn xxhash32(data: &[u8], seed: u32) -> u32 {
    // 简化实现，基于标准哈希组合
    const PRIME32_1: u32 = 0x9E3779B1;
    const PRIME32_2: u32 = 0x85EBCA77;
    const PRIME32_3: u32 = 0xC2B2AE3D;
    const PRIME32_4: u32 = 0x27D4EB2F;
    const PRIME32_5: u32 = 0x165667B1;

    let mut h32 = seed.wrapping_add(PRIME32_5);

    for (i, &byte) in data.iter().enumerate() {
        h32 ^= (byte as u32).wrapping_mul(PRIME32_3);
        h32 = h32.rotate_left(13);
        h32 = h32.wrapping_mul(PRIME32_1);
        h32 ^= (i as u32).wrapping_mul(PRIME32_2);
    }

    h32 ^= data.len() as u32;
    h32 ^= h32 >> 15;
    h32 = h32.wrapping_mul(PRIME32_2);
    h32 ^= h32 >> 13;
    h32 = h32.wrapping_mul(PRIME32_3);
    h32 ^= h32 >> 16;

    h32
}

/// xxHash 64位（简化版）
pub fn xxhash64(data: &[u8], seed: u64) -> u64 {
    const PRIME64_1: u64 = 0x9E3779B185EBCA87;
    const PRIME64_2: u64 = 0xC2B2AE3D27D4EB4F;
    const PRIME64_3: u64 = 0x165667B19E3779F9;
    const PRIME64_4: u64 = 0x85EBCA77C2B2AE63;
    const PRIME64_5: u64 = 0x27D4EB2F165667C5;

    let mut h64 = seed.wrapping_add(PRIME64_5);

    for (i, &byte) in data.iter().enumerate() {
        h64 ^= (byte as u64).wrapping_mul(PRIME64_3);
        h64 = h64.rotate_left(31);
        h64 = h64.wrapping_mul(PRIME64_1);
        h64 ^= (i as u64).wrapping_mul(PRIME64_2);
    }

    h64 ^= data.len() as u64;
    h64 ^= h64 >> 33;
    h64 = h64.wrapping_mul(PRIME64_2);
    h64 ^= h64 >> 29;
    h64 = h64.wrapping_mul(PRIME64_3);
    h64 ^= h64 >> 32;

    h64
}
