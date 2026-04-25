using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlVersionControl.Services.Formatting;

namespace SqlVersionControl.Tests;

public class TableFormattingTests
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
    public void Format_CreateTable_Minimal()
    {
        var input = "CREATE TABLE dbo.t (id INT)";
        var output = Fmt(input);
        var expected =
            "CREATE TABLE dbo.t\n" +
            "(\n" +
            "    id INT\n" +
            ")\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CreateTable_NullableColumns()
    {
        var input = "CREATE TABLE dbo.t (id INT NOT NULL, name NVARCHAR(100) NULL)";
        var output = Fmt(input);
        var expected =
            "CREATE TABLE dbo.t\n" +
            "(\n" +
            "    id INT NOT NULL,\n" +
            "    name NVARCHAR (100) NULL\n" +
            ")\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CreateTable_IdentityDefault()
    {
        var input = "CREATE TABLE dbo.t (id INT IDENTITY(1,1) NOT NULL, created DATETIME NOT NULL DEFAULT GETDATE())";
        var output = Fmt(input);
        var expected =
            "CREATE TABLE dbo.t\n" +
            "(\n" +
            "    id INT IDENTITY (1, 1) NOT NULL,\n" +
            "    created DATETIME DEFAULT GETDATE() NOT NULL\n" +
            ")\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CreateTable_InlineColumnPrimaryKey()
    {
        var input = "CREATE TABLE dbo.t (id INT PRIMARY KEY)";
        var output = Fmt(input);
        var expected =
            "CREATE TABLE dbo.t\n" +
            "(\n" +
            "    id INT PRIMARY KEY\n" +
            ")\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CreateTable_PkClusteredFullOptions_WrapsWith()
    {
        // D1 option C: WITH-options total exceeds MaxLineLength, so the constraint wraps —
        // each option on its own line at +2*IndentSize, ON [PRIMARY] trails at +IndentSize.
        var input =
            "CREATE TABLE dbo.t (id INT NOT NULL," +
            " CONSTRAINT PK_t PRIMARY KEY CLUSTERED (id ASC)" +
            " WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, FILLFACTOR = 80)" +
            " ON [PRIMARY])";
        var output = Fmt(input);
        var expected =
            "CREATE TABLE dbo.t\n" +
            "(\n" +
            "    id INT NOT NULL,\n" +
            "    CONSTRAINT PK_t PRIMARY KEY CLUSTERED (id ASC)\n" +
            "        WITH (\n" +
            "            PAD_INDEX = OFF,\n" +
            "            STATISTICS_NORECOMPUTE = OFF,\n" +
            "            IGNORE_DUP_KEY = OFF,\n" +
            "            ALLOW_ROW_LOCKS = ON,\n" +
            "            ALLOW_PAGE_LOCKS = ON,\n" +
            "            FILLFACTOR = 80\n" +
            "        )\n" +
            "        ON [PRIMARY]\n" +
            ")\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CreateTable_UniqueShortOptions_StaysInline()
    {
        // D1 option C: WITH (...) ON [PRIMARY] fits MaxLineLength, so stays inline.
        var input = "CREATE TABLE dbo.t (id INT, code NVARCHAR(10), CONSTRAINT UQ_t UNIQUE NONCLUSTERED (code) WITH (ALLOW_PAGE_LOCKS = ON) ON [PRIMARY])";
        var output = Fmt(input);
        var expected =
            "CREATE TABLE dbo.t\n" +
            "(\n" +
            "    id INT,\n" +
            "    code NVARCHAR (10),\n" +
            "    CONSTRAINT UQ_t UNIQUE NONCLUSTERED (code) WITH (ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]\n" +
            ")\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CreateTable_ForeignKey()
    {
        var input = "CREATE TABLE dbo.child (id INT, parent_id INT, CONSTRAINT FK FOREIGN KEY (parent_id) REFERENCES dbo.parent(id) ON DELETE CASCADE ON UPDATE NO ACTION)";
        var output = Fmt(input);
        var expected =
            "CREATE TABLE dbo.child\n" +
            "(\n" +
            "    id INT,\n" +
            "    parent_id INT,\n" +
            "    CONSTRAINT FK FOREIGN KEY (parent_id) REFERENCES dbo.parent (id) ON DELETE CASCADE ON UPDATE NO ACTION\n" +
            ")\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CreateTable_CheckConstraints()
    {
        var input = "CREATE TABLE dbo.t (id INT, qty INT CHECK (qty > 0), CONSTRAINT CK CHECK (id > 0))";
        var output = Fmt(input);
        var expected =
            "CREATE TABLE dbo.t\n" +
            "(\n" +
            "    id INT,\n" +
            "    qty INT CHECK (qty > 0),\n" +
            "    CONSTRAINT CK CHECK (id > 0)\n" +
            ")\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CreateTable_ComputedColumn()
    {
        var input = "CREATE TABLE dbo.t (a INT, b INT, c AS (a + b) PERSISTED NOT NULL)";
        var output = Fmt(input);
        var expected =
            "CREATE TABLE dbo.t\n" +
            "(\n" +
            "    a INT,\n" +
            "    b INT,\n" +
            "    c AS (a + b) PERSISTED NOT NULL\n" +
            ")\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CreateTable_OnFilegroupAndTextimage()
    {
        var input = "CREATE TABLE dbo.t (id INT NOT NULL, payload NVARCHAR(MAX)) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]";
        var output = Fmt(input);
        var expected =
            "CREATE TABLE dbo.t\n" +
            "(\n" +
            "    id INT NOT NULL,\n" +
            "    payload NVARCHAR (MAX)\n" +
            ")\n" +
            "ON [PRIMARY]\n" +
            "TEXTIMAGE_ON [PRIMARY]\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CreateTable_LongColumnList()
    {
        var input = "CREATE TABLE dbo.wide (a INT, b INT, c INT, d INT, e INT, f INT, g INT, h INT, i INT, j INT)";
        var output = Fmt(input);
        var expected =
            "CREATE TABLE dbo.wide\n" +
            "(\n" +
            "    a INT,\n" +
            "    b INT,\n" +
            "    c INT,\n" +
            "    d INT,\n" +
            "    e INT,\n" +
            "    f INT,\n" +
            "    g INT,\n" +
            "    h INT,\n" +
            "    i INT,\n" +
            "    j INT\n" +
            ")\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CreateTable_RealCorpusFull()
    {
        // Sorgu/create_table.sql body, verbatim. Both PK and UQ have the full 6-option WITH
        // block — both wrap (D1 option C). Outer ON / TEXTIMAGE_ON each on their own line.
        var input =
            "CREATE TABLE [dbo].[t_sap_report](\n" +
            "\t[load_id] [bigint] IDENTITY(1,1) NOT NULL,\n" +
            "\t[order_number] [nvarchar](256) NOT NULL,\n" +
            "\t[case_count] [int] NULL,\n" +
            "\t[tran_qty] [int] NULL,\n" +
            "\t[record_create_date] [datetime] NULL,\n" +
            " CONSTRAINT [PK_t_sap_report] PRIMARY KEY CLUSTERED \n" +
            "(\n" +
            "\t[load_id] ASC\n" +
            ")WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, FILLFACTOR = 80) ON [PRIMARY],\n" +
            " CONSTRAINT [UQ1_t_sap_report] UNIQUE NONCLUSTERED \n" +
            "(\n" +
            "\t[order_number] ASC\n" +
            ")WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, FILLFACTOR = 80) ON [PRIMARY]\n" +
            ") ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]";
        var output = Fmt(input);
        var expected =
            "CREATE TABLE [dbo].[t_sap_report]\n" +
            "(\n" +
            "    [load_id] BIGINT IDENTITY (1, 1) NOT NULL,\n" +
            "    [order_number] NVARCHAR (256) NOT NULL,\n" +
            "    [case_count] INT NULL,\n" +
            "    [tran_qty] INT NULL,\n" +
            "    [record_create_date] DATETIME NULL,\n" +
            "    CONSTRAINT [PK_t_sap_report] PRIMARY KEY CLUSTERED ([load_id] ASC)\n" +
            "        WITH (\n" +
            "            PAD_INDEX = OFF,\n" +
            "            STATISTICS_NORECOMPUTE = OFF,\n" +
            "            IGNORE_DUP_KEY = OFF,\n" +
            "            ALLOW_ROW_LOCKS = ON,\n" +
            "            ALLOW_PAGE_LOCKS = ON,\n" +
            "            FILLFACTOR = 80\n" +
            "        )\n" +
            "        ON [PRIMARY],\n" +
            "    CONSTRAINT [UQ1_t_sap_report] UNIQUE NONCLUSTERED ([order_number] ASC)\n" +
            "        WITH (\n" +
            "            PAD_INDEX = OFF,\n" +
            "            STATISTICS_NORECOMPUTE = OFF,\n" +
            "            IGNORE_DUP_KEY = OFF,\n" +
            "            ALLOW_ROW_LOCKS = ON,\n" +
            "            ALLOW_PAGE_LOCKS = ON,\n" +
            "            FILLFACTOR = 80\n" +
            "        )\n" +
            "        ON [PRIMARY]\n" +
            ")\n" +
            "ON [PRIMARY]\n" +
            "TEXTIMAGE_ON [PRIMARY]\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CreateTable_MemoryOptimized()
    {
        var input = "CREATE TABLE dbo.t (id INT PRIMARY KEY NONCLUSTERED) WITH (MEMORY_OPTIMIZED = ON)";
        var output = Fmt(input);
        var expected =
            "CREATE TABLE dbo.t\n" +
            "(\n" +
            "    id INT PRIMARY KEY NONCLUSTERED\n" +
            ")\n" +
            "WITH (MEMORY_OPTIMIZED = ON)\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }

    [Fact]
    public void Format_CreateTable_TemporalSystemVersioning()
    {
        var input = "CREATE TABLE dbo.t (id INT PRIMARY KEY, valid_from DATETIME2 GENERATED ALWAYS AS ROW START NOT NULL, valid_to DATETIME2 GENERATED ALWAYS AS ROW END NOT NULL, PERIOD FOR SYSTEM_TIME (valid_from, valid_to)) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.t_h))";
        var output = Fmt(input);
        var expected =
            "CREATE TABLE dbo.t\n" +
            "(\n" +
            "    id INT PRIMARY KEY,\n" +
            "    valid_from DATETIME2 GENERATED ALWAYS AS ROW START NOT NULL,\n" +
            "    valid_to DATETIME2 GENERATED ALWAYS AS ROW END NOT NULL,\n" +
            "    PERIOD FOR SYSTEM_TIME (valid_from, valid_to)\n" +
            ")\n" +
            "WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE=dbo.t_h))\n";
        Assert.Equal(expected, output);
        Assert.NotNull(ReParse(output));
    }
}
