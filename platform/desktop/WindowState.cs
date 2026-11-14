// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace DotGame.Framework.Desktop;

/// <summary>
/// The window's last normal (not maximized or minimized) position and size, whether it was
/// maximized or full screen, and the display scale they were measured at, saved in the game's local data
/// folder so the window reopens where the player left it.
/// </summary>
/// <remarks>
/// Positions and sizes are in the platform's window coordinates. The file is plain
/// <c>key=value</c> lines, read without reflection.
/// </remarks>
internal sealed class WindowState
{
    private const string FileName = "window.txt";

    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public bool IsMaximized { get; set; }

    public bool IsFullScreen { get; set; }

    /// <summary>
    /// The game's window settings when this was saved. If they've changed since, such as in an
    /// update to the game, this no longer applies. A file from before this was recorded has none.
    /// </summary>
    public string Settings { get; set; } = string.Empty;

    /// <summary>
    /// The content scale of the display the window was on, to rescale the size if the
    /// display's scaling changed since.
    /// </summary>
    public float DisplayScale { get; set; } = 1f;

    /// <summary>
    /// Gets the path of the saved window state: <c>window.txt</c> in the game's local data
    /// folder, named for its executable as the game's own settings folder is.
    /// </summary>
    public static string? GetPath()
    {
        string? processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
        {
            return null;
        }

        string folder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(folder))
        {
            return null;
        }

        return Path.Combine(folder, Path.GetFileNameWithoutExtension(processPath), FileName);
    }

    public static WindowState? Load()
    {
        try
        {
            string? path = GetPath();
            if ((path is null) || !File.Exists(path))
            {
                return null;
            }

            var state = new WindowState();
            int found = 0;
            foreach (string line in File.ReadAllLines(path))
            {
                int separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                string key = line.Substring(0, separator).Trim();
                string value = line.Substring(separator + 1).Trim();
                switch (key)
                {
                    case "x": state.X = ParseInt(value); found++; break;
                    case "y": state.Y = ParseInt(value); found++; break;
                    case "width": state.Width = ParseInt(value); found++; break;
                    case "height": state.Height = ParseInt(value); found++; break;
                    case "maximized": state.IsMaximized = (value == "true"); break;
                    case "fullscreen": state.IsFullScreen = (value == "true"); break;
                    case "settings": state.Settings = value; break;
                    case "scale": state.DisplayScale = float.Parse(value, CultureInfo.InvariantCulture); break;
                }
            }

            return ((found == 4) && (state.Width > 0) && (state.Height > 0) && (state.DisplayScale > 0f)) ? state : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or OverflowException)
        {
            // A missing or damaged file opens the window at its default size
            return null;
        }
    }

    public void Save()
    {
        try
        {
            string? path = GetPath();
            if (path is null)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var text = new StringBuilder();
            text.Append(CultureInfo.InvariantCulture, $"x={this.X}\n");
            text.Append(CultureInfo.InvariantCulture, $"y={this.Y}\n");
            text.Append(CultureInfo.InvariantCulture, $"width={this.Width}\n");
            text.Append(CultureInfo.InvariantCulture, $"height={this.Height}\n");
            text.Append(CultureInfo.InvariantCulture, $"maximized={(this.IsMaximized ? "true" : "false")}\n");
            text.Append(CultureInfo.InvariantCulture, $"fullscreen={(this.IsFullScreen ? "true" : "false")}\n");
            text.Append(CultureInfo.InvariantCulture, $"settings={this.Settings}\n");

            text.Append(CultureInfo.InvariantCulture, $"scale={this.DisplayScale}\n");
            File.WriteAllText(path, text.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not saving the window's size is harmless
        }
    }

    /// <summary>
    /// Deletes the saved window state, so the window next opens at its default size.
    /// </summary>
    public static void Delete()
    {
        try
        {
            string? path = GetPath();
            if ((path is not null) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static int ParseInt(
        string value)
    {
        return int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }
}
