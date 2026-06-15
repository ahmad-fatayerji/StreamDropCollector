using System.Collections.Concurrent;
using System.Windows.Media.Imaging;
using System.Security.Cryptography;
using System.Net.Http;
using System.Windows;
using System.Text;
using UI.Views;
using System.IO;

namespace UI
{
    public static class ImageEx
    {
        // -- Dependency Property ----------------------------------------------

        public static readonly DependencyProperty SourceUrlProperty =
            DependencyProperty.RegisterAttached(
                "SourceUrl",
                typeof(string),
                typeof(ImageEx),
                new PropertyMetadata(null, OnSourceUrlChanged));

        public static void SetSourceUrl(DependencyObject obj, string value) =>
            obj.SetValue(SourceUrlProperty, value);

        public static string GetSourceUrl(DependencyObject obj) =>
            (string)obj.GetValue(SourceUrlProperty);

        // -- Cache Config -----------------------------------------------------

        private static readonly string CacheDir = Path.Combine(
            Environment.ExpandEnvironmentVariables("%APPDATA%"),
            "Stream Drop Collector",
            "Image Cache");

        /// <summary>How long an image can go unaccessed before it's eligible for eviction.</summary>
        private static readonly TimeSpan EvictionAge = TimeSpan.FromHours(48);

        /// <summary>How often the eviction sweep runs.</summary>
        private static readonly TimeSpan EvictionInterval = TimeSpan.FromHours(1);

        // -- Memory Cache -----------------------------------------------------

        private record MemoryCacheEntry(byte[] Bytes, DateTime LastAccessed);

        private static readonly ConcurrentDictionary<string, MemoryCacheEntry> _memoryCache = new();
        private static readonly ConcurrentDictionary<string, Lazy<Task<byte[]?>>> _inFlightFetches = new();
        private static readonly SemaphoreSlim _httpFetchGate = new(6, 6);
        private static readonly TimeSpan HttpFetchTimeout = TimeSpan.FromSeconds(6);

        // -- HTTP Client (singleton - avoids socket exhaustion) ----------------

        private static readonly HttpClient _http = new();

        // -- WebView (single shared instance for fallback fetches) -------------

        private static HiddenWebViewHost? _sharedWebView;
        private static readonly SemaphoreSlim _webViewLock = new(1, 1);

        // -- Eviction Timer ----------------------------------------------------

        private static readonly System.Timers.Timer _evictionTimer;

        // -- Static Initializer ------------------------------------------------

        static ImageEx()
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/122 Safari/537.36");

            Directory.CreateDirectory(CacheDir);

            _evictionTimer = new System.Timers.Timer(EvictionInterval.TotalMilliseconds)
            {
                AutoReset = true
            };
            _evictionTimer.Elapsed += (_, _) => RunEviction();
            _evictionTimer.Start();
        }

        // -- Property Changed Handler ------------------------------------------

