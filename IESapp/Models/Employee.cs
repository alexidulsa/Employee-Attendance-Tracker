namespace IESapp.Models;

public class Employee
{
    public string Id { get; set; } = string.Empty;
    public string IesId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateOnly Birthday { get; set; }
    public string Sex { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string JobPosition { get; set; } = string.Empty;
    public decimal DailyWage { get; set; }

    /// <summary>Age calculated from Birthday to today.</summary>
    public int Age
    {
        get
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            int age = today.Year - Birthday.Year;
            if (Birthday.AddYears(age) > today) age--;
            return age;
        }
    }

    public static string GenerateId()
    {
        return Guid.NewGuid().ToString("N").Substring(0, 4).ToUpper();
    }
}
