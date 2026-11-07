using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Hodozip.Services;

/// <summary>バックグラウンドで続いているジョブ。メイン ウィンドウの一覧に表示する。</summary>
internal sealed class JobInfo : INotifyPropertyChanged
{
    private double _progressPercent;
    private string _statusText = string.Empty;

    public JobInfo(ProgressWindow window, string displayName, bool isCompression)
    {
        Window = window;
        DisplayName = displayName;
        IsCompression = isCompression;
        Header = $"{(isCompression ? Loc.Main_JobCompress : Loc.Main_JobExtract)} - {displayName}";
        ShowLabel = Loc.Main_JobShow;
        CancelLabel = Loc.Main_JobCancel;
    }

    public ProgressWindow Window { get; }

    public string DisplayName { get; }

    public bool IsCompression { get; }

    public string Header { get; }

    public string ShowLabel { get; }

    public string CancelLabel { get; }

    public double ProgressPercent
    {
        get => _progressPercent;
        set
        {
            if (Math.Abs(_progressPercent - value) < 0.5)
            {
                return;
            }

            _progressPercent = value;
            OnPropertyChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        set
        {
            if (string.Equals(_statusText, value, StringComparison.Ordinal))
            {
                return;
            }

            _statusText = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

internal static class JobRegistry
{
    public static ObservableCollection<JobInfo> Jobs { get; } = new();

    public static void Add(JobInfo job)
    {
        if (!Jobs.Contains(job))
        {
            Jobs.Add(job);
        }
    }

    public static void Remove(JobInfo? job)
    {
        if (job is not null)
        {
            Jobs.Remove(job);
        }
    }
}