        private static async void OnSourceUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not System.Windows.Controls.Image img)
                return;

            string? url = e.NewValue as string;
            if (string.IsNullOrWhiteSpace(url))
                return;

            img.Source = null;
            img.Opacity = .3;

            byte[]? bytes = await Task.Run(async () => await ResolveImageAsync(url).ConfigureAwait(false));

            if (!string.Equals(GetSourceUrl(img), url, StringComparison.Ordinal))
                return;

            if (bytes == null || bytes.Length == 0)
            {
                img.Opacity = 1;
                return;
            }

            await ApplyBytesAsync(img, bytes);
        }

        // -- Resolution Pipeline -----------------------------------------------

        private static async Task<byte[]?> ResolveImageAsync(string url)
        {
            // 1. Memory cache
            if (_memoryCache.TryGetValue(url, out MemoryCacheEntry? entry))
            {
                _memoryCache[url] = entry with { LastAccessed = DateTime.UtcNow };
                return entry.Bytes;
            }

            // 2. Disk cache
            string diskPath = DiskCachePath(url);
            if (File.Exists(diskPath))
            {
                try
                {
                    byte[] cached = await File.ReadAllBytesAsync(diskPath).ConfigureAwait(false);
                    File.SetLastAccessTimeUtc(diskPath, DateTime.UtcNow);
                    _memoryCache[url] = new MemoryCacheEntry(cached, DateTime.UtcNow);
                    return cached;
                }
                catch
                {
                    // Disk read failed - fall through to fetch
                }
            }

            Lazy<Task<byte[]?>> fetchTask = _inFlightFetches.GetOrAdd(
                url,
                key => new Lazy<Task<byte[]?>>(() => FetchAndCacheImageAsync(key)));

            try
            {
                return await fetchTask.Value.ConfigureAwait(false);
            }
            finally
            {
                _inFlightFetches.TryRemove(url, out _);
            }
        }

        private static async Task<byte[]?> FetchAndCacheImageAsync(string url)
        {
            // Avoid using the WebView fallback in the general image binding path.
            // Creating/navigating WebView2 for many inventory images can stall the UI during initial load.
            byte[]? fetched = await TryHttpFetchAsync(url);

            if (fetched != null && fetched.Length > 0)
                await PersistToCacheAsync(url, fetched).ConfigureAwait(false);

            return fetched;
        }

        // -- Fetchers ----------------------------------------------------------

        private static async Task<byte[]?> TryHttpFetchAsync(string url)
        {
            await _httpFetchGate.WaitAsync().ConfigureAwait(false);
            try
            {
                using CancellationTokenSource timeoutCts = new(HttpFetchTimeout);
                return await _http.GetByteArrayAsync(url, timeoutCts.Token).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
            finally
            {
                _httpFetchGate.Release();
            }
        }

        private static async Task<byte[]?> TryWebViewFetchAsync(string url)
        {
            await _webViewLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_sharedWebView == null)
                {
                    _sharedWebView = new HiddenWebViewHost();
                    await _sharedWebView.EnsureInitializedAsync().ConfigureAwait(false);
                }

                return await _sharedWebView.FetchImageBytesAsync(url).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
            finally
            {
                _webViewLock.Release();
            }
        }

        // -- Cache Persistence -------------------------------------------------

        private static async Task PersistToCacheAsync(string url, byte[] bytes)
        {
            _memoryCache[url] = new MemoryCacheEntry(bytes, DateTime.UtcNow);

            try
            {
                string diskPath = DiskCachePath(url);
                await File.WriteAllBytesAsync(diskPath, bytes).ConfigureAwait(false);
            }
            catch
            {
                // Non-fatal - memory cache still works
            }
        }

        private static string DiskCachePath(string url)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(url));
            return Path.Combine(CacheDir, Convert.ToHexString(hash) + ".png");
        }

        // -- Eviction ----------------------------------------------------------

        private static void RunEviction()
        {
            try
            {
                DateTime cutoff = DateTime.UtcNow - EvictionAge;

                // Memory eviction - materialize keys to avoid "collection modified" error
                foreach (string key in _memoryCache.Keys.ToList())
                {
                    if (_memoryCache.TryGetValue(key, out MemoryCacheEntry? entry) &&
                        entry.LastAccessed < cutoff)
                    {
                        _memoryCache.TryRemove(key, out _);
                    }
                }

                // Disk eviction - uses filesystem last access time
                foreach (string file in Directory.GetFiles(CacheDir, "*.png"))
                {
                    try
                    {
                        if (File.GetLastAccessTimeUtc(file) < cutoff)
                            File.Delete(file);
                    }
                    catch
                    {
                        // Skip files we can't delete
                    }
                }
            }
            catch
            {
                // Eviction is best-effort
            }
        }

        // -- Bitmap Application ------------------------------------------------

        private static async Task ApplyBytesAsync(System.Windows.Controls.Image img, byte[] bytes)
        {
            BitmapImage? bitmap = null;

            await Task.Run(() =>
            {
                BitmapImage bmp = new();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.None;

                using MemoryStream ms = new(bytes);
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                bitmap = bmp;
            }).ConfigureAwait(false);

            await img.Dispatcher.InvokeAsync(() =>
            {
                img.Source = bitmap;
                img.Opacity = 1;
            });
        }
    }
}
