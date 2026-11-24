using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DbLens.Docs.Inventory;

/// <summary>Route and key text as the code writes it. Anything that needs a value to be known is not text.</summary>
internal static class RouteText
{
    /// <summary>
    /// A string literal, an interpolation with no holes, a constant of the same file, or a concatenation of
    /// these. Null, with the reason, for anything else.
    /// </summary>
    public static string? Of(ExpressionSyntax? e, IReadOnlyDictionary<string, string> constants, out string? reason)
    {
        reason = null;
        switch (e)
        {
            case LiteralExpressionSyntax { Token.Value: string text }:
                return text;

            case InterpolatedStringExpressionSyntax interpolated when interpolated.Contents.All(c => c is InterpolatedStringTextSyntax):
                return string.Concat(interpolated.Contents.Cast<InterpolatedStringTextSyntax>().Select(c => c.TextToken.ValueText));

            case IdentifierNameSyntax id when constants.TryGetValue(id.Identifier.Text, out var constant):
                return constant;

            case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression):
                var left = Of(binary.Left, constants, out var leftReason);
                var right = Of(binary.Right, constants, out var rightReason);
                reason = leftReason ?? rightReason;
                return left is null || right is null ? null : left + right;

            case null:
                reason = "there is no route argument";
                return null;

            default:
                reason = $"the text is a {e.Kind()}, not a literal or a constant of the same file";
                return null;
        }
    }

    /// <summary>"/a/b" from any two parts: one slash between segments, a leading one, no trailing one.</summary>
    public static string Join(string prefix, string route) =>
        "/" + string.Join('/', (prefix + "/" + route).Split('/', StringSplitOptions.RemoveEmptyEntries));
}
