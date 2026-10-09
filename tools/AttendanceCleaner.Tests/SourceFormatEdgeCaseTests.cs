using System.Text;
using AttendanceCleaner.Core;
using Xunit;

namespace AttendanceCleaner.Tests;

public sealed class SourceFormatEdgeCaseTests
{
    [Theory]
    [InlineData("<meta charset='windows-1252'>")]
    [InlineData("<META CHARSET=windows-1252>")]
    [InlineData("<meta charset='windows&#45;1252'>")]
    [InlineData("<meta content='text/html; charset=windows-1252' http-equiv='Content-Type'>")]
    [InlineData("<meta http-equiv='CONTENT-TYPE' content='text/html; charset = windows-1252'>")]
    public void Actual_charset_metadata_preserves_legacy_names(string metadata)
    {
        var html = WithHead(metadata, Daily("José"));

        Assert.Equal("José", Assert.Single(AttendanceParser.ParseBytes(Encoding.Latin1.GetBytes(html)).Records).Name);
    }

    [Theory]
    [InlineData("<meta charset='windows-1252'>")]
    [InlineData("<meta content='text/html; charset=windows-1252' http-equiv='Content-Type'>")]
    public void Charset_before_body_is_read_when_the_head_tags_are_omitted(string metadata)
    {
        var html = WithHead(metadata, Daily("José"))
            .Replace("<head>", "", StringComparison.Ordinal).Replace("</head>", "", StringComparison.Ordinal);

        Assert.Equal("José", Assert.Single(AttendanceParser.ParseBytes(Encoding.Latin1.GetBytes(html)).Records).Name);
    }

    [Theory]
    [InlineData("<!-- <meta charset='unknown-encoding'> -->")]
    [InlineData("<script>const example = \"<meta charset='unknown-encoding'>\";</script>")]
    [InlineData("<style>/* <meta charset='unknown-encoding'> */</style>")]
    public void Implicit_head_ignores_metadata_lookalikes(string irrelevant)
    {
        var html = WithHead(irrelevant + "<meta charset='windows-1252'>", Daily("José"))
            .Replace("<head>", "", StringComparison.Ordinal).Replace("</head>", "", StringComparison.Ordinal);

        Assert.Equal("José", Assert.Single(AttendanceParser.ParseBytes(Encoding.Latin1.GetBytes(html)).Records).Name);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Charset_in_body_data_cannot_override_the_source_encoding(bool explicitBody)
    {
        var html = Daily("தமிழ் José").Replace("<head>", "", StringComparison.Ordinal).Replace("</head>", "", StringComparison.Ordinal)
            .Replace("</body>", "<meta charset='windows-1252'></body>", StringComparison.Ordinal);
        if (!explicitBody)
            html = html.Replace("<body>", "", StringComparison.Ordinal).Replace("</body>", "", StringComparison.Ordinal);

        Assert.Equal("தமிழ் José", Assert.Single(AttendanceParser.ParseBytes(Encoding.UTF8.GetBytes(html)).Records).Name);
    }

    [Fact]
    public void Metadata_after_body_start_is_ignored_even_before_the_first_table()
    {
        var html = Daily("தமிழ் José").Replace("<body>", "<body><meta charset='windows-1252'>", StringComparison.Ordinal);

        Assert.Equal("தமிழ் José", Assert.Single(AttendanceParser.ParseBytes(Encoding.UTF8.GetBytes(html)).Records).Name);
    }

    [Theory]
    [InlineData("<!-- <meta charset='unknown-encoding'> -->")]
    [InlineData("<script>const example = \"<meta charset='unknown-encoding'>\";</script>")]
    [InlineData("<style>/* <meta charset='unknown-encoding'> */</style>")]
    [InlineData("<meta name='keywords' content='charset=unknown-encoding'>")]
    [InlineData("<meta http-equiv='refresh' content='0; charset=unknown-encoding'>")]
    public void Text_that_resembles_charset_metadata_cannot_override_real_metadata(string irrelevant)
    {
        var html = WithHead(irrelevant + "<meta charset='windows-1252'>", Daily("José"));

        Assert.Equal("José", Assert.Single(AttendanceParser.ParseBytes(Encoding.Latin1.GetBytes(html)).Records).Name);
    }

    [Theory]
    [InlineData("<!-- <meta charset='unknown-encoding'> -->")]
    [InlineData("<script>const example = \"<meta charset='unknown-encoding'>\";</script>")]
    [InlineData("<meta name='keywords' content='charset=unknown-encoding'>")]
    public void Metadata_lookalikes_without_a_real_declaration_keep_utf8(string irrelevant)
    {
        var html = WithHead(irrelevant, Daily("தமிழ் José"));

        Assert.Equal("தமிழ் José", Assert.Single(AttendanceParser.ParseBytes(Encoding.UTF8.GetBytes(html)).Records).Name);
    }

    [Fact]
    public void Charset_after_a_large_stylesheet_is_still_read()
    {
        var html = WithHead("<style>/*" + new string('x', 8192) + "*/</style><meta charset='windows-1252'>", Daily("José"));

        Assert.Equal("José", Assert.Single(AttendanceParser.ParseBytes(Encoding.Latin1.GetBytes(html)).Records).Name);
    }

    [Fact]
    public void Bom_takes_precedence_over_stale_charset_metadata()
    {
        var html = WithHead("<meta charset='windows-1252'>", Daily("தமிழ் José"));
        var encoding = new UTF8Encoding(true, true);
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(html)).ToArray();

        Assert.Equal("தமிழ் José", Assert.Single(AttendanceParser.ParseBytes(bytes).Records).Name);
    }

