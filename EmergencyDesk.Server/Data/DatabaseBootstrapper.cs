using System.Data;
using Microsoft.EntityFrameworkCore;

namespace EmergencyDesk.Server.Data;

/// <summary>Creates a missing database/schema and adds columns introduced by later server versions.</summary>
public static class DatabaseBootstrapper
{
    private static readonly (string Table, string Column, string Definition)[] Columns =
    [
        ("Incidents", "PeopleCount", "int NOT NULL DEFAULT 1"),
        ("Incidents", "Source", "varchar(40) NULL"),
        ("Incidents", "AccuracyMeters", "double NULL"),
        ("Incidents", "LocationUpdatedAt", "datetime(6) NULL"),
        ("Incidents", "AcknowledgedAt", "datetime(6) NULL"),
        ("Incidents", "AcknowledgedBy", "varchar(100) NULL"),
        ("Incidents", "DispatchedAt", "datetime(6) NULL"),
        ("Incidents", "ArrivedAt", "datetime(6) NULL"),
        ("Incidents", "ResolvedAt", "datetime(6) NULL"),
        ("Incidents", "ReceivedAt", "datetime(6) NULL"),
        ("Incidents", "DeviceId", "varchar(64) NULL"),
        ("Responders", "Address", "varchar(300) NULL"),
        ("Responders", "Latitude", "double NULL"),
        ("Responders", "Longitude", "double NULL")
    ];

    public static void EnsureLatestSchema(AppDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone) connection.Open();

        try
        {
            foreach (var (table, column, definition) in Columns)
            {
                using var check = connection.CreateCommand();
                check.CommandText = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS " +
                                    "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @tableName AND COLUMN_NAME = @columnName";
                var tableParameter = check.CreateParameter();
                tableParameter.ParameterName = "@tableName";
                tableParameter.Value = table;
                check.Parameters.Add(tableParameter);
                var columnParameter = check.CreateParameter();
                columnParameter.ParameterName = "@columnName";
                columnParameter.Value = column;
                check.Parameters.Add(columnParameter);

                if (Convert.ToInt32(check.ExecuteScalar()) > 0) continue;

                using var alter = connection.CreateCommand();
                alter.CommandText = $"ALTER TABLE `{table}` ADD COLUMN `{column}` {definition}";
                alter.ExecuteNonQuery();
            }
        }
        finally
        {
            if (closeWhenDone) connection.Close();
        }
    }
}
