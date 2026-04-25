using System;
using SqlVersionControl.Services.Formatting;
using SqlVersionControl.Services.Formatting.Visitor;

namespace SqlVersionControl.Tests;

public class SqlEmitterTests
{
    [Fact]
    public void ClauseScope_RightAlignsKeywordsToWidestInScope()
    {
        var emitter = new SqlEmitter(new FormatterOptions());
        using (emitter.BeginClauseScope())
        {
            emitter.WriteClauseKeyword("SELECT"); emitter.Write("*"); emitter.NewLine();
            emitter.WriteClauseKeyword("FROM"); emitter.Write("t_employee"); emitter.NewLine();
            emitter.WriteClauseKeyword("WHERE"); emitter.Write("1 = 1"); emitter.NewLine();
            emitter.WriteClauseKeyword("AND"); emitter.Write("2 = 2"); emitter.NewLine();
        }

        var expected =
            "SELECT *\n" +
            "  FROM t_employee\n" +
            " WHERE 1 = 1\n" +
            "   AND 2 = 2\n";
        Assert.Equal(expected, emitter.ToString());
    }

    [Fact]
    public void ClauseScope_ContinuationIndentsToBodyColumn()
    {
        var emitter = new SqlEmitter(new FormatterOptions());
        using (emitter.BeginClauseScope())
        {
            emitter.WriteClauseKeyword("SELECT"); emitter.Write("a,"); emitter.NewLine();
            emitter.Write("b,"); emitter.NewLine();
            emitter.Write("c"); emitter.NewLine();
            emitter.WriteClauseKeyword("FROM"); emitter.Write("t"); emitter.NewLine();
        }

        // maxKw = 6 (SELECT). Continuations indent to col 7 (maxKw + 1).
        var expected =
            "SELECT a,\n" +
            "       b,\n" +
            "       c\n" +
            "  FROM t\n";
        Assert.Equal(expected, emitter.ToString());
    }

    [Fact]
    public void ClauseScope_AlignDisabled_LeavesKeywordsLeftAligned()
    {
        var options = new FormatterOptions { AlignClauseBodies = false };
        var emitter = new SqlEmitter(options);
        using (emitter.BeginClauseScope())
        {
            emitter.WriteClauseKeyword("SELECT"); emitter.Write("*"); emitter.NewLine();
            emitter.WriteClauseKeyword("FROM"); emitter.Write("t"); emitter.NewLine();
            emitter.WriteClauseKeyword("WHERE"); emitter.Write("1 = 1"); emitter.NewLine();
        }

        var expected =
            "SELECT *\n" +
            "FROM t\n" +
            "WHERE 1 = 1\n";
        Assert.Equal(expected, emitter.ToString());
    }

    [Fact]
    public void ClauseScope_RespectsOuterIndentLevel()
    {
        var emitter = new SqlEmitter(new FormatterOptions());
        using (emitter.Indent())
        using (emitter.BeginClauseScope())
        {
            emitter.WriteClauseKeyword("SELECT"); emitter.Write("*"); emitter.NewLine();
            emitter.WriteClauseKeyword("FROM"); emitter.Write("t"); emitter.NewLine();
        }

        // IndentSize=4, one level → 4-space prefix on every line.
        var expected =
            "    SELECT *\n" +
            "      FROM t\n";
        Assert.Equal(expected, emitter.ToString());
    }

    [Fact]
    public void ClauseScope_NestedInnerInjectsIntoParentBody()
    {
        // 4b-ii nested scope: inner scope's rendered lines flush into the parent's current
        // body. First inner line continues the parent line; subsequent lines become body-only
        // continuations in the parent (indented to parent's body column).
        var emitter = new SqlEmitter(new FormatterOptions());
        using (emitter.BeginClauseScope())
        {
            emitter.WriteClauseKeyword("WHERE"); emitter.Write("id IN ("); emitter.NewLine();
            using (emitter.Indent())
            using (emitter.BeginClauseScope())
            {
                emitter.WriteClauseKeyword("SELECT"); emitter.Write("*"); emitter.NewLine();
                emitter.WriteClauseKeyword("FROM"); emitter.Write("t"); emitter.NewLine();
            }
            emitter.NewLine();
            emitter.Write(")");
            emitter.NewLine();
        }

        // Outer maxKw = 5 (WHERE) so body column = 6. Inner capturedIndentLevel = 1, IndentSize = 4
        // → inner lines carry 4 leading spaces. Inner maxKw = 6 (SELECT): SELECT pad 0, FROM pad 2.
        // Parent flush prepends 6 spaces (body column) to each body-only continuation.
        var expected =
            "WHERE id IN (\n" +
            "          SELECT *\n" +     // 6 (parent body col) + 4 (inner indent) = 10 spaces, then SELECT
            "            FROM t\n" +     // 6 + 4 + 2 (FROM pad) = 12 spaces, then FROM
            "      )\n";                  // 6 spaces, then )
        Assert.Equal(expected, emitter.ToString());
    }

