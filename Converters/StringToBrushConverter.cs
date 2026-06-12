using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace CortexFX.Converters;

/// <summary>
/// Converts a hex color string (e.g. "#E11D2E") to a SolidColorBrush.
/// This eliminates the VisualTree-walking hack in UpdateFileStatus()
/// where we manually found TextBlocks and set their Foreground via code-behind.
/// 
/// Usage in XAML:
///   Foreground="{Binding StatusColor, Converter={StaticResource StringToBrushConverter}}"
/// </summary>
[ValueConversion(typeof(string), typeof(Brush))]
public sealed class StringToBrushConverter : IValueConverter
{
    /// <summary>Reusable static instance for resource dictionary registration.</summary>
    public static readonly StringToBrushConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string colorString && !string.IsNullOrEmpty(colorString))
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(colorString);
                var brush = new SolidColorBrush(color);
                brush.Freeze(); // Thread-safety + performance
                return brush;
            }
            catch
            {
                // Fallback: gray for invalid strings
            }
        }

        var fallback = new SolidColorBrush(Colors.Gray);
        fallback.Freeze();
        return fallback;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is SolidColorBrush brush)
        {
            return brush.Color.ToString();
        }
        return "#AAAAAA";
    }
}
