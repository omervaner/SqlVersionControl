-- ObjectVersions table for SQL Version Control
-- Run this on your DDL logging database

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ObjectVersions')
BEGIN
    CREATE TABLE dbo.ObjectVersions (
        VersionId INT IDENTITY PRIMARY KEY,
        DatabaseName NVARCHAR(128),
        SchemaName NVARCHAR(128),
        ObjectName NVARCHAR(128),
        ObjectType NVARCHAR(50),      -- PROCEDURE, FUNCTION, TABLE, VIEW, TRIGGER, INDEX
        Definition NVARCHAR(MAX),
        EventType NVARCHAR(50),       -- CREATE, ALTER, DROP
        ChangedBy NVARCHAR(128),      -- Login name
        HostName NVARCHAR(128),
        IPAddress NVARCHAR(50),
        AppName NVARCHAR(256),
        ChangedAt DATETIME2,
        VersionNumber INT,            -- Auto-increment per object
        INDEX IX_Object (DatabaseName, SchemaName, ObjectName),
        INDEX IX_ChangedAt (ChangedAt DESC)
    )
END
GO

-- Useful view for the app
CREATE OR ALTER VIEW dbo.vw_RecentChanges AS
SELECT TOP 500
    VersionId, DatabaseName, SchemaName, ObjectName, ObjectType,
    EventType, ChangedBy, HostName, ChangedAt, VersionNumber
FROM dbo.ObjectVersions
WHERE ChangedBy IN ('HJS', 'aictr01') -- Filter to user logins only
ORDER BY ChangedAt DESC
GO
