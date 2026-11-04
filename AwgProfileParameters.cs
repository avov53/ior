namespace HappBox;
public static class AwgProfileParameters
{
    public static Dictionary<string,string> Normalize(IReadOnlyDictionary<string,string> parameters)
    {
        var result=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var item in parameters)
        {
            var key=ProfileParser.AwgParameterNames.FirstOrDefault(pair=>pair.Key.Equals(item.Key,StringComparison.OrdinalIgnoreCase)||pair.Value.Equals(item.Key,StringComparison.OrdinalIgnoreCase)).Value;
            if(key is null || item.Value is null || item.Value.Contains('\n') || item.Value.Contains('\r') || item.Value.Length>8192)
                throw new FormatException("AWG_PARAMETER_INVALID: неизвестный параметр или недопустимый формат.");
            var value=item.Value.Trim();
            if(key is "random_trailers" or "disable_cookies")value=value.ToLowerInvariant() switch {"on" or "yes" or "1" or "true"=>"true","off" or "no" or "0" or "false"=>"false",_=>throw new FormatException("AWG_PARAMETER_INVALID: требуется логическое значение.")};
            if(!result.TryAdd(key,value))throw new FormatException("AWG_PARAMETER_DUPLICATE: повторный параметр.");
        }
        return result;
    }
}