    [Theory]
    [InlineData("unknown-encoding")]
    [InlineData("utf-7")]
    public void Unsupported_actual_encoding_has_a_reexport_instruction(string charset)
    {
        var html = WithHead($"<meta charset='{charset}'>", Daily("Employee"));

        var exception = Assert.Throws<InvalidDataException>(() => AttendanceParser.ParseBytes(Encoding.UTF8.GetBytes(html)));
        Assert.Contains("unsupported text encoding", exception.Message);
        Assert.Contains("export", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("<!-- </body> -->")]
    [InlineData("<!-- </html> -->")]
    [InlineData("<script>const text = '</body>';</script>")]
    [InlineData("<style>/* </html> */</style>")]
    [InlineData("<span data-note='</body>'></span>")]
    [InlineData("<span data-note=\"</html>\"></span>")]
    [InlineData("</html-export>")]
    [InlineData("<!-- unfinished </body>")]
    public void Closing_tag_lookalikes_cannot_make_a_truncated_download_valid(string ending)
    {
        var html = Daily("Employee").Replace("</body></html>", ending, StringComparison.Ordinal);

        var exception = Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(html));
        Assert.Contains("incomplete", exception.Message);
    }

    [Theory]
    [InlineData("</BODY >")]
    [InlineData("</HTML >")]
    [InlineData("<!-- optional footer --></body></html>")]
    public void Real_document_closing_tags_are_accepted(string ending)
    {
        var html = Daily("Employee").Replace("</body></html>", ending, StringComparison.Ordinal);

        Assert.Single(AttendanceParser.Parse(html).Records);
    }

    [Theory]
    [InlineData("<br>")]
    [InlineData("<br/>")]
    [InlineData("<BR />")]
    [InlineData("</p><p>")]
    [InlineData("</div><div>")]
    public void Visible_html_line_boundaries_separate_report_range_tokens(string separator)
    {
        var html = TestReports.MonthlyBlocks().Replace("00:00 To:", "00:00" + separator + "To:", StringComparison.Ordinal);

        var report = AttendanceParser.Parse(html);
        Assert.Equal(9, report.Records.Count);
        Assert.All(report.Records, record => Assert.Equal(2026, record.Date.Year));
        Assert.Equal(new[] { 1, 2, 3 }, report.Records.Select(record => record.Date.Day).Distinct().Order().ToArray());
    }

    [Fact]
    public void Header_line_breaks_are_read_as_label_spacing()
    {
        var html = Daily("Employee").Replace("Person ID", "Person<br>ID", StringComparison.Ordinal);

        Assert.Equal("0013", Assert.Single(AttendanceParser.Parse(html).Records).Id);
    }

    [Fact]
    public void Cell_text_keeps_inline_parts_entities_and_significant_spaces()
    {
        var html = Daily("<b>José</b>  <span>R&amp;D</span>&nbsp;Team");

        Assert.Equal("José  R&D Team", Assert.Single(AttendanceParser.Parse(html).Records).Name);
    }

    [Fact]
    public void Comments_and_script_content_do_not_become_employee_data()
    {
        var html = Daily("Employee<!-- ignored --><script>ignored</script><style>ignored</style>");

        Assert.Equal("Employee", Assert.Single(AttendanceParser.Parse(html).Records).Name);
    }

    [Theory]
    [InlineData("<!-- page break -->")]
    [InlineData("\n<!-- page break -->\n<!-- another comment -->\n")]
    public void Missing_employee_row_tags_are_repaired_across_comments(string comment)
    {
        var html = TestReports.MonthlyBlocks().Replace("<TD><b>Person ID", comment + "<TD><b>Person ID", StringComparison.Ordinal);

        var report = AttendanceParser.Parse(html);
        Assert.Equal(9, report.Records.Count);
        Assert.Equal(3, report.Records.Select(record => record.Id).Distinct().Count());
        Assert.Equal("06:51", report.Records[0].InPunch);
        Assert.Contains(report.Records, record => record.Id == "4" && record.Date.Day == 2 && record.InPunch == "08:10");
    }

    [Theory]
    [InlineData(".xls")]
    [InlineData(".XLS")]
    [InlineData(".html")]
    [InlineData(".xlsx")]
    public void Source_content_detection_is_independent_of_filename(string extension)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "report export" + extension);
        try
        {
            File.WriteAllText(path, Daily("Employee"), new UTF8Encoding(false, true));

            Assert.Equal("0013", Assert.Single(AttendanceParser.ParseFile(path).Records).Id);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string Daily(string name) => TestReports.DailyFlexible(
        new TestReports.DailyRow("1", "0013", name, "01-09-2026", "06:00", "19:00"));

    private static string WithHead(string content, string html) => html.Replace("<head>", "<head>" + content, StringComparison.Ordinal);
}
