//! Yama 真实物理：唯一允许的 Rust 数值热点。
//! 边界极窄：只做密度遍历（纯数值、无分配、无全局状态），通过 C ABI 单向传递。
//! C# 侧负责编排、确定性顺序、守恒账本与全部可回放状态。
//! 与 C# 的 `NativeKernel.ScalarDensityPass` 使用完全相同的公式和遍历顺序，
//! 保证「启用原生加速」与「纯 C# 回退」逐位一致。

const API_VERSION: i32 = 1;

#[no_mangle]
pub extern "C" fn yr_api_version() -> i32 {
    API_VERSION
}

/// 密度遍历：positions 为 SoA 交错布局 [x,y,z]*n，densities 长度 >= n。
/// h2 = h*h，poly6 = 315/(64*pi*h^9)，self = mass*poly6*h2^3。
///
/// # Safety
/// 调用方必须保证 positions 至少含 `3*n` 个有效 f32，densities 至少含 `n` 个。
#[no_mangle]
pub unsafe extern "C" fn yr_density_pass(
    positions: *const f32,
    densities: *mut f32,
    n: i32,
    h2: f32,
    mass: f32,
    poly6: f32,
) {
    if positions.is_null() || densities.is_null() || n <= 0 {
        return;
    }
    let n = n as usize;
    let pos = std::slice::from_raw_parts(positions, n * 3);
    let out = std::slice::from_raw_parts_mut(densities, n);
    let self_rho = mass * poly6 * h2 * h2 * h2;

    for i in 0..n {
        let bi = i * 3;
        let xi = pos[bi];
        let yi = pos[bi + 1];
        let zi = pos[bi + 2];
        let mut rho = self_rho;
        for j in 0..n {
            if j == i {
                continue;
            }
            let bj = j * 3;
            let rx = pos[bj] - xi;
            let ry = pos[bj + 1] - yi;
            let rz = pos[bj + 2] - zi;
            let r2 = rx * rx + ry * ry + rz * rz;
            if r2 >= h2 {
                continue;
            }
            let d = h2 - r2;
            rho += mass * poly6 * d * d * d;
        }
        out[i] = rho;
    }
}


/// CSR 邻域版密度遍历：邻域由 C# 的 SpatialHashGrid 构建（已按 j 升序），
/// Rust 只做连续内存上的纯数值累加，便于编译器向量化。
/// offsets 长度 n+1，indices 长度 offsets[n]，positions 为 [x,y,z]*n。
///
/// # Safety
/// 调用方必须保证四个指针与长度自洽：positions >= 3*n，offsets >= n+1，
/// indices >= offsets[n]，densities >= n。
#[no_mangle]
pub unsafe extern "C" fn yr_density_pass_csr(
    positions: *const f32,
    offsets: *const i32,
    indices: *const i32,
    densities: *mut f32,
    n: i32,
    h2: f32,
    mass: f32,
    poly6: f32,
) {
    if positions.is_null()
        || offsets.is_null()
        || indices.is_null()
        || densities.is_null()
        || n <= 0
    {
        return;
    }
    let n = n as usize;
    let pos = std::slice::from_raw_parts(positions, n * 3);
    let offs = std::slice::from_raw_parts(offsets, n + 1);
    let total = offs[n] as usize;
    if total < 0 || total > isize::MAX as usize {
        return;
    }
    let idx = std::slice::from_raw_parts(indices, total);
    let out = std::slice::from_raw_parts_mut(densities, n);
    let self_rho = mass * poly6 * h2 * h2 * h2;

    for i in 0..n {
        let bi = i * 3;
        let xi = pos[bi];
        let yi = pos[bi + 1];
        let zi = pos[bi + 2];
        let mut rho = self_rho;
        let start = offs[i] as usize;
        let end = offs[i + 1] as usize;
        if start > end || end > total {
            out[i] = rho;
            continue;
        }
        for &j in &idx[start..end] {
            let j = j as usize;
            if j == i || j >= n {
                continue;
            }
            let bj = j * 3;
            let rx = pos[bj] - xi;
            let ry = pos[bj + 1] - yi;
            let rz = pos[bj + 2] - zi;
            let r2 = rx * rx + ry * ry + rz * rz;
            if r2 >= h2 {
                continue;
            }
            let d = h2 - r2;
            rho += mass * poly6 * d * d * d;
        }
        out[i] = rho;
    }
}


