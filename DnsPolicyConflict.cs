using System.Text.Json;
namespace HappBox;

public static class DnsPolicyConflict
{
    public static string Message(string? report)
    {
        var ag=false; var corporate=false;
        try
        {
            using var document=JsonDocument.Parse(report ?? "{}");
            foreach(var section in new[]{"Rules","Effective"})
            {
                if(!document.RootElement.TryGetProperty(section,out var rows)||rows.ValueKind!=JsonValueKind.Array)continue;
                foreach(var rule in rows.EnumerateArray())
                {
                    if(!rule.TryGetProperty("Namespace",out var ns)||!HasNamespace(ns))continue;
                    // Never echo arbitrary display names, comments or internal namespaces.
                    var comment=Text(rule,"Comment");var name=Text(rule,"DisplayName");
                    ag |= comment.Equals("AG_UNLOCKER_NRPT_V2",StringComparison.OrdinalIgnoreCase) &&
                        name.StartsWith("AG Unlocker ",StringComparison.OrdinalIgnoreCase);
                    corporate |= Text(rule,"PolicyStore").Equals("GroupPolicy",StringComparison.OrdinalIgnoreCase) ||
                        Text(rule,"PolicyStoreSourceType").Equals("GroupPolicy",StringComparison.OrdinalIgnoreCase) ||
                        rule.TryGetProperty("DirectAccessEnabled",out var direct)&&direct.ValueKind==JsonValueKind.True;
                }
            }
        }
        catch(JsonException) { }
        var advice=ag ? "Обнаружены DNS-правила AG Unlocker, мешающие запуску ior-core в режиме TUN. Отключите изменение DNS в AG Unlocker штатным способом и повторите подключение. Простое завершение процесса может не удалить правила." :
            "Обнаружены сторонние DNS-правила, мешающие запуску ior-core в режиме TUN. Отключите изменение DNS в программе, создавшей правила, и повторите подключение. Если правила системные или корпоративные, обратитесь к администратору.";
        if(corporate)advice+=" Также обнаружены признаки корпоративной DNS-политики: обратитесь к администратору.";
        return "NATIVE_DNS_POLICY_CONFLICT: "+advice+" Чужие правила не изменены. Подробности — в локальном DNS-отчёте.";
    }
    private static bool HasNamespace(JsonElement ns)=>ns.ValueKind==JsonValueKind.String ? !string.IsNullOrWhiteSpace(ns.GetString()) :
        ns.ValueKind==JsonValueKind.Array && ns.EnumerateArray().Any(HasNamespace);
    private static string Text(JsonElement row,string key)=>row.TryGetProperty(key,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString()??"":"";
}
