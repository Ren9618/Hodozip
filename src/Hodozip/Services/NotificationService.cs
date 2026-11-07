using System.Runtime.InteropServices;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Hodozip.Services;

/// <summary>
/// 完了通知（Windows のトースト）と通知音。
/// MSIX / unpackaged の両方で動くよう、失敗してもアプリの動作は止めない。
/// </summary>
internal static class NotificationService
{
    private static bool _registered;
    private static bool _registrationFailed;
    private static Windows.Media.Playback.MediaPlayer? _mediaPlayer;

    public static void NotifyCompletion(string title, string message, AppSettings settings)
    {
        if (settings.NotifyOnCompletion)
        {
            ShowToast(title, message);
        }

        // 通知が OFF でも、通知音だけを鳴らす設定は尊重する。
        PlaySound(settings.NotificationSound, settings.NotificationSoundPath);
    }

    public static void ShowToast(string title, string message)
    {
        try
        {
            EnsureRegistered();
            if (_registrationFailed)
            {
                return;
            }

            var notification = new AppNotificationBuilder()
                .AddText(title)
                .AddText(message)
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
        catch
        {
            // 通知が使えない環境でも処理結果は失わない。
        }
    }

    public static void PlaySound(NotificationSoundMode mode, string? path)
    {
        try
        {
            switch (mode)
            {
                case NotificationSoundMode.System:
                    MessageBeep(0x00000040);
                    break;

                case NotificationSoundMode.File:
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    {
                        return;
                    }

                    _mediaPlayer?.Dispose();
                    var player = new Windows.Media.Playback.MediaPlayer
                    {
                        Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(path)),
                    };
                    _mediaPlayer = player;
                    player.Play();
                    break;
            }
        }
        catch
        {
            // 音声ファイルを再生できない場合は無音で続行する。
        }
    }

    [DllImport("user32.dll")]
    private static extern bool MessageBeep(uint uType);

    private static void EnsureRegistered()
    {
        if (_registered || _registrationFailed)
        {
            return;
        }

        try
        {
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch
        {
            _registrationFailed = true;
        }
    }
}
