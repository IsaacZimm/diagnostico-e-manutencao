using System.Management;
using System.Runtime.InteropServices;
using System.Security;

namespace WPM.App.Diagnostics;

internal static class WmiDiagnosticQuery
{
    public static string? Read(string query, Action<ManagementBaseObject> consume, string scope = @"root\CIMV2")
    {
        try
        {
            var options = new System.Management.EnumerationOptions
            {
                ReturnImmediately = true,
                Rewindable = false,
                Timeout = TimeSpan.FromSeconds(5)
            };

            using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(query), options);
            using ManagementObjectCollection results = searcher.Get();
            foreach (ManagementBaseObject row in results)
            {
                using (row)
                {
                    consume(row);
                }
            }

            return null;
        }
        catch (Exception error) when (error is ManagementException or COMException or UnauthorizedAccessException or SecurityException)
        {
            return "A consulta de diagnóstico ao Windows ficou indisponível.";
        }
    }
}
