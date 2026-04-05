-- =============================================================================
-- Server 2 (DEV) Seed Script — localhost,1434
-- Container: wonderful_ellis (fd62dee2e224)
-- Idempotent: safe to re-run (DROP IF EXISTS / CREATE OR ALTER throughout)
-- Creates TestDB with intentional schema differences from Server 1 (PROD)
-- for testing Database Compare without connecting to real servers.
-- No SQL Agent jobs on this server.
-- =============================================================================

USE master;
GO

IF DB_ID('TestDB') IS NULL
    CREATE DATABASE TestDB;
GO

USE TestDB;
GO

-- ---------------------------------------------------------------------------
-- Tables — same base schema as PROD but with intentional differences
-- ---------------------------------------------------------------------------

IF OBJECT_ID('dbo.ProjectAssignments', 'U') IS NOT NULL DROP TABLE dbo.ProjectAssignments;
IF OBJECT_ID('dbo.EmployeeSalaryHistory', 'U') IS NOT NULL DROP TABLE dbo.EmployeeSalaryHistory;
IF OBJECT_ID('dbo.AuditLog', 'U') IS NOT NULL DROP TABLE dbo.AuditLog;
IF OBJECT_ID('dbo.Employees', 'U') IS NOT NULL DROP TABLE dbo.Employees;
IF OBJECT_ID('dbo.Projects', 'U') IS NOT NULL DROP TABLE dbo.Projects;
IF OBJECT_ID('dbo.Departments', 'U') IS NOT NULL DROP TABLE dbo.Departments;
IF OBJECT_ID('dbo.HeartbeatLog', 'U') IS NOT NULL DROP TABLE dbo.HeartbeatLog;
GO

