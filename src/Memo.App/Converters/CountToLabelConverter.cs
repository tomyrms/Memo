using Microsoft.UI.Xaml.Data;

namespace Memo.App.Converters;

public sealed class CountToLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var count = value is int i ? i : 0;
        return count == 1 ? "1 souvenir" : $"{count} souvenirs";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
