using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentIcons.Avalonia;
using FluentIcons.Common;

namespace Tenantry.App.Controls;

/// <summary>
/// Copying from anywhere in the app, without each screen doing anything:
/// <list type="bullet">
/// <item>Right-click any text: copy that value, the item it belongs to (one step of a trace), its
/// section (everything under a heading), or everything in the pane, page or popup, laid out as it
/// reads (<see cref="ScreenText"/>). A selection in selectable text is offered first.</item>
/// <item>Point at an address, ID, link, name or heading: a copy button appears at its end.</item>
/// </list>
/// Places with a right-click menu of their own (list rows, text boxes) keep theirs.
/// </summary>
public static partial class CopyAnywhere
{
    const string HoverClass = "copyhover";

    static Control? _hoverText;
    static Canvas? _hoverLayer;
    static Button? _hoverButton;

    /// <summary>Registers both, once, for every window and popup.</summary>
    public static void Register()
    {
        InputElement.ContextRequestedEvent.AddClassHandler<TopLevel>(OnContextRequested);
        InputElement.PointerMovedEvent.AddClassHandler<TopLevel>((_, e) => OnPointerMoved(e), RoutingStrategies.Bubble, handledEventsToo: true);
        InputElement.PointerExitedEvent.AddClassHandler<TopLevel>((_, _) => HideHover());
        InputElement.PointerWheelChangedEvent.AddClassHandler<TopLevel>((_, _) => HideHover(), RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>Puts text on the clipboard and hands it to the system straight away.</summary>
    public static async Task CopyAsync(Visual anchor, string text)
    {
        if (TopLevel.GetTopLevel(anchor)?.Clipboard is not { } clipboard)
            return;
        await clipboard.SetTextAsync(text);
        // An unflushed clipboard on Windows blocks other apps' paste until this app exits.
        await clipboard.FlushAsync();
    }

    // ---- Right-click ------------------------------------------------------------------------

    /// <summary>What a right-click on this text offers, in order: (menu label, text it copies).</summary>
    public static IReadOnlyList<(string Label, string Text)> ChoicesFor(Control target)
    {
        var choices = new List<(string Label, string Text)>();
        void Offer(string label, string? text)
        {
            text = text?.Trim();
            if (!string.IsNullOrEmpty(text) && choices.All(c => c.Text != text))
                choices.Add((label, text));
        }

        if (target is SelectableTextBlock { SelectedText: { Length: > 0 } selected })
            Offer("Copy Selection", selected);
        if (target is TextBlock block && !InsideButton(block))
            Offer($"Copy “{Shorten(ScreenText.TextOf(block))}”", ScreenText.TextOf(block));
        var whole = WholeOf(target);
        if (ItemOf(target, whole) is { } item)
            Offer("Copy This Item", ScreenText.Of(item));
        if (SectionOf(target, whole) is { } section)
            Offer("Copy This Section", ScreenText.Of(section));
        if (whole is not null)
            Offer("Copy Everything Here", ScreenText.Of(whole));
        return choices;
    }

    static void OnContextRequested(TopLevel top, ContextRequestedEventArgs e)
    {
        if (e.Handled || e.Source is not Visual source)
            return;
        if (source.FindAncestorOfType<TextBox>(includeSelf: true) is not null || source.FindAncestorOfType<DataGrid>(includeSelf: true) is not null)
            return;
        var target = e.TryGetPosition(top, out var at) ? TextAt(top, source, at) ?? source as Control : source as Control;
        if (target is null)
            return;
        var choices = ChoicesFor(target);
        if (choices.Count == 0)
            return;
        var menu = new MenuFlyout();
        foreach (var (label, text) in choices)
        {
            var item = new MenuItem { Header = label, Icon = new FluentIcon { Icon = Icon.Copy, FontSize = 14 } };
            item.Click += (_, _) =>
            {
                _ = CopyAsync(target, text);
                ShowCopied(target);
            };
            menu.Items.Add(item);
        }
        menu.ShowAt(target, showAtPointer: true);
        e.Handled = true;
    }

    /// <summary>The text under the pointer: the source itself, or text inside it (a row's padding).</summary>
    static TextBlock? TextAt(TopLevel top, Visual source, Point at)
    {
        if (source is TextBlock direct)
            return direct;
        return source.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible)
            .FirstOrDefault(t => t.TranslatePoint(default, top) is { } origin && new Rect(origin, t.Bounds.Size).Contains(at));
    }

    /// <summary>The pane, page or popup the text is in: its outermost scrolling area.</summary>
    static Visual? WholeOf(Visual target)
    {
        Visual? whole = null;
        foreach (var ancestor in target.GetVisualAncestors())
        {
            if (ancestor is ScrollViewer { Content: Visual content })
                whole = content;
            if (ancestor is TopLevel)
                break;
        }
        return whole;
    }

    /// <summary>One entry of a repeated list (a step, a row of a card), when the text is in one.</summary>
    static Visual? ItemOf(Visual target, Visual? stop)
    {
        foreach (var ancestor in target.GetVisualAncestors())
        {
            if (ancestor == stop)
                return null;
            if (ancestor is Control control && ItemsControl.ItemsControlFromItemContainer(control) is { } owner && owner is not (ComboBox or MenuBase))
                return control;
        }
        return null;
    }

    /// <summary>The heading-led block or card the text is in.</summary>
    static Visual? SectionOf(Visual target, Visual? stop)
    {
        foreach (var ancestor in target.GetVisualAncestors())
        {
            if (ancestor == stop || ancestor is TopLevel)
                return null;
            if (ancestor is Border border && (border.Classes.Contains("card") || border.Classes.Contains("valuerow") || border.Classes.Contains("banner")))
                return border;
            if (ancestor is Panel panel && panel.Children.Count(c => c.IsVisible) >= 2
                && panel.Children.FirstOrDefault(c => c.IsVisible) is TextBlock first
                && (first.Classes.Contains("label") || first.Classes.Contains("h3") || first.Classes.Contains("h2")))
                return panel;
        }
        return null;
    }

    static bool InsideButton(Visual visual) =>
        visual.FindAncestorOfType<Button>() is { } button && !button.Classes.Contains("link");

    static string Shorten(string text)
    {
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= 36 ? text : text[..35] + "…";
    }

    /// <summary>A brief "Copied" beside the text, so a copy from a menu is seen to have happened.</summary>
    static void ShowCopied(Control target)
    {
        if (AdornerLayer.GetAdornerLayer(target) is not { } layer)
            return;
        var note = new Border
        {
            Classes = { "copiednote", ScreenText.Skip },
            Child = new TextBlock { Text = "Copied" },
            IsHitTestVisible = false,
        };
        var canvas = new Canvas { IsHitTestVisible = false, Children = { note } };
        Canvas.SetLeft(note, Math.Min(EndOfText(target).X + 6, target.Bounds.Width + 6));
        Canvas.SetTop(note, -2);
        AdornerLayer.SetAdornedElement(canvas, target);
        AdornerLayer.SetIsClipEnabled(canvas, false);
        layer.Children.Add(canvas);
        DispatcherTimer.RunOnce(() => layer.Children.Remove(canvas), TimeSpan.FromMilliseconds(1200));
    }

    // ---- Hover copy button ------------------------------------------------------------------

    /// <summary>Whether pointing at this text shows a copy button: addresses, IDs, links, names and headings.</summary>
    public static bool OffersHoverCopy(Control control)
    {
        if (control is not TextBlock block || block.Classes.Contains("label") || block.Classes.Contains(ScreenText.Skip))
            return false;
        var text = ScreenText.TextOf(block).Trim();
        if (text.Length == 0 || text.Length > 2000)
            return false;
        if (InsideButton(block) || block.FindAncestorOfType<TextBox>() is not null || block.FindAncestorOfType<DataGrid>() is not null
            || block.FindAncestorOfType<ComboBox>() is not null || block.FindAncestorOfType<MenuItem>() is not null
            || block.FindAncestorOfType<TabItem>() is not null || block.FindAncestorOfType<ToggleSwitch>() is not null)
            return false;
        return block is SelectableTextBlock || block.Classes.Contains("h1") || block.Classes.Contains("h2") || block.Classes.Contains("h3")
            || block.Classes.Contains("value") || LooksLikeIdentifier(text);
    }

    /// <summary>An email address, web address, ID, IP address, domain or phone number.</summary>
    public static bool LooksLikeIdentifier(string text) => Identifier().IsMatch(text.Trim());

    [GeneratedRegex(@"^(?:[^\s@]+@[^\s@]+\.[^\s@]+|https?://\S+|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|\d{1,3}(?:\.\d{1,3}){3}(?:/\d{1,2})?|[0-9a-fA-F]{0,4}(?::[0-9a-fA-F]{0,4}){3,7}(?:/\d{1,3})?|(?:[a-zA-Z0-9-]+\.)+[a-zA-Z]{2,}|\+\d[\d\s().-]{6,}\d|\(\d{3}\)\s?\d{3}-\d{4}|\d{3}[-.\s]\d{3}[-.\s]\d{4})$")]
    private static partial Regex Identifier();

    static void OnPointerMoved(PointerEventArgs e)
    {
        if (e.Source is not Visual source)
            return;
        if (_hoverButton is not null && (source == _hoverButton || source.GetVisualAncestors().Contains(_hoverButton)))
            return;
        var text = source as Control;
        if (text is not null && text == _hoverText)
            return;
        // Moving from the text to its button crosses a few pixels of neither; keep it while near both.
        if (_hoverText is not null && _hoverButton is not null && TopLevel.GetTopLevel(_hoverText) is { } top
            && Near(top, e.GetPosition(top)))
            return;
        HideHover();
        if (text is not null && OffersHoverCopy(text))
            ShowHover((TextBlock)text);
    }

    static bool Near(TopLevel top, Point at)
    {
        if (_hoverText is not { } text || _hoverButton is not { } button
            || text.TranslatePoint(default, top) is not { } textAt || button.TranslatePoint(default, top) is not { } buttonAt)
            return false;
        var area = new Rect(textAt, text.Bounds.Size).Union(new Rect(buttonAt, button.Bounds.Size)).Inflate(4);
        return area.Contains(at);
    }

    static void ShowHover(TextBlock text)
    {
        if (AdornerLayer.GetAdornerLayer(text) is not { } layer)
            return;
        var icon = new FluentIcon { Icon = Icon.Copy, FontSize = 12 };
        var button = new Button
        {
            Classes = { HoverClass, ScreenText.Skip },
            Content = icon,
            Focusable = false,
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Top,
        };
        AutomationProperties.SetName(button, "Copy");
        ToolTip.SetTip(button, "Copy");
        button.Click += (_, _) =>
        {
            _ = CopyAsync(text, ScreenText.TextOf(text).Trim());
            icon.Icon = Icon.Checkmark;
            ToolTip.SetTip(button, "Copied");
        };
        var canvas = new Canvas { Children = { button } };
        var end = EndOfText(text);
        Canvas.SetLeft(button, Math.Min(end.X + 2, text.Bounds.Width + 2));
        Canvas.SetTop(button, Math.Max(0, end.Y - 11));
        AdornerLayer.SetAdornedElement(canvas, text);
        AdornerLayer.SetIsClipEnabled(canvas, false);
        layer.Children.Add(canvas);
        _hoverText = text;
        _hoverLayer = canvas;
        _hoverButton = button;
        text.DetachedFromVisualTree += OnHoverTextGone;
    }

    static void OnHoverTextGone(object? sender, VisualTreeAttachmentEventArgs e) => HideHover();

    static void HideHover()
    {
        if (_hoverText is not null)
            _hoverText.DetachedFromVisualTree -= OnHoverTextGone;
        if (_hoverLayer?.GetVisualParent() is AdornerLayer layer)
            layer.Children.Remove(_hoverLayer);
        _hoverText = null;
        _hoverLayer = null;
        _hoverButton = null;
    }

    /// <summary>The middle of the end of the text's last line, in the text's own coordinates.</summary>
    static Point EndOfText(Control control)
    {
        if (control is not TextBlock block)
            return new Point(control.Bounds.Width, control.Bounds.Height / 2);
        var length = ScreenText.TextOf(block).Length;
        var end = block.TextLayout.HitTestTextPosition(length);
        return new Point(block.Padding.Left + end.Right, block.Padding.Top + end.Top + end.Height / 2);
    }
}
