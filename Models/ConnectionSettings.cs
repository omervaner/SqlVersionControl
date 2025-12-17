namespace SqlVersionControl.Models;

public class ConnectionSettings
{
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public bool UseWindowsAuth { get; set; }

    public string ConnectionString
    {
        get
        {
            if (UseWindowsAuth)
            {
                return $"Server={Server};Database={Database};Integrated Security=True;TrustServerCertificate=True;";
            }
            return $"Server={Server};Database={Database};User Id={Username};Password={Password};TrustServerCertificate=True;";
        }
    }
}
