namespace LancerNexus.Gateway;

public static class TransferRateLimitPartition
{
    public static string GetKey(HttpContext context)
    {
        // Use the matched route template, never the transfer ID or bearer credential.
        // Each step has its own budget so one handoff does not exhaust the next one.
        var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unknown";
        var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return $"{address}|{context.Request.Method}|{route}";
    }
}
