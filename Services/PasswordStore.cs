namespace SqlVersionControl.Services;

/// <summary>
/// In-memory password store. Passwords are never saved to disk.
/// </summary>
public static class PasswordStore
{
    private static readonly Dictionary<string, string> _passwords = new();

    private static string GetKey(string server, string database, string username)
        => $"{server}|{database}|{username}";

    public static void Store(string server, string database, string username, string password)
    {
        var key = GetKey(server, database, username);
        _passwords[key] = password;
    }

    public static string? Get(string server, string database, string username)
    {
        var key = GetKey(server, database, username);
        return _passwords.TryGetValue(key, out var password) ? password : null;
    }

    public static bool Has(string server, string database, string username)
    {
        var key = GetKey(server, database, username);
        return _passwords.ContainsKey(key);
    }
}
