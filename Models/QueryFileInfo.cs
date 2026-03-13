namespace SqlVersionControl.Models;

public class QueryFileInfo
{
    public string Name { get; set; } = "";
    public string Database { get; set; } = "";
    public string FilePath { get; set; } = "";
    public DateTime Created { get; set; }
    public DateTime Modified { get; set; }
}
