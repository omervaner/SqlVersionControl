using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlVersionControl.Services.Formatting;

namespace SqlVersionControl.Tests;

public class AlterTableFormattingTests
{
    private static string Fmt(string input) => ScriptDomFormatter.Format(input, new FormatterOptions());

    private static TSqlScript? ReParse(string sql)
    {
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        var result = parser.Parse(reader, out var errors) as TSqlScript;
        return (errors == null || errors.Count == 0) ? result : null;
    }

    [Fact]
    public void Format_AlterTable_AddColumn()
    {
        var input = "ALTER TABLE dbo.t ADD c INT NULL";
        var output = Fmt(input);
        var expected =
            "ALTER TABLE dbo.t\n" +
            "ADD c INT NULL\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_AlterTable_AddConstraintFk()
    {
        var input = "ALTER TABLE dbo.t ADD CONSTRAINT FK FOREIGN KEY (a) REFERENCES dbo.p(id)";
        var output = Fmt(input);
        var expected =
            "ALTER TABLE dbo.t\n" +
            "ADD CONSTRAINT FK FOREIGN KEY (a) REFERENCES dbo.p (id)\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_AlterTable_AddDefaultFor()
    {
        // Real corpus shape from create_table.sql: ALTER TABLE ... ADD DEFAULT (...) FOR <col>.
        // DefaultConstraintDefinition is generator-rendered wholesale (no per-constraint WITH/ON
        // tail, so the inline-or-wrap path doesn't apply).
        var input = "ALTER TABLE [dbo].[t_sap_report] ADD DEFAULT ((1)) FOR [active]";
        var output = Fmt(input);
        var expected =
            "ALTER TABLE [dbo].[t_sap_report]\n" +
            "ADD DEFAULT ((1)) FOR [active]\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_AlterTable_DropConstraint()
    {
        var input = "ALTER TABLE dbo.t DROP CONSTRAINT FK";
        var output = Fmt(input);
        var expected =
            "ALTER TABLE dbo.t\n" +
            "DROP CONSTRAINT FK\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_AlterTable_DropColumn()
    {
        var input = "ALTER TABLE dbo.t DROP COLUMN c";
        var output = Fmt(input);
        var expected =
            "ALTER TABLE dbo.t\n" +
            "DROP COLUMN c\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_AlterTable_AlterColumn()
    {
        var input = "ALTER TABLE dbo.t ALTER COLUMN c VARCHAR(200) NOT NULL";
        var output = Fmt(input);
        var expected =
            "ALTER TABLE dbo.t\n" +
            "ALTER COLUMN c VARCHAR (200) NOT NULL\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_AlterTable_EnableTriggerAll()
    {
        var input = "ALTER TABLE dbo.t ENABLE TRIGGER ALL";
        var output = Fmt(input);
        var expected =
            "ALTER TABLE dbo.t\n" +
            "ENABLE TRIGGER ALL\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_AlterTable_WithCheckAddConstraint()
    {
        // ExistingRowsCheckEnforcement = Check → emits "WITH CHECK " prefix on the action line.
        var input = "ALTER TABLE dbo.t WITH CHECK ADD CONSTRAINT FK_x FOREIGN KEY (a) REFERENCES dbo.p(id)";
        var output = Fmt(input);
        var expected =
            "ALTER TABLE dbo.t\n" +
            "WITH CHECK ADD CONSTRAINT FK_x FOREIGN KEY (a) REFERENCES dbo.p (id)\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_AlterTable_NoCheckConstraint()
    {
        var input = "ALTER TABLE dbo.t NOCHECK CONSTRAINT FK_x";
        var output = Fmt(input);
        var expected =
            "ALTER TABLE dbo.t\n" +
            "NOCHECK CONSTRAINT FK_x\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }
}
