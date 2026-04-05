-- =============================================================================
-- Server 1 (PROD) Seed Script — localhost,1433
-- Container: zealous_cannon (b370073ec7f5)
-- Idempotent: safe to re-run (DROP IF EXISTS / CREATE OR ALTER throughout)
-- Creates TestDB with tables, data, views, procs, functions, trigger, and
-- 6 SQL Agent jobs for testing Object Explorer, editable grid, dependency
-- explorer, peek definition, and the Jobs tab.
-- =============================================================================

USE master;
GO

IF DB_ID('TestDB') IS NULL
    CREATE DATABASE TestDB;
GO

USE TestDB;
GO

-- ---------------------------------------------------------------------------
-- Tables
-- ---------------------------------------------------------------------------

-- HeartbeatLog (used by the Quick Heartbeat agent job)
IF OBJECT_ID('dbo.HeartbeatLog', 'U') IS NOT NULL DROP TABLE dbo.HeartbeatLog;
GO
CREATE TABLE dbo.HeartbeatLog (
    Id          INT IDENTITY(1,1) PRIMARY KEY,
    LoggedAt    DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    Message     NVARCHAR(200) NOT NULL DEFAULT 'heartbeat'
);
GO

-- AuditLog (referenced by trigger, procs)
IF OBJECT_ID('dbo.AuditLog', 'U') IS NOT NULL DROP TABLE dbo.AuditLog;
GO

-- ProjectAssignments (FK to Employees + Projects)
IF OBJECT_ID('dbo.ProjectAssignments', 'U') IS NOT NULL DROP TABLE dbo.ProjectAssignments;
GO

-- EmployeeSalaryHistory (FK to Employees)
IF OBJECT_ID('dbo.EmployeeSalaryHistory', 'U') IS NOT NULL DROP TABLE dbo.EmployeeSalaryHistory;
GO

-- Employees (FK to Departments)
IF OBJECT_ID('dbo.Employees', 'U') IS NOT NULL DROP TABLE dbo.Employees;
GO

-- Projects
IF OBJECT_ID('dbo.Projects', 'U') IS NOT NULL DROP TABLE dbo.Projects;
GO

-- Departments
IF OBJECT_ID('dbo.Departments', 'U') IS NOT NULL DROP TABLE dbo.Departments;
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

CREATE TABLE dbo.EmployeeSalaryHistory (
    HistoryId       INT IDENTITY(1,1) PRIMARY KEY,
    EmployeeId      INT             NOT NULL REFERENCES dbo.Employees(EmployeeId),
    OldSalary       DECIMAL(12,2)   NOT NULL,
    NewSalary       DECIMAL(12,2)   NOT NULL,
    ChangeDate      DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    ChangedBy       NVARCHAR(100)   NOT NULL DEFAULT SYSTEM_USER,
    Reason          NVARCHAR(500)   NULL
);
GO

