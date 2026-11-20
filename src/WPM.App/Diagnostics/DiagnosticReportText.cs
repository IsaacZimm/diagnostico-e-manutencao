using System.Globalization;
using System.Text;

namespace WPM.App.Diagnostics;

internal static class DiagnosticReportText
{
    public static string Format(DiagnosticReport report)
    {
        var text = new StringBuilder();
        text.AppendLine("Diagnóstico e Manutenção — Relatório de diagnóstico");
        text.AppendLine($"Coleta realizada em {report.CollectedAt.ToString("dd/MM/yyyy HH:mm:ss zzz", CultureInfo.InvariantCulture)}");

        foreach (var section in report.Sections)
        {
            text.AppendLine();
            text.AppendLine(section.Title);

            foreach (var item in section.Items)
            {
                text.AppendLine($"  {item.Label}: {item.Value}");
            }

            if (!string.IsNullOrWhiteSpace(section.Issue))
            {
                text.AppendLine($"  Observação: {section.Issue}");
            }
        }

        return text.ToString();
    }
}
