using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace ShanyuEngine.MicroCluster
{
    /// <summary>
    /// MicroCluster流送系统 — 按需加载/卸载MicroCluster数据页。
    /// 管理磁盘上的.microcluster资源文件和内存缓存。
    /// </summary>
    public class MicroClusterStreaming : IDisposable
    {
        private readonly string _assetBasePath;
        private readonly Dictionary<string, MicroClusterPage> _pageCache = new();
        private readonly Queue<string> _lruQueue = new();
        private readonly int _maxCachePages = 256;
        private readonly long _maxCacheBytes = 512 * 1024 * 1024; // 512MB
        private long _currentCacheBytes;

        public MicroClusterStreaming(string assetBasePath)
        {
            _assetBasePath = assetBasePath;
        }

        public async Task<MicroClusterPage?> RequestPageAsync(string assetId, int pageIndex)
        {
            string key = $"{assetId}_page_{pageIndex}";

            if (_pageCache.TryGetValue(key, out var cached))
            {
                // 更新LRU
                _lruQueue = new Queue<string>(_lruQueue.Where(k => k != key));
                _lruQueue.Enqueue(key);
                return cached;
            }

            // 从磁盘加载
            string path = Path.Combine(_assetBasePath, $"{assetId}.microcluster");
            if (!File.Exists(path)) return null;

            var page = await LoadPageAsync(path, pageIndex);
            if (page != null)
            {
                AddToCache(key, page);
            }
            return page;
        }

        private async Task<MicroClusterPage?> LoadPageAsync(string path, int pageIndex)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
                // 读取页表头
                var header = new byte[16];
                await stream.ReadAsync(header, 0, 16);
                int pageCount = BitConverter.ToInt32(header, 0);
                long pageTableOffset = BitConverter.ToInt64(header, 4);

                if (pageIndex >= pageCount) return null;

                // 读取页表
                stream.Seek(pageTableOffset + pageIndex * 16, SeekOrigin.Begin);
                var pageEntry = new byte[16];
                await stream.ReadAsync(pageEntry, 0, 16);
                long pageOffset = BitConverter.ToInt64(pageEntry, 0);
                int pageSize = BitConverter.ToInt32(pageEntry, 8);

                // 读取页数据
                stream.Seek(pageOffset, SeekOrigin.Begin);
                var data = new byte[pageSize];
                await stream.ReadAsync(data, 0, pageSize);

                return new MicroClusterPage
                {
                    AssetId = Path.GetFileNameWithoutExtension(path),
                    PageIndex = pageIndex,
                    Data = data,
                    CompressedSize = pageSize
                };
            }
            catch { return null; }
        }

        private void AddToCache(string key, MicroClusterPage page)
        {
            while (_pageCache.Count >= _maxCachePages || _currentCacheBytes + page.Data.Length > _maxCacheBytes)
            {
                if (!_lruQueue.TryDequeue(out var oldest)) break;
                if (_pageCache.Remove(oldest, out var oldPage))
                    _currentCacheBytes -= oldPage.Data.Length;
            }

            _pageCache[key] = page;
            _lruQueue.Enqueue(key);
            _currentCacheBytes += page.Data.Length;
        }

        public void EvictAll()
        {
            _pageCache.Clear();
            _lruQueue.Clear();
            _currentCacheBytes = 0;
        }

        public void Dispose()
        {
            EvictAll();
        }
    }

    public class MicroClusterPage
    {
        public string AssetId { get; set; } = "";
        public int PageIndex { get; set; }
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public int CompressedSize { get; set; }
        public bool IsCompressed => CompressedSize < Data.Length;
    }
}
