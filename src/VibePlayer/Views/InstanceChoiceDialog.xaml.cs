using System.Windows;
using System.Windows.Input;
using VibePlayer.Core;

namespace VibePlayer.Views;

/// <summary>"VibePlayer is already open - use that window or open a new one?"</summary>
public partial class InstanceChoiceDialog : Window
{
    public InstanceChoiceDialog(List<string> files)
    {
        Files = files;
        InitializeComponent();
        UpdateMessage();

        // More files can arrive while we wait (Explorer opens one process per selected file).
        InstanceManager.SetLaunchHandler(more => Dispatcher.BeginInvoke(() =>
        {
            lock (Files) Files.AddRange(more);
            UpdateMessage();
        }));

        ExistingButton.Click += (_, _) => Choose(true);
        NewButton.Click += (_, _) => Choose(false);
        CancelButton.Click += (_, _) => DialogResult = false;
        Card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is not System.Windows.Controls.CheckBox) DragMove();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Choose(true); e.Handled = true; }
            else if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
        };
        Loaded += (_, _) => Activate();
    }

    public List<string> Files { get; }
    public bool UseOpenWindow { get; private set; }
    public bool Remember => RememberBox.IsChecked == true;

    private void UpdateMessage()
    {
        int count;
        string first;
        lock (Files)
        {
            count = Files.Count;
            first = count > 0 ? Files[0] : "";
        }

        if (count == 0)
        {
            Message.Text = "Switch to the window that's already open, or start a new player window?";
            ExistingButton.Content = "Switch to open window";
        }
        else
        {
            var what = count == 1 ? $"“{(MediaFormats.LooksLikeUrl(first) ? first : Path.GetFileName(first))}”" : $"these {count} files";
            Message.Text = $"Play {what} in the window that's already open, or in a new window?";
            ExistingButton.Content = "Play in open window";
        }
    }

    private void Choose(bool useOpenWindow)
    {
        UseOpenWindow = useOpenWindow;
        DialogResult = true;
    }
}