CREATE TABLE dbo.AuditLog (
    AuditId         INT IDENTITY(1,1) PRIMARY KEY,
    TableName       NVARCHAR(128)   NOT NULL,
    Operation       NVARCHAR(10)    NOT NULL,  -- INSERT, UPDATE, DELETE
    RecordId        INT             NULL,
    OldValues       NVARCHAR(MAX)   NULL,
    NewValues       NVARCHAR(MAX)   NULL,
    ChangedBy       NVARCHAR(128)   NOT NULL DEFAULT SYSTEM_USER,
    ChangedAt       DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

-- ---------------------------------------------------------------------------
-- Sample Data
-- ---------------------------------------------------------------------------

SET IDENTITY_INSERT dbo.Departments ON;
INSERT INTO dbo.Departments (DepartmentId, Name, Code, ManagerEmail, Budget)
VALUES
    (1, 'Engineering',      'ENG',  'alice.chen@example.com',    2500000.00),
    (2, 'Product',          'PROD', 'bob.martinez@example.com',   800000.00),
    (3, 'Data Science',     'DS',   'carol.okafor@example.com',  1200000.00),
    (4, 'Design',           'DES',  'diana.novak@example.com',    600000.00),
    (5, 'DevOps',           'OPS',  'erik.johansson@example.com', 900000.00),
    (6, 'Quality Assurance','QA',   'fiona.kelly@example.com',    500000.00);
SET IDENTITY_INSERT dbo.Departments OFF;
GO

SET IDENTITY_INSERT dbo.Employees ON;
INSERT INTO dbo.Employees (EmployeeId, FirstName, LastName, Email, DepartmentId, HireDate, Salary)
VALUES
    ( 1, 'Alice',   'Chen',       'alice.chen@example.com',       1, '2019-03-15', 165000.00),
    ( 2, 'Bob',     'Martinez',   'bob.martinez@example.com',     2, '2020-01-10', 145000.00),
    ( 3, 'Carol',   'Okafor',     'carol.okafor@example.com',     3, '2018-07-22', 170000.00),
    ( 4, 'Diana',   'Novak',      'diana.novak@example.com',      4, '2021-04-01', 130000.00),
    ( 5, 'Erik',    'Johansson',  'erik.johansson@example.com',   5, '2019-11-18', 155000.00),
    ( 6, 'Fiona',   'Kelly',      'fiona.kelly@example.com',      6, '2020-06-30', 125000.00),
    ( 7, 'George',  'Tanaka',     'george.tanaka@example.com',    1, '2022-02-14', 140000.00),
    ( 8, 'Hannah',  'Petrov',     'hannah.petrov@example.com',    1, '2021-09-05', 135000.00),
    ( 9, 'Ivan',    'Rossi',      'ivan.rossi@example.com',       3, '2023-01-20', 120000.00),
    (10, 'Julia',   'Park',       'julia.park@example.com',       2, '2022-08-12', 115000.00),
    (11, 'Kevin',   'Muller',     'kevin.muller@example.com',     5, '2023-06-01', 110000.00),
    (12, 'Laura',   'Singh',      'laura.singh@example.com',      1, '2020-11-10', 150000.00),
    (13, 'Marcus',  'Thompson',   'marcus.thompson@example.com',  6, '2024-01-15',  95000.00),
    (14, 'Nina',    'Yamamoto',   'nina.yamamoto@example.com',    4, '2023-03-28', 105000.00),
    (15, 'Oscar',   'Fernandez',  'oscar.fernandez@example.com',  3, '2022-05-09', 130000.00);
SET IDENTITY_INSERT dbo.Employees OFF;
GO

SET IDENTITY_INSERT dbo.Projects ON;
INSERT INTO dbo.Projects (ProjectId, Name, Code, DepartmentId, StartDate, EndDate, Status, Budget)
VALUES
    (1, 'Platform Rewrite',       'PLAT-01', 1, '2024-01-15', NULL,          'Active',    800000.00),
    (2, 'Mobile App v3',          'MOB-03',  2, '2024-03-01', NULL,          'Active',    400000.00),
    (3, 'ML Pipeline',            'ML-01',   3, '2023-09-01', '2024-12-31', 'Completed', 600000.00),
    (4, 'Design System Refresh',  'DES-02',  4, '2024-06-01', NULL,          'Active',    200000.00),
    (5, 'CI/CD Migration',        'OPS-05',  5, '2024-04-01', '2024-10-30', 'Completed', 150000.00),
    (6, 'Test Automation Suite',  'QA-01',   6, '2024-07-15', NULL,          'Active',    250000.00);
SET IDENTITY_INSERT dbo.Projects OFF;
GO

INSERT INTO dbo.ProjectAssignments (EmployeeId, ProjectId, Role, AssignedDate, HoursPerWeek)
VALUES
    ( 1, 1, 'Lead',     '2024-01-15', 30.0),
    ( 7, 1, 'Member',   '2024-01-20', 40.0),
    ( 8, 1, 'Member',   '2024-02-01', 40.0),
    (12, 1, 'Member',   '2024-01-15', 35.0),
    ( 2, 2, 'Lead',     '2024-03-01', 25.0),
    (10, 2, 'Member',   '2024-03-15', 40.0),
    ( 3, 3, 'Lead',     '2023-09-01', 20.0),
    ( 9, 3, 'Member',   '2023-10-01', 40.0),
    (15, 3, 'Member',   '2023-09-15', 40.0),
    ( 4, 4, 'Lead',     '2024-06-01', 30.0),
    (14, 4, 'Member',   '2024-06-15', 40.0),
    ( 5, 5, 'Lead',     '2024-04-01', 35.0),
    (11, 5, 'Member',   '2024-05-01', 40.0),
    ( 6, 6, 'Lead',     '2024-07-15', 30.0),
    (13, 6, 'Member',   '2024-07-20', 40.0);
GO

INSERT INTO dbo.EmployeeSalaryHistory (EmployeeId, OldSalary, NewSalary, ChangeDate, Reason)
VALUES
    ( 1, 150000.00, 165000.00, '2024-01-01', 'Annual review - exceeds expectations'),
    ( 3, 155000.00, 170000.00, '2024-01-01', 'Annual review - promotion to principal'),
    ( 7, 125000.00, 140000.00, '2024-07-01', 'Mid-year adjustment - expanded scope'),
    (12, 140000.00, 150000.00, '2024-01-01', 'Annual review - strong performance'),
    ( 5, 145000.00, 155000.00, '2024-01-01', 'Annual review - meets expectations'),
    ( 2, 135000.00, 145000.00, '2024-01-01', 'Annual review - product launch bonus'),
    ( 9, 105000.00, 120000.00, '2024-07-01', 'Mid-year adjustment - market correction');
GO

-- ---------------------------------------------------------------------------
-- Views
-- ---------------------------------------------------------------------------

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

CREATE OR ALTER VIEW dbo.vw_ProjectOverview
AS
SELECT
    p.ProjectId,
    p.Name          AS ProjectName,
    p.Code          AS ProjectCode,
    d.Name          AS OwningDepartment,
    p.Status,
    p.Budget,
    p.StartDate,
    p.EndDate,
    COUNT(pa.AssignmentId)          AS TeamSize,
    SUM(pa.HoursPerWeek)           AS TotalWeeklyHours
FROM dbo.Projects p
JOIN dbo.Departments d ON d.DepartmentId = p.DepartmentId
LEFT JOIN dbo.ProjectAssignments pa ON pa.ProjectId = p.ProjectId
GROUP BY p.ProjectId, p.Name, p.Code, d.Name, p.Status, p.Budget, p.StartDate, p.EndDate;
GO

CREATE OR ALTER VIEW dbo.vw_DepartmentStats
AS
SELECT
    d.DepartmentId,
    d.Name                          AS Department,
    d.Budget                        AS DeptBudget,
    COUNT(e.EmployeeId)             AS EmployeeCount,
    AVG(e.Salary)                   AS AvgSalary,
    MIN(e.HireDate)                 AS EarliestHire,
    MAX(e.HireDate)                 AS LatestHire
FROM dbo.Departments d
LEFT JOIN dbo.Employees e ON e.DepartmentId = d.DepartmentId AND e.IsActive = 1
GROUP BY d.DepartmentId, d.Name, d.Budget;
GO

-- ---------------------------------------------------------------------------
-- Functions
-- ---------------------------------------------------------------------------

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

CREATE OR ALTER FUNCTION dbo.fn_GetDepartmentHeadcount(@DepartmentId INT)
RETURNS INT
AS
BEGIN
    DECLARE @count INT;
    SELECT @count = COUNT(*)
    FROM dbo.Employees
    WHERE DepartmentId = @DepartmentId AND IsActive = 1;
    RETURN ISNULL(@count, 0);
END;
GO

-- ---------------------------------------------------------------------------
-- Stored Procedures
-- ---------------------------------------------------------------------------

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

CREATE OR ALTER PROCEDURE dbo.usp_UpdateEmployeeSalary
    @EmployeeId INT,
    @NewSalary  DECIMAL(12,2),
    @Reason     NVARCHAR(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @OldSalary DECIMAL(12,2);
    SELECT @OldSalary = Salary FROM dbo.Employees WHERE EmployeeId = @EmployeeId;

    IF @OldSalary IS NULL
    BEGIN
        RAISERROR('Employee %d not found.', 16, 1, @EmployeeId);
        RETURN;
    END;

    BEGIN TRANSACTION;
        UPDATE dbo.Employees
        SET Salary = @NewSalary, ModifiedAt = SYSUTCDATETIME()
        WHERE EmployeeId = @EmployeeId;

        INSERT INTO dbo.EmployeeSalaryHistory (EmployeeId, OldSalary, NewSalary, Reason)
        VALUES (@EmployeeId, @OldSalary, @NewSalary, @Reason);
    COMMIT;
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_SearchEmployees
    @SearchTerm NVARCHAR(100)
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
    WHERE e.FirstName LIKE '%' + @SearchTerm + '%'
       OR e.LastName  LIKE '%' + @SearchTerm + '%'
       OR e.Email     LIKE '%' + @SearchTerm + '%'
    ORDER BY e.LastName;
END;
GO

CREATE OR ALTER PROCEDURE dbo.usp_GetProjectTeam
    @ProjectId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        e.EmployeeId,
        e.FirstName + ' ' + e.LastName AS FullName,
        pa.Role,
        pa.AssignedDate,
        pa.HoursPerWeek,
        d.Name AS Department
    FROM dbo.ProjectAssignments pa
    JOIN dbo.Employees e ON e.EmployeeId = pa.EmployeeId
    JOIN dbo.Departments d ON d.DepartmentId = e.DepartmentId
    WHERE pa.ProjectId = @ProjectId
    ORDER BY
        CASE pa.Role WHEN 'Lead' THEN 0 ELSE 1 END,
        e.LastName;
END;
GO

-- ---------------------------------------------------------------------------
-- Trigger
-- ---------------------------------------------------------------------------

IF OBJECT_ID('dbo.trg_Employees_Audit', 'TR') IS NOT NULL
    DROP TRIGGER dbo.trg_Employees_Audit;
GO

CREATE TRIGGER dbo.trg_Employees_Audit
ON dbo.Employees
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted)
    BEGIN
        -- UPDATE
        INSERT INTO dbo.AuditLog (TableName, Operation, RecordId, OldValues, NewValues)
        SELECT
            'Employees', 'UPDATE', i.EmployeeId,
            'Salary=' + CAST(d.Salary AS NVARCHAR) + ',Active=' + CAST(d.IsActive AS NVARCHAR),
            'Salary=' + CAST(i.Salary AS NVARCHAR) + ',Active=' + CAST(i.IsActive AS NVARCHAR)
        FROM inserted i
        JOIN deleted d ON d.EmployeeId = i.EmployeeId;
    END
    ELSE IF EXISTS (SELECT 1 FROM inserted)
    BEGIN
        -- INSERT
        INSERT INTO dbo.AuditLog (TableName, Operation, RecordId, NewValues)
        SELECT 'Employees', 'INSERT', EmployeeId,
            'Name=' + FirstName + ' ' + LastName + ',Email=' + Email
        FROM inserted;
    END
    ELSE IF EXISTS (SELECT 1 FROM deleted)
    BEGIN
        -- DELETE
        INSERT INTO dbo.AuditLog (TableName, Operation, RecordId, OldValues)
        SELECT 'Employees', 'DELETE', EmployeeId,
            'Name=' + FirstName + ' ' + LastName
        FROM deleted;
    END;
END;
GO


-- =============================================================================
-- SQL Agent Jobs
-- =============================================================================

USE msdb;
GO

-- ---------------------------------------------------------------------------
-- Job 1: Test - Quick Heartbeat (every 1 minute)
-- ---------------------------------------------------------------------------
IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = N'Test - Quick Heartbeat')
    EXEC msdb.dbo.sp_delete_job @job_name = N'Test - Quick Heartbeat', @delete_unused_schedule = 1;
