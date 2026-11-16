using System;
using System.IO;
using YamaEngine.Assets;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    // ══════════════════════════════════════════════════════════════
    // FileSystemDepthTests — VFileSystem 工业级深度验证
    // 覆盖：路径穿越防护（.. 越界）、NormalizePath 规范化、挂载边界
    // ══════════════════════════════════════════════════════════════
    public static class FileSystemDepthTests
    {
        // ── NormalizePath：.. 不得逃逸到挂载根之上（clamp at root） ──
        public static void TestNormalizePathClampsTraversal()
        {
            // NormalizePath 返回不带首尾斜杠的规范化形式
            AssertEqual("assets/file.txt", VFileSystem.NormalizePath("/assets/../file.txt"), "basic ..");
            AssertEqual("file.txt", VFileSystem.NormalizePath("/../../file.txt"), ".. beyond root clamps");
            AssertEqual("a/b/c", VFileSystem.NormalizePath("/a/./b//c"), "redundant separators + dot");
            AssertEqual("a/b", VFileSystem.NormalizePath("a/b"), "no leading slash unchanged");
            AssertEqual("a/b", VFileSystem.NormalizePath("\\a\\b"), "backslash normalized");
        }

        // ── ReadAllBytes：.. 穿越不得读取挂载根之外的文件 ──
        public static void TestVFileSystemTraversalCannotEscapeMountRoot(string tempRoot)
        {
            var vfs = new VFileSystem();
            try
            {
                // 在 tempRoot 下建一个 mount 子目录，再在 mount 之外放一个"机密"文件
                string mountDir = Path.Combine(tempRoot, "mount");
                string outsideFile = Path.Combine(tempRoot, "secret.txt");
                Directory.CreateDirectory(mountDir);
                File.WriteAllText(Path.Combine(mountDir, "good.txt"), "inside");
                File.WriteAllText(outsideFile, "SECRET");

                vfs.MountDirectory("/assets/", mountDir);

                // 尝试用 .. 读取 mount 外的 secret.txt —— 必须失败（被 clamp 到 root）
                bool escaped = false;
                try
                {
                    var data = vfs.ReadAllBytes("/assets/../secret.txt");
                    escaped = data != null && data.Length > 0;
                }
                catch (FileNotFoundException)
                {
                    escaped = false;
                }
                AssertTrue(!escaped, "Path traversal must not escape the mount root.");

                // 合法读取仍工作
                AssertEqual("inside", vfs.ReadAllText("/assets/good.txt"), "legit read works");
            }
            finally
            {
                vfs.Dispose();
            }
        }

        // ── Exists：.. 穿越不得命中 mount 之外；挂载根作为目录存在 ──
        public static void TestVFileSystemTraversalDoesNotHitOutside(string tempRoot)
        {
            var vfs = new VFileSystem();
            try
            {
                string mountDir = Path.Combine(tempRoot, "m2");
                string outsideFile = Path.Combine(tempRoot, "outside.txt");
                Directory.CreateDirectory(mountDir);
                File.WriteAllText(outsideFile, "x");

                vfs.MountDirectory("/content/", mountDir);

                AssertTrue(!vfs.Exists("/content/../outside.txt"), "Traversal must not resolve to outside file.");
                // Exists 是"文件"语义：目录挂载根本身不是文件，应返回 false；
                // 但挂载根下的文件应可找到（挂载根前缀匹配正确）
                AssertTrue(!vfs.Exists("/content/"), "Mount root is a directory, not a file, so Exists=false.");
                File.WriteAllText(Path.Combine(mountDir, "real.txt"), "y");
                AssertTrue(vfs.Exists("/content/real.txt"), "A file inside the mount root must be found.");
            }
            finally
            {
                vfs.Dispose();
            }
        }

        // ── 前缀边界对齐：挂载点 "assets" 不得误匹配 "assets2"（前缀混淆回归） ──
        public static void TestMountPrefixBoundaryNotConfused(string tempRoot)
        {
            var vfs = new VFileSystem();
            try
            {
                string mountDir = Path.Combine(tempRoot, "m3");
                Directory.CreateDirectory(mountDir);
                File.WriteAllText(Path.Combine(mountDir, "a.txt"), "assets-data");

                vfs.MountDirectory("/assets/", mountDir);

                // /assets2/x 不得路由到 /assets 挂载点 —— 边界必须对齐
                AssertTrue(!vfs.Exists("/assets2/a.txt"), "assets2 must not match assets mount root.");
                bool read = false;
                try { vfs.ReadAllBytes("/assets2/a.txt"); read = true; }
                catch (FileNotFoundException) { read = false; }
                AssertTrue(!read, "ReadAllBytes(/assets2/a.txt) must throw, not leak into assets mount.");
            }
            finally
            {
                vfs.Dispose();
            }
        }

        // ── 挂载根切片：/assets/ 下文件的 relativePath 必须正确剥离挂载根 ──
        public static void TestMountRootSliceRelativePath(string tempRoot)
        {
            var vfs = new VFileSystem();
            try
            {
                string mountDir = Path.Combine(tempRoot, "m4");
                Directory.CreateDirectory(Path.Combine(mountDir, "sub"));
                File.WriteAllText(Path.Combine(mountDir, "sub", "deep.txt"), "deep-content");

                vfs.MountDirectory("/assets/", mountDir);

                AssertEqual("deep-content", vfs.ReadAllText("/assets/sub/deep.txt"), "Nested file resolves under mount root.");
                AssertEqual("deep-content", vfs.ReadAllText("/assets/sub/deep.txt"), "Repeated read is stable.");
            }
            finally
            {
                vfs.Dispose();
            }
        }
    }
}
