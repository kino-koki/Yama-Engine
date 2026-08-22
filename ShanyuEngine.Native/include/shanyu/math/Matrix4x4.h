#pragma once

#include <array>
#include <cmath>

namespace shanyu::math {

/// <summary>4x4 行主序矩阵（C++20 constexpr友好）。</summary>
struct alignas(16) Matrix4x4
{
    std::array<float, 16> m{};

    static constexpr Matrix4x4 Identity()
    {
        Matrix4x4 mat{};
        mat.m[0] = mat.m[5] = mat.m[10] = mat.m[15] = 1.0f;
        return mat;
    }

    constexpr float operator()(int row, int col) const { return m[row * 4 + col]; }
    constexpr float& operator()(int row, int col) { return m[row * 4 + col]; }

    static Matrix4x4 Translation(float x, float y, float z);
    static Matrix4x4 RotationX(float radians);
    static Matrix4x4 RotationY(float radians);
    static Matrix4x4 RotationZ(float radians);
    static Matrix4x4 Scale(float x, float y, float z);
    static Matrix4x4 PerspectiveFov(float fov, float aspect, float nearZ, float farZ);
    static Matrix4x4 LookAt(const float* eye, const float* target, const float* up);
};

/// <summary>4D向量。</summary>
struct alignas(16) Vector4
{
    float x, y, z, w;

    static Vector4 Zero() { return { 0, 0, 0, 0 }; }
    static Vector4 One()  { return { 1, 1, 1, 1 }; }
};

} // namespace shanyu::math
