// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.

using QuickLook.Common.Helpers;
using QuickLook.Common.Plugin;
using QuickLookNext.Helpers;
using QuickLookNext.Properties;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using QuickLook.Common.NativeMethods;
using Wpf.Ui.Controls;
using Wpf.Ui.Violeta.Win32;
using OSThemeHelper = QuickLook.Common.Helpers.OSThemeHelper;
using ToolTipIcon = Wpf.Ui.Violeta.Win32.ToolTipIcon;

namespace QuickLookNext;

internal partial class TrayIconManager : IDisposable
{
    private static TrayIconManager _instance;

    private readonly TrayIconHost _icon;

    private TrayIconManager()
    {
        _icon = new TrayIconHost
        {
            ToolTipText = string.Format(TranslationHelper.Get("Icon_ToolTip"),
                Application.ProductVersion),
            Icon = GetTrayIconByDPI(),
            ThemeMode = TrayThemeMode.System,
            // v1.2.8: the built-in native menu cannot render Mica, so it is
            // disabled (Menu = null is safe: the host's ShowContextMenu uses a
            // null-conditional) and replaced by TrayMenuWindow.
            Menu = null,
            IsVisible = SettingHelper.Get("ShowTrayIcon", true)
        };

        // RightClick fires on WM_RBUTTONUP, immediately before the host would
        // open the (now disabled) native menu - the same timing users expect
        // from a system tray context menu.
        _icon.RightClick += (_, _) =>
        {
            // v3.18.0: right-clicking the icon while the menu is open toggles
            // it closed instead of closing-and-reopening (which flickered).
            if (TrayMenuWindow.IsOpen)
            {
                TrayMenuWindow.CloseCurrentMenu();
                return;
            }

            TrayMenuWindow.ShowMenu(BuildMenuEntries(), IsDarkTheme());
        };

        // v3.18.0: left-click toggles the menu too, so clicking the tray icon
        // behaves predictably instead of silently closing an open menu.
        _icon.LeftClick += (_, _) =>
        {
            if (TrayMenuWindow.IsOpen)
                TrayMenuWindow.CloseCurrentMenu();
            else
                TrayMenuWindow.ShowMenu(BuildMenuEntries(), IsDarkTheme());
        };
    }

