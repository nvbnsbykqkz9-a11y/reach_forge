using System;
using System.Drawing;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace ReachForge.Desktop;

/// <summary>タスクトレイのアイコンとメニュー（開く・データフォルダー・設定ファイル・Windows の起動時に開始・終了）。</summary>
public sealed class TrayIcon : IDisposable
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "ReachForge";

    private readonly WinForms.NotifyIcon _icon;

    public TrayIcon(App app)
    {
        var autoStart = new WinForms.ToolStripMenuItem("Windows の起動時に開始") { Checked = IsAutoStartEnabled(), CheckOnClick = true };
        autoStart.CheckedChanged += (_, _) => SetAutoStart(autoStart.Checked);

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("ReachForge を開く", null, (_, _) => app.ShowWindow());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("設定ファイルを開く（上級者向け）", null, (_, _) => App.OpenExternal(app.Paths.UserConfig));
        menu.Items.Add("データフォルダーを開く", null, (_, _) => App.OpenInExplorer(app.Paths.Root));
        menu.Items.Add(autoStart);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("終了", null, async (_, _) => await app.ExitAsync());

        _icon = new WinForms.NotifyIcon
        {
            Icon = AppIcon(),
            Text = "ReachForge",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => app.ShowWindow();
    }

    public void Notify(string title, string text) =>
        _icon.ShowBalloonTip(5000, title, text, WinForms.ToolTipIcon.Info);

    /// <summary>実行ファイルのアイコン（なければ Windows の既定のアプリのアイコン）。</summary>
    private static Icon AppIcon() =>
        Environment.ProcessPath is { } exe ? Icon.ExtractAssociatedIcon(exe) ?? SystemIcons.Application : SystemIcons.Application;

    private static bool IsAutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string;
    }

    /// <summary>サインイン時にトレイだけで起動する（予約投稿を続けるため）。管理者権限は不要（利用者ごとの設定）。</summary>
    private static void SetAutoStart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled && Environment.ProcessPath is { } exe) key.SetValue(RunValue, $"\"{exe}\" {App.MinimizedArgument}");
        else key.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
