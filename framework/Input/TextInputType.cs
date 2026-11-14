// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Framework.Input;

/// <summary>
/// Hint to the platform's text-input pipeline (soft keyboard layout,
/// IME mode, autocapitalization defaults). Mirrors common conventions
/// across SDL3, HTML <c>&lt;input&gt;</c>, UIKit <c>UIKeyboardType</c>,
/// and Android <c>EditorInfo.inputType</c>.
/// </summary>
public enum TextInputType
{
    /// <summary>Plain text. Default soft keyboard.</summary>
    Text = 0,

    /// <summary>Obscured text entry (passwords, PINs).</summary>
    Password,

    /// <summary>Integer numeric entry. Digits only.</summary>
    Number,

    /// <summary>Decimal numeric entry. Digits, decimal separator, sign.</summary>
#pragma warning disable CA1720 // Identifier contains type name
    Decimal,
#pragma warning restore CA1720 // Identifier contains type name

    /// <summary>Telephone number entry.</summary>
    Phone,

    /// <summary>Email address entry.</summary>
    Email,

    /// <summary>URL entry.</summary>
    Url,

    /// <summary>Search query entry.</summary>
    Search,
}
