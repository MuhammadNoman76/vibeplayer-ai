using System.Globalization;
using System.Windows;
using System.Windows.Input;

namespace VibePlayer.Views;

public partial class SkipRangeDialog : Window
{
    private readonly long _length;

    public SkipRangeDialog(long fromMs, long toMs, long lengthMs)
    {
        _length = lengthMs;
        InitializeComponent();
        FromBox.Text = PlayerView.FormatTime(fromMs, lengthMs);
        ToBox.Text = PlayerView.FormatTime(toMs, lengthMs);

        OkButton.Click += (_, _) => Accept();
        CancelButton.Click += (_, _) => DialogResult = false;
        Card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is not System.Windows.Controls.TextBox) DragMove();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Accept(); e.Handled = true; }
            else if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
        };
        Loaded += (_, _) =>
        {
            FromBox.Focus();
            FromBox.SelectAll();
        };
    }

    public long FromMs { get; private set; }
    public long ToMs { get; private set; }

    private void Accept()
    {
        if (!TryParseTime(FromBox.Text, out var from) || !TryParseTime(ToBox.Text, out var to))
        {
            ShowError("Enter times like 34:05 or 1:02:30.");
            return;
        }
        if (to <= from)
        {
            ShowError("“To” must be later than “From”.");
            return;
        }
        if (_length > 0 && from >= _length)
        {
            ShowError("“From” is after the end of the video.");
            return;
        }

        FromMs = from;
        ToMs = _length > 0 ? Math.Min(to, _length) : to;
        DialogResult = true;
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
    }

    /// <summary>Parses "45", "34:05", "1:02:30" or "34:05.5" into milliseconds.</summary>
    public static bool TryParseTime(string text, out long ms)
    {
        ms = 0;
        var parts = text.Trim().Replace(',', '.').Split(':');
        if (parts.Length is < 1 or > 3) return false;

        double total = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            var isSeconds = i == parts.Length - 1;
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0) return false;
            if (!isSeconds && value != Math.Floor(value)) return false;
            if (i > 0 && value >= 60) return false;
            total = total * 60 + value;
        }
        ms = (long)Math.Round(total * 1000);
        return true;
    }
}