    [Fact]
    public void ClauseScope_NestedHasOwnLocalMaxKw()
    {
        // The inner scope's keyword padding is computed from the *inner* scope's keywords only,
        // independent of the outer scope's maxKw.
        var emitter = new SqlEmitter(new FormatterOptions());
        using (emitter.BeginClauseScope())
        {
            emitter.WriteClauseKeyword("SELECT"); emitter.Write("("); emitter.NewLine();
            using (emitter.Indent())
            using (emitter.BeginClauseScope())
            {
                // Inner has only one keyword — maxKw_inner should be just len("SELECT") = 6,
                // not influenced by anything outer.
                emitter.WriteClauseKeyword("SELECT"); emitter.Write("MAX(x)"); emitter.NewLine();
                emitter.WriteClauseKeyword("FROM"); emitter.Write("u"); emitter.NewLine();
            }
            emitter.NewLine();
            emitter.Write(") FROM t");
            emitter.NewLine();
        }

        // Outer maxKw = 6 (SELECT) → body col 7. Inner: SELECT pad 0, FROM pad 2. Inner indent +4.
        var expected =
            "SELECT (\n" +
            "           SELECT MAX(x)\n" +   // 7 (outer body col) + 4 (inner indent) + 0 (pad) = 11
            "             FROM u\n" +        // 7 + 4 + 2 = 13
            "       ) FROM t\n";              // 7 spaces, then ") FROM t"
        Assert.Equal(expected, emitter.ToString());
    }

    [Fact]
    public void ClauseScope_DoublyNestedRendersEachLevelIndependently()
    {
        // Three levels deep: outer, middle, inner. Each maintains its own maxKw and indent.
        var emitter = new SqlEmitter(new FormatterOptions());
        using (emitter.BeginClauseScope())
        {
            emitter.WriteClauseKeyword("WHERE"); emitter.Write("a IN ("); emitter.NewLine();
            using (emitter.Indent())
            using (emitter.BeginClauseScope())
            {
                emitter.WriteClauseKeyword("SELECT"); emitter.Write("b"); emitter.NewLine();
                emitter.WriteClauseKeyword("FROM"); emitter.Write("u"); emitter.NewLine();
                emitter.WriteClauseKeyword("WHERE"); emitter.Write("c IN ("); emitter.NewLine();
                using (emitter.Indent())
                using (emitter.BeginClauseScope())
                {
                    emitter.WriteClauseKeyword("SELECT"); emitter.Write("d"); emitter.NewLine();
                    emitter.WriteClauseKeyword("FROM"); emitter.Write("v"); emitter.NewLine();
                }
                emitter.NewLine();
                emitter.Write(")");
                emitter.NewLine();
            }
            emitter.NewLine();
            emitter.Write(")");
            emitter.NewLine();
        }

        // 4c-step0 strip-parent-outer fix: inner's captured outerIndent is stripped before
        // injecting into the parent, so each nesting level adds IndentSize past the parent's
        // body col (not the cumulative captured*IndentSize). Trace with fix:
        //   Inner buffer renders "        SELECT d\n          FROM v\n" (8/10 leading).
        //   On pop into middle, strip middle.captured * IndentSize = 4. Inner lines become
        //     "    SELECT d" / "      FROM v" (4/6 leading).
        //   Middle buffer (captured=1, maxKw=6, body col=7, outer=4) renders continuation lines
        //     at col 4+7 = 11 + inner content leading (4/6) → col 15/17 for inner SELECT/FROM.
        //   On pop into outer, strip outer.captured * IndentSize = 0 (no change).
        //   Outer (captured=0, maxKw=5, body col=6) renders continuations at col 0+6 = 6 +
        //   middle content → final col 6+15 = 21 for SELECT d, 6+17 = 23 for FROM v.
        var expected =
            "WHERE a IN (\n" +
            "          SELECT b\n" +              // 6 + 4 = 10
            "            FROM u\n" +              // 6 + 6 = 12
            "           WHERE c IN (\n" +         // 6 + 5 = 11
            "                     SELECT d\n" +   // 6 + 15 = 21
            "                       FROM v\n" +   // 6 + 17 = 23
            "                 )\n" +              // 6 + 11 = 17
            "      )\n";                          // 6 + 0 = 6
        Assert.Equal(expected, emitter.ToString());
    }

    [Fact]
    public void WriteClauseKeyword_OutsideScope_FallsBackToWriteKeyword()
    {
        var emitter = new SqlEmitter(new FormatterOptions());
        emitter.WriteClauseKeyword("select");
        Assert.Equal("SELECT", emitter.ToString());
    }
}
