using System.Globalization;
using System.Text;

namespace TDPdf.Services
{
    /// <summary>Which PDF/UA rules a file was checked against.</summary>
    internal enum PdfUaStandard { Ua1, Ua2 }

    internal enum PdfUaSeverity { Error, Warning, Note }

    /// <summary>What the machine checks found. Never "compliant": a person still has to check the manual list.</summary>
    internal enum PdfUaVerdict { PassedMachineChecks, Failed, PartlyChecked }

    /// <summary>A problem that TDPdf can fix by itself, with one undo step.</summary>
    internal enum PdfUaFix { None, SetLanguage, SetTitle, SetDisplayDocTitle, MarkTagged }

    /// <summary>A box on a page in points from the page's top left corner, as displayed.</summary>
    internal readonly record struct PdfUaBounds(double X, double Y, double W, double H);

    /// <summary>
    /// One finding: the standard it was checked under, the Matterhorn checkpoint, how serious it is, what is
    /// wrong in plain words, and where (page, object, box) when that is known.
    /// </summary>
    internal sealed record PdfUaFinding(
        PdfUaStandard Standard, string Checkpoint, PdfUaSeverity Severity, string Message,
        int? PageIndex = null, int? ObjectNumber = null, int? Generation = null,
        PdfUaBounds? Bounds = null, PdfUaFix Fix = PdfUaFix.None);

    /// <summary>The result of checking one file.</summary>
    internal sealed class PdfUaReport
    {
        internal PdfUaReport(PdfUaStandard standard, PdfUaVerdict verdict, IReadOnlyList<PdfUaFinding> findings,
            IReadOnlyList<string> manualChecks, IReadOnlyList<string> coverage, bool claimsPdfUa)
        {
            Standard = standard; Verdict = verdict; Findings = findings;
            ManualChecks = manualChecks; Coverage = coverage; ClaimsPdfUa = claimsPdfUa;
        }

        internal PdfUaStandard Standard { get; }
        internal PdfUaVerdict Verdict { get; }
        internal IReadOnlyList<PdfUaFinding> Findings { get; }
        /// <summary>What no program can decide: listed for a person, never scored.</summary>
        internal IReadOnlyList<string> ManualChecks { get; }
        /// <summary>What was looked at (and any limit that cut a check short).</summary>
        internal IReadOnlyList<string> Coverage { get; }
        /// <summary>True when the file's own metadata says it is PDF/UA.</summary>
        internal bool ClaimsPdfUa { get; }

        internal string StandardName => StandardLabel(Standard);

        internal static string StandardLabel(PdfUaStandard s) => s == PdfUaStandard.Ua2 ? "PDF/UA-2" : "PDF/UA-1";

        internal string VerdictText => Verdict switch
        {
            PdfUaVerdict.PassedMachineChecks => $"Passed TDPdf's machine checks against {StandardName}",
            PdfUaVerdict.PartlyChecked => $"Only partly checked against {StandardName}",
            _ => $"Failed TDPdf's machine checks against {StandardName}",
        };

        /// <summary>The report as JSON with a fixed key order, so two runs on one file are byte for byte the same.</summary>
        internal string ToJson(string? file = null)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            if (file is not null) sb.Append("\"file\":").Append(Quote(file)).Append(',');
            sb.Append("\"standard\":").Append(Quote(StandardName)).Append(',');
            sb.Append("\"claimsPdfUa\":").Append(ClaimsPdfUa ? "true" : "false").Append(',');
            sb.Append("\"verdict\":").Append(Quote(VerdictKey(Verdict))).Append(',');
            sb.Append("\"errors\":").Append(Findings.Count(f => f.Severity == PdfUaSeverity.Error)).Append(',');
            sb.Append("\"warnings\":").Append(Findings.Count(f => f.Severity == PdfUaSeverity.Warning)).Append(',');
            sb.Append("\"findings\":[");
            for (int i = 0; i < Findings.Count; i++)
            {
                if (i > 0) sb.Append(',');
                AppendFinding(sb, Findings[i]);
            }
            sb.Append("],\"coverage\":");
            AppendStrings(sb, Coverage);
            sb.Append(",\"manualChecks\":");
            AppendStrings(sb, ManualChecks);
            sb.Append(",\"note\":").Append(Quote("A pass means TDPdf's machine checks found nothing. It is not a statement of PDF/UA conformance."));
            sb.Append('}');
            return sb.ToString();
        }

        internal static string VerdictKey(PdfUaVerdict v) => v switch
        {
            PdfUaVerdict.PassedMachineChecks => "passedMachineChecks",
            PdfUaVerdict.PartlyChecked => "partlyChecked",
            _ => "failed",
        };

        private static void AppendFinding(StringBuilder sb, PdfUaFinding f)
        {
            sb.Append('{');
            sb.Append("\"standard\":").Append(Quote(StandardLabel(f.Standard))).Append(',');
            sb.Append("\"checkpoint\":").Append(Quote(f.Checkpoint)).Append(',');
            sb.Append("\"severity\":").Append(Quote(f.Severity.ToString().ToLowerInvariant())).Append(',');
            sb.Append("\"message\":").Append(Quote(f.Message)).Append(',');
            sb.Append("\"page\":").Append(f.PageIndex is { } p ? (p + 1).ToString(CultureInfo.InvariantCulture) : "null").Append(',');
            sb.Append("\"object\":").Append(f.ObjectNumber is { } o ? o.ToString(CultureInfo.InvariantCulture) : "null").Append(',');
            sb.Append("\"generation\":").Append(f.Generation is { } g ? g.ToString(CultureInfo.InvariantCulture) : "null").Append(',');
            sb.Append("\"bounds\":");
            if (f.Bounds is { } b)
                sb.Append('[').Append(Num(b.X)).Append(',').Append(Num(b.Y)).Append(',').Append(Num(b.W)).Append(',').Append(Num(b.H)).Append(']');
            else sb.Append("null");
            sb.Append(",\"fixable\":").Append(f.Fix != PdfUaFix.None ? "true" : "false");
            sb.Append('}');
        }

        private static void AppendStrings(StringBuilder sb, IReadOnlyList<string> items)
        {
            sb.Append('[');
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Quote(items[i]));
            }
            sb.Append(']');
        }

        private static string Num(double d) => Math.Round(d, 2).ToString("0.##", CultureInfo.InvariantCulture);

        internal static string Quote(string s)
        {
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
