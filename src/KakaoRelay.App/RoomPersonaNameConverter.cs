using System.Globalization;
using System.Windows.Data;
using KakaoRelay.Core;

namespace KakaoRelay.App;

public sealed class RoomPersonaNameConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not LocalRoom room || values[1] is not AiSettings settings) return "";
        return settings.RoomPersonas.TryGetValue(AiSettings.RoomKey(room.Profile, room.Id), out var id)
            ? settings.Personas.FirstOrDefault(p => p.Id == id)?.Persona.Name ?? "" : "";
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class RoomReplyModeConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not LocalRoom room || values[1] is not AiSettings settings) return "";
        return settings.ReplyModeForRoom(room.Profile, room.Id) switch
        {
            "immediate" => "즉시", "trigger" => "호출", "context" => "맥락", _ => ""
        };
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
