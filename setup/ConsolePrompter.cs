namespace AgentStudio.Setup;

internal sealed class ConsolePrompter(bool nonInteractive)
{
    public string Ask(string label, string? defaultValue = null, bool required = true)
    {
        if (nonInteractive)
        {
            if (!string.IsNullOrWhiteSpace(defaultValue))
                return defaultValue;
            if (!required)
                return string.Empty;
            throw new InvalidOperationException(
                $"Non-interactive setup requires a value for: {label}");
        }

        while (true)
        {
            Console.Write(defaultValue is null ? $"{label}: " : $"{label} [{defaultValue}]: ");
            var value = Console.ReadLine()?.Trim() ?? string.Empty;
            if (value.Length > 0)
                return value;
            if (defaultValue is not null)
                return defaultValue;
            if (!required)
                return string.Empty;
            Console.WriteLine("A value is required.");
        }
    }

    public bool Confirm(string label, bool defaultValue = false)
    {
        if (nonInteractive)
            return defaultValue;
        var marker = defaultValue ? "Y/n" : "y/N";
        while (true)
        {
            Console.Write($"{label} [{marker}]: ");
            var value = Console.ReadLine()?.Trim().ToLowerInvariant() ?? string.Empty;
            if (value.Length == 0)
                return defaultValue;
            if (value is "y" or "yes")
                return true;
            if (value is "n" or "no")
                return false;
            Console.WriteLine("Enter y or n.");
        }
    }

    public string Secret(string label)
    {
        if (nonInteractive || Console.IsInputRedirected)
            throw new InvalidOperationException(
                $"{label} must be entered interactively and is not accepted in command-line arguments.");
        Console.Write($"{label}: ");
        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (chars.Count > 0)
                    chars.RemoveAt(chars.Count - 1);
                continue;
            }
            if (!char.IsControl(key.KeyChar))
                chars.Add(key.KeyChar);
        }
        Console.WriteLine();
        return new string(chars.ToArray());
    }
}