    private static List<TrayMenuEntry> BuildMenuEntries()
    {
        var currentBackdrop = SettingHelper.Get(
            "WindowBackdrop", nameof(Dwmapi.SystembackdropType.Acrylic), "QuickLookNext")?.Trim();
        var currentTheme = (Themes)SettingHelper.Get("LastTheme", (int)Themes.None, "QuickLookNext");

        // v1.3.7: group headers show the current selection (e.g. 主题模式：亮色),
        // and the choices live in a nested flyout to keep the top level short.
        var themeMode = ThemeModes.FirstOrDefault(m => m.Theme == currentTheme);
        var themeGroupLabel =
            $"{TranslationHelper.Get("Icon_ThemeMode", failsafe: "Theme Mode")}：" +
            (themeMode.Key is not null
                ? TranslationHelper.Get(themeMode.Key, failsafe: themeMode.Name)
                : themeMode.Name);

        var backdropMode = BackdropModes.FirstOrDefault(m =>
            string.Equals(m.Name, currentBackdrop, StringComparison.OrdinalIgnoreCase));
        var backdropGroupLabel =
            $"{TranslationHelper.Get("Icon_BackdropMode", failsafe: "Backdrop Mode")}：" +
            (backdropMode.Key is not null
                ? TranslationHelper.Get(backdropMode.Key, failsafe: backdropMode.Name)
                : currentBackdrop);

        // v1.5.0: language override (empty = follow the OS UI culture).
        var currentLanguage = SettingHelper.Get("Language", string.Empty, "QuickLookNext");
        var languageGroupLabel =
            $"{TranslationHelper.Get("Icon_Language", failsafe: "Language")}：" +
            (string.IsNullOrEmpty(currentLanguage)
                ? TranslationHelper.Get("Icon_Language_FollowSystem", failsafe: "Follow System")
                : LanguageDisplayName(currentLanguage));

        return
        [
            new TrayMenuEntry
            {
                Header = $"v{Application.ProductVersion}{(App.IsUWP ? " (UWP)" : string.Empty)}",
                IsEnabled = false,
                IsBold = true,
            },
            TrayMenuEntry.Separator,
            new TrayMenuEntry
            {
                Header = themeGroupLabel,
                // v3.3.0: top-level entries get Fluent glyphs so the menu
                // reads like a real settings surface instead of plain text.
                Icon = FontSymbols.Color,
                Children =
                [
                    ..ThemeModes.Select(mode => new TrayMenuEntry
                    {
                        Header = TranslationHelper.Get(mode.Key, failsafe: mode.Name),
                        // v3.10.0: submenu items get glyphs too (System = auto,
                        // Light / Dark match the theme).
                        Icon = mode.Theme switch
                        {
                            Themes.None => FontSymbols.Sync,
                            Themes.Light => FontSymbols.Light,
                            Themes.Dark => FontSymbols.Contrast,
                            _ => null,
                        },
                        Command = () => SetThemeMode(mode.Theme),
                        IsChecked = currentTheme == mode.Theme,
                    }),
                ],
            },
            new TrayMenuEntry
            {
                Header = languageGroupLabel,
                Icon = FontSymbols.LocaleLanguage,
                Children =
                [
                    new TrayMenuEntry
                    {
                        Header = TranslationHelper.Get("Icon_Language_FollowSystem", failsafe: "Follow System"),
                        Icon = FontSymbols.Globe,
                        Command = () => SetLanguage(string.Empty),
                        IsChecked = string.IsNullOrEmpty(currentLanguage),
                    },
                    // v3.20.0: most-used languages first, then the rest sorted
                    // by their native display name.
                    ..OrderLanguages(TranslationHelper.GetAvailableLanguages()).Select(language => new TrayMenuEntry
                    {
                        Header = LanguageDisplayName(language),
                        Command = () => SetLanguage(language),
                        IsChecked = string.Equals(currentLanguage, language, StringComparison.OrdinalIgnoreCase),
                    }),
                ],
            },
            new TrayMenuEntry
            {
                Header = backdropGroupLabel,
                Icon = FontSymbols.Photo,
                Children =
                [
                    ..BackdropModes.Select(mode => new TrayMenuEntry
                    {
                        Header = TranslationHelper.Get(mode.Key, failsafe: mode.Name),
                        Command = () => SetBackdropMode(mode.Name),
                        IsChecked = string.Equals(currentBackdrop, mode.Name, StringComparison.OrdinalIgnoreCase),
                    }),
                ],
            },
            new TrayMenuEntry
            {
                Header = TranslationHelper.Get("Icon_Settings", failsafe: "Options"),
                Icon = FontSymbols.Settings,
                Children =
                [
                    new TrayMenuEntry
                    {
                        Header = TranslationHelper.Get("Icon_RunAtStartup"),
                        Icon = FontSymbols.PowerButton,
                        Command = ToggleAutorun,
                        IsChecked = AutoStartupHelper.IsAutorun(),
                        IsEnabled = !App.IsUWP,
                    },
                    new TrayMenuEntry
                    {
                        Header = TranslationHelper.Get("Icon_CloseOnLostFocus"),
                        Icon = FontSymbols.RedEye,
                        Command = ToggleCloseOnLostFocus,
                        IsChecked = SettingHelper.Get("CloseOnLostFocus", false),
                    },
                    new TrayMenuEntry
                    {
                        Header = TranslationHelper.Get("Icon_HideTopBarByDefault", failsafe: "Hide Top Bar by Default"),
                        Icon = FontSymbols.ChevronUp,
                        Command = ToggleHideTopBarByDefault,
                        IsChecked = SettingHelper.Get("HideTopBarByDefault", true, "QuickLookNext"),
                    },
                ],
            },
            TrayMenuEntry.Separator,
            new TrayMenuEntry
            {
                Header = TranslationHelper.Get("Icon_CheckUpdate"),
                Icon = FontSymbols.Download,
                Command = () => Updater.CheckForUpdates(),
            },
            new TrayMenuEntry
            {
                Header = TranslationHelper.Get("Icon_GetPlugin"),
                Icon = FontSymbols.CloudDownload,
                // v1.3.9: .NET Core no longer shells out by default, so a
                // bare Process.Start(url) throws; UseShellExecute opens the
                // default browser instead.
                Command = () => OpenUrl("https://github.com/QL-Win/QuickLook/wiki/Available-Plugins"),
            },
            new TrayMenuEntry
            {
                // v1.3.11: plugin management panel (uninstall user plugins).
                Header = TranslationHelper.Get("Icon_PluginManager", failsafe: "Manage &Plugins..."),
                Icon = FontSymbols.Puzzle,
                Command = PluginManagerWindow.ShowWindow,
            },
            new TrayMenuEntry
            {
                Header = TranslationHelper.Get("Icon_OpenDataFolder"),
                Icon = FontSymbols.FolderOpen,
                Command = () => Process.Start("explorer.exe", SettingHelper.LocalDataPath),
            },
            new TrayMenuEntry
            {
                // v5.0.7: shows how much of the data folder is rebuildable cache and clears it
                // (upstream #1933).
                Header = TranslationHelper.Get("Icon_DataCache", failsafe: "Data && cache..."),
                Icon = FontSymbols.Broom,
                Command = () => DataCacheWindow.ShowWindow(),
            },
            TrayMenuEntry.Separator,
            new TrayMenuEntry
            {
                Header = TranslationHelper.Get("Icon_Restart"),
                Icon = FontSymbols.Sync,
                Command = () => GetInstance().Restart(forced: true),
            },
            new TrayMenuEntry
            {
                Header = TranslationHelper.Get("Icon_Quit"),
                Icon = FontSymbols.Cancel,
                Command = () => System.Windows.Application.Current.Shutdown(),
            },
        ];
    }

