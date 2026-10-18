using System.Windows;
using System.Windows.Input;
using VibePlayer.Core;

namespace VibePlayer.Views;

public partial class UrlDialog : Window
{
    public UrlDialog()
    {
        InitializeComponent();

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
        UrlBox.TextChanged += (_, _) => ErrorText.Visibility = Visibility.Collapsed;

        Loaded += (_, _) =>
        {
            try
            {
                var clip = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "";
                if (MediaFormats.LooksLikeUrl(clip)) UrlBox.Text = clip;
            }
            catch
            {
                // Clipboard can be locked by another app - not important.
            }
            UrlBox.Focus();
            UrlBox.SelectAll();
        };
    }

    public string Url => UrlBox.Text.Trim().Trim('"');

    private void Accept()
    {
        if (MediaFormats.LooksLikeUrl(Url) || File.Exists(Url))
            DialogResult = true;
        else
            ErrorText.Visibility = Visibility.Visible;
    }
}