/// 形状匹配协方差热点：out9 = Σ p_i q_iᵀ（行主序，p/q 均为 [x,y,z]*n）。
/// Rust 只做连续内存上的 9 路乘加，无分配、无分支预测之外状态。
///
/// # Safety
/// 调用方必须保证 p/q 至少含 3*n 个 f32，out9 至少含 9 个 f32。
#[no_mangle]
pub unsafe extern "C" fn yr_covariance_accum(
    p: *const f32,
    q: *const f32,
    out9: *mut f32,
    n: i32,
) {
    if p.is_null() || q.is_null() || out9.is_null() || n <= 0 {
        return;
    }
    let n = n as usize;
    let pv = std::slice::from_raw_parts(p, n * 3);
    let qv = std::slice::from_raw_parts(q, n * 3);
    let out = std::slice::from_raw_parts_mut(out9, 9);
    for v in out.iter_mut() {
        *v = 0.0;
    }
    for i in 0..n {
        let b = i * 3;
        let px = pv[b];
        let py = pv[b + 1];
        let pz = pv[b + 2];
        let qx = qv[b];
        let qy = qv[b + 1];
        let qz = qv[b + 2];
        out[0] += px * qx;
        out[1] += px * qy;
        out[2] += px * qz;
        out[3] += py * qx;
        out[4] += py * qy;
        out[5] += py * qz;
        out[6] += pz * qx;
        out[7] += pz * qy;
        out[8] += pz * qz;
    }
}


// ---------------------------------------------------------------------------
// 可选内核的自测：主工程 CI 不编译 Rust，但真机 `cargo test` 可以独立验证
// 三个 C ABI 导出与 C# Scalar* 同构实现的一致性。不引入任何外部 crate。
// ---------------------------------------------------------------------------
#[cfg(test)]
mod tests {
    use super::*;

    fn poly6(h: f32) -> f32 {
        let h3 = h * h * h;
        315.0 / (64.0 * std::f32::consts::PI * h3 * h3 * h3)
    }

    #[test]
    fn density_pass_matches_scalar_reference() {
        let h = 0.3f32;
        let h2 = h * h;
        let mass = 10.0f32;
        let c = poly6(h);
        let pos = [0.0f32, 0.0, 0.0, 0.1, 0.0, 0.0, 0.0, 0.1, 0.0];
        let mut out = [0.0f32; 3];
        unsafe { yr_density_pass(pos.as_ptr(), out.as_mut_ptr(), 3, h2, mass, c) };

        let self_rho = mass * c * h2 * h2 * h2;
        let mut expected = [0.0f32; 3];
        for i in 0..3 {
            let mut rho = self_rho;
            for j in 0..3 {
                if j == i { continue; }
                let rx = pos[j * 3] - pos[i * 3];
                let ry = pos[j * 3 + 1] - pos[i * 3 + 1];
                let rz = pos[j * 3 + 2] - pos[i * 3 + 2];
                let r2 = rx * rx + ry * ry + rz * rz;
                if r2 >= h2 { continue; }
                let d = h2 - r2;
                rho += mass * c * d * d * d;
            }
            expected[i] = rho;
        }
        for i in 0..3 {
            assert_eq!(out[i].to_bits(), expected[i].to_bits(), "density mismatch @{i}");
        }
        assert_eq!(yr_api_version(), 1);
    }

    #[test]
    fn csr_density_matches_dense() {
        let h = 0.3f32;
        let h2 = h * h;
        let mass = 10.0f32;
        let c = poly6(h);
        let pos = [0.0f32, 0.0, 0.0, 0.1, 0.0, 0.0, 0.2, 0.1, 0.0];
        // 全邻域（与 dense 的 j != i 遍历等价），按 j 升序
        let offsets: [i32; 4] = [0, 2, 4, 6];
        let indices: [i32; 6] = [1, 2, 0, 2, 0, 1];
        let mut dense = [0.0f32; 3];
        let mut csr = [0.0f32; 3];
        unsafe {
            yr_density_pass(pos.as_ptr(), dense.as_mut_ptr(), 3, h2, mass, c);
            yr_density_pass_csr(
                pos.as_ptr(), offsets.as_ptr(), indices.as_ptr(),
                csr.as_mut_ptr(), 3, h2, mass, c,
            );
        }
        for i in 0..3 {
            assert_eq!(dense[i].to_bits(), csr[i].to_bits(), "csr mismatch @{i}");
        }
    }

    #[test]
    fn covariance_accum_values() {
        let p = [1.0f32, 2.0, 3.0, 0.5, -1.0, 2.0];
        let q = [2.0f32, 0.0, 1.0, 1.0, 1.0, 1.0];
        let mut out = [0.0f32; 9];
        unsafe { yr_covariance_accum(p.as_ptr(), q.as_ptr(), out.as_mut_ptr(), 2) };
        // 第一行: Σ px*qx = 1*2 + 0.5*1 = 2.5
        assert!((out[0] - 2.5).abs() < 1e-6, "out[0]={}", out[0]);
        // 第二行第一列: Σ py*qx = 2*2 + (-1)*1 = 3
        assert!((out[3] - 3.0).abs() < 1e-6, "out[3]={}", out[3]);
        // 第三行第三列: Σ pz*qz = 3*1 + 2*1 = 5
        assert!((out[8] - 5.0).abs() < 1e-6, "out[8]={}", out[8]);
        let total: f32 = out.iter().sum();
        assert!(total.is_finite());
    }
}
