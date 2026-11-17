// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace DotGame.Framework.ContentPipeline.Tasks;

// _StripScriptComments - the web platform's scripts as a Release build serves them: without their
// comments and indentation, which are most of their size (about 60% of it after Brotli). Every line
// stays where it is, so a crash report's line numbers and function names still point at the source.
// A comment that says Copyright, @license or @preserve, or starts with '!', is kept.
//
// Strings, template literals and regular expressions are copied as they are: a lexer tells them
// from comments and division, the way a JavaScript engine does, so it needs no parser.
public sealed class _StripScriptComments : Task
{
    [Required]
    public ITaskItem[] Scripts { get; set; }

    [Required]
    public string OutputDirectory { get; set; }

    // Each script's copy, with the script's metadata
    [Output]
    public ITaskItem[] StrippedScripts { get; set; }

    public override bool Execute()
    {
        Directory.CreateDirectory(this.OutputDirectory);
        var stripped = new List<ITaskItem>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ITaskItem script in this.Scripts)
        {
            string source = script.GetMetadata("FullPath");
            string name = Path.GetFileName(source);
            if (!names.Add(name))
            {
                this.Log.LogError("_StripScriptComments: two scripts are named '{0}'; their copies would share a file.", name);
                return false;
            }

            string text = File.ReadAllText(source);
            string result;
            string error;
            if (!TryStrip(text, out result, out error))
            {
                this.Log.LogError(null, null, null, source, 0, 0, 0, 0, "_StripScriptComments: {0}", error);
                return false;
            }

            // Written only when it changes, so the static web assets that follow stay up to date
            string destination = Path.Combine(this.OutputDirectory, name);
            if (!File.Exists(destination) || (File.ReadAllText(destination) != result))
            {
                File.WriteAllText(destination, result, new UTF8Encoding(false));
            }

            var item = new TaskItem(Path.GetFullPath(destination));
            script.CopyMetadataTo(item);
            item.SetMetadata("OriginalScript", source);
            stripped.Add(item);
        }

