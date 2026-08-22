//! 山雨引擎 Rust 安全层
//! 
//! 提供内存安全的资产管道和网络序列化核心。
//! 通过 C ABI 导出函数供 C# P/Invoke 调用。

pub mod asset_pipeline;
pub mod network;
pub mod ffi;

// 重新导出C ABI函数
pub use ffi::bridge::*;
