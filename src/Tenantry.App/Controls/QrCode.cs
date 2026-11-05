using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using QRCoder;

namespace Tenantry.App.Controls;

/// <summary>
/// A QR code for <see cref="Text"/>, drawn as squares (sharp at any size, black on white in both
/// themes, with the quiet margin phone cameras need).
/// </summary>
public sealed class QrCode : Control
{
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<QrCode, string?>(nameof(Text));

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    static QrCode() => AffectsRender<QrCode>(TextProperty);

    bool[,]? _modules;
    string? _for;

    bool[,]? Modules()
    {
        if (_for == Text)
            return _modules;
        _for = Text;
        if (string.IsNullOrEmpty(Text))
            return _modules = null;
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(Text, QRCodeGenerator.ECCLevel.M);
        var matrix = data.ModuleMatrix;
        var size = matrix.Count;
        var modules = new bool[size, size];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                modules[x, y] = matrix[y][x];
        return _modules = modules;
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Brushes.White, bounds);
        if (Modules() is not { } modules)
            return;
        var count = modules.GetLength(0);
        var cell = Math.Floor(Math.Min(bounds.Width, bounds.Height) / count);
        if (cell < 1)
            return;
        var offset = (Math.Min(bounds.Width, bounds.Height) - cell * count) / 2;
        for (var y = 0; y < count; y++)
            for (var x = 0; x < count; x++)
                if (modules[x, y])
                    context.FillRectangle(Brushes.Black, new Rect(offset + x * cell, offset + y * cell, cell, cell));
    }
}
