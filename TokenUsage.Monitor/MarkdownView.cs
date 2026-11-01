using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using MdTable = Markdig.Extensions.Tables.Table;
using WpfBlock = System.Windows.Documents.Block;
using WpfInline = System.Windows.Documents.Inline;
using WpfTable = System.Windows.Documents.Table;

namespace TokenUsage.Monitor;

/// <summary>
/// Turns Markdown, such as an agent file, into a themed FlowDocument: headings, lists, code, quotes, tables,
/// and a YAML frontmatter as a key/value table at the top. Links are shown, not followed.
/// </summary>
public static class MarkdownView
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseYamlFrontMatter().UsePipeTables().UseEmphasisExtras().UseAutoLinks().Build();

    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    public static FlowDocument Render(string markdown)
    {
        var doc = new FlowDocument
        {
            FontFamily = (FontFamily)Application.Current.FindResource("UiFont"),
            FontSize = 13.5,
            Foreground = Brush("Text"),
            PagePadding = new Thickness(14, 10, 14, 10),
            TextAlignment = TextAlignment.Left,
        };
        foreach (var block in Markdown.Parse(markdown, Pipeline))
            if (Convert(block) is { } b) doc.Blocks.Add(b);
        return doc;
    }

    private static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);

    private static WpfBlock? Convert(MdBlock block) => block switch
    {
        YamlFrontMatterBlock yaml => FrontMatter(yaml),
        HeadingBlock h => Heading(h),
        ParagraphBlock p => Paragraph(p),
        ListBlock l => List(l),
        FencedCodeBlock or CodeBlock => Code((LeafBlock)block),
        QuoteBlock q => Quote(q),
        ThematicBreakBlock => new Paragraph { BorderBrush = Brush("Line"), BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 4, 0, 12) },
        MdTable t => Table(t),
        HtmlBlock html => new Paragraph(new Run(Lines((LeafBlock)html))) { Foreground = Brush("Muted") },
        _ => null,
    };

    private static Paragraph Heading(HeadingBlock h)
    {
        var p = Inlines(new Paragraph(), h.Inline);
        p.FontWeight = FontWeights.Bold;
        p.FontSize = h.Level switch { 1 => 22, 2 => 18, 3 => 15.5, _ => 14 };
        p.Foreground = h.Level <= 2 ? Brush("Accent") : Brush("Text");
        p.Margin = new Thickness(0, h.Level <= 2 ? 16 : 10, 0, 6);
        return p;
    }

    private static Paragraph Paragraph(ParagraphBlock block)
    {
        var p = Inlines(new Paragraph(), block.Inline);
        p.Margin = new Thickness(0, 0, 0, 8);
        return p;
    }

    private static List List(ListBlock block)
    {
        var list = new List
        {
            MarkerStyle = block.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(22, 0, 0, 0),
        };
        if (block.IsOrdered && int.TryParse(block.OrderedStart, out var start)) list.StartIndex = start;
        foreach (var item in block.OfType<ListItemBlock>())
        {
            var li = new ListItem();
            foreach (var child in item)
                if (Convert(child) is { } b)
                {
                    if (b is Paragraph p) p.Margin = new Thickness(0, 0, 0, 3);
                    li.Blocks.Add(b);
                }
            list.ListItems.Add(li);
        }
        return list;
    }

    private static Paragraph Code(LeafBlock block) => new(new Run(Lines(block).TrimEnd()))
    {
        FontFamily = Mono,
        FontSize = 12.5,
        Background = Brush("Surface"),
        Padding = new Thickness(10, 8, 10, 8),
        Margin = new Thickness(0, 2, 0, 10),
    };

    private static Section Quote(QuoteBlock block)
    {
        var section = new Section
        {
            BorderBrush = Brush("Accent"),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(10, 0, 0, 0),
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = Brush("Muted"),
        };
        foreach (var child in block)
            if (Convert(child) is { } b) section.Blocks.Add(b);
        return section;
    }

    private static WpfTable Table(MdTable block)
    {
        var table = new WpfTable { CellSpacing = 0, Margin = new Thickness(0, 2, 0, 10), BorderBrush = Brush("Line"), BorderThickness = new Thickness(1) };
        var group = new TableRowGroup();
        foreach (var row in block.OfType<Markdig.Extensions.Tables.TableRow>())
        {
            var tr = new System.Windows.Documents.TableRow();
            if (row.IsHeader) tr.FontWeight = FontWeights.Bold;
            foreach (var cell in row.OfType<Markdig.Extensions.Tables.TableCell>())
            {
                var tc = new System.Windows.Documents.TableCell { Padding = new Thickness(8, 4, 8, 4), BorderBrush = Brush("Line"), BorderThickness = new Thickness(0, 0, 1, 1) };
                foreach (var child in cell)
                    if (Convert(child) is { } b) { if (b is Paragraph p) p.Margin = new Thickness(0); tc.Blocks.Add(b); }
                tr.Cells.Add(tc);
            }
            group.Rows.Add(tr);
        }
        table.RowGroups.Add(group);
        return table;
    }

    // "key: value" lines as a two-column table; anything more complex is shown as it is.
    private static WpfBlock FrontMatter(YamlFrontMatterBlock block)
    {
        var lines = Lines(block).Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim() is { Length: > 0 } t && t != "---").ToList();
        var table = new WpfTable { CellSpacing = 0, Background = Brush("Surface"), Margin = new Thickness(0, 0, 0, 12) };
        table.Columns.Add(new TableColumn { Width = new GridLength(120) });
        table.Columns.Add(new TableColumn());
        var group = new TableRowGroup();
        foreach (var line in lines)
        {
            var colon = line.IndexOf(':');
            if (colon <= 0 || char.IsWhiteSpace(line[0])) return Code(block);
            var row = new System.Windows.Documents.TableRow();
            row.Cells.Add(new System.Windows.Documents.TableCell(new Paragraph(new Run(line[..colon])) { Foreground = Brush("Muted") }) { Padding = new Thickness(10, 4, 14, 4) });
            row.Cells.Add(new System.Windows.Documents.TableCell(new Paragraph(new Run(line[(colon + 1)..].Trim()))) { Padding = new Thickness(0, 4, 10, 4) });
            group.Rows.Add(row);
        }
        table.RowGroups.Add(group);
        return table;
    }

    private static string Lines(LeafBlock block) => block.Lines.ToString();

    private static Paragraph Inlines(Paragraph p, ContainerInline? container)
    {
        if (container is not null)
            foreach (var inline in container) p.Inlines.Add(Convert(inline));
        return p;
    }

    private static WpfInline Convert(MdInline inline)
    {
        switch (inline)
        {
            case LiteralInline lit:
                return new Run(lit.Content.ToString());
            case CodeInline code:
                return new Run(code.Content) { FontFamily = Mono, FontSize = 12.5, Background = Brush("SurfaceRaised") };
            case LineBreakInline br:
                return br.IsHard ? new LineBreak() : new Run(" ");
            case EmphasisInline em:
            {
                Span span = em.DelimiterChar == '~' ? new Span { TextDecorations = TextDecorations.Strikethrough }
                    : em.DelimiterCount >= 2 ? new Bold() : new Italic();
                foreach (var child in em) span.Inlines.Add(Convert(child));
                return span;
            }
            case AutolinkInline auto:
                return new Run(auto.Url) { Foreground = Brush("Accent"), TextDecorations = TextDecorations.Underline };
            case LinkInline link:
            {
                var span = new Span { Foreground = Brush("Accent"), TextDecorations = TextDecorations.Underline, ToolTip = link.Url };
                foreach (var child in link) span.Inlines.Add(Convert(child));
                return span;
            }
            case HtmlEntityInline entity:
                return new Run(entity.Transcoded.ToString());
            case HtmlInline html:
                return new Run(html.Tag) { Foreground = Brush("Muted") };
            case ContainerInline container:
            {
                var span = new Span();
                foreach (var child in container) span.Inlines.Add(Convert(child));
                return span;
            }
            default:
                return new Run(inline.ToString());
        }
    }
}
