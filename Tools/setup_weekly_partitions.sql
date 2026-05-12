/*
  SQL Server Script to setup Weekly Partitions for FortnaFire tables.
  Tables: Conveyable, ConveyableDestination, MessageQueue, SystemEvents
*/

USE [FortnaFire]
GO

-- 1. Create the Partition Function
-- This function defines the boundaries for the partitions based on a weekly schedule.
IF NOT EXISTS (SELECT * FROM sys.partition_functions WHERE name = 'pf_WeeklyCreatedTime')
BEGIN
    -- We start with some initial boundaries around the current date.
    CREATE PARTITION FUNCTION [pf_WeeklyCreatedTime] (DATETIME)
    AS RANGE RIGHT FOR VALUES (
        '2026-05-04 00:00:00', -- Last Monday
        '2026-05-11 00:00:00', -- Next Monday
        '2026-05-18 00:00:00', 
        '2026-05-25 00:00:00'
    );
    PRINT 'Partition Function [pf_WeeklyCreatedTime] created.';
END
GO

-- 2. Create the Partition Scheme
-- This scheme maps partitions to filegroups (all to PRIMARY in this simple example).
IF NOT EXISTS (SELECT * FROM sys.partition_schemes WHERE name = 'ps_WeeklyCreatedTime')
BEGIN
    CREATE PARTITION SCHEME [ps_WeeklyCreatedTime]
    AS PARTITION [pf_WeeklyCreatedTime]
    ALL TO ([PRIMARY]);
    PRINT 'Partition Scheme [ps_WeeklyCreatedTime] created.';
END
GO

/*
  HOW TO APPLY TO EXISTING TABLES:
  
  To partition an existing table, you must recreate its primary key (or clustered index) 
  on the partition scheme. The partitioning column (e.g., CreatedTime) MUST be part of the clustered index.

  EXAMPLE for MessageQueue:
  
  -- 1. Drop existing PK (if it is clustered)
  ALTER TABLE [dbo].[MessageQueue] DROP CONSTRAINT [PK_MessageQueue];
  
  -- 2. Recreate PK as non-clustered (optional, depends on needs)
  ALTER TABLE [dbo].[MessageQueue] ADD CONSTRAINT [PK_MessageQueue] PRIMARY KEY NONCLUSTERED ([Id]);
  
  -- 3. Create a clustered index on the partition scheme
  CREATE CLUSTERED INDEX [IX_MessageQueue_Partitioning] ON [dbo].[MessageQueue] ([CreatedTime])
  ON [ps_WeeklyCreatedTime]([CreatedTime]);

*/

PRINT 'Partition infrastructure setup complete.';
