// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.ContentPipeline.Testing;
using DotGame.Framework.ContentPipeline.Tasks;
using Microsoft.Build.Utilities;
using Xunit;

namespace DotGame.Framework.ContentPipeline.Tests.Tasks;

public sealed class ScriptCommentsTaskTests
{
    private static string Strip(string script)
    {
        Assert.True(_StripScriptComments.TryStrip(script, out string? result, out string? error), error);
        return result!;
    }

    private static int Lines(string text) => text.Split('\n').Length;

    [Fact]
    public void CommentsAndIndentationGo_TheLinesStay()
    {
        string script = "// Says what it does\nexport function f(a) {\n    /* one\n       two */\n    return a; // the value\n}\n";

        Assert.Equal("\nexport function f(a) {\n\n\nreturn a;\n}\n", Strip(script));
    }

    [Fact]
    public void ANoticeStays()
    {
        string script = "// Copyright (c) 2026 Someone. Licensed under the MIT License.\n/*! kept */\n/** @license MIT */\n// gone\nx();\n";

        Assert.Equal("// Copyright (c) 2026 Someone. Licensed under the MIT License.\n/*! kept */\n/** @license MIT */\n\nx();\n", Strip(script));
    }

    [Theory]
    [InlineData("const url = 'https://example.com/a'; // b", "const url = 'https://example.com/a';")]
    [InlineData("const s = \"/* not a comment */\";", "const s = \"/* not a comment */\";")]
    [InlineData("const s = 'it\\'s // fine';", "const s = 'it\\'s // fine';")]
    [InlineData("const r = /\\/\\/[/*]+/g.test(x); // b", "const r = /\\/\\/[/*]+/g.test(x);")]
    [InlineData("if (!/^\\s*class[\\s{]/.test(s)) {}", "if (!/^\\s*class[\\s{]/.test(s)) {}")]
    [InlineData("return /a*/.exec(s);", "return /a*/.exec(s);")]
    [InlineData("const half = width / 2 / scale; // c", "const half = width / 2 / scale;")]
    [InlineData("const n = (a + b) / 2 /* half */ / c;", "const n = (a + b) / 2 / c;")]
    [InlineData("const m = items[i] / 2;", "const m = items[i] / 2;")]
    [InlineData("i++ / 2;", "i++ / 2;")]
    [InlineData("a/**/b", "a b")]
    public void StringsRegularExpressionsAndDivisionsAreKeptApart(string script, string expected)
    {
        Assert.Equal(expected, Strip(script));
    }

    [Fact]
    public void ATemplateLiteralIsCopiedAsItIs_SubstitutionsIncluded()
    {
        string script = "const t = `line one\n    // not a comment\n    ${a /* gone */ + `inner ${ {x: 1}.x } // kept`}\n  end`;\n";

        Assert.Equal("const t = `line one\n    // not a comment\n    ${a + `inner ${ {x: 1}.x } // kept`}\n  end`;\n", Strip(script));
    }

    [Fact]
    public void ACommentAcrossLines_StillEndsAStatement()
    {
        // Automatic semicolon insertion: "a /* \n */ b" is two statements; "a  b" wouldn't be
        string stripped = Strip("let a = 1 /* x\n */ let b = 2\n");

        Assert.Equal("let a = 1\nlet b = 2\n", stripped);
    }

    [Theory]
    [InlineData("const s = 'open;")]
    [InlineData("const t = `open;")]
    [InlineData("/* open")]
    [InlineData("function f() {")]
    public void AScriptThatDoesntEnd_FailsInsteadOfGuessing(string script)
    {
        Assert.False(_StripScriptComments.TryStrip(script, out _, out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void ThePlatformsScripts_KeepTheirLines_AndLoseMostOfTheirSize()
    {
        string folder = Path.Combine(Paths.FrameworkRoot, "platform", "webgl", "wwwroot", "_dotgame");
        string[] scripts = Directory.GetFiles(folder, "*.js");
        Assert.NotEmpty(scripts);

        long before = 0;
        long after = 0;
        foreach (string script in scripts)
        {
            string text = File.ReadAllText(script);
            string stripped = Strip(text);
            Assert.True(Lines(text) == Lines(stripped), Path.GetFileName(script));
            before += text.Length;
            after += stripped.Length;
        }

        Assert.True(after < before * 3 / 4, $"{before} -> {after}");
    }

    [Fact]
    public void TheTask_WritesEachCopy_WithTheScriptsMetadata()
    {
        using var directory = new TestDirectory();
        directory.WriteFile("src/_dotgame/main.js", "// gone\nexport const a = 1;\n");
        var script = new TaskItem(directory.Path_("src", "_dotgame", "main.js"));
        script.SetMetadata("Kind", "module");

        var engine = new RecordingBuildEngine();
        var task = new _StripScriptComments
        {
            BuildEngine = engine,
            Scripts = [script],
            OutputDirectory = directory.Path_("out", "_dotgame"),
        };

        Assert.True(task.Execute(), string.Join(" ", engine.Errors));
        Assert.Single(task.StrippedScripts);
        Assert.Equal(directory.Path_("out", "_dotgame", "main.js"), task.StrippedScripts[0].ItemSpec);
        Assert.Equal("module", task.StrippedScripts[0].GetMetadata("Kind"));
        Assert.Equal("\nexport const a = 1;\n", File.ReadAllText(directory.Path_("out", "_dotgame", "main.js")));
    }

    [Fact]
    public void TheTask_ReportsAScriptItCantRead_AndFails()
    {
        using var directory = new TestDirectory();
        directory.WriteFile("broken.js", "const s = 'open;\n");

        var engine = new RecordingBuildEngine();
        var task = new _StripScriptComments
        {
            BuildEngine = engine,
            Scripts = [new TaskItem(directory.Path_("broken.js"))],
            OutputDirectory = directory.Path_("out"),
        };

        Assert.False(task.Execute());
        Assert.NotEmpty(engine.Errors);
    }
}
