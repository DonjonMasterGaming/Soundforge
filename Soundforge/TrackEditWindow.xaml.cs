using System.Globalization;
using Soundforge.Audio;
using Soundforge.Models;
using System.Windows;

namespace Soundforge;

public partial class TrackEditWindow : Window
{
    private readonly Track _track;
    private readonly TimeSpan _duration;

    public TrackEditWindow(Track track)
    {
        InitializeComponent();
        _track = track;

        using var reader = new LocalAudioReader(track.FilePath);
        _duration = reader.TotalTime;

        TrackNameText.Text = track.Name;
        DurationText.Text = $"Full track duration: {FormatTime(_duration)}";
        StartTimeText.Text = FormatTime(TimeSpan.FromSeconds(Math.Max(0, track.TrimStartSeconds)));
        EndTimeText.Text = FormatTime(track.TrimEndSeconds is > 0
            ? TimeSpan.FromSeconds(track.TrimEndSeconds.Value)
            : _duration);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseTime(StartTimeText.Text, out var start) || !TryParseTime(EndTimeText.Text, out var end))
        {
            ValidationText.Text = "Enter each time as seconds (for example 75.5) or minutes:seconds (for example 1:15.5).";
            return;
        }

        if (start < TimeSpan.Zero)
        {
            ValidationText.Text = "The start time cannot be negative.";
            return;
        }

        if (end > _duration + TimeSpan.FromMilliseconds(10))
        {
            ValidationText.Text = $"The end time cannot exceed the full duration of {FormatTime(_duration)}.";
            return;
        }

        if (end <= start)
        {
            ValidationText.Text = "The end time must be later than the start time.";
            return;
        }

        _track.TrimStartSeconds = start.TotalSeconds;
        _track.TrimEndSeconds = Math.Abs((end - _duration).TotalMilliseconds) <= 10
            ? null
            : end.TotalSeconds;
        DialogResult = true;
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        StartTimeText.Text = FormatTime(TimeSpan.Zero);
        EndTimeText.Text = FormatTime(_duration);
        ValidationText.Text = "";
    }

    internal static bool TryParseTime(string text, out TimeSpan value)
    {
        text = text.Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var seconds) ||
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
        {
            if (!double.IsFinite(seconds))
            {
                value = default;
                return false;
            }

            value = TimeSpan.FromSeconds(seconds);
            return true;
        }

        var parts = text.Split(':');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) &&
            (double.TryParse(parts[1], NumberStyles.Float, CultureInfo.CurrentCulture, out seconds) ||
             double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)) &&
            minutes >= 0 && seconds is >= 0 and < 60)
        {
            value = TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
            return true;
        }

        value = default;
        return false;
    }

    private static string FormatTime(TimeSpan time)
    {
        var totalMinutes = (int)Math.Floor(time.TotalMinutes);
        var formatted = $"{totalMinutes}:{time.Seconds:00}.{time.Milliseconds:000}";
        return formatted.TrimEnd('0').TrimEnd('.');
    }
}
