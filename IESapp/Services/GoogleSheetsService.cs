using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using System.Reflection;

namespace IESapp.Services;

public class GoogleSheetsService
{
    static readonly string[] Scopes = { SheetsService.Scope.Spreadsheets };
    private readonly string InitialApplicationName = "IES Employee Time Tracker";

    // THE SPREADSHEET ID (From the user's sample database structure we know we need this later)
    // We will hardcode this or fetch it from appsettings later
    public string SpreadsheetId { get; set; } = "";

    private SheetsService _sheetsService;

    public GoogleSheetsService()
    {
        InitializeService();
    }

    private void InitializeService()
    {
        try
        {
            var assembly = IntrospectionExtensions.GetTypeInfo(typeof(GoogleSheetsService)).Assembly;
            var resourceName = "IESapp.iesemployeetimetrackerapp.json";

            GoogleCredential credential;
#pragma warning disable CS0618 // Type or member is obsolete
            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                    throw new FileNotFoundException("Could not find the Google Service Account JSON file as an embedded resource.");

                credential = GoogleCredential.FromStream(stream).CreateScoped(Scopes);
            }
#pragma warning restore CS0618 // Type or member is obsolete

            _sheetsService = new SheetsService(new BaseClientService.Initializer()
            {
                HttpClientInitializer = credential,
                ApplicationName = InitialApplicationName,
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error initializing Google Sheets Service: {ex.Message}");
            throw;
        }
    }

    public SheetsService GetService() => _sheetsService;

    // --- SPREADSHEET OPERATIONS ---

    public async Task<IList<IList<object>>> ReadSheetAsync(string range)
    {
        var request = _sheetsService.Spreadsheets.Values.Get(SpreadsheetId, range);
        var response = await request.ExecuteAsync();
        return response.Values;
    }

    public async Task AppendRowAsync(string range, IList<object> rowData)
    {
        var valueRange = new ValueRange { Values = new List<IList<object>> { rowData } };
        var request = _sheetsService.Spreadsheets.Values.Append(valueRange, SpreadsheetId, range);
        request.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.USERENTERED;
        await request.ExecuteAsync();
    }

    // --- EMPLOYEE INFO ---
    // Sheet columns: A=ID, B=Name, C=Birthday, D=Age, E=Sex, F=Address, G=Job Position, H=Daily Wage

    public async Task<List<Models.Employee>> GetEmployeesAsync()
    {
        var values = await ReadSheetAsync("EmployeeInfo!A2:I");
        var employees = new List<Models.Employee>();

        if (values != null)
        {
            foreach (var row in values)
            {
                if (row.Count >= 1 && !string.IsNullOrEmpty(row[0]?.ToString()))
                {
                    DateOnly birthday = DateOnly.MinValue;
                    if (row.Count > 3)
                    {
                        var bStr = row[3]?.ToString() ?? "";
                        // Try strict format first, then fall back to general parse
                        if (!DateOnly.TryParseExact(bStr, "MM/dd/yyyy",
                                System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.None, out birthday))
                            DateOnly.TryParse(bStr, out birthday);
                    }

                    employees.Add(new Models.Employee
                    {
                        Id          = row[0]?.ToString() ?? "",
                        IesId       = row.Count > 1 ? row[1]?.ToString() ?? "" : "",
                        Name        = row.Count > 2 ? row[2]?.ToString() ?? "" : "",
                        Birthday    = birthday,
                        // Age is computed from Birthday — not stored in sheet
                        Sex         = row.Count > 5 ? row[5]?.ToString() ?? "" : "",
                        Address     = row.Count > 6 ? row[6]?.ToString() ?? "" : "",
                        JobPosition = row.Count > 7 ? row[7]?.ToString() ?? "" : "",
                        DailyWage   = row.Count > 8 && decimal.TryParse(row[8]?.ToString(), out decimal dw) ? dw : 0
                    });
                }
            }
        }
        return employees;
    }

    public async Task AddEmployeeAsync(Models.Employee employee, Services.SupabaseService? supabase = null)
    {
        // If Supabase is available, generate the unique ID there first
        if (supabase != null && string.IsNullOrEmpty(employee.Id))
            employee.Id = await supabase.GenerateUniqueEmployeeIdAsync();
        else if (string.IsNullOrEmpty(employee.Id))
            employee.Id = Models.Employee.GenerateId();

        var rowData = new List<object>
        {
            employee.Id,
            employee.IesId,   // B — new IES_ID column
            employee.Name,
            employee.Birthday.ToString("MM/dd/yyyy", System.Globalization.CultureInfo.InvariantCulture),
            employee.Age,   // computed from Birthday
            employee.Sex,
            employee.Address,
            employee.JobPosition,
            employee.DailyWage
        };
        await AppendRowAsync("EmployeeInfo!A:I", rowData);

        // Mirror full record to Supabase employees table
        if (supabase != null)
            await supabase.AddEmployeeToSupabaseAsync(employee);
    }

    public async Task UpdateEmployeeAsync(Models.Employee employee, Services.SupabaseService? supabase = null)
    {
        // Find the row index by ID
        var values = await ReadSheetAsync("EmployeeInfo!A2:A");
        if (values == null) return;

        int rowIndex = -1;
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i].Count > 0 && values[i][0]?.ToString() == employee.Id)
            {
                rowIndex = i + 2; // +1 for header row, +1 for 1-based index
                break;
            }
        }
        if (rowIndex < 0) return;

        var range = $"EmployeeInfo!A{rowIndex}:I{rowIndex}";
        var rowData = new List<object>
        {
            employee.Id,
            employee.IesId,   // B — new IES_ID column
            employee.Name,
            employee.Birthday.ToString("MM/dd/yyyy", System.Globalization.CultureInfo.InvariantCulture),
            employee.Age,
            employee.Sex,
            employee.Address,
            employee.JobPosition,
            employee.DailyWage
        };

        var vr = new ValueRange { Values = new List<IList<object>> { rowData } };
        var req = _sheetsService.Spreadsheets.Values.Update(vr, SpreadsheetId, range);
        req.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
        await req.ExecuteAsync();

        // Mirror update to Supabase
        if (supabase != null)
            await supabase.UpdateEmployeeInSupabaseAsync(employee);
    }

    // --- DAILY TIME LOG INFO ---
    // Flat table columns: A=ID, B=Name, C=Time-in, D=Time-out, E=Status, F=Total Hrs, G=Date

    public async Task<List<Models.TimeLog>> GetTimeLogsAsync(string? filterDate = null)
    {
        var values = await ReadSheetAsync("Daily_TimeLog!A:G");
        var logs = new List<Models.TimeLog>();
        if (values == null) return logs;

        var targetDate = filterDate ?? DateTime.Now.ToString("MM/dd/yyyy");

        for (int i = 0; i < values.Count; i++)
        {
            var row = values[i];
            if (row.Count == 0) continue;

            var cellA = row[0]?.ToString() ?? "";

            // Skip the header row
            if (cellA == "ID" || cellA == "Employee ID") continue;

            // Column G is the Date — only return rows matching target date
            var rowDate = row.Count > 6 ? row[6]?.ToString() ?? "" : "";
            if (rowDate != targetDate) continue;

            logs.Add(new Models.TimeLog
            {
                RowIndex = i + 1,
                EmployeeId = cellA,
                Name = row.Count > 1 ? row[1]?.ToString() ?? "" : "",
                TimeIn = row.Count > 2 ? row[2]?.ToString() ?? "" : "",
                TimeOut = row.Count > 3 ? row[3]?.ToString() ?? "" : "",
                Status = row.Count > 4 ? row[4]?.ToString() ?? "Not Started" : "Not Started",
                TotalHrs = row.Count > 5 ? row[5]?.ToString() ?? "" : "",
                Date = rowDate
            });
        }
        return logs;
    }

    public async Task AddTimeInAsync(Models.TimeLog log)
    {
        var today = DateTime.Now.ToString("MM/dd/yyyy");
        var rowData = new List<object>
        {
            log.EmployeeId,
            log.Name,
            log.TimeIn,
            "",        // TimeOut — empty on time-in
            log.Status,
            "",        // TotalHrs — empty on time-in
            today      // Date column G
        };
        await AppendRowAsync("Daily_TimeLog!A:G", rowData);
    }

    public async Task UpdateTimeOutAsync(Models.TimeLog log)
    {
        // Update columns D:G on the specific row (TimeOut, Status, TotalHrs, Date stays unchanged)
        var range = $"Daily_TimeLog!D{log.RowIndex}:F{log.RowIndex}";
        var rowData = new List<object>
        {
            log.TimeOut,
            log.Status,
            log.TotalHrs
        };

        var valueRange = new ValueRange { Values = new List<IList<object>> { rowData } };
        var request = _sheetsService.Spreadsheets.Values.Update(valueRange, SpreadsheetId, range);
        request.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
        await request.ExecuteAsync();
    }

    // --- SHEET INITIALIZATION ---

    public async Task InitializeSheetsAsync(string sessionId = "", IESapp.Services.SupabaseService? supabase = null)
    {
        try
        {
            // 4. Log today's session ID in AppInfo
            if (!string.IsNullOrEmpty(sessionId))
                await LogSessionIdAsync(sessionId);

            // 5. Auto-timeout employees still On Duty from previous days (dual-write)
            await CheckAndAutoTimeOutPreviousDayAsync(supabase);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Sheet initialization warning: {ex.Message}");
        }
    }


    private async Task EnsureSheetHeaderAsync(string sheetName, string range, List<object> headers)
    {
        try
        {
            var existing = await ReadSheetAsync($"{sheetName}!{range}");
            if (existing == null || existing.Count == 0 || existing[0].Count == 0)
            {
                var vr = new ValueRange { Values = new List<IList<object>> { headers } };
                var req = _sheetsService.Spreadsheets.Values.Update(vr, SpreadsheetId, $"{sheetName}!{range}");
                req.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
                await req.ExecuteAsync();
            }
        }
        catch { /* sheet may not exist yet — silently skip */ }
    }

    private async Task LogSessionIdAsync(string sessionId)
    {
        try
        {
            var today = DateTime.Now.ToString("MM/dd/yyyy");
            var all = await ReadSheetAsync("AppInfo!A:D");
            // Check if today's row already has this session
            bool exists = all != null && all.Any(r =>
                r.Count >= 3 && r[0]?.ToString() == today && r[2]?.ToString() == sessionId);
            if (!exists)
                await AppendRowAsync("AppInfo!A:D", new List<object> { today, "", sessionId, "" });
        }
        catch { }
    }

    // --- ACCOUNT / AUTH ---

    public async Task<List<(string username, string password)>> GetAccountsAsync()
    {
        var values = await ReadSheetAsync("Account!A2:B");
        var accounts = new List<(string, string)>();
        if (values == null) return accounts;
        foreach (var row in values)
        {
            if (row.Count >= 2)
                accounts.Add((row[0]?.ToString() ?? "", row[1]?.ToString() ?? ""));
        }
        return accounts;
    }

    // --- ERROR LOGGING ---

    public async Task AppendErrorLogAsync(string sessionId, string error)
    {
        try
        {
            var currentTime = TimeOnly.FromDateTime(DateTime.Now);
            var today = DateTime.Now.ToString("MM/dd/yyyy");
            // Find the row for today + sessionId in AppInfo
            var all = await ReadSheetAsync("AppInfo!A:D");
            if (all == null) return;

            //all.RemoveAt(0); //Remove Header
            for (int i = 0; i < all.Count; i++)
            {
                var row = all[i];
                if (row.Count >= 3 && row[0]?.ToString() == today && row[2]?.ToString() == sessionId)
                {
                    int sheetRow = i + 1;
                    var existing = row.Count >= 4 ? row[3]?.ToString() ?? "" : "";
                    var updated = string.IsNullOrEmpty(existing) ? $"{currentTime.ToString("hh:mm tt")} - {error}" : $"{existing}\n---\n{currentTime.ToString("hh:mm tt")} - {error}";
                    var vr = new ValueRange { Values = new List<IList<object>> { new List<object> { updated } } };
                    var req = _sheetsService.Spreadsheets.Values.Update(vr, SpreadsheetId, $"AppInfo!D{sheetRow}");
                    req.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
                    await req.ExecuteAsync();
                    return;
                }
            }
        }
        catch { }
    }

    // --- PREVIOUS-DAY AUTO TIMEOUT ---

    public async Task CheckAndAutoTimeOutPreviousDayAsync(IESapp.Services.SupabaseService? supabase = null)
    {
        try
        {
            var today = DateTime.Now.ToString("MM/dd/yyyy");
            var yesterday = DateTime.Now.AddDays(-1).ToString("MM/dd/yyyy");

            // Check AppInfo — if Previous-Date OnDuty Checked? for today, skip
            var appInfo = await ReadSheetAsync("AppInfo!A:D");
            if (appInfo != null)
            {
                foreach (var row in appInfo)
                {
                    if (row.Count >= 2 &&
                        row[0]?.ToString() == today &&
                        row[1]?.ToString()?.Equals("TRUE", StringComparison.OrdinalIgnoreCase) == true)
                        return; // already processed
                }
            }

            // Scan Daily_TimeLog flat table for yesterday On Duty employees (column G = Date)
            var allLogs = await ReadSheetAsync("Daily_TimeLog!A:G");
            if (allLogs == null) return;

            for (int i = 0; i < allLogs.Count; i++)
            {
                var row = allLogs[i];
                if (row.Count < 7) continue;

                var cellA = row[0]?.ToString() ?? "";
                if (cellA == "ID" || cellA == "Employee ID") continue; // skip header

                var rowDate = row[6]?.ToString() ?? "";
                if (rowDate != yesterday) continue;

                var status = row.Count > 4 ? row[4]?.ToString() ?? "" : "";
                if (status != "On Duty") continue;

                // Auto time-out at 23:59
                const string autoOut = "23:59";
                var timeIn = row.Count > 2 ? row[2]?.ToString() ?? "" : "";
                var totalHrs = "";
                if (TimeSpan.TryParse(timeIn, out var tIn))
                {
                    var diff = TimeSpan.Parse(autoOut) - tIn;
                    if (diff.TotalHours < 0) diff = diff.Add(TimeSpan.FromHours(24));
                    totalHrs = diff.ToString(@"hh\:mm");
                }

                var log = new Models.TimeLog
                {
                    RowIndex   = i + 1,
                    EmployeeId = cellA,
                    Name       = row.Count > 1 ? row[1]?.ToString() ?? "" : "",
                    TimeIn     = timeIn,
                    TimeOut    = autoOut,
                    Status     = "Shift Done",
                    TotalHrs   = totalHrs,
                    Date       = yesterday
                };
                // 1. Update Google Sheets
                await UpdateTimeOutAsync(log);
                // 2. Mirror to Supabase
                if (supabase != null)
                    await supabase.UpdateTimeOutInSupabaseAsync(log);
            }

            // Mark Previous-Date OnDuty Checked? today as checked in AppInfo
            bool marked = false;
            if (appInfo != null)
            {
                for (int i = 0; i < appInfo.Count; i++)
                {
                    if (appInfo[i].Count >= 1 && appInfo[i][0]?.ToString() == today)
                    {
                        var vr = new ValueRange { Values = new List<IList<object>> { new List<object> { "TRUE" } } };
                        var req = _sheetsService.Spreadsheets.Values.Update(vr, SpreadsheetId, $"AppInfo!B{i + 1}");
                        req.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
                        await req.ExecuteAsync();
                        marked = true;
                        break;
                    }
                }
            }
            if (!marked)
                await AppendRowAsync("AppInfo!A:B", new List<object> { today, "TRUE" });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Auto-timeout check failed: {ex.Message}");
        }
    }

    // --- DELETE EMPLOYEE ---

    public async Task DeleteEmployeeAsync(string employeeId)
    {
        var values = await ReadSheetAsync("EmployeeInfo!A2:A");
        if (values == null) return;

        int rowIndex = -1;
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i].Count > 0 && values[i][0]?.ToString() == employeeId)
            {
                rowIndex = i + 2;
                break;
            }
        }
        if (rowIndex < 0) return;

        var spreadsheet = await _sheetsService.Spreadsheets.Get(SpreadsheetId).ExecuteAsync();
        var sheet = spreadsheet.Sheets.FirstOrDefault(s => s.Properties.Title == "EmployeeInfo");
        if (sheet == null) return;

        int sheetId = (int)(sheet.Properties.SheetId ?? 0);

        var deleteRequest = new Request
        {
            DeleteDimension = new DeleteDimensionRequest
            {
                Range = new DimensionRange
                {
                    SheetId = sheetId,
                    Dimension = "ROWS",
                    StartIndex = rowIndex - 1,
                    EndIndex = rowIndex
                }
            }
        };

        var batchUpdate = new BatchUpdateSpreadsheetRequest { Requests = new List<Request> { deleteRequest } };
        await _sheetsService.Spreadsheets.BatchUpdate(batchUpdate, SpreadsheetId).ExecuteAsync();
    }
}

