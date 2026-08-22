cmake_minimum_required(VERSION 3.20)
project(ShanyuEngine.Native VERSION 1.0.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# 源文件（递归包含 src/ 下全部，含 src/jolt/ShanyuJolt.cpp 等）
file(GLOB_RECURSE SOURCES src/*.cpp)
file(GLOB_RECURSE HEADERS include/*.h)

# 创建DLL
add_library(ShanyuEngine.Native SHARED ${SOURCES} ${HEADERS})

target_include_directories(ShanyuEngine.Native PUBLIC
    include
    src            # 内部头（EdgeAdjacency.h / SmartEdgeFilter.h / MeshAdjacencyRegistry.h）
)

# --------------------------------------------------------------------------------------------
#  Jolt Physics（经 vcpkg）：
#    cmake -DCMAKE_TOOLCHAIN_FILE=<vcpkg>/scripts/buildsystems/vcpkg.cmake ..
#    vcpkg install joltphysics
# --------------------------------------------------------------------------------------------
find_package(Jolt CONFIG QUIET)
if(Jolt_FOUND)
    target_link_libraries(ShanyuEngine.Native PRIVATE Jolt::Jolt)
    message(STATUS "ShanyuEngine.Native: linked Jolt via vcpkg (Jolt::Jolt)")
else()
    # 回退：尝试直接找 joltphysics
    find_package(joltphysics CONFIG QUIET)
    if(joltphysics_FOUND)
        target_link_libraries(ShanyuEngine.Native PRIVATE joltphysics::joltphysics)
        message(STATUS "ShanyuEngine.Native: linked Jolt via vcpkg (joltphysics::joltphysics)")
    else()
        message(FATAL_ERROR
            "Jolt Physics 未找到。请先：\n"
            "  1. vcpkg install joltphysics\n"
            "  2. cmake 配置时加 -DCMAKE_TOOLCHAIN_FILE=<vcpkg>/scripts/buildsystems/vcpkg.cmake\n")
    endif()
endif()

# 编译选项
if(MSVC)
    target_compile_options(ShanyuEngine.Native PRIVATE /O2 /arch:AVX2 /fp:fast /W4 /permissive-)
    # SHANYU_EXPORTS: 原有 native 模块导出；SHANYUJOLT_EXPORTS: ShanyuJolt.h 的 SJ_API dllexport
    target_compile_definitions(ShanyuEngine.Native PRIVATE
        SHANYU_EXPORTS SHANYUJOLT_EXPORTS NOMINMAX WIN32_LEAN_AND_MEAN
        _CRT_SECURE_NO_WARNINGS)
    target_link_options(ShanyuEngine.Native PRIVATE /DEF:${CMAKE_CURRENT_SOURCE_DIR}/ShanyuEngine.Native.def)
endif()

# 设置输出目录（DLL 输出到 ShanyuEngine/Native，供 C# 运行时加载）
set_target_properties(ShanyuEngine.Native PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY ${CMAKE_SOURCE_DIR}/../ShanyuEngine/Native
    LIBRARY_OUTPUT_DIRECTORY ${CMAKE_SOURCE_DIR}/../ShanyuEngine/Native
    ARCHIVE_OUTPUT_DIRECTORY ${CMAKE_SOURCE_DIR}/../ShanyuEngine/Native
)

# 安装规则
install(TARGETS ShanyuEngine.Native
    RUNTIME DESTINATION bin
    LIBRARY DESTINATION lib
    ARCHIVE DESTINATION lib
)
install(DIRECTORY include/ DESTINATION include)