GO

EXEC msdb.dbo.sp_add_job
    @job_name = N'Test - Quick Heartbeat',
    @enabled = 1,
    @description = N'Inserts a heartbeat row every minute. Generates job history fast for testing.',
    @category_name = N'[Uncategorized (Local)]',
    @owner_login_name = N'sa';
GO

EXEC msdb.dbo.sp_add_jobstep
    @job_name = N'Test - Quick Heartbeat',
    @step_name = N'Insert heartbeat',
    @step_id = 1,
    @subsystem = N'TSQL',
    @command = N'INSERT INTO TestDB.dbo.HeartbeatLog (Message) VALUES (''heartbeat at '' + CONVERT(VARCHAR, GETDATE(), 120));',
    @database_name = N'TestDB',
    @on_success_action = 1;
GO

EXEC msdb.dbo.sp_add_jobschedule
    @job_name = N'Test - Quick Heartbeat',
    @name = N'Every 1 minute',
    @freq_type = 4,
    @freq_interval = 1,
    @freq_subday_type = 4,
    @freq_subday_interval = 1,
    @active_start_time = 0;
GO

EXEC msdb.dbo.sp_add_jobserver
    @job_name = N'Test - Quick Heartbeat',
    @server_name = N'(LOCAL)';
GO

-- ---------------------------------------------------------------------------
-- Job 2: Test - DB Maintenance (every 5 minutes, 2 steps)
-- ---------------------------------------------------------------------------
IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = N'Test - DB Maintenance')
    EXEC msdb.dbo.sp_delete_job @job_name = N'Test - DB Maintenance', @delete_unused_schedule = 1;
