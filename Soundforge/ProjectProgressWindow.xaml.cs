using System.ComponentModel;
using System.Windows;
using Soundforge.Persistence;

namespace Soundforge;

public partial class ProjectProgressWindow : Window
{
    private bool _allowClose;

    public ProjectProgressWindow(string heading)
    {
        InitializeComponent();
        HeadingText.Text = heading;
        ShowProgress(ProjectStoreProgress.Indeterminate("Preparing…"));
    }

    public void ShowProgress(ProjectStoreProgress progress)
    {
        StageText.Text = progress.Stage;
        DetailText.Text = progress.CurrentItem ?? string.Empty;
        OperationProgress.IsIndeterminate = progress.TotalBytes <= 0;

        if (progress.TotalBytes > 0)
        {
            OperationProgress.Value = progress.Percentage;
            PercentageText.Text = progress.TotalItems > 0
                ? $"{progress.Percentage:0}%  ·  {Math.Min(progress.CompletedItems + 1, progress.TotalItems)} of {progress.TotalItems} files"
                : $"{progress.Percentage:0}%";
        }
        else
        {
            PercentageText.Text = string.Empty;
        }
    }

    public void ShowFinalizing(string stage)
    {
        StageText.Text = stage;
        DetailText.Text = string.Empty;
        PercentageText.Text = string.Empty;
        OperationProgress.IsIndeterminate = true;
    }

    public void FinishAndClose()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
            e.Cancel = true;
        base.OnClosing(e);
    }
}