        this.StrippedScripts = stripped.ToArray();
        return true;
    }

    // Words after which a '/' starts a regular expression, not a division
    private static readonly HashSet<string> RegexAfterWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "return", "typeof", "instanceof", "in", "of", "new", "delete", "void", "throw",
        "case", "do", "else", "yield", "await",
    };

    private enum Previous
    {
        // Nothing yet, or a punctuator: '/' starts a regular expression
        Operator,

        // A value (a name, number, string, template, ')' or ']'): '/' divides
        Value,
    }

    public static bool TryStrip(string text, out string result, out string error)
    {
        var output = new StringBuilder(text.Length);

        // Where each open '{' belongs: true for a template literal's '${'
        var braces = new Stack<bool>();
        var previous = Previous.Operator;
        bool lineStart = true;
        int i = 0;
        int length = text.Length;
        error = null;
        result = null;

        while (i < length)
        {
            char c = text[i];

            // Whitespace between tokens: line breaks stay, the rest goes at a line's start and end
            if ((c == ' ') || (c == '\t') || (c == '\f') || (c == '\v') || (c == ' ') || (c == '﻿'))
            {
                int end = i;
                while ((end < length) && IsSpace(text[end]))
                {
                    end++;
                }

                bool lineEnd = (end == length) || (text[end] == '\n') || (text[end] == '\r')
                    || ((text[end] == '/') && (end + 1 < length) && (text[end + 1] == '/') && !IsKept(text, end + 2, LineCommentEnd(text, end)));
                if (!lineStart && !lineEnd && (output[output.Length - 1] != ' '))
                {
                    output.Append(' ');
                }

                i = end;
                continue;
            }

            if ((c == '\n') || (c == '\r'))
            {
                output.Append(c);
                lineStart = true;
                i++;
                continue;
            }

            if ((c == '/') && (i + 1 < length) && (text[i + 1] == '/'))
            {
                int end = LineCommentEnd(text, i);
                if (IsKept(text, i + 2, end))
                {
                    output.Append(text, i, end - i);
                    lineStart = false;
                }

                i = end;
                continue;
            }

            if ((c == '/') && (i + 1 < length) && (text[i + 1] == '*'))
            {
                int close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    error = $"a comment at offset {i} doesn't end";
                    return false;
                }

                int end = close + 2;
                if (IsKept(text, i + 2, close))
                {
                    output.Append(text, i, end - i);
                    lineStart = false;
                }
                else
                {
                    // Its line breaks stay, so the lines after it keep their numbers; one that spans
                    // lines also ends a statement where automatic semicolon insertion needs it to
                    bool lines = false;
                    for (int k = i; k < end; k++)
                    {
                        if ((text[k] == '\n') || (text[k] == '\r'))
                        {
                            while (!lines && (output.Length > 0) && (output[output.Length - 1] == ' '))
                            {
                                output.Length--;
                            }

                            output.Append(text[k]);
                            lines = true;
                        }
                    }

                    if (lines)
                    {
                        lineStart = true;
                    }
                    else if (!lineStart && (end < length) && !IsSpace(text[end]) && (text[end] != '\n') && (text[end] != '\r'))
                    {
                        // a/**/b stays two tokens
                        output.Append(' ');
                    }
                }

                i = end;
                continue;
            }

            lineStart = false;

            if ((c == '"') || (c == '\''))
            {
                int end = i + 1;
                while ((end < length) && (text[end] != c))
                {
                    if ((text[end] == '\n') || (text[end] == '\r'))
                    {
                        error = $"a string at offset {i} doesn't end on its line";
                        return false;
                    }

                    end += (text[end] == '\\') ? 2 : 1;
                }

                if (end >= length)
                {
                    error = $"a string at offset {i} doesn't end";
                    return false;
                }

                end++;
                output.Append(text, i, end - i);
                previous = Previous.Value;
                i = end;
                continue;
            }

            if (c == '`')
            {
                output.Append(c);
                i++;
                if (!CopyTemplate(text, ref i, output, braces, out error))
                {
                    return false;
                }

                previous = Previous.Value;
                continue;
            }

            if (c == '}' && (braces.Count > 0) && braces.Peek())
            {
                // The end of a template literal's substitution: the literal goes on
                braces.Pop();
                output.Append(c);
                i++;
                if (!CopyTemplate(text, ref i, output, braces, out error))
                {
                    return false;
                }

                previous = Previous.Value;
                continue;
            }

            if ((c == '/') && (previous == Previous.Operator))
            {
                int end = i + 1;
                bool inClass = false;
                while (true)
                {
                    if ((end >= length) || (text[end] == '\n') || (text[end] == '\r'))
                    {
                        error = $"a regular expression at offset {i} doesn't end on its line";
                        return false;
                    }

                    char r = text[end];
                    if (r == '\\')
                    {
                        end += 2;
                        continue;
                    }

                    if (r == '[')
                    {
                        inClass = true;
                    }
                    else if (r == ']')
                    {
                        inClass = false;
                    }
                    else if ((r == '/') && !inClass)
                    {
                        break;
                    }

                    end++;
                }

                end++;
                while ((end < length) && IsWordChar(text[end]))
                {
                    end++;
                }

                output.Append(text, i, end - i);
                previous = Previous.Value;
                i = end;
                continue;
            }

            if (IsWordChar(c) || (c == '#') || (c == '\\'))
            {
                int end = i + 1;
                while ((end < length) && (IsWordChar(text[end]) || (text[end] == '\\')))
                {
                    end++;
                }

                // A number's exponent sign or fraction is part of it: 1e-5, 1.5
                string word = text.Substring(i, end - i);
                output.Append(word);
                previous = RegexAfterWords.Contains(word) ? Previous.Operator : Previous.Value;
                i = end;
                continue;
            }

            // A value's postfix ++ or -- leaves a value: a++ / 2 divides
            if (((c == '+') || (c == '-')) && (i + 1 < length) && (text[i + 1] == c) && (previous == Previous.Value))
            {
                output.Append(c).Append(c);
                i += 2;
                continue;
            }

            if (c == '{')
            {
                braces.Push(false);
            }
            else if (c == '}')
            {
                if (braces.Count > 0)
                {
                    braces.Pop();
                }
            }

            output.Append(c);
            previous = ((c == ')') || (c == ']') || (c == '}')) ? Previous.Value : Previous.Operator;

            // '}' ends a block more often than an object: a statement starting with a regular
            // expression after it is legal, a division after an object literal is not (it would
            // need parentheses), so after '}' a '/' starts a regular expression
            if (c == '}')
            {
                previous = Previous.Operator;
            }

            i++;
        }

        if (braces.Count > 0)
        {
            error = "a '{' or a template literal's '${' isn't closed";
            return false;
        }

        result = output.ToString();
        return true;
    }

    // Copies a template literal from just after its '`' or a substitution's '}', through its
    // closing '`' or its next '${'
    private static bool CopyTemplate(string text, ref int i, StringBuilder output, Stack<bool> braces, out string error)
    {
        int start = i;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\\')
            {
                i += 2;
                continue;
            }

            if (c == '`')
            {
                i++;
                output.Append(text, start, i - start);
                error = null;
                return true;
            }

            if ((c == '$') && (i + 1 < text.Length) && (text[i + 1] == '{'))
            {
                i += 2;
                output.Append(text, start, i - start);
                braces.Push(true);
                error = null;
                return true;
            }

            i++;
        }

        error = $"a template literal at offset {start} doesn't end";
        return false;
    }

    private static int LineCommentEnd(string text, int start)
    {
        int end = start;
        while ((end < text.Length) && (text[end] != '\n') && (text[end] != '\r'))
        {
            end++;
        }

        return end;
    }

    // A comment to keep: a copyright or license notice, or one marked to be preserved
    private static bool IsKept(string text, int start, int end)
    {
        if ((start < end) && (text[start] == '!'))
        {
            return true;
        }

        string body = text.Substring(start, end - start);
        return (body.IndexOf("Copyright", StringComparison.Ordinal) >= 0)
            || (body.IndexOf("@license", StringComparison.Ordinal) >= 0)
            || (body.IndexOf("@preserve", StringComparison.Ordinal) >= 0);
    }

    private static bool IsSpace(char c)
    {
        return (c == ' ') || (c == '\t') || (c == '\f') || (c == '\v') || (c == ' ') || (c == '﻿');
    }

    private static bool IsWordChar(char c)
    {
        return ((c >= 'a') && (c <= 'z')) || ((c >= 'A') && (c <= 'Z')) || ((c >= '0') && (c <= '9'))
            || (c == '_') || (c == '$') || ((c > 0x7f) && char.IsLetterOrDigit(c));
    }
}