GO

EXEC msdb.dbo.sp_add_job
    @job_name = N'Test - DB Maintenance',
    @enabled = 1,
    @description = N'Two-step maintenance: UPDATE STATISTICS + DBCC CHECKDB. Tests multi-step job display.',
    @category_name = N'[Uncategorized (Local)]',
    @owner_login_name = N'sa';
GO

EXEC msdb.dbo.sp_add_jobstep
    @job_name = N'Test - DB Maintenance',
    @step_name = N'Update Statistics',
    @step_id = 1,
    @subsystem = N'TSQL',
    @command = N'EXEC sp_updatestats;',
    @database_name = N'TestDB',
    @on_success_action = 3;
GO

EXEC msdb.dbo.sp_add_jobstep
    @job_name = N'Test - DB Maintenance',
    @step_name = N'Check Database Integrity',
    @step_id = 2,
    @subsystem = N'TSQL',
    @command = N'DBCC CHECKDB (N''TestDB'') WITH NO_INFOMSGS;',
    @database_name = N'TestDB',
    @on_success_action = 1;
GO

EXEC msdb.dbo.sp_add_jobschedule
    @job_name = N'Test - DB Maintenance',
    @name = N'Every 5 minutes',
    @freq_type = 4,
    @freq_interval = 1,
    @freq_subday_type = 4,
    @freq_subday_interval = 5,
    @active_start_time = 0;
