using System.Globalization;

namespace CeleCamIp.App.Converters;

/// <summary>true (online) -> verde, false (offline) -> gris.</summary>
public class BoolToStatusColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Colors.LimeGreen : Colors.Gray;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>true (online) -> "En linea", false (offline) -> "Desconectada".</summary>
public class BoolToStatusTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "En linea" : "Desconectada";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Cuenta de una lista -> texto tipo "3 camara(s)".</summary>
public class CameraCountConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = (value as System.Collections.ICollection)?.Count ?? 0;
        return count == 1 ? "1 camara" : $"{count} camaras";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
