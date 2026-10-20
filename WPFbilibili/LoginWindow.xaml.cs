
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace BiliClient
{
    public partial class LoginWindow : Window
    {
        public string SessData { get; private set; } = string.Empty;
        public string BiliJct { get; private set; } = string.Empty;

        private bool _isCheckingCookies;
        private bool _isClosing;

        public LoginWindow()
        {
            InitializeComponent();
            Loaded += LoginWindow_Loaded;
        }

        private void LoginWindow_Loaded(object sender, RoutedEventArgs e)
        {
            InitializeWebViewAsync();
        }

        private async void InitializeWebViewAsync()
        {
            try
            {
                await WebView.EnsureCoreWebView2Async(null);

                if (WebView.CoreWebView2 == null)
                {
                    MessageBox.Show("WebView2 初始化失败，请检查 WebView2 运行时。",
                        "Bilibili 登录");
                    return;
                }

                WebView.CoreWebView2.Settings.UserAgent =
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                    "AppleWebKit/537.36 (KHTML, like Gecko) " +
                    "Chrome/126.0.0.0 Safari/537.36";

                WebView.CoreWebView2.NavigationCompleted +=
                    async (s, e) => await CheckCookiesAsync();

                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1.5)
                };

                timer.Tick += async (s, e) => await CheckCookiesAsync();
                timer.Start();

                Closed += (s, e) => timer.Stop();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "WebView2 初始化失败：" + ex.Message,
                    "Bilibili 登录");
            }
        }

        private async Task CheckCookiesAsync()
        {
            if (_isCheckingCookies || _isClosing)
                return;

            _isCheckingCookies = true;

            try
            {
                if (WebView.CoreWebView2 == null)
                    return;

                var cookies =
                    await WebView.CoreWebView2.CookieManager
                        .GetCookiesAsync("https://www.bilibili.com");

                var sess = cookies.FirstOrDefault(
                    c => c.Name == "SESSDATA");

                var jct = cookies.FirstOrDefault(
                    c => c.Name == "bili_jct");

                if (sess == null || jct == null ||
                    string.IsNullOrWhiteSpace(sess.Value) ||
                    string.IsNullOrWhiteSpace(jct.Value))
                {
                    return;
                }

                SessData = sess.Value;
                BiliJct = jct.Value;

                _isClosing = true;

                await Task.Delay(300);

                if (Dispatcher.CheckAccess())
                {
                    Close();
                }
                else
                {
                    await Dispatcher.InvokeAsync(Close);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "读取 Bilibili Cookie 失败：" + ex.Message);
            }
            finally
            {
                _isCheckingCookies = false;
            }
        }
    }
}
