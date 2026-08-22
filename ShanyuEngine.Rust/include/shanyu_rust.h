/**
 * @file shanyu_rust.h
 * @brief 山雨引擎 Rust 安全层 C 头文件
 *
 * 提供资产管道和网络序列化的 C ABI 导出函数声明。
 * 供 C# P/Invoke 及其他 C 语言调用方使用。
 */

#ifndef SHANYU_RUST_H
#define SHANYU_RUST_H

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ==========================================================================
 * 资产管道 (Asset Pipeline)
 * ========================================================================== */

/**
 * 创建资产包。
 *
 * @param entries_hashes   资产哈希值数组（长度 entry_count）
 * @param entries_data     资产数据指针数组（长度 entry_count）
 * @param entries_sizes    资产数据大小数组（长度 entry_count）
 * @param entry_count      资产条目数量
 * @param compression      压缩类型（0=无, 1=LZ4, 2=LZMA）
 * @param out_size         输出：包的大小（字节）
 * @return 指向包数据的指针，调用方必须使用 shanyu_rust_free 释放
 */
uint8_t* shanyu_rust_create_package(
    const uint64_t* entries_hashes,
    const uint8_t* const* entries_data,
    const size_t* entries_sizes,
    size_t entry_count,
    uint8_t compression,
    size_t* out_size);

/**
 * 从资产包中提取单个条目。
 *
 * @param package_data     资产包数据指针
 * @param package_len      资产包数据长度
 * @param entry_hash       要提取的条目哈希
 * @param out_size         输出：提取数据的大小（字节）
 * @return 指向提取数据的指针，调用方必须使用 shanyu_rust_free 释放
 */
uint8_t* shanyu_rust_unpack_entry(
    const uint8_t* package_data,
    size_t package_len,
    uint64_t entry_hash,
    size_t* out_size);

/**
 * 释放 Rust 分配的内存。
 *
 * @param ptr  由 Rust 分配的内存指针
 * @param len  内存块长度（字节）
 */
void shanyu_rust_free(uint8_t* ptr, size_t len);

/**
 * 计算 FNV-1a 64 位哈希。
 *
 * @param data 输入数据指针
 * @param len  输入数据长度
 * @return 64 位哈希值
 */
uint64_t shanyu_rust_fnv1a_hash(const uint8_t* data, size_t len);

/* ==========================================================================
 * 网络序列化 (Network Serialization)
 * ========================================================================== */

/**
 * 序列化 VarInt（Google Protocol Buffers 风格）。
 *
 * @param value     要序列化的无符号 64 位整数
 * @param out_buffer 输出缓冲区指针
 * @param max_len    输出缓冲区最大长度
 * @return 实际写入的字节数
 */
size_t shanyu_rust_serialize_varint(
    uint64_t value,
    uint8_t* out_buffer,
    size_t max_len);

/**
 * 反序列化 VarInt。
 *
 * @param data       输入数据指针
 * @param len        输入数据长度
 * @param out_value  输出：反序列化后的值
 * @return 成功时返回消耗的字节数，失败返回 -1
 */
int32_t shanyu_rust_deserialize_varint(
    const uint8_t* data,
    size_t len,
    uint64_t* out_value);

#ifdef __cplusplus
}
#endif

#endif /* SHANYU_RUST_H */
