-- Sample SQL Server Migration with multiple high/critical production hazards
-- Comments like DROP TABLE [dbo].[FakeCommentedTable] must NOT trigger false positives.

ALTER TABLE [dbo].[Orders]
    DROP COLUMN [LegacyTaxCode];
GO

ALTER TABLE [dbo].[Customers]
    ALTER COLUMN [Email] NVARCHAR(120) NOT NULL;
GO

CREATE NONCLUSTERED INDEX [IX_Orders_CreatedAt]
    ON [dbo].[Orders] ([CreatedAt]);
GO

EXEC sp_rename 'dbo.Invoices.TotalAmt', 'TotalAmount', 'COLUMN';
GO
