namespace SqlVersionControl.Helpers;

public static class SqlNameParser
{
    public static (string Schema, string Name) ParseSchemaQualifiedName(string objectName)
    {
        if (objectName.Contains('.'))
        {
            var parts = objectName.Split('.', 2);
            return (parts[0].Trim('[', ']'), parts[1].Trim('[', ']'));
        }
        return ("dbo", objectName.Trim('[', ']'));
    }
}
