using System.IO;
using System.Windows;
using Soundforge.Cloud;

namespace Soundforge;

public partial class AddUrlSourceWindow : Window
{
    public IReadOnlyList<string> SourceUrls => CloudImportInput.ParseUrls(UrlText.Text);
    public string? DisplayName => SourceUrls.Count == 1 && !string.IsNullOrWhiteSpace(NameText.Text)
        ? NameText.Text.Trim()
        : null;

    public AddUrlSourceWindow() => InitializeComponent();

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (SourceUrls.Count == 0)
            {
                MessageBox.Show(this, "Paste at least one complete HTTPS address.", "Add cloud audio", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }
        catch (InvalidDataException ex)
        {
            MessageBox.Show(this, ex.Message, "Add cloud audio", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }
}
