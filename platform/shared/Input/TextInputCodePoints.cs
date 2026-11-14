// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.Framework.Input;

namespace DotGame.Framework.Platform.Common.Input;

/// <summary>
/// Shared validation rules describing which Unicode code points are
/// admissible for a given <see cref="TextInputType"/>. Used by platform
/// text-input strategies to filter platform editor input consistently.
/// </summary>
internal static class TextInputCodePoints
{
    internal static bool IsValid(
        TextInputType inputType,
        int codePoint,
        bool allowNewline)
    {
        if (allowNewline && (codePoint == 0x0A))
        {
            return true;
        }

        switch (inputType)
        {
            case TextInputType.Number:
                return (codePoint >= '0') && (codePoint <= '9');

            case TextInputType.Decimal:
                return ((codePoint >= '0') && (codePoint <= '9'))
                    || (codePoint == '.')
                    || (codePoint == '-');

            case TextInputType.Phone:
                return ((codePoint >= '0') && (codePoint <= '9'))
                    || (codePoint == '+')
                    || (codePoint == '-')
                    || (codePoint == ' ')
                    || (codePoint == '(')
                    || (codePoint == ')');

            default:
                // Printable Unicode: exclude C0 controls and DEL.
                return (codePoint >= 0x20) && (codePoint != 0x7f);
        }
    }
}
