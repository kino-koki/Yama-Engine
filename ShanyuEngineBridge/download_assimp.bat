@echo off
echo ========================================================
echo   Shanyu Engine - Assimp SDK 下载器
echo ========================================================
echo.
echo 此脚本将下载 Assimp (Open Asset Import Library)
echo Assimp 是跨平台的 3D 模型导入库,支持:
echo   - FBX, OBJ, glTF, Collada, PLY, STL, 3DS 等格式
echo   - 几乎所有主流 3D 软件的导出格式
echo.

set "ASSIMP_VERSION=5.4.2"
set "DOWNLOAD_URL=https://github.com/assimp/assimp/releases/download/v%ASSIMP_VERSION%/assimp-%ASSIMP_VERSION%-bin.zip"

echo 当前版本: %ASSIMP_VERSION%
echo 下载地址: %DOWNLOAD_URL%
echo.

:: 检查是否已安装
if exist "C:\Windows\System32\assimp.dll" (
    echo [OK] Assimp DLL 已安装在 System32
    goto :done
)

if exist "%~dp0assimp.dll" (
    echo [OK] Assimp DLL 已存在于当前目录
    goto :done
)

:: 创建临时目录
set "TEMP_DIR=%TEMP%\ShanyuAssimp_%RANDOM%"
mkdir "%TEMP_DIR%" 2>nul

echo.
echo [1/3] 下载 Assimp...
powershell -Command "Invoke-WebRequest -Uri '%DOWNLOAD_URL%' -OutFile '%TEMP_DIR%\assimp.zip' -UseBasicParsing"

if errorlevel 1 (
    echo [错误] 下载失败,尝试备用源...
    set "ALT_URL=https://github.com/assimp/assimp/releases/download/v%ASSIMP_VERSION%/assimp-%ASSIMP_VERSION%-dev.zip"
    powershell -Command "Invoke-WebRequest -Uri '%ALT_URL%' -OutFile '%TEMP_DIR%\assimp.zip' -UseBasicParsing"
)

echo.
echo [2/3] 解压文件...
powershell -Command "Expand-Archive -Path '%TEMP_DIR%\assimp.zip' -DestinationPath '%TEMP_DIR%' -Force"

echo.
echo [3/3] 安装 DLL...

:: 查找 bin 目录中的 DLL
set "FOUND=0"
for /r "%TEMP_DIR%" %%f in (assimp*.dll) do (
    if exist "%%f" (
        echo 安装: %%f
        copy "%%f" "%~dp0assimp.dll" /Y
        copy "%%f" "C:\Windows\System32\assimp.dll" /Y
        set "FOUND=1"
    )
)

:: 也尝试 copy bin
if "%FOUND%"=="0" (
    for /r "%TEMP_DIR%\bin" %%f in (*.dll) do (
        if exist "%%f" (
            echo 安装: %%f
            copy "%%f" "%~dp0assimp.dll" /Y
            copy "%%f" "C:\Windows\System32\assimp.dll" /Y
        )
    )
)

:: 清理
rd /s /q "%TEMP_DIR%" 2>nul

:done
echo.
echo ========================================================
echo   安装完成!
echo ========================================================
echo.
echo 接下来:
echo   1. 重启 Visual Studio
echo   2. 重新编译 ShanyuEngine
echo   3. 运行编辑器,导入 FBX/OBJ/glTF 文件
echo.
echo 如果导入仍失败,手动复制 assimp.dll 到:
echo   - 项目输出目录 (bin/Debug 或 bin/Release)
echo   - 或 C:\Windows\System32\
echo.
pause
