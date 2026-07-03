using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;
using System.Globalization;

namespace IESapp.Models;

/// <summary>Maps to the Supabase "employees" table.</summary>
[Table("employees")]
public class SupabaseEmployee : BaseModel
{
    [PrimaryKey("id", false)]
    public long Id { get; set; }

    [Column("employee_id")]
    public string EmployeeId { get; set; } = string.Empty;

    [Column("ies_id")]
    public string IesId { get; set; } = string.Empty;

    [Column("name")]
    public string Name { get; set; } = string.Empty;

    [Column("birthday")]
    public string? Birthday { get; set; }   // stored as ISO date string "yyyy-MM-dd"

    [Column("age")]
    public int Age { get; set; }

    [Column("sex")]
    public string Sex { get; set; } = string.Empty;

    [Column("address")]
    public string Address { get; set; } = string.Empty;

    [Column("job_position")]
    public string JobPosition { get; set; } = string.Empty;

    [Column("daily_wage")]
    public decimal DailyWage { get; set; }

    // --- Conversion helpers ---

    public static SupabaseEmployee FromEmployee(Employee emp) => new()
    {
        EmployeeId = emp.Id,
        IesId = emp.IesId,
        Name = emp.Name,
        Birthday = emp.Birthday == DateOnly.MinValue ? null : emp.Birthday.ToString("yyyy-MM-dd"),
        Age = emp.Age,
        Sex = emp.Sex,
        Address = emp.Address,
        JobPosition = emp.JobPosition,
        DailyWage = emp.DailyWage
    };
}

/// <summary>Maps to the Supabase "time_logs" table.</summary>
[Table("time_logs")]
public class SupabaseTimeLog : BaseModel
{
    [PrimaryKey("id", false)]
    public long Id { get; set; }

    [Column("employee_id")]
    public string EmployeeId { get; set; } = string.Empty;

    [Column("name")]
    public string Name { get; set; } = string.Empty;

    [Column("time_in")]
    public string? TimeIn { get; set; }     // "HH:mm" — Supabase TIME type accepts this string

    [Column("time_out")]
    public string? TimeOut { get; set; }

    [Column("status")]
    public string Status { get; set; } = string.Empty;

    [Column("date")]
    public string? Date { get; set; }       // "yyyy-MM-dd" — Supabase DATE type

    // --- Conversion helpers ---

    public static SupabaseTimeLog FromTimeLog(TimeLog log)
    {
        // Parse date from MM/dd/yyyy and convert to yyyy-MM-dd for Supabase DATE column
        string? isoDate = null;
        if (!string.IsNullOrEmpty(log.Date) &&
            DateOnly.TryParseExact(log.Date, "MM/dd/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            isoDate = d.ToString("yyyy-MM-dd");

        return new SupabaseTimeLog
        {
            EmployeeId = log.EmployeeId,
            Name = log.Name,
            TimeIn = string.IsNullOrEmpty(log.TimeIn) ? null : log.TimeIn,
            TimeOut = string.IsNullOrEmpty(log.TimeOut) ? null : log.TimeOut,
            Status = log.Status,
            Date = isoDate
        };
    }
}
