using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace FeishuMinutes
{
    public partial class MainWindow : Window
    {
        private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(0, 103, 192));
        private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(15, 123, 15));
        private static readonly Brush WarningBrush = new SolidColorBrush(Color.FromRgb(157, 93, 0));
        private static readonly Brush DangerBrush = new SolidColorBrush(Color.FromRgb(196, 43, 28));
        private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(96, 96, 96));
        private static readonly Brush PendingBadgeBrush = new SolidColorBrush(Color.FromRgb(233, 233, 233));
        private static readonly Brush ActiveBadgeBrush = new SolidColorBrush(Color.FromRgb(220, 236, 248));
        private static readonly Brush DoneBadgeBrush = new SolidColorBrush(Color.FromRgb(221, 242, 221));

        private readonly MinutesDownloader _downloader = new MinutesDownloader();
        private CancellationTokenSource _cancellation;
        private string _lastOutputDirectory;
        private bool _isRunning;

        public MainWindow()
        {
            InitializeComponent();
            OutputTextBox.Text = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "minutes");
            AppendLog("准备就绪。请粘贴飞书妙记分享链接。");
            UrlTextBox.Focus();
        }

        private void Window_SourceInitialized(object sender, EventArgs e)
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            SetDwmAttribute(handle, 33, 2); // DWMWA_WINDOW_CORNER_PREFERENCE
            SetDwmAttribute(handle, 20, 0); // DWMWA_USE_IMMERSIVE_DARK_MODE
            SetDwmAttribute(handle, 38, 2); // DWMWA_SYSTEMBACKDROP_TYPE
        }

        private static void SetDwmAttribute(IntPtr handle, int attribute, int value)
        {
            try
            {
                DwmSetWindowAttribute(handle, attribute, ref value, Marshal.SizeOf(typeof(int)));
            }
            catch (DllNotFoundException)
            {
                // Windows 11 always provides dwmapi; keep a harmless fallback for tooling.
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        private void PasteButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    UrlTextBox.Text = Clipboard.GetText().Trim();
                    UrlTextBox.CaretIndex = UrlTextBox.Text.Length;
                    UrlTextBox.Focus();
                    return;
                }
            }
            catch (COMException)
            {
            }

            SetStatus("剪贴板为空", "请先复制飞书妙记分享链接", WarningBrush);
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new Forms.FolderBrowserDialog
            {
                Description = "选择字幕保存位置",
                ShowNewFolderButton = true,
                SelectedPath = Directory.Exists(OutputTextBox.Text) ? OutputTextBox.Text : string.Empty
            })
            {
                if (dialog.ShowDialog() == Forms.DialogResult.OK)
                {
                    OutputTextBox.Text = dialog.SelectedPath;
                }
            }
        }

        private async void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            await StartDownloadAsync();
        }

        private async void UrlTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await StartDownloadAsync();
            }
        }

        private async Task StartDownloadAsync()
        {
            if (_isRunning)
            {
                return;
            }

            if (!MinutesDownloader.TryParseShareUrl(UrlTextBox.Text, out _, out _))
            {
                SetStatus("链接格式不正确", "需要包含 /minutes/<token>", DangerBrush);
                MessageBox.Show(
                    this,
                    "请粘贴完整的飞书妙记分享链接，例如：\nhttps://example.feishu.cn/minutes/xxxxxxxx",
                    "链接格式不正确",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                UrlTextBox.Focus();
                return;
            }

            string outputRoot = Environment.ExpandEnvironmentVariables(OutputTextBox.Text.Trim());
            if (outputRoot.Length == 0)
            {
                SetStatus("请选择保存位置", "字幕需要一个输出目录", DangerBrush);
                return;
            }

            if (!int.TryParse(MaxCharactersTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int maxCharacters) ||
                maxCharacters < 8 || maxCharacters > 60)
            {
                MessageBox.Show(this, "每行字数应为 8–60。", "参数不正确", MessageBoxButton.OK, MessageBoxImage.Warning);
                MaxCharactersTextBox.Focus();
                return;
            }

            if (!int.TryParse(MaxDurationTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int maxDuration) ||
                maxDuration < 500 || maxDuration > 10000)
            {
                MessageBox.Show(this, "最长时间应为 500–10000 毫秒。", "参数不正确", MessageBoxButton.OK, MessageBoxImage.Warning);
                MaxDurationTextBox.Focus();
                return;
            }

            try
            {
                outputRoot = Path.GetFullPath(outputRoot);
                Directory.CreateDirectory(outputRoot);
                OutputTextBox.Text = outputRoot;
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is ArgumentException)
            {
                MessageBox.Show(this, exception.Message, "无法创建保存目录", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var request = new DownloadRequest
            {
                ShareUrl = UrlTextBox.Text.Trim(),
                OutputRoot = outputRoot,
                MaxCharacters = maxCharacters,
                MaxDurationMilliseconds = maxDuration,
                NameByTitle = NameByTitleCheckBox.IsChecked == true
            };

            _cancellation = new CancellationTokenSource();
            SetRunning(true);
            ClearLog();
            AppendLog("正在启动原生 HTTP 下载核心...");
            SetStatus("正在连接妙记", "建立匿名分享会话", AccentBrush);
            SetStep(1);

            try
            {
                var progress = new Progress<DownloadProgress>(OnDownloadProgress);
                DownloadResult result = await _downloader.DownloadAsync(request, _cancellation.Token, progress);
                _lastOutputDirectory = result.OutputDirectory;
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Value = 100;
                LogStateText.Text = "已完成";
                LogStateText.Foreground = SuccessBrush;
                SetStatus("字幕下载完成", result.SubtitleLineCount + " 行 SRT 已保存到本地", SuccessBrush);
                SetStep(3);
                AppendLog(string.Empty);
                AppendLog("完成：" + result.WordCount + " 个词/短语，" + result.SubtitleLineCount + " 行字幕。");
                System.Media.SystemSounds.Asterisk.Play();
            }
            catch (OperationCanceledException)
            {
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Value = 0;
                LogStateText.Text = "已取消";
                LogStateText.Foreground = WarningBrush;
                SetStatus("下载已取消", "可以修改设置后重新开始", WarningBrush);
                AppendLog(string.Empty);
                AppendLog("已取消本次下载。");
            }
            catch (Exception exception)
            {
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Value = 0;
                LogStateText.Text = "失败";
                LogStateText.Foreground = DangerBrush;
                SetStatus("下载失败", "请查看右侧运行记录", DangerBrush);
                AppendLog(string.Empty);
                AppendLog("[!] " + exception.Message);
                MessageBox.Show(this, exception.Message, "下载失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetRunning(false);
                _cancellation.Dispose();
                _cancellation = null;
            }
        }

        private void OnDownloadProgress(DownloadProgress update)
        {
            SetStatus(update.Status, update.Detail, update.Step >= 3 ? SuccessBrush : AccentBrush);
            SetStep(update.Step);
            if (!string.IsNullOrWhiteSpace(update.LogLine))
            {
                AppendLog(update.LogLine);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (_cancellation == null)
            {
                return;
            }

            SetStatus("正在取消", "等待当前网络请求安全退出", WarningBrush);
            _cancellation.Cancel();
        }

        private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            string target = _lastOutputDirectory;
            if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target))
            {
                target = OutputTextBox.Text.Trim();
            }

            if (!Directory.Exists(target))
            {
                try
                {
                    Directory.CreateDirectory(target);
                }
                catch (Exception exception)
                {
                    MessageBox.Show(this, exception.Message, "无法打开文件夹", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + target + "\"")
            {
                UseShellExecute = true
            });
        }

        private void SetRunning(bool running)
        {
            _isRunning = running;
            DownloadButton.IsEnabled = !running;
            CancelButton.IsEnabled = running;
            UrlTextBox.IsEnabled = !running;
            OutputTextBox.IsEnabled = !running;
            NameByTitleCheckBox.IsEnabled = !running;
            MaxCharactersTextBox.IsEnabled = !running;
            MaxDurationTextBox.IsEnabled = !running;

            if (running)
            {
                ProgressBar.Value = 0;
                ProgressBar.IsIndeterminate = true;
                LogStateText.Text = "运行中";
                LogStateText.Foreground = AccentBrush;
            }
        }

        private void SetStatus(string title, string detail, Brush colour)
        {
            StatusTitle.Text = title;
            StatusDetail.Text = detail;
            StatusDot.Fill = colour;
        }

        private void SetStep(int activeStep)
        {
            SetStepVisual(Step1Badge, Step1Text, 1, activeStep);
            SetStepVisual(Step2Badge, Step2Text, 2, activeStep);
            SetStepVisual(Step3Badge, Step3Text, 3, activeStep);
        }

        private static void SetStepVisual(System.Windows.Controls.Border badge, System.Windows.Controls.TextBlock text, int step, int activeStep)
        {
            if (step < activeStep)
            {
                badge.Background = DoneBadgeBrush;
                text.Foreground = SuccessBrush;
                text.FontWeight = FontWeights.Normal;
            }
            else if (step == activeStep)
            {
                badge.Background = ActiveBadgeBrush;
                text.Foreground = AccentBrush;
                text.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                badge.Background = PendingBadgeBrush;
                text.Foreground = MutedBrush;
                text.FontWeight = FontWeights.Normal;
            }
        }

        private void AppendLog(string line)
        {
            if (LogTextBox.Text.Length > 0)
            {
                LogTextBox.AppendText(Environment.NewLine);
            }
            LogTextBox.AppendText(line);
            LogTextBox.ScrollToEnd();
        }

        private void ClearLog()
        {
            LogTextBox.Clear();
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (!_isRunning)
            {
                return;
            }

            MessageBoxResult result = MessageBox.Show(
                this,
                "关闭窗口会终止当前下载，确定要退出吗？",
                "下载仍在进行",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            _cancellation?.Cancel();
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}
