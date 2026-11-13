using System.Net;

namespace DbLens.Core.Inspection;

/// <summary>Decides whether a server address names this machine. Pure; no DNS lookups.</summary>
public static class LocalServer
{
    public static bool IsLocal(string host)
    {
        var h = host.Trim();
        if (h.Length == 0) return false;

        // PostgreSQL Unix-domain socket directory.
        if (h.StartsWith('/')) return true;

        // SQL Server: "tcp:host,port", "np:\\.\pipe\…", "host\INSTANCE", "(local)", ".", "(localdb)\…".
        if (h.StartsWith("lpc:", StringComparison.OrdinalIgnoreCase)
            || h.StartsWith("(localdb)", StringComparison.OrdinalIgnoreCase))
            return true;

        // A named pipe names its server first: np:\\server\pipe\…
        if (h.StartsWith("np:", StringComparison.OrdinalIgnoreCase)) h = h[3..].TrimStart('\\');
        if (h.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) h = h[4..];
        h = h.Split(',')[0].Split('\\')[0].Trim();

        if (h is "." or "(local)") return true;
        if (h.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || h.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || h.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            return true;

        if (h.StartsWith('[') && h.EndsWith(']')) h = h[1..^1];
        else if (h.Count(c => c == ':') == 1) h = h.Split(':')[0];

        return IPAddress.TryParse(h, out var ip) && IPAddress.IsLoopback(ip);
    }
}