GO

EXEC msdb.dbo.sp_add_jobserver
    @job_name = N'Test - DB Maintenance',
    @server_name = N'(LOCAL)';
GO

-- ---------------------------------------------------------------------------
-- Job 3: Test - Legacy Cleanup (disabled)
-- ---------------------------------------------------------------------------
IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = N'Test - Legacy Cleanup')
    EXEC msdb.dbo.sp_delete_job @job_name = N'Test - Legacy Cleanup', @delete_unused_schedule = 1;
GO

EXEC msdb.dbo.sp_add_job
    @job_name = N'Test - Legacy Cleanup',
    @enabled = 0,
    @description = N'Disabled job for testing enabled/disabled split in Jobs dashboard and Enable/Disable toggle.',
    @category_name = N'[Uncategorized (Local)]',
    @owner_login_name = N'sa';
GO

EXEC msdb.dbo.sp_add_jobstep
    @job_name = N'Test - Legacy Cleanup',
    @step_name = N'Purge old records',
    @step_id = 1,
    @subsystem = N'TSQL',
    @command = N'DELETE FROM TestDB.dbo.AuditLog WHERE ChangedAt < DATEADD(YEAR, -1, GETDATE());',
    @database_name = N'TestDB',
    @on_success_action = 1;
GO

EXEC msdb.dbo.sp_add_jobschedule
    @job_name = N'Test - Legacy Cleanup',
    @name = N'Daily at midnight',
    @freq_type = 4,
    @freq_interval = 1,
    @active_start_time = 0;
