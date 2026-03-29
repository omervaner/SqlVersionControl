using Microsoft.Data.SqlClient;

namespace SqlVersionControl.Models;

public class ConnectionSettings
{
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public bool UseWindowsAuth { get; set; }
    public bool TrustServerCertificate { get; set; } = true;

    public string ConnectionString
    {
        get
        {
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = Server,
                InitialCatalog = Database,
                TrustServerCertificate = TrustServerCertificate,
                ConnectTimeout = 5,
                Pooling = true,
                MinPoolSize = 0,
                MaxPoolSize = 10
            };

            if (UseWindowsAuth)
                builder.IntegratedSecurity = true;
            else
            {
                builder.UserID = Username;
                builder.Password = Password;
            }

            return builder.ConnectionString;
        }
    }
}
