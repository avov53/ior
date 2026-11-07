namespace HappBox;
public static class NativeFailureReason
{
    public static string? TransportCode(string? codes)=>codes?.Split(", ").LastOrDefault(code=>
        code=="TUN_EGRESS_RECAPTURED" || code.StartsWith("REALITY_",StringComparison.Ordinal) &&
        (code.EndsWith("_FAILED",StringComparison.Ordinal)||code.EndsWith("_INVALID",StringComparison.Ordinal)||code.EndsWith("_REJECTED",StringComparison.Ordinal)||code.EndsWith("_MISMATCH",StringComparison.Ordinal)||code.EndsWith("_TIMEOUT",StringComparison.Ordinal)));
}
