using System.Windows;

namespace Soundforge;

public partial class AddUrlSourceWindow : Window
{
    public string SourceUrl => UrlText.Text.Trim();
    public string? DisplayName => string.IsNullOrWhiteSpace(NameText.Text) ? null : NameText.Text.Trim();

    public AddUrlSourceWindow() => InitializeComponent();

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (!Uri.TryCreate(SourceUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            MessageBox.Show(this, "Enter a complete HTTPS address.", "Add from URL", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }
}
