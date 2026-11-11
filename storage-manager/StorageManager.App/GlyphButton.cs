using System.Windows;
using System.Windows.Controls;

namespace StorageManager.App;

/// <summary>A button with an icon-font glyph before its text label, and an optional "current page" state.</summary>
public class GlyphButton : Button
{
    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(GlyphButton), new PropertyMetadata(""));
    public static readonly DependencyProperty IsSelectedProperty =
        DependencyProperty.Register(nameof(IsSelected), typeof(bool), typeof(GlyphButton), new PropertyMetadata(false));

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public bool IsSelected { get => (bool)GetValue(IsSelectedProperty); set => SetValue(IsSelectedProperty, value); }
}
