using System.Text;

namespace MbaLms.Api.Infrastructure;

/// <summary>
/// CLI helpers for <c>bootstrap-owner</c>. Manual use is interactive password entry only.
/// Automated tests call <see cref="OwnerBootstrap.RunAsync"/> with synthetic passwords, not <c>--password</c>.
/// </summary>
public static class OwnerBootstrapCommand
{
    public const string PasswordFlagHint = "Do not pass --password. Enter the password interactively.";

    public static bool HasDisallowedPasswordFlag(IEnumerable<string> args) =>
        args.Any(a =>
            string.Equals(a, "--password", StringComparison.OrdinalIgnoreCase)
            || a.StartsWith("--password=", StringComparison.OrdinalIgnoreCase));

    public static string? ParseEmail(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--email", StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    /// <summary>Reads the password twice without echo. Never logs the value.</summary>
    public static string? PromptPassword(ISecretPrompt prompt)
    {
        if (!prompt.CanPrompt) return null;
        var first = prompt.ReadSecret("Password: ");
        var second = prompt.ReadSecret("Confirm password: ");
        if (string.IsNullOrEmpty(first) || first != second) return null;
        return first;
    }
}

public interface ISecretPrompt
{
    bool CanPrompt { get; }
    string ReadSecret(string prompt);
}

/// <summary>Interactive console; keystrokes are not echoed.</summary>
public sealed class ConsoleSecretPrompt : ISecretPrompt
{
    public bool CanPrompt => !Console.IsInputRedirected && Environment.UserInteractive;

    public string ReadSecret(string prompt)
    {
        Console.Error.Write(prompt);
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0) sb.Length--;
                continue;
            }
            if (char.IsControl(key.KeyChar) || sb.Length >= 256) continue;
            sb.Append(key.KeyChar);
        }
        Console.Error.WriteLine();
        return sb.ToString();
    }
}
