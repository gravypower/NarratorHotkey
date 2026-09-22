using System;
using System.IO;
using System.Runtime.InteropServices;

namespace NarratorHotkey;

/// <summary>
/// Host lookups that differ between Windows and everything else.
/// </summary>
public static class Platform
{
    /// <summary>
    /// Whether a command can be run, resolved the way a shell resolves it.
    /// </summary>
    /// <remarks>
    /// PATH is walked here rather than shelling out to <c>which</c>, which is not
    /// installed on minimal Debian and Ubuntu images and which costs a process per
    /// candidate. The audio player is looked up once per clip, so that added up.
    /// </remarks>
    public static bool CommandExists(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;

        // A command containing a separator is a path, not a name to search for.
        if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
        {
            return IsExecutable(command);
        }

        string path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return false;

        foreach (string directory in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;

            try
            {
                if (IsExecutable(Path.Combine(directory, command)))
                {
                    return true;
                }
            }
            catch
            {
                // An unreadable or malformed PATH entry is simply not a match.
            }
        }

        return false;
    }

    private static bool IsExecutable(string file)
    {
        try
        {
            if (!File.Exists(file)) return false;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Windows has no execute bit; being on PATH is as much as we can check.
                return true;
            }

            const UnixFileMode executable =
                UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

            return (File.GetUnixFileMode(file) & executable) != 0;
        }
        catch
        {
            return false;
        }
    }
}
