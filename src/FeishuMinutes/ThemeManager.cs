using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace FeishuMinutes
{
    internal enum ThemeMode
    {
        System,
        Light,
        Dark
    }

    internal static class ThemeManager
    {
        private const string PersonalizeKey =
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        private static bool _initialized;
        private static bool _hasAppliedTheme;

        public static bool IsDark { get; private set; }
        public static ThemeMode CurrentMode { get; private set; } = ThemeMode.System;

        public static event EventHandler ThemeChanged;

        public static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            ApplySystemTheme();
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }

        public static void Shutdown()
        {
            if (!_initialized)
            {
                return;
            }

            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _initialized = false;
        }

        public static void CycleMode()
        {
            switch (CurrentMode)
            {
                case ThemeMode.System:
                    SetMode(ThemeMode.Light);
                    break;
                case ThemeMode.Light:
                    SetMode(ThemeMode.Dark);
                    break;
                default:
                    SetMode(ThemeMode.System);
                    break;
            }
        }

        public static void SetMode(ThemeMode mode)
        {
            if (CurrentMode == mode)
            {
                return;
            }

            CurrentMode = mode;
            bool dark = mode == ThemeMode.System ? ReadSystemDarkMode() : mode == ThemeMode.Dark;
            if (_hasAppliedTheme && IsDark == dark)
            {
                ThemeChanged?.Invoke(null, EventArgs.Empty);
                return;
            }

            ApplyTheme(dark);
        }

        private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            Application application = Application.Current;
            Dispatcher dispatcher = application?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
            {
                return;
            }

            if (dispatcher.CheckAccess())
            {
                ApplySystemTheme();
            }
            else
            {
                dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ApplySystemTheme));
            }
        }

        private static void ApplySystemTheme()
        {
            if (CurrentMode == ThemeMode.System)
            {
                ApplyTheme(ReadSystemDarkMode());
            }
        }

        private static bool ReadSystemDarkMode()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(PersonalizeKey))
                {
                    object value = key?.GetValue("AppsUseLightTheme");
                    return value != null && Convert.ToInt32(value) == 0;
                }
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException ||
                exception is InvalidCastException ||
                exception is FormatException ||
                exception is OverflowException)
            {
                return false;
            }
        }

        private static void ApplyTheme(bool dark)
        {
            if (_hasAppliedTheme && IsDark == dark)
            {
                return;
            }

            ResourceDictionary resources = Application.Current.Resources;
            IsDark = dark;

            if (dark)
            {
                SetBrush(resources, "WindowBrush", "#202020");
                SetBrush(resources, "SidebarBrush", "#191919");
                SetBrush(resources, "CardBrush", "#2B2B2B");
                SetBrush(resources, "ControlBackgroundBrush", "#292929");
                SetBrush(resources, "LogBackgroundBrush", "#252525");
                SetBrush(resources, "BorderBrush", "#3D3D3D");
                SetBrush(resources, "ControlBorderBrush", "#5A5A5A");
                SetBrush(resources, "ButtonBackgroundBrush", "#333333");
                SetBrush(resources, "ButtonBorderBrush", "#454545");
                SetBrush(resources, "ButtonHoverBrush", "#3B3B3B");
                SetBrush(resources, "ButtonPressedBrush", "#303030");
                SetBrush(resources, "TextBrush", "#F5F5F5");
                SetBrush(resources, "MutedTextBrush", "#C8C8C8");
                SetBrush(resources, "SubtleTextBrush", "#A0A0A0");
                SetBrush(resources, "LogTextBrush", "#E8E8E8");
                SetBrush(resources, "AccentBrush", "#0F6CBD");
                SetBrush(resources, "AccentHoverBrush", "#115EA3");
                SetBrush(resources, "AccentPressedBrush", "#0C3B5E");
                SetBrush(resources, "AccentForegroundBrush", "#FFFFFF");
                SetBrush(resources, "AccentTextBrush", "#75B6E7");
                SetBrush(resources, "SuccessBrush", "#6CCB5F");
                SetBrush(resources, "WarningBrush", "#F9A825");
                SetBrush(resources, "DangerBrush", "#FF99A4");
                SetBrush(resources, "StatusCardBrush", "#213444");
                SetBrush(resources, "PendingBadgeBrush", "#3A3A3A");
                SetBrush(resources, "ActiveBadgeBrush", "#17324A");
                SetBrush(resources, "DoneBadgeBrush", "#183F2A");
                SetBrush(resources, "MenuBackgroundBrush", "#2C2C2C");
                SetBrush(resources, "MenuBorderBrush", "#484848");
                SetBrush(resources, "MenuHoverBrush", "#3A3A3A");
            }
            else
            {
                SetBrush(resources, "WindowBrush", "#F3F3F3");
                SetBrush(resources, "SidebarBrush", "#FAFAFA");
                SetBrush(resources, "CardBrush", "#FFFFFF");
                SetBrush(resources, "ControlBackgroundBrush", "#FBFBFB");
                SetBrush(resources, "LogBackgroundBrush", "#FBFBFB");
                SetBrush(resources, "BorderBrush", "#E1E1E1");
                SetBrush(resources, "ControlBorderBrush", "#BDBDBD");
                SetBrush(resources, "ButtonBackgroundBrush", "#FBFBFB");
                SetBrush(resources, "ButtonBorderBrush", "#C7C7C7");
                SetBrush(resources, "ButtonHoverBrush", "#F3F3F3");
                SetBrush(resources, "ButtonPressedBrush", "#E9E9E9");
                SetBrush(resources, "TextBrush", "#1B1B1B");
                SetBrush(resources, "MutedTextBrush", "#606060");
                SetBrush(resources, "SubtleTextBrush", "#858585");
                SetBrush(resources, "LogTextBrush", "#303030");
                SetBrush(resources, "AccentBrush", "#0F6CBD");
                SetBrush(resources, "AccentHoverBrush", "#115EA3");
                SetBrush(resources, "AccentPressedBrush", "#0C3B5E");
                SetBrush(resources, "AccentForegroundBrush", "#FFFFFF");
                SetBrush(resources, "AccentTextBrush", "#0F6CBD");
                SetBrush(resources, "SuccessBrush", "#0F7B0F");
                SetBrush(resources, "WarningBrush", "#9D5D00");
                SetBrush(resources, "DangerBrush", "#C42B1C");
                SetBrush(resources, "StatusCardBrush", "#EEF6FC");
                SetBrush(resources, "PendingBadgeBrush", "#E9E9E9");
                SetBrush(resources, "ActiveBadgeBrush", "#DCECF8");
                SetBrush(resources, "DoneBadgeBrush", "#DDF2DD");
                SetBrush(resources, "MenuBackgroundBrush", "#FFFFFF");
                SetBrush(resources, "MenuBorderBrush", "#D1D1D1");
                SetBrush(resources, "MenuHoverBrush", "#F0F0F0");
            }

            _hasAppliedTheme = true;
            ThemeChanged?.Invoke(null, EventArgs.Empty);
        }

        private static void SetBrush(ResourceDictionary resources, string key, string value)
        {
            Color color = (Color)ColorConverter.ConvertFromString(value);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            resources[key] = brush;
        }
    }
}