CREATE TABLE dbo.Departments (
    DepartmentId    INT IDENTITY(1,1) PRIMARY KEY,
    Name            NVARCHAR(100)   NOT NULL,
    Code            NVARCHAR(10)    NOT NULL,
    ManagerEmail    NVARCHAR(200)   NULL,
    Budget          DECIMAL(15,2)   NOT NULL DEFAULT 0,
    IsActive        BIT             NOT NULL DEFAULT 1,
    CreatedAt       DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

CREATE TABLE dbo.Employees (
    EmployeeId      INT IDENTITY(1,1) PRIMARY KEY,
    FirstName       NVARCHAR(100)   NOT NULL,
    LastName        NVARCHAR(100)   NOT NULL,
    Email           NVARCHAR(200)   NOT NULL,
    DepartmentId    INT             NOT NULL REFERENCES dbo.Departments(DepartmentId),
    HireDate        DATE            NOT NULL,
    Salary          DECIMAL(12,2)   NOT NULL,
    IsActive        BIT             NOT NULL DEFAULT 1,
    CreatedAt       DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    ModifiedAt      DATETIME2       NULL
);
GO

CREATE TABLE dbo.Projects (
    ProjectId       INT IDENTITY(1,1) PRIMARY KEY,
    Name            NVARCHAR(200)   NOT NULL,
    Code            NVARCHAR(20)    NOT NULL,
    DepartmentId    INT             NOT NULL REFERENCES dbo.Departments(DepartmentId),
    StartDate       DATE            NOT NULL,
    EndDate         DATE            NULL,
    Status          NVARCHAR(20)    NOT NULL DEFAULT 'Active',
    Budget          DECIMAL(15,2)   NOT NULL DEFAULT 0,
    CreatedAt       DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

CREATE TABLE dbo.ProjectAssignments (
    AssignmentId    INT IDENTITY(1,1) PRIMARY KEY,
    EmployeeId      INT             NOT NULL REFERENCES dbo.Employees(EmployeeId),
    ProjectId       INT             NOT NULL REFERENCES dbo.Projects(ProjectId),
    Role            NVARCHAR(50)    NOT NULL DEFAULT 'Member',
    AssignedDate    DATE            NOT NULL DEFAULT GETDATE(),
    HoursPerWeek    DECIMAL(4,1)    NOT NULL DEFAULT 40.0
);
GO

-- EmployeeSalaryHistory intentionally MISSING (tests "only in source" compare)
-- AuditLog intentionally MISSING (tests "only in source" compare)
-- HeartbeatLog intentionally MISSING (tests "only in source" compare)

-- Extra table only on DEV (tests "only in target" compare)
IF OBJECT_ID('dbo.OfficeLocations', 'U') IS NOT NULL DROP TABLE dbo.OfficeLocations;
GO
CREATE TABLE dbo.OfficeLocations (
    LocationId      INT IDENTITY(1,1) PRIMARY KEY,
    City            NVARCHAR(100)   NOT NULL,
    Country         NVARCHAR(100)   NOT NULL,
    Address         NVARCHAR(500)   NULL,
    Capacity        INT             NOT NULL DEFAULT 100
);
GO

-- ---------------------------------------------------------------------------
-- Views — 2 matching, 1 modified, 1 missing (vw_DepartmentStats), 1 extra
-- ---------------------------------------------------------------------------

-- Same as PROD
CREATE OR ALTER VIEW dbo.vw_EmployeeDirectory
AS
SELECT
    e.EmployeeId,
    e.FirstName + ' ' + e.LastName  AS FullName,
    e.Email,
    d.Name                          AS Department,
    d.Code                          AS DeptCode,
    e.HireDate,
    e.Salary,
    e.IsActive
FROM dbo.Employees e
JOIN dbo.Departments d ON d.DepartmentId = e.DepartmentId;
GO

-- Modified version — added extra columns vs PROD
CREATE OR ALTER VIEW dbo.vw_ProjectOverview
AS
SELECT
    p.ProjectId,
    p.Name          AS ProjectName,
    p.Code          AS ProjectCode,
    d.Name          AS OwningDepartment,
    d.Code          AS DeptCode,              -- extra column vs PROD
    p.Status,
    p.Budget,
    p.StartDate,
    p.EndDate,
    COUNT(pa.AssignmentId)          AS TeamSize,
    SUM(pa.HoursPerWeek)           AS TotalWeeklyHours,
    AVG(pa.HoursPerWeek)           AS AvgWeeklyHours  -- extra column vs PROD
FROM dbo.Projects p
JOIN dbo.Departments d ON d.DepartmentId = p.DepartmentId
LEFT JOIN dbo.ProjectAssignments pa ON pa.ProjectId = p.ProjectId
GROUP BY p.ProjectId, p.Name, p.Code, d.Name, d.Code, p.Status, p.Budget, p.StartDate, p.EndDate;
GO

-- vw_DepartmentStats intentionally MISSING from DEV

-- Extra view only on DEV
CREATE OR ALTER VIEW dbo.vw_OfficeCapacity
AS
SELECT City, Country, Capacity
FROM dbo.OfficeLocations;
GO

-- ---------------------------------------------------------------------------
-- Functions — 1 matching, 1 modified, 1 extra
-- ---------------------------------------------------------------------------

-- Same as PROD
CREATE OR ALTER FUNCTION dbo.fn_GetEmployeeTenureYears(@EmployeeId INT)
RETURNS DECIMAL(5,1)
AS
BEGIN
    DECLARE @tenure DECIMAL(5,1);
    SELECT @tenure = DATEDIFF(DAY, HireDate, GETDATE()) / 365.25
    FROM dbo.Employees
    WHERE EmployeeId = @EmployeeId;
    RETURN ISNULL(@tenure, 0);
END;
GO

-- Modified: counts all employees, not just active (differs from PROD)
CREATE OR ALTER FUNCTION dbo.fn_GetDepartmentHeadcount(@DepartmentId INT)
RETURNS INT
AS
BEGIN
    DECLARE @count INT;
    SELECT @count = COUNT(*)
    FROM dbo.Employees
    WHERE DepartmentId = @DepartmentId;  -- no IsActive filter (differs from PROD)
    RETURN ISNULL(@count, 0);
END;
GO

-- Extra function only on DEV
CREATE OR ALTER FUNCTION dbo.fn_FormatEmployeeName(@FirstName NVARCHAR(100), @LastName NVARCHAR(100))
RETURNS NVARCHAR(201)
AS
BEGIN
    RETURN @LastName + ', ' + @FirstName;
END;
GO

-- ---------------------------------------------------------------------------
-- Stored Procedures — 1 matching, 1 modified, 2 missing, 1 extra
-- ---------------------------------------------------------------------------

-- Same as PROD
CREATE OR ALTER PROCEDURE dbo.usp_GetEmployeesByDepartment
    @DepartmentId INT,
    @ActiveOnly BIT = 1
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        e.EmployeeId,
        e.FirstName,
        e.LastName,
        e.Email,
        e.HireDate,
        e.Salary,
        e.IsActive,
        dbo.fn_GetEmployeeTenureYears(e.EmployeeId) AS TenureYears
    FROM dbo.Employees e
    WHERE e.DepartmentId = @DepartmentId
      AND (@ActiveOnly = 0 OR e.IsActive = 1)
    ORDER BY e.LastName, e.FirstName;
END;
GO

-- Modified: added @MinSalary filter that PROD doesn't have
CREATE OR ALTER PROCEDURE dbo.usp_SearchEmployees
    @SearchTerm NVARCHAR(100),
    @MinSalary DECIMAL(12,2) = 0  -- extra param vs PROD
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        e.EmployeeId,
        e.FirstName + ' ' + e.LastName AS FullName,
        e.Email,
        d.Name AS Department,
        e.HireDate,
        e.Salary
    FROM dbo.Employees e
    JOIN dbo.Departments d ON d.DepartmentId = e.DepartmentId
    WHERE (e.FirstName LIKE '%' + @SearchTerm + '%'
       OR e.LastName  LIKE '%' + @SearchTerm + '%'
       OR e.Email     LIKE '%' + @SearchTerm + '%')
      AND e.Salary >= @MinSalary
    ORDER BY e.LastName;
END;
GO

-- usp_UpdateEmployeeSalary intentionally MISSING from DEV
-- usp_GetProjectTeam intentionally MISSING from DEV

-- Extra proc only on DEV
CREATE OR ALTER PROCEDURE dbo.usp_GetOfficeLocations
AS
BEGIN
    SET NOCOUNT ON;
    SELECT City, Country, Address, Capacity
    FROM dbo.OfficeLocations
    ORDER BY Country, City;
END;
GO

-- ---------------------------------------------------------------------------
-- Trigger — missing from DEV (trg_Employees_Audit not created here)
-- ---------------------------------------------------------------------------

-- No trigger on DEV — tests "only in source" for triggers in compare

PRINT '====================================================================';
PRINT 'Server 2 (DEV) seed completed successfully.';
PRINT 'Created: TestDB (5 tables, 3 views, 3 procs, 3 functions)';
PRINT 'No SQL Agent jobs on this server.';
PRINT '====================================================================';
GO