GO

EXEC msdb.dbo.sp_add_jobserver
    @job_name = N'Test - Legacy Cleanup',
    @server_name = N'(LOCAL)';
GO

-- ---------------------------------------------------------------------------
-- Job 4: Test - Nightly Report (daily at 02:00, multi-step)
-- ---------------------------------------------------------------------------
IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = N'Test - Nightly Report')
    EXEC msdb.dbo.sp_delete_job @job_name = N'Test - Nightly Report', @delete_unused_schedule = 1;
GO

EXEC msdb.dbo.sp_add_job
    @job_name = N'Test - Nightly Report',
    @enabled = 1,
    @description = N'Daily scheduled job at 02:00. Multi-step for testing "Next Run" display.',
    @category_name = N'[Uncategorized (Local)]',
    @owner_login_name = N'sa';
GO

EXEC msdb.dbo.sp_add_jobstep
    @job_name = N'Test - Nightly Report',
    @step_name = N'Collect department stats',
    @step_id = 1,
    @subsystem = N'TSQL',
    @command = N'SELECT d.Name, COUNT(e.EmployeeId) AS HeadCount, AVG(e.Salary) AS AvgSalary
FROM TestDB.dbo.Departments d
LEFT JOIN TestDB.dbo.Employees e ON e.DepartmentId = d.DepartmentId AND e.IsActive = 1
GROUP BY d.Name;',
    @database_name = N'TestDB',
    @on_success_action = 3;
GO

EXEC msdb.dbo.sp_add_jobstep
    @job_name = N'Test - Nightly Report',
    @step_name = N'Collect project summary',
    @step_id = 2,
    @subsystem = N'TSQL',
    @command = N'SELECT p.Name, p.Status, COUNT(pa.AssignmentId) AS TeamSize
FROM TestDB.dbo.Projects p
LEFT JOIN TestDB.dbo.ProjectAssignments pa ON pa.ProjectId = p.ProjectId
GROUP BY p.Name, p.Status;',
    @database_name = N'TestDB',
    @on_success_action = 3;
GO

EXEC msdb.dbo.sp_add_jobstep
    @job_name = N'Test - Nightly Report',
    @step_name = N'Log completion',
    @step_id = 3,
    @subsystem = N'TSQL',
    @command = N'INSERT INTO TestDB.dbo.AuditLog (TableName, Operation, NewValues)
VALUES (''NightlyReport'', ''INSERT'', ''Report generated at '' + CONVERT(VARCHAR, GETDATE(), 120));',
    @database_name = N'TestDB',
    @on_success_action = 1;
GO

EXEC msdb.dbo.sp_add_jobschedule
    @job_name = N'Test - Nightly Report',
    @name = N'Daily at 2AM',
    @freq_type = 4,
    @freq_interval = 1,
    @active_start_time = 020000;
GO

EXEC msdb.dbo.sp_add_jobserver
    @job_name = N'Test - Nightly Report',
    @server_name = N'(LOCAL)';
