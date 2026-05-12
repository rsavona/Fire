USE [FortnaFire]
GO

IF OBJECT_ID('[dbo].[usp_QueueMessage]', 'P') IS NOT NULL
    DROP PROCEDURE [dbo].[usp_QueueMessage];
GO

CREATE PROCEDURE [dbo].[usp_QueueMessage]
    @Direction NVARCHAR(10),
    @Type NVARCHAR(50),
    @RawData NVARCHAR(MAX),
    @Status INT,
    @StatusDescription NVARCHAR(MAX),
    @DeviceName NVARCHAR(100)
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO MessageQueue (Direction, Type, RawData, Status, StatusDescription, DeviceName, CreatedTime)
    VALUES (@Direction, @Type, @RawData, @Status, @StatusDescription, @DeviceName, GETDATE());
END
GO