    /// <summary>
    /// v1.3.9: opens a URL in the default browser. Process.Start(string) on
    /// .NET Core tries to execute the URL as a file, so UseShellExecute must
    /// be set explicitly.
    /// </summary>
    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // No default browser / shell unavailable; keep the menu usable.
        }
    }

    // v1.2.14: backdrop choices offered in the tray menu, matching the
    // WindowBackdrop setting read by ViewerWindow.GetBackdropOption.
    private static readonly (string Name, string Key)[] BackdropModes =
    [
        (nameof(Dwmapi.SystembackdropType.Auto), "Icon_Backdrop_Auto"),
        (nameof(Dwmapi.SystembackdropType.None), "Icon_Backdrop_None"),
        (nameof(Dwmapi.SystembackdropType.Mica), "Icon_Backdrop_Mica"),
        (nameof(Dwmapi.SystembackdropType.Acrylic), "Icon_Backdrop_Acrylic"),
        (nameof(Dwmapi.SystembackdropType.Acrylic10), "Icon_Backdrop_Acrylic10"),
        (nameof(Dwmapi.SystembackdropType.Acrylic11), "Icon_Backdrop_Acrylic11"),
        (nameof(Dwmapi.SystembackdropType.Tabbed), "Icon_Backdrop_Tabbed"),
    ];

    // v1.3.6: theme choices offered in the tray menu, matching the LastTheme
    // setting read by ViewerWindow (None = follow the system light/dark).
    private static readonly (Themes Theme, string Name, string Key)[] ThemeModes =
    [
        (Themes.None, "System", "Icon_Theme_System"),
        (Themes.Light, "Light", "Icon_Theme_Light"),
        (Themes.Dark, "Dark", "Icon_Theme_Dark"),
    ];

    private static void SetBackdropMode(string mode)
    {
        SettingHelper.Set("WindowBackdrop", mode, "QuickLookNext");

        // Apply to the open preview immediately (BeginShow re-applies the
        // backdrop from the setting on every preview).
        var manager = ViewWindowManager.GetInstance();
        if (manager.CurrentViewerWindow is { IsVisible: true })
            manager.ReloadPreview();
    }

    private static void ToggleAutorun()
    {
        if (AutoStartupHelper.IsAutorun())
            AutoStartupHelper.RemoveAutorunShortcut();
        else
            AutoStartupHelper.CreateAutorunShortcut();
    }

    private static void ToggleCloseOnLostFocus()
    {
        var current = SettingHelper.Get("CloseOnLostFocus", false);
        SettingHelper.Set("CloseOnLostFocus", !current);
    }

    // v1.3.6: apply a light/dark/system theme from the tray menu and persist
    // it; an open preview switches immediately.
    private static void SetThemeMode(Themes theme)
    {
        var manager = ViewWindowManager.GetInstance();
        if (manager.CurrentViewerWindow is { IsVisible: true } w)
        {
            w.ApplyTheme(theme);
            return;
        }

        SettingHelper.Set("LastTheme", (int)theme, "QuickLookNext");
        SettingHelper.Set("LastTheme", (int)theme, "QuickLook.Plugin.ImageViewer");
    }

    // v1.5.0: persist the language override. An empty value means follow the
    // OS UI culture; menus and windows pick the new language up on next open.
    private static void SetLanguage(string cultureName)
    {
        SettingHelper.Set("Language", cultureName, "QuickLookNext");
    }

    private static string LanguageDisplayName(string cultureName)
    {
        // v3.21.0: use the plain "简体中文 / 繁体中文" labels instead of the
        // culture's native "中文（中国）/ 中文（台湾）".
        if (string.Equals(cultureName, "zh-CN", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(cultureName, "zh-Hans", StringComparison.OrdinalIgnoreCase))
        {
            return "简体中文";
        }

        if (string.Equals(cultureName, "zh-TW", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(cultureName, "zh-Hant", StringComparison.OrdinalIgnoreCase))
        {
            return "繁体中文";
        }

        try
        {
            return CultureInfo.GetCultureInfo(cultureName).NativeName;
        }
        catch (CultureNotFoundException)
        {
            return cultureName;
        }
    }

    /// <summary>
    /// v3.20.0: languages most people actually use are pinned to the top of
    /// the picker (in this order); everything else follows alphabetically by
    /// its native display name.
    /// </summary>
    private static readonly string[] CommonLanguages =
    [
        "zh-CN", "zh-TW", "en", "ja", "ko", "fr", "de", "es", "pt-BR", "ru",
        "ar", "hi", "vi", "tr", "id", "nl", "it", "pl", "uk", "th",
    ];

    private static IOrderedEnumerable<string> OrderLanguages(IEnumerable<string> languages)
    {
        return languages.OrderBy(language =>
        {
            var index = Array.FindIndex(CommonLanguages,
                c => string.Equals(c, language, StringComparison.OrdinalIgnoreCase));
            return index < 0 ? int.MaxValue : index;
        }).ThenBy(LanguageDisplayName, StringComparer.CurrentCultureIgnoreCase);
    }

    // v1.3.6: 顶部状态栏默认隐藏开关 - 开启后鼠标移入内容区不再弹出顶栏，
    // 只有移到窗口顶部标题栏区域才显示（可随时在托盘菜单切回旧行为）。
    private static void ToggleHideTopBarByDefault()
    {
        var current = SettingHelper.Get("HideTopBarByDefault", true, "QuickLookNext");
        SettingHelper.Set("HideTopBarByDefault", !current, "QuickLookNext");

        // Apply to the open preview immediately.
        var manager = ViewWindowManager.GetInstance();
        if (manager.CurrentViewerWindow is { IsVisible: true } w)
            w.ApplyTopBarMode();
    }

    internal static bool IsDarkTheme()
    {
        var theme = (Themes)SettingHelper.Get("LastTheme", (int)Themes.None, "QuickLookNext");
        return theme switch
        {
            Themes.Dark => true,
            Themes.Light => false,
            _ => OSThemeHelper.AppsUseDarkTheme(),
        };
    }

    /// <summary>
    /// Test hook used by test.ps1 (via the hidden /test-tray-menu startup
    /// switch): opens the Mica tray menu once and lets it auto-close.
    /// </summary>
    internal static void ShowTestMenu()
    {
        // QL_TEST_MENU_MS overrides the auto-close delay so automated tests can
        // interact with the menu (click submenu rows) before it closes.
        var autoCloseMs = int.TryParse(Environment.GetEnvironmentVariable("QL_TEST_MENU_MS"), out var ms)
            ? ms
            : 4000;
        TrayMenuWindow.ShowMenu(BuildMenuEntries(), IsDarkTheme(), autoCloseMs: autoCloseMs);

        try
        {
            var diagDir = App.SmokeDir;
            var diagFile = System.IO.Path.Combine(diagDir, "tray-menu-dwm.txt");

            // Wait until the tray menu is on screen, dump the DWM backdrop
            // readback so test.ps1 can assert Mica is really applied.
            System.Threading.Tasks.Task.Delay(1500).ContinueWith(_ =>
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    System.IO.Directory.CreateDirectory(diagDir);
                    System.IO.File.WriteAllText(diagFile,
                        $"{TrayMenuWindow.DiagnoseBackdrop()}\nentries={TrayMenuWindow.DiagnoseEntries()}\nmore-menu-opened=false");
                }));

            // After the tray menu auto-closed, exercise the same unified menu
            // through the preview window's "More" button path.
            System.Threading.Tasks.Task.Delay(5000).ContinueWith(_ =>
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    try
                    {
                        ViewWindowManager.GetInstance().CurrentViewerWindow?.ShowMoreMenuForTest();
                        System.IO.Directory.CreateDirectory(diagDir);
                        System.IO.File.AppendAllText(diagFile, "\nmore-menu-opened=true");
                    }
                    catch (Exception ex)
                    {
                        System.IO.File.AppendAllText(diagFile, $"\nmore-menu-error: {ex}");
                    }
                }));
        }
        catch
        {
            // Diagnostics must never break the test itself.
        }
    }

    public void Dispose()
    {
        _icon.IsVisible = false;
    }

    public void Restart(string fileName = null, string dir = null, string args = null, int? exitCode = null, bool forced = false)
    {
        _ = args; // Currently there is no cli supported by QL

        try
        {
            using Process process = new()
            {
                StartInfo = new ProcessStartInfo()
                {
                    // v1.2.16: AppFullPath is the .exe, not the managed .dll.
                    FileName = fileName ?? App.AppFullPath,
                    WorkingDirectory = dir ?? Environment.CurrentDirectory,
                    UseShellExecute = true,
                },
            };
            process.Start();
        }
        catch (Win32Exception)
        {
            return;
        }
        if (forced)
        {
            Process.GetCurrentProcess().Kill();
        }
        Environment.Exit(exitCode ?? 'r' + 'e' + 's' + 't' + 'a' + 'r' + 't');
    }

    private nint GetTrayIconByDPI()
    {
        var scale = DisplayDeviceHelper.GetCurrentScaleFactor().Vertical;

        if (!App.IsWin10)
            return scale > 1 ? Resources.app.Handle : Resources.app_16.Handle;

        return OSThemeHelper.SystemUsesDarkTheme()
            ? (scale > 1 ? Resources.app_white.Handle : Resources.app_white_16.Handle)
            : (scale > 1 ? Resources.app_black.Handle : Resources.app_black_16.Handle);
    }

    public static void ShowNotification(string title, string content, bool isError = false, int timeout = 5000,
        Action clickEvent = null,
        Action closeEvent = null)
    {
        var icon = GetInstance()._icon;
        icon.ShowBalloonTip(timeout, title, content, isError ? ToolTipIcon.Error : ToolTipIcon.Info);
        icon.BalloonTipClicked += OnIconOnBalloonTipClicked;
        icon.BalloonTipClosed += OnIconOnBalloonTipClosed;

        void OnIconOnBalloonTipClicked(object sender, EventArgs e)
        {
            clickEvent?.Invoke();
            icon.BalloonTipClicked -= OnIconOnBalloonTipClicked;
        }

        void OnIconOnBalloonTipClosed(object sender, EventArgs e)
        {
            closeEvent?.Invoke();
            icon.BalloonTipClosed -= OnIconOnBalloonTipClosed;
        }
    }

    public static TrayIconManager GetInstance()
    {
        return _instance ??= new TrayIconManager();
    }

    public static void Start()
    {
        // v1.2.34: the native tray context menu is disabled (Menu = null), so
        // the TrayIconWindow warm-up is obsolete - the TrayIconHost creates
        // its own hidden window. Skipping the extra window saves startup time.
        _ = GetInstance();
    }
}
