namespace NarratorHotkey.Speech;

using System;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using Microsoft.Win32;
 
public static class VoiceInstallerHelper
{
    private const string SpeechVoicesTokensPath = @"SOFTWARE\Microsoft\Speech\Voices\Tokens";
    private const string SpeechOneCoreVoicesTokensPath = @"SOFTWARE\Microsoft\Speech_OneCore\Voices\Tokens";

    private static bool IsInstallingVoices { get; set; }

    private static bool IsRunAsAdministrator()
    {
        try
        {
            var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static bool CanAccessRegistry(string path)
    {
        try
        {
            using (var key = Registry.LocalMachine.OpenSubKey(path, true))
            {
                return key != null;
            }
        }
        catch
        {
            return false;
        }
    }

    public static void RunVoiceInstaller()
    {
        if (IsInstallingVoices)
        {
            MessageBox.Show("Voice installation is already in progress.", "Information",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Check for admin privileges
        if (!IsRunAsAdministrator())
        {
            MessageBox.Show("This feature requires administrator privileges. Please run the application as Administrator.",
                "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Verify registry access
        if (!CanAccessRegistry(SpeechVoicesTokensPath))
        {
            MessageBox.Show("Unable to access the required registry keys. Please ensure you are running as Administrator.",
                "Registry Access Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        try
        {
            IsInstallingVoices = true;

            using (var speechVoices = Registry.LocalMachine.OpenSubKey(SpeechVoicesTokensPath, true))
            using (var oneCoreVoices = Registry.LocalMachine.OpenSubKey(SpeechOneCoreVoicesTokensPath))
            {
                if (speechVoices == null || oneCoreVoices == null)
                {
                    var error =
                        $"Could not access required registry keys.\nSpeech Voices: {speechVoices != null}\nOneCore Voices: {oneCoreVoices != null}";
                    MessageBox.Show(error, "Registry Access Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var oneCoreTokens = oneCoreVoices.GetSubKeyNames();
                var installedCount = 0;

                foreach (var token in oneCoreTokens)
                {
                    try
                    {
                        using (var sourceKey = oneCoreVoices.OpenSubKey(token))
                        {
                            if (sourceKey == null) continue;

                            // Skip if voice already exists
                            if (speechVoices.OpenSubKey(token) != null)
                                continue;

                            // Create new key for the voice
                            using (var destKey = speechVoices.CreateSubKey(token))
                            {
                                // Copy main values
                                foreach (var valueName in sourceKey.GetValueNames())
                                {
                                    var value = sourceKey.GetValue(valueName);
                                    var valueKind = sourceKey.GetValueKind(valueName);
                                    destKey.SetValue(valueName, value, valueKind);
                                }

                                // Handle Attributes subkey
                                using (var sourceAttrib = sourceKey.OpenSubKey("Attributes"))
                                using (var destAttrib = destKey.CreateSubKey("Attributes"))
                                {
                                    if (sourceAttrib != null)
                                    {
                                        foreach (var valueName in sourceAttrib.GetValueNames())
                                        {
                                            var value = sourceAttrib.GetValue(valueName);
                                            var valueKind = sourceAttrib.GetValueKind(valueName);
                                            destAttrib.SetValue(valueName, value, valueKind);
                                        }
                                    }
                                }
                            }

                            installedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error copying voice token {token}: {ex.Message}");
                    }
                }

                string message = installedCount > 0
                    ? $"Successfully installed {installedCount} new voices. Please restart the application for changes to take effect."
                    : "No new voices were found to install.";

                MessageBox.Show(message, installedCount > 0 ? "Success" : "Information",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to install voices: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Debug.WriteLine($"Voice installation error stack trace: {ex.StackTrace}");
        }
        finally
        {
            IsInstallingVoices = false;
        }
    }
}