#nullable disable
using BiliClient;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace WPFbilibili
{
    public partial class MainWindow : Window
    {
        // ================= 原生CMD控制台相关 =================
        [DllImport("kernel32.dll")]
        private static extern bool AllocConsole();
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private bool _isConsoleShown = false;

        private void ShowLogConsole()
        {
            if (!_isConsoleShown)
            {
                AllocConsole();
                // 修正中文乱码
                Console.OutputEncoding = Encoding.UTF8;
                _isConsoleShown = true;
            }
            IntPtr hWnd = GetConsoleWindow();
            if (hWnd != IntPtr.Zero)
            {
                ShowWindow(hWnd, 5); // SW_SHOW = 5
            }
        }
        // =====================================================

        private const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

        private const string LocalVersionStr = "beta 2.0";

        private static readonly string[] VersionUrls = new string[]
        {
            "https://cdn.jsdelivr.net/gh/scMC-NB/WPFbilibili@main/version.txt",
            "https://ghproxy.net/https://raw.githubusercontent.com/scMC-NB/WPFbilibili/main/version.txt",
            "https://raw.githubusercontent.com/scMC-NB/WPFbilibili/main/version.txt"
        };

        private static readonly HttpClient client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate });
        private static readonly HttpClient versionClient = new HttpClient();

        private string sessData = "";
        private string biliJct = "";
        private string buvid3 = "";

        private string currentBvid = "";
        private string currentAid = "";
        private string currentCid = "";

        private string currentUpMid = "";
        private string currentUpName = "";

        private string _spacePreviousView = "ListViewer";

        private int currentPage = 1;
        private string currentViewMode = "Home";
        private bool isLoading = false;
        private bool isPlayingTaskRunning = false;

        private string commentPaginationOffset = "";
        private bool isCommentLoading = false;
        private int _commentTotal = 0;
        private readonly ObservableCollection<CommentItem> _comments = new();

        private bool _isSendingComment = false;
        private bool _isSendingDanmaku = false;

        private LibVLC _libVlc;
        private VlcMediaPlayer _mediaPlayer;
        private bool _isDragging = false;

        private double _lastDanmakuTime = -1;
        private List<DanmakuItem> _currentDanmakuList = new List<DanmakuItem>();
        private int[] _laneEndTimes = new int[12];
        private readonly Random _rng = new Random();
        private bool _danmakuVisible = true;
        private double _danmakuOpacity = 1.0;

        private static string _mixinKey = null;
        private BiliDanmakuClient _danmakuClient;

        private string CredFile => System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "credential.json");

        public class VideoItem
        {
            public string Bvid { get; set; }
            public string Title { get; set; }
            public string Author { get; set; }
            public string PicUrl { get; set; }
            public string Duration { get; set; }
            public string PlayCount { get; set; }
            public string DanmakuCount { get; set; }
        }

        public class CommentItem
        {
            public string UserName { get; set; }
            public string Message { get; set; }
            public string LikeInfo { get; set; }
            public bool IsTop { get; set; } = false;
        }

        public class DanmakuItem
        {
            public double TimeSec { get; set; }
            public string Text { get; set; }
            public int Color { get; set; }
        }

        public MainWindow()
        {
            InitializeComponent();

            TxtLocalVersion.Text = $"v {LocalVersionStr}";

            TxtSearch.KeyDown += (s, e) => { if (e.Key == Key.Enter) BtnSearch_Click(null, null); };
            CommentList.ItemsSource = _comments;

            TxtDanmakuInput.KeyDown += (s, e) => { if (e.Key == Key.Enter) { e.Handled = true; BtnSendDanmaku_Click(null, null); } };
            TxtCommentInput.KeyDown += (s, e) => { if (e.Key == Key.Enter) { e.Handled = true; BtnSendComment_Click(null, null); } };

            try
            {
                Core.Initialize();
                _libVlc = new LibVLC();
                _mediaPlayer = new VlcMediaPlayer(_libVlc);
                VideoView.MediaPlayer = _mediaPlayer;
                _mediaPlayer.LengthChanged += (s, e) => Dispatcher.InvokeAsync(() => SliderProgress.Maximum = e.Length);
                _mediaPlayer.TimeChanged += MediaPlayer_TimeChanged;

                _mediaPlayer.EncounteredError += (s, e) => Dispatcher.InvokeAsync(() =>
                {
                    Console.WriteLine("[VLC Error] 播放流失败");
                    MessageBox.Show("VLC 播放流失败，可能是未登录限制或网络无法连接 CDN。", "播放错误");
                });
            }
            catch (Exception ex) { MessageBox.Show($"VLC 初始化失败: {ex.Message}"); }

            LoadCredential();

            Loaded += async (s, e) =>
            {
                await EnsureBuvid3Cookie();
                await CheckForUpdatesAsync(false);
                await LoadHomeAsync(1, true);
            };
        }

        // ================= 窗口按钮与版本检查 =================
        private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void OnMaximizeClick(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            MaximizeIcon.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        }
        private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

        private async void BtnCheckUpdate_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(true);

        private async Task CheckForUpdatesAsync(bool isManual)
        {
            string remoteRaw = null;
            Exception lastError = null;

            foreach (var url in VersionUrls)
            {
                try
                {
                    using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(6));
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.TryAddWithoutValidation("User-Agent", UA);
                    request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");

                    var response = await versionClient.SendAsync(request, cts.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        remoteRaw = await response.Content.ReadAsStringAsync();
                        if (!string.IsNullOrWhiteSpace(remoteRaw)) break;
                    }
                }
                catch (Exception ex) { lastError = ex; }
            }

            if (string.IsNullOrWhiteSpace(remoteRaw))
            {
                if (isManual) MessageBox.Show($"检查更新失败，所有线路均无法访问。\n错误: {lastError?.Message}", "检查更新");
                return;
            }

            remoteRaw = remoteRaw.Trim();

            if (!TryParseVersion(remoteRaw, out bool isBeta, out Version remoteVersion, out string displayVersion))
            {
                if (isManual) MessageBox.Show($"版本格式错误：{remoteRaw}", "检查更新");
                return;
            }

            TryParseVersion(LocalVersionStr, out _, out Version localVersion, out _);

            if (remoteVersion > localVersion)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show($"发现新版本：{displayVersion}\n当前版本：v {LocalVersionStr}\n\n请前往 GitHub 下载最新版本。", "发现更新", MessageBoxButton.OK, MessageBoxImage.Information);
                });
            }
            else
            {
                if (isManual) MessageBox.Show($"当前已是最新版本 v {LocalVersionStr}。\n(远程: {remoteRaw})", "检查更新");
            }
        }

        private static bool TryParseVersion(string raw, out bool isBeta, out Version version, out string display)
        {
            isBeta = false; version = null; display = raw;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            string numericPart = raw;
            if (raw.StartsWith("beta", StringComparison.OrdinalIgnoreCase))
            {
                isBeta = true;
                int idx = raw.IndexOf(' ');
                if (idx < 0 || idx >= raw.Length - 1) return false;
                numericPart = raw.Substring(idx + 1).Trim();
            }

            var parts = numericPart.Split('.');
            if (parts.Length == 0 || parts.Length > 4) return false;
            while (parts.Length < 4) { numericPart += ".0"; parts = numericPart.Split('.'); }
            return Version.TryParse(numericPart, out version);
        }

        // ================= 基础工具与登录 =================
        private int GetSafeInt(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number) return element.GetInt32();
            if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out int v)) return v;
            return 0;
        }

        private void SaveCredential() => File.WriteAllText(CredFile, JsonSerializer.Serialize(new { sessData, biliJct }));
        private void LoadCredential()
        {
            try
            {
                if (File.Exists(CredFile))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(CredFile));
                    sessData = doc.RootElement.GetProperty("sessData").GetString();
                    biliJct = doc.RootElement.GetProperty("biliJct").GetString();
                    if (!string.IsNullOrEmpty(sessData))
                    {
                        BtnLogin.Content = "已登录";
                        BtnLogin.Background = (Brush)new BrushConverter().ConvertFromString("#E0E0E0");
                        BtnLogin.Foreground = Brushes.Gray;
                    }
                }
            }
            catch { }
        }

        private void BtnLogin_Click(object sender, RoutedEventArgs e)
        {
            if ((string)BtnLogin.Content == "已登录") return;
            var loginWin = new LoginWindow();
            loginWin.ShowDialog();
            if (!string.IsNullOrEmpty(loginWin.SessData) && !string.IsNullOrEmpty(loginWin.BiliJct))
            {
                sessData = loginWin.SessData;
                biliJct = loginWin.BiliJct;
                SaveCredential();
                BtnLogin.Content = "已登录";
                BtnLogin.Background = (Brush)new BrushConverter().ConvertFromString("#E0E0E0");
                BtnLogin.Foreground = Brushes.Gray;
                MessageBox.Show("登录成功！");
            }
        }

        private async Task EnsureBuvid3Cookie()
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.bilibili.com/");
                request.Headers.TryAddWithoutValidation("User-Agent", UA);
                request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                using var response = await client.SendAsync(request);
                if (response.Headers.TryGetValues("Set-Cookie", out var cookieValues))
                {
                    foreach (var cookie in cookieValues)
                    {
                        var match = Regex.Match(cookie, @"(?:^|[,;\s])buvid3=([^;,]+)");
                        if (match.Success)
                        {
                            buvid3 = match.Groups[1].Value;
                            if (!buvid3.EndsWith("infoc")) buvid3 += "infoc";
                            return;
                        }
                    }
                }
            }
            catch { }
        }

        private string BuildBiliCookie()
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(sessData)) parts.Add("SESSDATA=" + sessData);
            if (!string.IsNullOrWhiteSpace(biliJct)) parts.Add("bili_jct=" + biliJct);
            if (!string.IsNullOrWhiteSpace(buvid3)) parts.Add("buvid3=" + buvid3);
            return string.Join("; ", parts);
        }

        // ================= 个人空间 =================
        private async void BtnMyProfile_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(sessData)) { MessageBox.Show("请先点击右上角登录！", "提示"); return; }
            try
            {
                string myMid = await GetMyMidAsync();
                if (!string.IsNullOrEmpty(myMid))
                {
                    _spacePreviousView = "ListViewer";
                    await LoadSpaceAsync(myMid, "我的空间");
                }
            }
            catch { MessageBox.Show("获取个人信息失败", "提示"); }
        }

        private async Task<string> GetMyMidAsync()
        {
            try
            {
                var paramsDict = new Dictionary<string, string>();
                string signedQuery = await SignWbiParamsAsync(paramsDict);
                var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.bilibili.com/x/space/myinfo?{signedQuery}");
                request.Headers.TryAddWithoutValidation("User-Agent", UA);
                request.Headers.TryAddWithoutValidation("Cookie", BuildBiliCookie());
                var response = await client.SendAsync(request);
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (doc.RootElement.GetProperty("code").GetInt32() == 0)
                    return doc.RootElement.GetProperty("data").GetProperty("mid").GetInt64().ToString();
            }
            catch { }
            return null;
        }

        private async Task GetUpStatAsync(long mid)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.bilibili.com/x/space/upstat?mid={mid}");
                request.Headers.TryAddWithoutValidation("User-Agent", UA);
                request.Headers.TryAddWithoutValidation("Cookie", BuildBiliCookie());

                var response = await client.SendAsync(request);
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (doc.RootElement.GetProperty("code").GetInt32() == 0)
                {
                    int videoView = GetSafeInt(doc.RootElement.GetProperty("data").GetProperty("archive").GetProperty("view"));
                    await Dispatcher.InvokeAsync(() => TxtSpaceView.Text = $"总播放: {FormatCount(videoView)}");
                }
            }
            catch { }
        }

        private async Task LoadSpaceAsync(string mid, string uname)
        {
            ResetPlayerState();
            currentUpMid = mid;
            currentUpName = uname;
            currentViewMode = "Space";

            ListViewer.Visibility = Visibility.Collapsed;
            PlayView.Visibility = Visibility.Collapsed;
            SpaceView.Visibility = Visibility.Visible;

            TxtSpaceLoading.Visibility = Visibility.Visible;
            TxtSpaceLoading.Text = "正在加载空间信息...";
            SpaceVideoGrid.ItemsSource = null;

            TxtSpaceName.Text = uname;
            TxtSpaceSign.Text = "加载简介...";
            TxtSpaceView.Text = "总播放: -";
            ImgSpaceAvatar.ImageSource = null;

            string cookie = BuildBiliCookie();

            try
            {
                var paramsDict = new Dictionary<string, string> { { "mid", mid } };
                string signedQuery = await SignWbiParamsAsync(paramsDict);
                var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.bilibili.com/x/space/wbi/acc/info?{signedQuery}");
                request.Headers.TryAddWithoutValidation("User-Agent", UA);
                request.Headers.TryAddWithoutValidation("Referer", $"https://space.bilibili.com/{mid}");
                if (!string.IsNullOrWhiteSpace(cookie)) request.Headers.TryAddWithoutValidation("Cookie", cookie);

                var response = await client.SendAsync(request);
                string resStr = await response.Content.ReadAsStringAsync();

                if (!string.IsNullOrWhiteSpace(resStr) && resStr.TrimStart().StartsWith("{"))
                {
                    using var doc = JsonDocument.Parse(resStr);
                    if (doc.RootElement.GetProperty("code").GetInt32() == 0)
                    {
                        var data = doc.RootElement.GetProperty("data");
                        string face = data.TryGetProperty("face", out var f) ? f.GetString() : "";
                        TxtSpaceName.Text = data.GetProperty("name").GetString();

                        string sign = data.TryGetProperty("sign", out var s) ? s.GetString() : "";
                        TxtSpaceSign.Text = string.IsNullOrWhiteSpace(sign) ? "这个人很神秘，什么都没写" : sign;

                        if (!string.IsNullOrEmpty(face))
                        {
                            if (!face.StartsWith("http")) face = "https:" + face;
                            ImgSpaceAvatar.ImageSource = new BitmapImage(new Uri(face));
                        }
                    }
                }
            }
            catch { TxtSpaceSign.Text = "获取资料失败"; }

            _ = GetUpStatAsync(long.Parse(mid));

            try
            {
                var videoParams = new Dictionary<string, string>
                {
                    { "mid", mid },
                    { "ps", "30" },
                    { "pn", "1" },
                    { "order", "pubdate" },
                    { "web_location", "1550101" },
                    { "dm_img_list", "[]" },
                    { "dm_img_str", "V2ViR0wgMS4wIChPcGVuR0wgRVMgMi4wIENocm9taXVtKQ" },
                    { "dm_cover_img_str", "QU5HTEUgKE5WSURJQSwgTlZJRElBIEdlRm9yY2UgR1RYIDEwNjAgNkdCIERpcmVjdDNEMTEgdnNfNV8wIHBzXzVfMCwgRDNEMTEpR29vZ2xlIEluYy4gKE5WSURJQS)" }
                };

                string videoQuery = await SignWbiParamsAsync(videoParams);
                var videoReq = new HttpRequestMessage(HttpMethod.Get, $"https://api.bilibili.com/x/space/wbi/arc/search?{videoQuery}");
                videoReq.Headers.TryAddWithoutValidation("User-Agent", UA);
                videoReq.Headers.TryAddWithoutValidation("Referer", $"https://space.bilibili.com/{mid}/video");
                if (!string.IsNullOrWhiteSpace(cookie)) videoReq.Headers.TryAddWithoutValidation("Cookie", cookie);

                var videoRes = await client.SendAsync(videoReq);
                string videoResStr = await videoRes.Content.ReadAsStringAsync();

                if (string.IsNullOrWhiteSpace(videoResStr) || (!videoResStr.TrimStart().StartsWith("{") && !videoResStr.TrimStart().StartsWith("[")))
                {
                    throw new Exception("请求被彻底风控，返回了 HTML。请先点击右上角登录！");
                }

                using var videoDoc = JsonDocument.Parse(videoResStr);
                int code = videoDoc.RootElement.GetProperty("code").GetInt32();

                if (code == 0)
                {
                    var vlist = videoDoc.RootElement.GetProperty("data").GetProperty("list").GetProperty("vlist");
                    var list = new List<VideoItem>();
                    foreach (var item in vlist.EnumerateArray())
                    {
                        string pic = item.TryGetProperty("pic", out var coverProp) ? coverProp.GetString() : "";
                        if (!string.IsNullOrEmpty(pic) && !pic.StartsWith("http")) pic = "https:" + pic;

                        list.Add(new VideoItem
                        {
                            Bvid = item.GetProperty("bvid").GetString(),
                            Title = item.GetProperty("title").GetString(),
                            Author = TxtSpaceName.Text,
                            PicUrl = pic,
                            Duration = FormatDuration(item.TryGetProperty("length", out var durProp) ? GetSafeInt(durProp) : 0),
                            PlayCount = FormatCount(item.TryGetProperty("play", out var viewProp) ? GetSafeInt(viewProp) : 0),
                            DanmakuCount = FormatCount(item.TryGetProperty("video_review", out var dmProp) ? GetSafeInt(dmProp) : 0)
                        });
                    }
                    SpaceVideoGrid.ItemsSource = list;
                    if (list.Count == 0) TxtSpaceLoading.Text = "该UP主暂无投稿视频";
                    else TxtSpaceLoading.Visibility = Visibility.Collapsed;
                }
                else
                {
                    string msg = videoDoc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : "未知";
                    throw new Exception($"接口返回错误 (Code: {code}): {msg}");
                }
            }
            catch (Exception ex)
            {
                TxtSpaceLoading.Text = $"加载失败: {ex.Message}";
            }
        }

        private async void DetailAuthor_Click(object sender, MouseButtonEventArgs e)
        {
            if (string.IsNullOrEmpty(currentUpMid)) return;
            ResetPlayerState();
            _spacePreviousView = "PlayView";
            await LoadSpaceAsync(currentUpMid, currentUpName);
        }

        private void BtnBackFromSpace_Click(object sender, RoutedEventArgs e)
        {
            SpaceView.Visibility = Visibility.Collapsed;
            if (_spacePreviousView == "PlayView") PlayView.Visibility = Visibility.Visible;
            else ListViewer.Visibility = Visibility.Visible;
        }

        // ================= 播放器与弹幕 =================
        private void MediaPlayer_TimeChanged(object sender, MediaPlayerTimeChangedEventArgs e)
        {
            Dispatcher.InvokeAsync(() => {
                if (!_isDragging)
                {
                    if (SliderProgress.Maximum < e.Time) SliderProgress.Maximum = e.Time;
                    SliderProgress.Value = Math.Max(SliderProgress.Minimum, Math.Min(e.Time, SliderProgress.Maximum));
                    TxtTime.Text = $"{TimeSpan.FromMilliseconds(e.Time):mm\\:ss} / {TimeSpan.FromMilliseconds((long)SliderProgress.Maximum):mm\\:ss}";

                    if (PlayView.Visibility == Visibility.Visible && !currentBvid.StartsWith("LIVE_"))
                        CheckAndTriggerDanmaku(e.Time / 1000.0);
                }
            });
        }

        private void CheckAndTriggerDanmaku(double currentTimeSec)
        {
            if (_lastDanmakuTime >= 0 && currentTimeSec < _lastDanmakuTime - 0.5) _lastDanmakuTime = currentTimeSec - 0.5;

            var pending = _currentDanmakuList
                .Where(d => d.TimeSec <= currentTimeSec + 0.15 && d.TimeSec > _lastDanmakuTime)
                .Take(20)
                .ToList();

            foreach (var dm in pending) AddDanmakuToCanvas(dm.Text, dm.Color);
            _lastDanmakuTime = currentTimeSec;
        }

        private void AddDanmakuToCanvas(string text, int colorInt)
        {
            if (!_danmakuVisible || PlayView.Visibility != Visibility.Visible) return;
            try
            {
                Color color = Colors.White;
                if (colorInt > 0) color = Color.FromRgb((byte)(colorInt >> 16), (byte)(colorInt >> 8), (byte)colorInt);

                TextBlock tb = new TextBlock
                {
                    Text = text,
                    Foreground = new SolidColorBrush(color),
                    FontSize = 20,
                    FontWeight = FontWeights.Bold,
                    Opacity = _danmakuOpacity,
                    Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 4, ShadowDepth = 1 }
                };

                DanmakuCanvas.Children.Add(tb);
                tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double textWidth = tb.DesiredSize.Width;
                double canvasWidth = DanmakuCanvas.ActualWidth > 0 ? DanmakuCanvas.ActualWidth : 800;
                double canvasHeight = DanmakuCanvas.ActualHeight > 0 ? DanmakuCanvas.ActualHeight : 450;
                double usableHeight = canvasHeight * 0.8;

                int lane = -1;
                int currentMs = _mediaPlayer != null ? (int)_mediaPlayer.Time : 0;
                for (int i = 0; i < _laneEndTimes.Length; i++) { if (_laneEndTimes[i] <= currentMs) { lane = i; break; } }
                if (lane == -1) lane = _rng.Next(0, _laneEndTimes.Length);

                double laneHeight = Math.Min(30.0, usableHeight / Math.Max(1, _laneEndTimes.Length));
                double top = lane * laneHeight + 8;
                if (top > canvasHeight - 30) top = canvasHeight - 30;
                if (top < 8) top = 8;

                Canvas.SetLeft(tb, canvasWidth);
                Canvas.SetTop(tb, top);

                double speed = (canvasWidth + textWidth) / 8.0;
                _laneEndTimes[lane] = currentMs + (int)((textWidth + 50) / speed * 1000);

                DoubleAnimation anim = new DoubleAnimation { From = canvasWidth, To = -textWidth - 20, Duration = TimeSpan.FromSeconds((canvasWidth + textWidth) / speed) };
                var clock = anim.CreateClock();
                tb.Tag = clock;
                tb.ApplyAnimationClock(Canvas.LeftProperty, clock);
                if (_mediaPlayer != null && !_mediaPlayer.IsPlaying) clock.Controller.Pause();
                clock.Completed += (s, e) => DanmakuCanvas.Children.Remove(tb);
            }
            catch { }
        }

        private void ChkDanmakuEnabled_Checked(object sender, RoutedEventArgs e) => _danmakuVisible = true;
        private void ChkDanmakuEnabled_Unchecked(object sender, RoutedEventArgs e) { _danmakuVisible = false; DanmakuCanvas.Children.Clear(); }

        private async Task LoadVideoDanmakuAsync(string cid)
        {
            _currentDanmakuList.Clear();
            await Dispatcher.InvokeAsync(() => DanmakuCanvas.Children.Clear());
            _lastDanmakuTime = -1;
            if (string.IsNullOrWhiteSpace(cid)) return;

            try
            {
                string url = $"https://api.bilibili.com/x/v1/dm/list.so?oid={Uri.EscapeDataString(cid)}";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", UA);
                request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
                string cookie = BuildBiliCookie();
                if (!string.IsNullOrWhiteSpace(cookie)) request.Headers.TryAddWithoutValidation("Cookie", cookie);

                using var res = await client.SendAsync(request);
                string xml = await res.Content.ReadAsStringAsync();

                if (res.IsSuccessStatusCode && xml.Contains("<d "))
                {
                    XDocument doc = XDocument.Parse(xml);
                    foreach (var d in doc.Descendants("d"))
                    {
                        string pAttr = d.Attribute("p")?.Value;
                        if (string.IsNullOrWhiteSpace(pAttr)) continue;
                        string[] p = pAttr.Split(',');
                        if (p.Length >= 4 && double.TryParse(p[0], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double timeSec) && int.TryParse(p[3], out int colorInt))
                            _currentDanmakuList.Add(new DanmakuItem { TimeSec = timeSec, Text = d.Value, Color = colorInt });
                    }
                }
            }
            catch { }

            if (_mediaPlayer != null && _mediaPlayer.Time > 0)
                _lastDanmakuTime = _mediaPlayer.Time / 1000.0 - 2.0;
        }

        private string FormatCount(int count) => count >= 100000000 ? (count / 100000000.0).ToString("0.#") + "亿" : count >= 10000 ? (count / 10000.0).ToString("0.#") + "万" : count.ToString();
        private string FormatDuration(int sec) => sec >= 3600 ? TimeSpan.FromSeconds(sec).ToString(@"hh\:mm\:ss") : TimeSpan.FromSeconds(sec).ToString(@"mm\:ss");

        // ================= WBI 签名 =================
        private async Task<string> GetWbiKeysAsync()
        {
            if (_mixinKey != null) return _mixinKey;
            try
            {
                var res = await client.GetStringAsync("https://api.bilibili.com/x/web-interface/nav");
                using var doc = JsonDocument.Parse(res);
                var wbiImg = doc.RootElement.GetProperty("data").GetProperty("wbi_img");
                string imgKey = wbiImg.GetProperty("img_url").GetString().Split('/').Last().Split('.')[0];
                string subKey = wbiImg.GetProperty("sub_url").GetString().Split('/').Last().Split('.')[0];
                string rawKey = imgKey + subKey;
                int[] mixinKeyEncTab = { 46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49, 33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13, 37, 48, 7, 16, 24, 55, 40, 61, 26, 17, 0, 1, 60, 51, 30, 4, 22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11, 36, 20, 34, 44, 52 };
                StringBuilder mixinKeyBuilder = new StringBuilder();
                foreach (int i in mixinKeyEncTab) { if (i < rawKey.Length) mixinKeyBuilder.Append(rawKey[i]); }
                _mixinKey = mixinKeyBuilder.ToString().Substring(0, 32);
                return _mixinKey;
            }
            catch { return null; }
        }

        private async Task<string> SignWbiParamsAsync(Dictionary<string, string> parameters)
        {
            string mixinKey = await GetWbiKeysAsync();
            if (string.IsNullOrEmpty(mixinKey)) return null;
            parameters["wts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var sorted = parameters.OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
            var queryParts = new List<string>();
            foreach (var kv in sorted) { string value = kv.Value.Replace("!", "").Replace("'", "").Replace("(", "").Replace(")", "").Replace("*", ""); queryParts.Add($"{kv.Key}={Uri.EscapeDataString(value)}"); }
            string query = string.Join("&", queryParts);
            using var md5 = MD5.Create();
            byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(query + mixinKey));
            parameters["w_rid"] = string.Concat(hash.Select(b => b.ToString("x2")));
            var finalParts = new List<string>();
            foreach (var kv in parameters.OrderBy(p => p.Key, StringComparer.Ordinal)) { finalParts.Add($"{kv.Key}={Uri.EscapeDataString(kv.Value)}"); }
            return string.Join("&", finalParts);
        }

        // ================= 数据加载 =================
        private async Task LoadHomeAsync(int page, bool reset)
        {
            if (isLoading) return;
            isLoading = true;
            currentViewMode = "Home";
            ListViewer.Visibility = Visibility.Visible;
            PlayView.Visibility = Visibility.Collapsed;
            SpaceView.Visibility = Visibility.Collapsed;
            LiveCategoryPanel.Visibility = Visibility.Collapsed;
            TxtLoading.Visibility = Visibility.Visible;
            TxtLoading.Text = "加载推荐中...";
            try
            {
                var paramsDict = new Dictionary<string, string> { { "fresh_type", "78" }, { "ps", "20" }, { "fresh_idx", page.ToString() }, { "feed_version", "V8" } };
                string signedQuery = await SignWbiParamsAsync(paramsDict);

                var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.bilibili.com/x/web-interface/wbi/index/top/feed/rcmd?{signedQuery}");
                request.Headers.TryAddWithoutValidation("User-Agent", UA);
                request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
                string cookie = BuildBiliCookie();
                if (!string.IsNullOrWhiteSpace(cookie)) request.Headers.TryAddWithoutValidation("Cookie", cookie);

                var response = await client.SendAsync(request);
                string res = await response.Content.ReadAsStringAsync();

                using var doc = JsonDocument.Parse(res);
                var data = doc.RootElement.GetProperty("data");
                var list = new List<VideoItem>();
                if (data.TryGetProperty("item", out var items))
                {
                    foreach (var jsonItem in items.EnumerateArray())
                    {
                        if (!jsonItem.TryGetProperty("bvid", out var bvidProp) || string.IsNullOrEmpty(bvidProp.GetString())) continue;
                        string pic = jsonItem.TryGetProperty("pic", out var picProp) ? picProp.GetString() : "";
                        if (!string.IsNullOrEmpty(pic) && !pic.StartsWith("http")) pic = "https:" + pic;
                        int dur = jsonItem.TryGetProperty("duration", out var durProp) && durProp.ValueKind == JsonValueKind.Number ? durProp.GetInt32() : 0;
                        list.Add(new VideoItem { Bvid = bvidProp.GetString(), Title = jsonItem.GetProperty("title").GetString(), Author = jsonItem.GetProperty("owner").GetProperty("name").GetString(), PicUrl = pic, Duration = FormatDuration(dur), PlayCount = FormatCount(jsonItem.GetProperty("stat").GetProperty("view").GetInt32()), DanmakuCount = "0" });
                    }
                }
                if (reset) { VideoGrid.ItemsSource = list; ListViewer.ScrollToTop(); } else { var currentList = (List<VideoItem>)VideoGrid.ItemsSource; currentList.AddRange(list); VideoGrid.ItemsSource = null; VideoGrid.ItemsSource = currentList; }
            }
            catch { }
            finally { isLoading = false; TxtLoading.Visibility = Visibility.Collapsed; }
        }

        // ================= 排行榜 =================
        private async void BtnNavRank_Click(object sender, RoutedEventArgs e)
        {
            NavRank.IsChecked = true;
            ResetPlayerState();
            currentViewMode = "Rank";
            ListViewer.Visibility = Visibility.Visible;
            PlayView.Visibility = Visibility.Collapsed;
            SpaceView.Visibility = Visibility.Collapsed;
            LiveCategoryPanel.Visibility = Visibility.Collapsed;

            TxtLoading.Visibility = Visibility.Visible;
            TxtLoading.Text = "加载排行榜中...";
            VideoGrid.ItemsSource = null;

            try
            {
                string url = "https://api.bilibili.com/x/web-interface/ranking/v2";
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", UA);
                request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
                string cookie = BuildBiliCookie();
                if (!string.IsNullOrWhiteSpace(cookie)) request.Headers.TryAddWithoutValidation("Cookie", cookie);

                var response = await client.SendAsync(request);
                string resStr = await response.Content.ReadAsStringAsync();
                if (resStr.TrimStart().StartsWith("<")) throw new Exception("排行榜接口被风控");

                using var doc = JsonDocument.Parse(resStr);
                if (doc.RootElement.GetProperty("code").GetInt32() == 0)
                {
                    var data = doc.RootElement.GetProperty("data").GetProperty("list");
                    var list = new List<VideoItem>();
                    foreach (var item in data.EnumerateArray())
                    {
                        string pic = item.GetProperty("pic").GetString();
                        if (!string.IsNullOrEmpty(pic) && !pic.StartsWith("http")) pic = "https:" + pic;

                        list.Add(new VideoItem
                        {
                            Bvid = item.GetProperty("bvid").GetString(),
                            Title = item.GetProperty("title").GetString(),
                            Author = item.GetProperty("owner").GetProperty("name").GetString(),
                            PicUrl = pic,
                            Duration = FormatDuration(item.GetProperty("duration").GetInt32()),
                            PlayCount = FormatCount(item.GetProperty("stat").GetProperty("view").GetInt32()),
                            DanmakuCount = FormatCount(item.GetProperty("stat").GetProperty("danmaku").GetInt32())
                        });
                    }
                    VideoGrid.ItemsSource = list;
                    ListViewer.ScrollToTop();
                }
            }
            catch (Exception ex) { MessageBox.Show($"获取排行榜失败: {ex.Message}"); }
            finally { TxtLoading.Visibility = Visibility.Collapsed; }
        }

        private async Task LoadLiveAsync(int parentAreaId = 0)
        {
            if (isLoading) return;
            isLoading = true;
            currentViewMode = "Live";
            ListViewer.Visibility = Visibility.Visible;
            PlayView.Visibility = Visibility.Collapsed;
            SpaceView.Visibility = Visibility.Collapsed;
            LiveCategoryPanel.Visibility = Visibility.Visible;
            TxtLoading.Visibility = Visibility.Visible;
            TxtLoading.Text = "加载直播中...";
            try
            {
                if (string.IsNullOrEmpty(buvid3)) await EnsureBuvid3Cookie();

                string url = parentAreaId == 0
                    ? "https://api.live.bilibili.com/xlive/web-interface/v1/webMain/getMoreRecList?platform=web&web_location=333.1007"
                    : $"https://api.live.bilibili.com/room/v1/area/getRoomList?platform=web&parent_area_id={parentAreaId}&area_id=0&sort_type=online&page=1&page_size=30";

                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", UA);
                request.Headers.TryAddWithoutValidation("Referer", "https://live.bilibili.com/");
                request.Headers.TryAddWithoutValidation("Origin", "https://live.bilibili.com");
                string cookie = BuildBiliCookie();
                if (!string.IsNullOrWhiteSpace(cookie)) request.Headers.TryAddWithoutValidation("Cookie", cookie);

                var res = await client.SendAsync(request);
                string resStr = await res.Content.ReadAsStringAsync();
                if (resStr.TrimStart().StartsWith("<")) throw new Exception("请求被风控拦截。");

                using var doc = JsonDocument.Parse(resStr);
                if (doc.RootElement.TryGetProperty("code", out var codeProp) && codeProp.GetInt32() != 0)
                    throw new Exception($"接口异常，Code: {codeProp.GetInt32()}");

                var list = new List<VideoItem>();
                var data = doc.RootElement.GetProperty("data");

                if (parentAreaId == 0 && data.TryGetProperty("recommend_room_list", out var rooms))
                {
                    foreach (var room in rooms.EnumerateArray())
                    {
                        string cover = room.GetProperty("cover").GetString();
                        if (!cover.StartsWith("http")) cover = "https:" + cover;
                        list.Add(new VideoItem { Bvid = "LIVE_" + room.GetProperty("roomid").GetInt64(), Title = room.GetProperty("title").GetString(), Author = room.GetProperty("uname").GetString(), PicUrl = cover, Duration = "直播中", PlayCount = FormatCount(room.GetProperty("online").GetInt32()), DanmakuCount = "0" });
                    }
                }
                else if (data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var room in data.EnumerateArray())
                    {
                        string cover = room.TryGetProperty("cover", out var c) ? c.GetString() : "";
                        if (!string.IsNullOrEmpty(cover) && !cover.StartsWith("http")) cover = "https:" + cover;
                        long roomId = room.TryGetProperty("roomid", out var ridProp) ? ridProp.GetInt64() : 0;
                        if (roomId == 0 && room.TryGetProperty("room_id", out var ridProp2)) roomId = ridProp2.GetInt64();
                        if (roomId > 0) list.Add(new VideoItem { Bvid = "LIVE_" + roomId, Title = room.GetProperty("title").GetString(), Author = room.GetProperty("uname").GetString(), PicUrl = cover, Duration = "直播中", PlayCount = FormatCount(room.TryGetProperty("online", out var onProp) ? onProp.GetInt32() : 0), DanmakuCount = "0" });
                    }
                }

                if (list.Count == 0) throw new Exception("直播数据为空");
                VideoGrid.ItemsSource = list;
                ListViewer.ScrollToTop();
            }
            catch (Exception ex) { MessageBox.Show($"直播加载失败: {ex.Message}"); }
            finally { isLoading = false; TxtLoading.Visibility = Visibility.Collapsed; }
        }

        private async void BtnLiveCategory_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && int.TryParse(btn.Tag.ToString(), out int parentAreaId)) await LoadLiveAsync(parentAreaId);
        }

        private async Task LoadDetailsAsync(string bvid)
        {
            try
            {
                var paramsDict = new Dictionary<string, string> { { "bvid", bvid } };
                string signedQuery = await SignWbiParamsAsync(paramsDict);

                var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.bilibili.com/x/web-interface/wbi/view?{signedQuery}");
                request.Headers.TryAddWithoutValidation("User-Agent", UA);
                request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
                string cookie = BuildBiliCookie();
                if (!string.IsNullOrWhiteSpace(cookie)) request.Headers.TryAddWithoutValidation("Cookie", cookie);

                var response = await client.SendAsync(request);
                string res = await response.Content.ReadAsStringAsync();
                if (res.TrimStart().StartsWith("<")) throw new Exception("详情被风控拦截");

                using var doc = JsonDocument.Parse(res);
                if (doc.RootElement.GetProperty("code").GetInt32() != 0) throw new Exception("解析失败");

                var data = doc.RootElement.GetProperty("data");
                currentAid = data.GetProperty("aid").GetInt64().ToString();
                currentCid = data.GetProperty("cid").GetInt64().ToString();
                currentUpMid = data.GetProperty("owner").GetProperty("mid").GetInt64().ToString();
                currentUpName = data.GetProperty("owner").GetProperty("name").GetString();

                DetailTitle.Text = data.GetProperty("title").GetString();
                DetailAuthor.Text = "UP主: " + currentUpName;
                DetailDesc.Text = data.TryGetProperty("desc", out var descProp) ? descProp.GetString() : "暂无简介";

                await PlayVideoAsync(bvid, 127);
                await LoadCommentsAsync(currentAid);
            }
            catch (Exception ex) { DetailTitle.Text = "加载失败"; DetailAuthor.Text = ex.Message; DetailDesc.Text = ""; }
        }

        // ================= 视频播放 =================
        private async Task PlayVideoAsync(string bvid, int qn = 127)
        {
            if (isPlayingTaskRunning) return;
            isPlayingTaskRunning = true;
            try
            {
                _ = LoadVideoDanmakuAsync(currentCid);
                string videoUrl = "";
                string audioUrl = "";
                string errorMsg = "";

                try
                {
                    string videoPageUrl = $"https://www.bilibili.com/video/{bvid}/";
                    string encodedUrl = Uri.EscapeDataString(videoPageUrl);
                    string apiUrl = $"https://api.mir6.com/api/bzjiexi?url={encodedUrl}&type=json";

                    Console.WriteLine($"\n========== [解析请求] 第三方接口 ==========");
                    Console.WriteLine($"请求地址: {apiUrl}");

                    var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                    request.Headers.TryAddWithoutValidation("User-Agent", UA);

                    var res = await client.SendAsync(request);
                    string jsonStr = await res.Content.ReadAsStringAsync();

                    Console.WriteLine($"返回数据: {jsonStr}");

                    if (!string.IsNullOrWhiteSpace(jsonStr) && !jsonStr.TrimStart().StartsWith("<"))
                    {
                        using var doc = JsonDocument.Parse(jsonStr);
                        int code = doc.RootElement.TryGetProperty("code", out var codeProp) ? codeProp.GetInt32() : -1;

                        if (code == 200 && doc.RootElement.TryGetProperty("data", out var dataArray) && dataArray.GetArrayLength() > 0)
                        {
                            var firstVideo = dataArray[0];
                            if (firstVideo.TryGetProperty("video_url", out var urlProp))
                            {
                                videoUrl = urlProp.GetString();
                            }
                        }
                        else
                        {
                            string msg = doc.RootElement.TryGetProperty("msg", out var msgProp) ? msgProp.GetString() : "未知错误";
                            errorMsg = $"第三方解析失败: {msg} (Code: {code})";
                        }
                    }
                }
                catch (Exception ex) { errorMsg += $" 第三方解析异常: {ex.Message};"; }

                if (string.IsNullOrEmpty(videoUrl))
                {
                    try
                    {
                        var paramsDict = new Dictionary<string, string> {
                            { "bvid", bvid }, { "cid", currentCid }, { "qn", qn.ToString() },
                            { "fnval", "16" }, { "fourk", "1" }, { "try_look", "1" }
                        };
                        string signedQuery = await SignWbiParamsAsync(paramsDict);
                        string apiUrl = $"https://api.bilibili.com/x/player/wbi/playurl?{signedQuery}";

                        Console.WriteLine($"\n========== [解析请求] 官方 WBI 接口 ==========");
                        Console.WriteLine($"请求地址: {apiUrl}");

                        var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                        request.Headers.TryAddWithoutValidation("User-Agent", UA);
                        request.Headers.TryAddWithoutValidation("Referer", $"https://www.bilibili.com/video/{bvid}");
                        string cookie = BuildBiliCookie();
                        if (!string.IsNullOrWhiteSpace(cookie)) request.Headers.TryAddWithoutValidation("Cookie", cookie);

                        var res = await client.SendAsync(request);
                        string responseStr = await res.Content.ReadAsStringAsync();

                        Console.WriteLine($"返回数据: {responseStr}");

                        if (!string.IsNullOrWhiteSpace(responseStr) && !responseStr.TrimStart().StartsWith("<"))
                        {
                            using var playDoc = JsonDocument.Parse(responseStr);
                            if (playDoc.RootElement.GetProperty("code").GetInt32() == 0 && playDoc.RootElement.TryGetProperty("data", out var playData))
                            {
                                if (playData.TryGetProperty("dash", out var dash))
                                {
                                    if (dash.TryGetProperty("video", out var videos) && videos.GetArrayLength() > 0)
                                    {
                                        videoUrl = videos[0].GetProperty("baseUrl").GetString();
                                    }
                                    if (dash.TryGetProperty("audio", out var audios) && audios.GetArrayLength() > 0)
                                    {
                                        audioUrl = audios[0].GetProperty("baseUrl").GetString();
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex) { Console.WriteLine($"[解析] 官方异常: {ex.Message}"); }
                }

                if (string.IsNullOrEmpty(videoUrl))
                {
                    throw new Exception($"视频直链解析失败！\n详细信息: {errorMsg}\n\n原因：第三方接口和官方接口均无法获取链接。");
                }

                Console.WriteLine($"\n========== [解析成功] 准备播放 ==========");
                Console.WriteLine($"完整视频地址: {videoUrl}");
                if (!string.IsNullOrEmpty(audioUrl))
                {
                    Console.WriteLine($"完整音频地址: {audioUrl}");
                }
                Console.WriteLine($"==========================================\n");

                var newMedia = new LibVLCSharp.Shared.Media(_libVlc, new Uri(videoUrl));
                if (!string.IsNullOrEmpty(audioUrl)) newMedia.AddOption(":input-slave=" + audioUrl);

                newMedia.AddOption(":network-caching=2000");
                newMedia.AddOption(":http-referrer=https://www.bilibili.com/");
                newMedia.AddOption(":http-user-agent=" + UA);

                _mediaPlayer.Play(newMedia);
                BtnPlayPause.Content = "⏸";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n[播放失败] {ex.Message}");
                MessageBox.Show(ex.Message, "视频播放失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { isPlayingTaskRunning = false; }
        }

        private async Task PlayLiveAsync(string roomId)
        {
            try
            {
                string url = $"https://api.live.bilibili.com/xlive/web-room/v2/index/getRoomPlayInfo?room_id={roomId}&protocol=0,1&format=0,1,2&codec=0,1&qn=10000&platform=web";
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", UA);
                request.Headers.TryAddWithoutValidation("Referer", "https://live.bilibili.com/");
                string cookie = BuildBiliCookie();
                if (!string.IsNullOrWhiteSpace(cookie)) request.Headers.TryAddWithoutValidation("Cookie", cookie);

                var res = await client.SendAsync(request);
                string resStr = await res.Content.ReadAsStringAsync();
                if (resStr.TrimStart().StartsWith("<")) throw new Exception("播放接口被风控");

                using var doc = JsonDocument.Parse(resStr);
                var data = doc.RootElement.GetProperty("data");
                if (!data.TryGetProperty("playurl_info", out var playurlInfo)) throw new Exception("主播未开播");

                var stream = playurlInfo.GetProperty("playurl").GetProperty("stream")[0].GetProperty("format")[0].GetProperty("codec")[0];
                string liveUrl = stream.GetProperty("url_info")[0].GetProperty("host").GetString() + stream.GetProperty("base_url").GetString() + Regex.Unescape(stream.GetProperty("url_info")[0].GetProperty("extra").GetString());

                var newMedia = new LibVLCSharp.Shared.Media(_libVlc, new Uri(liveUrl));
                newMedia.AddOption(":http-referrer=https://live.bilibili.com/");
                newMedia.AddOption(":http-user-agent=" + UA);
                _mediaPlayer.Play(newMedia);
                BtnPlayPause.Content = "⏸";

                _danmakuClient = new BiliDanmakuClient($"SESSDATA={sessData}; bili_jct={biliJct}; buvid3={buvid3}");
                _danmakuClient.OnDanmakuReceived += (uname, msg) => Dispatcher.InvokeAsync(() => AddDanmakuToCanvas(msg, 0));
                await _danmakuClient.ConnectAsync(int.Parse(roomId));
            }
            catch (Exception ex) { MessageBox.Show($"直播播放失败: {ex.Message}"); }
        }

        // ================= 评论与发送 =================
        private void UpdateCommentMoreButton()
        {
            BtnLoadMoreComments.Visibility = !string.IsNullOrEmpty(commentPaginationOffset) ? Visibility.Visible : Visibility.Collapsed;
            BtnLoadMoreComments.IsEnabled = !isCommentLoading;
            BtnLoadMoreComments.Content = isCommentLoading ? "加载中..." : "加载更多评论";
        }

        private async Task LoadCommentsAsync(string aid, bool isLoadMore = false)
        {
            if (isCommentLoading || string.IsNullOrWhiteSpace(aid)) return;
            isCommentLoading = true;
            if (!isLoadMore) { commentPaginationOffset = ""; _commentTotal = 0; _comments.Clear(); }
            UpdateCommentMoreButton();

            try
            {
                var paramsDict = new Dictionary<string, string> {
                    { "oid", aid }, { "type", "1" }, { "mode", "3" },
                    { "pagination_str", isLoadMore ? $"{{\"offset\":\"{commentPaginationOffset}\"}}" : "{\"offset\":\"\"}" },
                    { "plat", "1" }, { "web_location", "1315875" }
                };
                string signedQuery = await SignWbiParamsAsync(paramsDict);
                var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.bilibili.com/x/v2/reply/wbi/main?{signedQuery}");
                request.Headers.TryAddWithoutValidation("User-Agent", UA);
                request.Headers.TryAddWithoutValidation("Referer", $"https://www.bilibili.com/video/{currentBvid}");
                string cookie = BuildBiliCookie();
                if (!string.IsNullOrWhiteSpace(cookie)) request.Headers.TryAddWithoutValidation("Cookie", cookie);

                var res = await client.SendAsync(request);
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                var data = doc.RootElement.GetProperty("data");

                if (data.TryGetProperty("cursor", out var cursor) && cursor.TryGetProperty("all_count", out var allCount))
                    _commentTotal = allCount.GetInt32();

                var addComments = new Action<JsonElement>((replies) => {
                    foreach (var reply in replies.EnumerateArray())
                    {
                        if (!reply.TryGetProperty("member", out var member) || !reply.TryGetProperty("content", out var content)) continue;
                        _comments.Add(new CommentItem
                        {
                            UserName = member.GetProperty("uname").GetString(),
                            Message = content.GetProperty("message").GetString(),
                            LikeInfo = $"👍 {reply.GetProperty("like").GetInt32()}"
                        });
                    }
                });

                if (!isLoadMore && data.TryGetProperty("top_replies", out var tops) && tops.ValueKind == JsonValueKind.Array) addComments(tops);
                if (data.TryGetProperty("replies", out var replies) && replies.ValueKind == JsonValueKind.Array) addComments(replies);

                commentPaginationOffset = (data.TryGetProperty("cursor", out var c2) && c2.TryGetProperty("pagination_reply", out var p) && p.TryGetProperty("next_offset", out var n)) ? n.GetString() : "";
                TxtCommentHeader.Text = _commentTotal > 0 ? $"热门评论 · {_commentTotal}" : "热门评论";
            }
            catch { }
            finally { isCommentLoading = false; UpdateCommentMoreButton(); }
        }

        private void CommentList_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.OriginalSource is ScrollViewer sv && sv.VerticalOffset + sv.ViewportHeight >= sv.ExtentHeight - 24)
                BtnLoadMoreComments_Click(null, null);
        }

        private void BtnLoadMoreComments_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(commentPaginationOffset) && !isCommentLoading) _ = LoadCommentsAsync(currentAid, true);
        }

        private async void BtnSendComment_Click(object sender, RoutedEventArgs e)
        {
            string text = TxtCommentInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrEmpty(sessData) || _isSendingComment) return;

            _isSendingComment = true; BtnSendComment.IsEnabled = false;
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "https://api.bilibili.com/x/v2/reply/add")
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string> {
                        { "oid", currentAid }, { "type", "1" }, { "message", text }, { "plat", "1" }, { "csrf", biliJct }
                    })
                };
                request.Headers.TryAddWithoutValidation("User-Agent", UA);
                request.Headers.TryAddWithoutValidation("Referer", $"https://www.bilibili.com/video/{currentBvid}");
                request.Headers.TryAddWithoutValidation("Cookie", BuildBiliCookie());

                var res = await client.SendAsync(request);
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                if (doc.RootElement.GetProperty("code").GetInt32() != 0) throw new Exception(doc.RootElement.GetProperty("message").GetString());

                TxtCommentInput.Clear();
                _comments.Insert(0, new CommentItem { UserName = "我", Message = text, LikeInfo = "👍 0" });
                CommentList.ScrollIntoView(_comments[0]);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "评论失败"); }
            finally { _isSendingComment = false; BtnSendComment.IsEnabled = true; }
        }

        private async void BtnSendDanmaku_Click(object sender, RoutedEventArgs e)
        {
            string text = TxtDanmakuInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrEmpty(sessData) || _isSendingDanmaku) return;

            _isSendingDanmaku = true; BtnSendDanmaku.IsEnabled = false;
            try
            {
                int colorInt = (CmbDanmakuColor.SelectedItem as ComboBoxItem)?.Tag is string t ? int.Parse(t) : 16777215;
                var request = new HttpRequestMessage(HttpMethod.Post, "https://api.bilibili.com/x/v2/dm/post")
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string> {
                        { "type", "1" }, { "oid", currentCid }, { "msg", text }, { "bvid", currentBvid },
                        { "progress", Math.Max(0, _mediaPlayer.Time).ToString() }, { "mode", "1" }, { "fontsize", "25" },
                        { "color", colorInt.ToString() }, { "csrf", biliJct }, { "plat", "1" }
                    })
                };
                request.Headers.TryAddWithoutValidation("User-Agent", UA);
                request.Headers.TryAddWithoutValidation("Referer", $"https://www.bilibili.com/video/{currentBvid}");
                request.Headers.TryAddWithoutValidation("Cookie", BuildBiliCookie());

                var res = await client.SendAsync(request);
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                if (doc.RootElement.GetProperty("code").GetInt32() != 0) throw new Exception(doc.RootElement.GetProperty("message").GetString());

                TxtDanmakuInput.Clear();
                _currentDanmakuList.Add(new DanmakuItem { TimeSec = _mediaPlayer.Time / 1000.0, Text = text, Color = colorInt });
                AddDanmakuToCanvas(text, colorInt);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "弹幕失败"); }
            finally { _isSendingDanmaku = false; BtnSendDanmaku.IsEnabled = true; }
        }

        // ================= 导航与交互 =================
        private void BtnNavHome_Click(object sender, RoutedEventArgs e) { NavHome.IsChecked = true; ResetPlayerState(); _ = LoadHomeAsync(1, true); }
        private void BtnNavLive_Click(object sender, RoutedEventArgs e) { NavLive.IsChecked = true; ResetPlayerState(); _ = LoadLiveAsync(0); }

        private void VideoCard_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.DataContext is VideoItem selectedVideo)
            {
                currentBvid = selectedVideo.Bvid;
                ListViewer.Visibility = Visibility.Collapsed;
                SpaceView.Visibility = Visibility.Collapsed;
                PlayView.Visibility = Visibility.Visible;

                DetailTitle.Text = selectedVideo.Title; DetailAuthor.Text = "UP主: " + selectedVideo.Author;
                ResetPlayerState();
                if (selectedVideo.Bvid.StartsWith("LIVE_")) _ = PlayLiveAsync(selectedVideo.Bvid.Replace("LIVE_", ""));
                else _ = LoadDetailsAsync(selectedVideo.Bvid);
            }
        }

        private void ResetPlayerState()
        {
            _danmakuClient?.Close(); _danmakuClient = null;
            _currentDanmakuList.Clear();
            DanmakuCanvas.Children.Clear();
            _lastDanmakuTime = -1;
            _mediaPlayer?.Stop();

            BtnPlayPause.Content = "▶"; SliderProgress.Value = 0; TxtTime.Text = "00:00 / 00:00";
            DetailDesc.Text = ""; commentPaginationOffset = ""; isCommentLoading = false;
            _commentTotal = 0; _comments.Clear(); TxtCommentHeader.Text = "热门评论";
            TxtDanmakuInput.Clear(); TxtCommentInput.Clear(); UpdateCommentMoreButton();
        }

        private void BtnPlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaPlayer.IsPlaying)
            {
                _mediaPlayer.Pause(); BtnPlayPause.Content = "▶";
                foreach (UIElement child in DanmakuCanvas.Children) if (child is TextBlock tb && tb.Tag is AnimationClock clock) clock.Controller.Pause();
            }
            else
            {
                _mediaPlayer.Play(); BtnPlayPause.Content = "⏸";
                foreach (UIElement child in DanmakuCanvas.Children) if (child is TextBlock tb && tb.Tag is AnimationClock clock) clock.Controller.Resume();
            }
        }

        private void BtnBackToList_Click(object sender, RoutedEventArgs e)
        {
            ResetPlayerState();
            PlayView.Visibility = Visibility.Collapsed;
            ListViewer.Visibility = Visibility.Visible;
            LiveCategoryPanel.Visibility = currentViewMode == "Live" ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Slider_DragStarted(object sender, RoutedEventArgs e) => _isDragging = true;
        private void Slider_DragCompleted(object sender, RoutedEventArgs e) { _isDragging = false; if (_mediaPlayer.IsSeekable) { _mediaPlayer.Time = (long)SliderProgress.Value; _lastDanmakuTime = -1; } }
        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (_isDragging) TxtTime.Text = $"{TimeSpan.FromMilliseconds(e.NewValue):mm\\:ss} / {TimeSpan.FromMilliseconds((long)SliderProgress.Maximum):mm\\:ss}"; }
        private void CmbSpeed_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (_mediaPlayer != null && CmbSpeed.SelectedItem is ComboBoxItem item && float.TryParse(item.Content.ToString().Replace("x", ""), out float speed)) _mediaPlayer.SetRate(speed); }

        private async void BtnSearch_Click(object sender, RoutedEventArgs e)
        {
            string keyword = TxtSearch.Text.Trim();
            if (string.IsNullOrWhiteSpace(keyword)) return;

            // ★ 当点击搜索时，自动呼出 CMD 日志窗口
            ShowLogConsole();

            if (Regex.IsMatch(keyword, @"^[Bb][Vv]1[0-9a-zA-Z]{9}$"))
            {
                currentBvid = keyword; ListViewer.Visibility = Visibility.Collapsed; SpaceView.Visibility = Visibility.Collapsed; PlayView.Visibility = Visibility.Visible;
                ResetPlayerState(); _ = LoadDetailsAsync(keyword); return;
            }

            NavHome.IsChecked = false; NavLive.IsChecked = false; NavMine.IsChecked = false; NavRank.IsChecked = false;
            currentViewMode = "Search";
            ListViewer.Visibility = Visibility.Visible; PlayView.Visibility = Visibility.Collapsed; SpaceView.Visibility = Visibility.Collapsed;
            TxtLoading.Visibility = Visibility.Visible; TxtLoading.Text = $"正在搜索：{keyword}...";
            VideoGrid.ItemsSource = null;

            try
            {
                var paramsDict = new Dictionary<string, string> { { "keyword", keyword }, { "search_type", "video" }, { "page", "1" } };
                string signedQuery = await SignWbiParamsAsync(paramsDict);
                string apiUrl = $"https://api.bilibili.com/x/web-interface/wbi/search/type?{signedQuery}";

                Console.WriteLine($"\n========== [搜索请求] ==========");
                Console.WriteLine($"关键字: {keyword}");
                Console.WriteLine($"完整请求地址: {apiUrl}");

                var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                request.Headers.TryAddWithoutValidation("User-Agent", UA);
                request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
                string cookie = BuildBiliCookie();
                if (!string.IsNullOrWhiteSpace(cookie)) request.Headers.TryAddWithoutValidation("Cookie", cookie);

                var res = await client.SendAsync(request);
                string resStr = await res.Content.ReadAsStringAsync();

                Console.WriteLine($"\n========== [搜索返回] ==========");
                Console.WriteLine($"完整返回JSON: {resStr}");

                if (resStr.TrimStart().StartsWith("<")) throw new Exception("搜索被风控");

                using var doc = JsonDocument.Parse(resStr);
                var data = doc.RootElement.GetProperty("data");
                var list = new List<VideoItem>();

                if (data.TryGetProperty("result", out var results))
                {
                    foreach (var item in results.EnumerateArray())
                    {
                        string title = Regex.Replace(item.GetProperty("title").GetString() ?? "", "<.*?>", "");
                        string pic = item.GetProperty("pic").GetString() ?? "";
                        if (!string.IsNullOrEmpty(pic) && !pic.StartsWith("http")) pic = "https:" + pic;
                        list.Add(new VideoItem
                        {
                            Bvid = item.GetProperty("bvid").GetString(),
                            Title = title,
                            Author = item.GetProperty("author").GetString(),
                            PicUrl = pic,
                            Duration = item.GetProperty("duration").GetString(),
                            PlayCount = FormatCount(item.GetProperty("play").GetInt32()),
                            DanmakuCount = FormatCount(item.GetProperty("video_review").GetInt32())
                        });
                    }
                }
                VideoGrid.ItemsSource = list; ListViewer.ScrollToTop();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n[搜索失败] {ex.Message}");
                MessageBox.Show($"搜索失败: {ex.Message}");
            }
            finally { TxtLoading.Visibility = Visibility.Collapsed; }
        }

        private void ListScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e) { if (e.VerticalOffset == e.ExtentHeight - e.ViewportHeight && !isLoading && currentViewMode == "Home") { currentPage++; _ = LoadHomeAsync(currentPage, false); } }

        protected override void OnClosed(EventArgs e) { ResetPlayerState(); _mediaPlayer?.Dispose(); _libVlc?.Dispose(); base.OnClosed(e); }
    }

    public class BiliDanmakuClient
    {
        private System.Net.WebSockets.ClientWebSocket _ws;
        private System.Threading.CancellationTokenSource _cts;
        private string _cookie; private string _buvid; private bool _isRunning = false;
        public event Action<string, string> OnDanmakuReceived;

        public BiliDanmakuClient(string cookie)
        {
            _cookie = cookie;
            if (!string.IsNullOrEmpty(cookie)) foreach (var item in cookie.Split(';')) { if (item.Trim().StartsWith("buvid3=")) { _buvid = item.Trim().Substring(7); break; } }
        }

        public async Task ConnectAsync(int roomId)
        {
            if (_isRunning) return; _isRunning = true;
            _cts = new System.Threading.CancellationTokenSource();
            _ws = new System.Net.WebSockets.ClientWebSocket();
            if (!string.IsNullOrEmpty(_cookie)) _ws.Options.SetRequestHeader("Cookie", _cookie);
            try
            {
                var (host, port, token) = await GetDanmuInfoAsync(roomId);
                await _ws.ConnectAsync(new Uri($"wss://{host}:{port}/sub"), _cts.Token);
                var auth = new { uid = 0, roomid = roomId, protover = 3, buvid = _buvid ?? "", platform = "web", type = 2, key = token };
                await SendPacketAsync(JsonSerializer.Serialize(auth), 7, 1);
                _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
                _ = Task.Run(() => HeartbeatLoopAsync(_cts.Token));
            }
            catch { Close(); }
        }

        public void Close() { if (!_isRunning) return; _isRunning = false; _cts?.Cancel(); _ws?.Dispose(); }

        private async Task<(string, int, string)> GetDanmuInfoAsync(int roomId)
        {
            using var client = new HttpClient();
            if (!string.IsNullOrEmpty(_cookie)) client.DefaultRequestHeaders.Add("Cookie", _cookie);
            var res = await client.GetStringAsync($"https://api.live.bilibili.com/xlive/web-room/v1/index/getDanmuInfo?id={roomId}");
            using var doc = JsonDocument.Parse(res);
            var data = doc.RootElement.GetProperty("data");
            var host = data.GetProperty("host_list")[0];
            return (host.GetProperty("host").GetString(), host.GetProperty("wss_port").GetInt32(), data.GetProperty("token").GetString());
        }

        private async Task SendPacketAsync(string body, int op, int ver)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            byte[] packet = new byte[16 + bytes.Length];
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(0, 4), packet.Length);
            BinaryPrimitives.WriteInt16BigEndian(packet.AsSpan(4, 2), 16);
            BinaryPrimitives.WriteInt16BigEndian(packet.AsSpan(6, 2), (short)ver);
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(8, 4), op);
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(12, 4), 1);
            bytes.CopyTo(packet, 16);
            await _ws.SendAsync(new ArraySegment<byte>(packet), System.Net.WebSockets.WebSocketMessageType.Binary, true, _cts.Token);
        }

        private async Task HeartbeatLoopAsync(System.Threading.CancellationToken token) { while (!token.IsCancellationRequested) { try { await SendPacketAsync("", 2, 1); await Task.Delay(30000, token); } catch { break; } } }

        private async Task ReceiveLoopAsync(System.Threading.CancellationToken token)
        {
            byte[] buffer = new byte[16384];
            while (!token.IsCancellationRequested && _ws.State == System.Net.WebSockets.WebSocketState.Open)
            {
                try { var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), token); if (result.Count > 0) ParsePackets(buffer.Take(result.Count).ToArray()); } catch { break; }
            }
        }

        private void ParsePackets(byte[] data)
        {
            int offset = 0;
            while (offset < data.Length - 16)
            {
                int len = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4));
                int headLen = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(offset + 4, 2));
                int ver = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(offset + 6, 2));
                int op = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset + 8, 4));
                byte[] body = new byte[len - headLen];
                Array.Copy(data, offset + headLen, body, 0, body.Length);
                if (op == 5)
                {
                    if (ver == 2) Decompress(body, 2);
                    else if (ver == 3) Decompress(body, 3);
                    else ProcessJson(Encoding.UTF8.GetString(body));
                }
                offset += len;
            }
        }

        private void Decompress(byte[] data, int type)
        {
            try
            {
                using var ms = new MemoryStream(data);
                using var decomp = type == 2 ? (Stream)new ZLibStream(ms, CompressionMode.Decompress) : new BrotliStream(ms, CompressionMode.Decompress);
                using var outMs = new MemoryStream();
                decomp.CopyTo(outMs);
                ParsePackets(outMs.ToArray());
            }
            catch { }
        }

        private void ProcessJson(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("cmd", out var cmd) && cmd.GetString() == "DANMU_MSG")
                {
                    var info = doc.RootElement.GetProperty("info");
                    OnDanmakuReceived?.Invoke(info[2][1].GetString(), info[1].GetString());
                }
            }
            catch { }
        }
    }
}
