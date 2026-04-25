using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlVersionControl.Services.Formatting;

namespace SqlVersionControl.Tests;

public class TriggerFormattingTests
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
    public void Format_CreateTrigger_AfterInsert_Minimal()
    {
        var input = "CREATE TRIGGER tr_min ON dbo.t AFTER INSERT AS SELECT 1";
        var output = Fmt(input);
        var expected =
            "CREATE TRIGGER tr_min\n" +
            "ON dbo.t\n" +
            "AFTER INSERT\n" +
            "AS\n" +
            "SELECT 1;\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CreateTrigger_MultiEvent_AfterIUD()
    {
        // Source events stay in source order (probe-confirmed: ScriptDom preserves
        // TriggerActions[] order). Comma-joined inline after the timing keyword.
        var input = "CREATE TRIGGER tr_multi ON dbo.t AFTER INSERT, UPDATE, DELETE AS BEGIN SET NOCOUNT ON; END";
        var output = Fmt(input);
        var expected =
            "CREATE TRIGGER tr_multi\n" +
            "ON dbo.t\n" +
            "AFTER INSERT, UPDATE, DELETE\n" +
            "AS\n" +
            "BEGIN\n" +
            "    SET NOCOUNT ON;\n" +
            "END;\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CreateTrigger_InsteadOfInsert()
    {
        var input = "CREATE TRIGGER tr_io ON dbo.t INSTEAD OF INSERT AS BEGIN SELECT 1; END";
        var output = Fmt(input);
        var expected =
            "CREATE TRIGGER tr_io\n" +
            "ON dbo.t\n" +
            "INSTEAD OF INSERT\n" +
            "AS\n" +
            "BEGIN\n" +
            "    SELECT 1;\n" +
            "END;\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CreateTrigger_WithOptions_NotForReplication()
    {
        // Confirms grammar order: WITH options before timing+events; NOT FOR REPLICATION
        // after events, before AS. Two options comma-joined: TriggerOption (Encryption)
        // and ExecuteAsTriggerOption (EXECUTE AS CALLER).
        var input = "CREATE TRIGGER tr_opt ON dbo.t WITH ENCRYPTION, EXECUTE AS CALLER FOR INSERT NOT FOR REPLICATION AS SELECT 1";
        var output = Fmt(input);
        var expected =
            "CREATE TRIGGER tr_opt\n" +
            "ON dbo.t\n" +
            "WITH ENCRYPTION, EXECUTE AS CALLER\n" +
            "FOR INSERT\n" +
            "NOT FOR REPLICATION\n" +
            "AS\n" +
            "SELECT 1;\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CreateTrigger_RealisticBody()
    {
        // Exercises body recursion: BEGIN/END wraps a DECLARE + IF EXISTS (subquery) +
        // UPDATE. The IF EXISTS predicate breaks to indented block via the 4e-ii
        // ExistsPredicate route + 4b-ii ScalarSubquery break-to-block. Vertical-spacing
        // rule (4e-ii-b) inserts the blank line between the single-liner DECLARE and
        // the block-level IF.
        var input = "CREATE TRIGGER tr_body ON dbo.t AFTER UPDATE AS BEGIN DECLARE @id INT; IF EXISTS (SELECT 1 FROM inserted) UPDATE x SET y = 1; END";
        var output = Fmt(input);
        var expected =
            "CREATE TRIGGER tr_body\n" +
            "ON dbo.t\n" +
            "AFTER UPDATE\n" +
            "AS\n" +
            "BEGIN\n" +
            "    DECLARE @id INT;\n" +
            "\n" +
            "    IF EXISTS (\n" +
            "        SELECT 1\n" +
            "          FROM inserted\n" +
            "    )\n" +
            "        UPDATE x\n" +
            "           SET y = 1;\n" +
            "END;\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CreateTrigger_DdlOnDatabase()
    {
        // DDL trigger: TriggerObject.TriggerScope=Database renders as the literal
        // "DATABASE"; TriggerAction wraps an EventTypeContainer (single event).
        var input = "CREATE TRIGGER tr_ddl ON DATABASE FOR CREATE_TABLE AS BEGIN SELECT 1; END";
        var output = Fmt(input);
        var expected =
            "CREATE TRIGGER tr_ddl\n" +
            "ON DATABASE\n" +
            "FOR CREATE_TABLE\n" +
            "AS\n" +
            "BEGIN\n" +
            "    SELECT 1;\n" +
            "END;\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CreateTrigger_DdlOnAllServer_EventGroup()
    {
        // Server-scope DDL: TriggerScope=AllServer → "ALL SERVER"; TriggerAction wraps
        // an EventGroupContainer (event group, not single event).
        var input = "CREATE TRIGGER tr_srv ON ALL SERVER FOR DDL_DATABASE_LEVEL_EVENTS AS BEGIN SELECT 1; END";
        var output = Fmt(input);
        var expected =
            "CREATE TRIGGER tr_srv\n" +
            "ON ALL SERVER\n" +
            "FOR DDL_DATABASE_LEVEL_EVENTS\n" +
            "AS\n" +
            "BEGIN\n" +
            "    SELECT 1;\n" +
            "END;\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_AlterTrigger_AfterInsert()
    {
        var input = "ALTER TRIGGER tr_min ON dbo.t AFTER INSERT AS SELECT 2";
        var output = Fmt(input);
        var expected =
            "ALTER TRIGGER tr_min\n" +
            "ON dbo.t\n" +
            "AFTER INSERT\n" +
            "AS\n" +
            "SELECT 2;\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CreateOrAlterTrigger_AfterInsert()
    {
        var input = "CREATE OR ALTER TRIGGER tr_min ON dbo.t AFTER INSERT AS SELECT 3";
        var output = Fmt(input);
        var expected =
            "CREATE OR ALTER TRIGGER tr_min\n" +
            "ON dbo.t\n" +
            "AFTER INSERT\n" +
            "AS\n" +
            "SELECT 3;\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CreateTrigger_LogonAllServer()
    {
        // Logon trigger: TriggerActionType=LogOn renders as "LOGON". WITH EXECUTE AS 'sa'
        // is the typical security-bound option.
        var input = "CREATE TRIGGER tr_logon ON ALL SERVER WITH EXECUTE AS 'sa' FOR LOGON AS BEGIN ROLLBACK; END";
        var output = Fmt(input);
        var expected =
            "CREATE TRIGGER tr_logon\n" +
            "ON ALL SERVER\n" +
            "WITH EXECUTE AS 'sa'\n" +
            "FOR LOGON\n" +
            "AS\n" +
            "BEGIN\n" +
            "    ROLLBACK TRANSACTION;\n" +
            "END;\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }
}
