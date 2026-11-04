using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace PodcastHelper.App.Controls;

// Только собственное ограниченное подмножество. Ни HTML, ни XAML из текста не загружаются.
public sealed class SafeMarkdownView : FlowDocumentScrollViewer
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(SafeMarkdownView),
        new PropertyMetadata("", (d, _) => ((SafeMarkdownView)d).Rebuild()));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    private static readonly Regex Tokens = new(@"!\[[^\]]*\]\([^)]+\)|\*\*(.+?)\*\*|\*(.+?)\*|`([^`]+)`|\[([^\]]+)\]\(([^)]+)\)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public SafeMarkdownView() { FontSize = 15; IsToolBarVisible = false; VerticalScrollBarVisibility = ScrollBarVisibility.Auto; IsSelectionEnabled = true; }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if ((e.Property == ForegroundProperty || e.Property == FontSizeProperty) && Text is not null) Rebuild();
    }
    public static bool IsSafeLink(string target) => Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";
    private void Rebuild()
    {
        var document = new FlowDocument { PagePadding = new Thickness(0), FontFamily = new FontFamily("Segoe UI"), FontSize = FontSize, Foreground = Foreground };
        foreach (string source in (Text ?? "").Replace("\r", "").Split('\n'))
        {
            string line = source;
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 7) };
            // Заголовки/таблицы остаются простым текстом; внешние изображения удаляются из отображения.
            if (line.StartsWith("#", StringComparison.Ordinal) || line.Contains('|')) paragraph.Inlines.Add(new Run(line));
            else
            {
                if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal)) line = "• " + line[2..];
                try { AddInlines(paragraph, line); }
                catch (RegexMatchTimeoutException) { paragraph.Inlines.Clear(); paragraph.Inlines.Add(new Run(line)); }
            }
            document.Blocks.Add(paragraph);
        }
        Document = document;
    }
    private static void AddInlines(Paragraph paragraph, string text)
    {
        int offset = 0;
        foreach (Match match in Tokens.Matches(text))
        {
            paragraph.Inlines.Add(new Run(text[offset..match.Index]));
            if (match.Value.StartsWith("![", StringComparison.Ordinal)) { /* Нет внешних ресурсов и сетевых запросов. */ }
            else if (match.Groups[1].Success) paragraph.Inlines.Add(new Bold(new Run(match.Groups[1].Value)));
            else if (match.Groups[2].Success) paragraph.Inlines.Add(new Italic(new Run(match.Groups[2].Value)));
            else if (match.Groups[3].Success) paragraph.Inlines.Add(new Run(match.Groups[3].Value) { FontFamily = new FontFamily("Consolas") });
            else if (match.Groups[4].Success)
            {
                string target = match.Groups[5].Value;
                if (IsSafeLink(target))
                {
                    var link = new Hyperlink(new Run(match.Groups[4].Value)) { NavigateUri = new Uri(target) };
                    link.RequestNavigate += (_, e) =>
                    {
                        e.Handled = true;
                        if (!IsSafeLink(e.Uri.AbsoluteUri)) return;
                        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
                        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
                    };
                    paragraph.Inlines.Add(link);
                }
                else paragraph.Inlines.Add(new Run(match.Groups[4].Value));
            }
            offset = match.Index + match.Length;
        }
        paragraph.Inlines.Add(new Run(text[offset..]));
    }
}
