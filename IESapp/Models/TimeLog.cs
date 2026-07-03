namespace IESapp.Models;

public class TimeLog
{
    // The row index in google sheets (useful for updating 'Time-out' later)
    public int RowIndex { get; set; }

    public string EmployeeId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    public string TimeIn { get; set; } = string.Empty;
    public string TimeOut { get; set; } = string.Empty;

    public string Status { get; set; } = "Not Started";
    public string TotalHrs { get; set; } = string.Empty;

    /// <summary>Date of this log entry — MM/dd/yyyy (column G in sheet).</summary>
    public string Date { get; set; } = string.Empty;
}
