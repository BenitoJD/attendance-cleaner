using AttendanceCleaner.Core;

namespace AttendanceCleaner;

public sealed record ResultRow(string Title, string Details, string Punches, string Remark);

public partial class ResultPage : ContentPage
{
    public ResultPage(IReadOnlyList<TemplateRow> rows, string scopeSummary, string savedFileName)
    {
        InitializeComponent();

        var present = rows.Count(r => r.Remark == TemplateSpec.RemarkPresent);
        var absent = rows.Count(r => r.Remark == TemplateSpec.RemarkAbsent);
        var inOnly = rows.Count(r => r.Remark == TemplateSpec.RemarkInPunchOnly);

        SummaryLabel.Text = $"{rows.Count} rows · {present} present · {absent} absent"
            + (inOnly > 0 ? $" · {inOnly} in-punch-only" : "");
        SavedLabel.Text = $"{scopeSummary} · Saved as: {savedFileName}";

        ItemsView.ItemsSource = rows.Select(r => new ResultRow(
            Title: $"{r.SlNo}. {r.Name}",
            Details: $"ID {r.IdNo} · {r.DateText} {r.Day}"
                + (r.Gender != TemplateSpec.UnknownGender ? $" · {r.Gender}" : ""),
            Punches: (r.InPunch, r.OutPunch) switch
            {
                (null, null) => "",
                (_, null) => $"In {r.InPunch}",
                (null, _) => $"Out {r.OutPunch}",
                (_, _) => $"{r.InPunch} → {r.OutPunch} · {r.TotalHours}",
            },
            Remark: r.Remark)).ToList();
    }
}
