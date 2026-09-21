using System.Reflection;
using System.Runtime.InteropServices;

namespace D3Parking.Web.Hosting;

public sealed record ReleaseInformation(string Version, string Commit, string Environment, string Runtime)
{
    public static ReleaseInformation Read(string environment)
    {
        var info = typeof(ReleaseInformation).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var parts = info.Split('+', 2);
        return new(parts[0], parts.Length == 2 ? parts[1] : "unknown", environment, RuntimeInformation.FrameworkDescription);
    }
}
