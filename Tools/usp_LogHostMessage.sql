USE [FortnaFire]
GO

IF OBJECT_ID('[dbo].[usp_LogHostMessage]', 'P') IS NOT NULL
    DROP PROCEDURE [dbo].[usp_LogHostMessage];
GO

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE PROCEDURE [dbo].[usp_LogHostMessage]
    @deviceId NVARCHAR(100),
    @barcode NVARCHAR(50),
    @messageType NVARCHAR(10),
    @payload NVARCHAR(MAX),
    @data NVARCHAR(MAX) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    -- Insert into an audit table for host communications
    -- Note: Ensure the 'HostCommLog' table exists or create it here if needed
    IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'HostCommLog')
    BEGIN
        CREATE TABLE [dbo].[HostCommLog] (
            [Id] BIGINT IDENTITY(1,1) PRIMARY KEY,
            [DeviceId] NVARCHAR(100),
            [Barcode] NVARCHAR(50),
            [MessageType] NVARCHAR(10),
            [Payload] NVARCHAR(MAX),
            [Data] NVARCHAR(MAX),
            [LoggedTime] DATETIME DEFAULT GETDATE()
        );
    END

    INSERT INTO [dbo].[HostCommLog] ([DeviceId], [Barcode], [MessageType], [Payload], [Data])
    VALUES (@deviceId, @barcode, @messageType, @payload, @data);

    -- Also log to SystemEvents for high-level monitoring
    INSERT INTO [dbo].[SystemEvents] 
        ([barcode], [Severity], [Source], [EventType], [Message], [Payload])
    VALUES 
        (@barcode, 'Info', @deviceId, @messageType, 
         CONCAT('Host message ', @messageType, ' received for barcode ', @barcode), 
         @payload);
END
GO