GO

-- ---------------------------------------------------------------------------
-- Job 5: Test - Broken Import (always fails, every 3 minutes)
-- ---------------------------------------------------------------------------
IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = N'Test - Broken Import')
    EXEC msdb.dbo.sp_delete_job @job_name = N'Test - Broken Import', @delete_unused_schedule = 1;
GO

EXEC msdb.dbo.sp_add_job
    @job_name = N'Test - Broken Import',
    @enabled = 1,
    @description = N'Always fails — references nonexistent table. Tests failed jobs alert badge and Failed (24h) stat card.',
    @category_name = N'[Uncategorized (Local)]',
    @owner_login_name = N'sa';
GO

EXEC msdb.dbo.sp_add_jobstep
    @job_name = N'Test - Broken Import',
    @step_name = N'Import from staging',
    @step_id = 1,
    @subsystem = N'TSQL',
    @command = N'SELECT * FROM TestDB.dbo.StagingImport_DoesNotExist;',
    @database_name = N'TestDB',
    @on_success_action = 1,
    @on_fail_action = 2;
GO

EXEC msdb.dbo.sp_add_jobschedule
    @job_name = N'Test - Broken Import',
    @name = N'Every 3 minutes',
    @freq_type = 4,
    @freq_interval = 1,
    @freq_subday_type = 4,
    @freq_subday_interval = 3,
    @active_start_time = 0;
GO

EXEC msdb.dbo.sp_add_jobserver
    @job_name = N'Test - Broken Import',
    @server_name = N'(LOCAL)';
GO

-- ---------------------------------------------------------------------------
-- Job 6: Test - Intermittent Fail (~50% failure rate, every 2 minutes)
-- ---------------------------------------------------------------------------
IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = N'Test - Intermittent Fail')
    EXEC msdb.dbo.sp_delete_job @job_name = N'Test - Intermittent Fail', @delete_unused_schedule = 1;
GO

EXEC msdb.dbo.sp_add_job
    @job_name = N'Test - Intermittent Fail',
    @enabled = 1,
    @description = N'Fails ~50% of the time using random check. Tests mixed success/failure history.',
    @category_name = N'[Uncategorized (Local)]',
    @owner_login_name = N'sa';
GO

EXEC msdb.dbo.sp_add_jobstep
    @job_name = N'Test - Intermittent Fail',
    @step_name = N'Unreliable processing',
    @step_id = 1,
    @subsystem = N'TSQL',
    @command = N'
-- Fail roughly 50% of the time
DECLARE @roll INT = ABS(CHECKSUM(NEWID())) % 100;
IF @roll < 50
BEGIN
    RAISERROR(''Intermittent failure: random roll was %d (< 50)'', 16, 1, @roll);
END
ELSE
BEGIN
    INSERT INTO TestDB.dbo.HeartbeatLog (Message)
    VALUES (''Intermittent job succeeded: roll was '' + CAST(@roll AS VARCHAR));
END;',
    @database_name = N'TestDB',
    @on_success_action = 1,
    @on_fail_action = 2;
GO

EXEC msdb.dbo.sp_add_jobschedule
    @job_name = N'Test - Intermittent Fail',
    @name = N'Every 2 minutes',
    @freq_type = 4,
    @freq_interval = 1,
    @freq_subday_type = 4,
    @freq_subday_interval = 2,
    @active_start_time = 0;
GO

EXEC msdb.dbo.sp_add_jobserver
    @job_name = N'Test - Intermittent Fail',
    @server_name = N'(LOCAL)';
GO

PRINT '====================================================================';
PRINT 'Server 1 (PROD) seed completed successfully.';
PRINT 'Created: TestDB (7 tables, 3 views, 4 procs, 2 functions, 1 trigger)';
PRINT 'Created: 6 SQL Agent jobs (4 standard + 2 intentionally failing)';
PRINT '====================================================================';
GO
