using System.Runtime.InteropServices;
using System.Windows;

namespace TokenUsage.Monitor;

/// <summary>
/// A run's whole prompt, read from Claude Code's log and shown only; nothing is stored. Also shows other
/// long texts, such as an agent's definition.
/// </summary>
public partial class PromptWindow : Window
{
    /// <param name="markdown">Show the text formatted as Markdown; copying still gives the raw text.</param>
    public PromptWindow(string prompt, string details, string heading = "Hela prompten", bool markdown = false)
    {
        InitializeComponent();
        Heading.Text = heading;
        Title = heading;
        Text.Text = prompt;
        Details.Text = details;
        if (markdown)
        {
            Formatted.Document = MarkdownView.Render(prompt);
            Formatted.Visibility = Visibility.Visible;
            Text.Visibility = Visibility.Collapsed;
            (Width, Height) = (880, 720);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (MainWindow.UseSystemFrame(this)) WindowFrame.BorderThickness = new Thickness(0);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(Text.Text);
            CopiedText.Text = "Kopierad";
        }
        catch (ExternalException ex)   // another program is holding the clipboard
        {
            CopiedText.Text = $"Kunde inte kopiera: {ex.Message}";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
