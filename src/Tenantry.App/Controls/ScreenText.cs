using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace Tenantry.App.Controls;

/// <summary>
/// The text a part of the screen shows, laid out as it reads: things side by side on one line
/// (a label and its value, a time and its step), things stacked on their own lines, and a blank
/// line where the screen leaves a gap between sections. Buttons' own words, icons, hidden parts and
/// password boxes are left out; a switch or tick box says whether it's on.
/// </summary>
public static class ScreenText
{
    /// <summary>Marks a control whose words never belong in copied text (the copy button itself).</summary>
    public const string Skip = "nocopy";

    sealed record Piece(string Text, Rect Box, bool IsLabel);

    sealed class Line
    {
        public List<Piece> Pieces { get; } = [];
        public double Top { get; set; } = double.MaxValue;
        public double Bottom { get; set; } = double.MinValue;

        public void Add(Piece piece)
        {
            Pieces.Add(piece);
            Top = Math.Min(Top, piece.Box.Top);
            Bottom = Math.Max(Bottom, piece.Box.Bottom);
        }
    }

    public static string Of(Visual root)
    {
        var pieces = new List<Piece>();
        Collect(root, root, pieces);
        return Lay(pieces);
    }

    /// <summary>The words of one text control, runs and line breaks included.</summary>
    public static string TextOf(TextBlock block)
    {
        if (!string.IsNullOrEmpty(block.Text))
            return block.Text;
        if (block.Inlines is not { Count: > 0 } inlines)
            return "";
        var text = new StringBuilder();
        foreach (var inline in inlines)
            Append(inline, text);
        return text.ToString();
    }

    static void Append(Inline inline, StringBuilder text)
    {
        switch (inline)
        {
            case Run run:
                text.Append(run.Text);
                break;
            case LineBreak:
                text.Append('\n');
                break;
            case Span span:
                foreach (var child in span.Inlines)
                    Append(child, text);
                break;
        }
    }

    static void Collect(Visual root, Visual parent, List<Piece> pieces)
    {
        foreach (var child in parent.GetVisualChildren())
        {
            if (!child.IsVisible || child.Opacity <= 0.01 || child is Control { Classes: var classes } && classes.Contains(Skip))
                continue;
            switch (child)
            {
                case ScrollBar or DataGrid:
                    continue;
                case ToggleSwitch toggle:
                    Add(root, toggle, ((toggle.Content as string) is { Length: > 0 } name ? name + ": " : "") + (toggle.IsChecked == true ? "On" : "Off"), pieces);
                    continue;
                case CheckBox or RadioButton:
                {
                    var box = (ToggleButton)child;
                    var inner = new List<Piece>();
                    Collect(root, box, inner);
                    var words = string.Join(" ", inner.Select(p => p.Text));
                    Add(root, box, (box.IsChecked == true ? "☑ " : "☐ ") + words, pieces);
                    continue;
                }
                case Button button when !button.Classes.Contains("link"):
                    continue;
                case TextBox box:
                    if (box.PasswordChar == default && !string.IsNullOrWhiteSpace(box.Text))
                        Add(root, box, box.Text, pieces);
                    continue;
                case ComboBox combo when combo.SelectedItem is null:
                    continue;
                case TextBlock block:
                    Add(root, block, TextOf(block), pieces, block.Classes.Contains("label"));
                    continue;
            }
            Collect(root, child, pieces);
        }
    }

    static void Add(Visual root, Visual visual, string? text, List<Piece> pieces, bool isLabel = false)
    {
        if (string.IsNullOrWhiteSpace(text) || visual.TranslatePoint(default, root) is not { } at)
            return;
        pieces.Add(new Piece(text.Trim(), new Rect(at, visual.Bounds.Size), isLabel));
    }

    static string Lay(List<Piece> pieces)
    {
        // Top to bottom, then left to right. A piece joins a line when it sits mostly within the
        // line's height and beside (not under) everything already on it.
        var lines = new List<Line>();
        foreach (var piece in pieces.OrderBy(p => Math.Round(p.Box.Top)).ThenBy(p => p.Box.Left))
        {
            var line = lines.Skip(Math.Max(0, lines.Count - 6)).LastOrDefault(l => Beside(l, piece));
            if (line is null)
            {
                line = new Line();
                lines.Add(line);
            }
            line.Add(piece);
        }
        lines.Sort((a, b) => a.Top.CompareTo(b.Top));

        var text = new StringBuilder();
        Line? previous = null;
        foreach (var line in lines)
        {
            if (previous is not null)
            {
                text.Append('\n');
                if (line.Top - previous.Bottom > 10)
                    text.Append('\n');
            }
            var ordered = line.Pieces.OrderBy(p => p.Box.Left).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                if (i > 0)
                    text.Append(ordered[i - 1].IsLabel ? ": " : "\t");
                text.Append(ordered[i].Text);
            }
            previous = line;
        }
        return text.ToString();
    }

    static bool Beside(Line line, Piece piece)
    {
        var overlap = Math.Min(line.Bottom, piece.Box.Bottom) - Math.Max(line.Top, piece.Box.Top);
        if (overlap < Math.Min(piece.Box.Height, line.Bottom - line.Top) * 0.5)
            return false;
        return line.Pieces.All(p => p.Box.Right <= piece.Box.Left + 1 || piece.Box.Right <= p.Box.Left + 1);
    }
}
