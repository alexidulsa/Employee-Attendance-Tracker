using IESapp.Models;
using Supabase;
using Supabase.Gotrue;
using Supabase.Postgrest.Exceptions;

namespace IESapp.Services;

/// <summary>
/// Manages all interaction with the Supabase backend.
/// Tables: employees (employee registry), time_logs (daily time entries).
/// </summary>
public class SupabaseService
{
    private const string ProjectUrl = "";
    private const string AnonKey = "";
    private const string RedirectUri = "iesapp://login-callback";

    private Supabase.Client? _client;
    private bool _initialized = false;

    // Holds the authenticated Supabase user after sign-in
    public Supabase.Gotrue.User? CurrentUser => _client?.Auth.CurrentUser;

    // Used to hand the callback URI back to SignInWithGoogleAsync
    private static TaskCompletionSource<Uri>? _oauthCallbackTcs;

    // ─── Initialization ────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        try
        {
            var options = new SupabaseOptions { AutoRefreshToken = true, AutoConnectRealtime = false };
            _client = new Supabase.Client(ProjectUrl, AnonKey, options);
            await _client.InitializeAsync();
            _initialized = true;
            Console.WriteLine("[Supabase] Connected successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Supabase] Connection failed: {ex.Message}");
        }
    }

    // ─── Google OAuth ───────────────────────────────────────────────────────

    /// <summary>
    /// Called by App.xaml.cs (Windows platform) when the OS routes the
    /// iesapp://login-callback URI back to this app after Google sign-in.
    /// </summary>
    public static void HandleOAuthCallback(Uri callbackUri)
    {
        Console.WriteLine($"[Supabase] OAuth callback received: {callbackUri}");
        _oauthCallbackTcs?.TrySetResult(callbackUri);
    }

    /// <summary>
    /// Opens the system browser for Google sign-in via Supabase Auth.
    /// Waits for the iesapp://login-callback redirect and exchanges it for a session.
    /// Returns the email of the signed-in user, or null on failure/timeout.
    /// </summary>
    public async Task<string?> SignInWithGoogleAsync()
    {
        if (!IsReady()) return null;

        try
        {
            // SignIn(Provider) returns a ProviderAuthState whose .Uri is the OAuth URL
            var authState = await _client!.Auth.SignIn(
                Supabase.Gotrue.Constants.Provider.Google,
                new Supabase.Gotrue.SignInOptions { RedirectTo = RedirectUri });

            var oauthUrl = authState?.Uri?.AbsoluteUri;
            if (string.IsNullOrEmpty(oauthUrl))
            {
                Console.WriteLine("[Supabase] Failed to get OAuth URL.");
                return null;
            }

            // Set up the callback listener before opening browser
            _oauthCallbackTcs = new TaskCompletionSource<Uri>();

            // Open the sign-in URL in the default browser
            await Microsoft.Maui.ApplicationModel.Launcher.OpenAsync(new Uri(oauthUrl));

            // Wait up to 1 minutes for the user to complete sign-in
            var timeoutTask = Task.Delay(TimeSpan.FromMinutes(1));
            var completedTask = await Task.WhenAny(_oauthCallbackTcs.Task, timeoutTask);

            if (completedTask == timeoutTask)
            {
                Console.WriteLine("[Supabase] OAuth timed out.");
                _oauthCallbackTcs = null;
                return null;
            }

            var callbackUri = await _oauthCallbackTcs.Task;
            _oauthCallbackTcs = null;

            // Parse the fragment — Supabase embeds tokens in the URI fragment after #
            // e.g. iesapp://login-callback#access_token=...&refresh_token=...
            var fragment = callbackUri.Fragment.TrimStart('#');
            var query = System.Web.HttpUtility.ParseQueryString(fragment);

            var accessToken = query["access_token"];
            var refreshToken = query["refresh_token"];

            if (string.IsNullOrEmpty(accessToken))
            {
                Console.WriteLine("[Supabase] No access_token in callback.");
                return null;
            }

            // Set the session on the Supabase client
            await _client!.Auth.SetSession(accessToken, refreshToken ?? "");
            var user = _client.Auth.CurrentUser;
            Console.WriteLine($"[Supabase] Signed in as: {user?.Email}");
            return user?.Email;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Supabase] Google sign-in error: {ex.Message}");
            _oauthCallbackTcs = null;
            return null;
        }
    }

    private bool IsReady()
    {
        if (!_initialized || _client == null)
        {
            Console.WriteLine("[Supabase] Not initialized — skipping operation.");
            return false;
        }
        return true;
    }

    // ─── Employee ID Generation (uniqueness guaranteed by Supabase UNIQUE) ─

    /// <summary>
    /// Generates a unique 4-char hex EmployeeID by attempting INSERT into the
    /// Supabase employees table. Retries if the ID already exists.
    /// </summary>
    public async Task<string> GenerateUniqueEmployeeIdAsync()
    {
        if (!IsReady())
            return Guid.NewGuid().ToString("N")[..4].ToUpper(); // fallback if offline

        const int maxAttempts = 10;
        for (int i = 0; i < maxAttempts; i++)
        {
            var candidate = Guid.NewGuid().ToString("N")[..4].ToUpper();
            try
            {
                // Attempt a partial insert — just the employee_id to claim uniqueness
                // Full record is inserted via AddEmployeeToSupabaseAsync after confirmation
                var probe = new SupabaseEmployee { EmployeeId = candidate };
                await _client!.From<SupabaseEmployee>().Insert(probe);
                return candidate; // Insert succeeded → ID is unique and claimed
            }
            catch (PostgrestException pex) when (pex.Message?.Contains("duplicate") == true ||
                                                   pex.Message?.Contains("unique") == true ||
                                                   pex.Message?.Contains("23505") == true)
            {
                // Unique violation — try another ID
                Console.WriteLine($"[Supabase] ID collision on {candidate}, retrying...");
            }
        }
        throw new InvalidOperationException("Could not generate a unique EmployeeID after 10 attempts.");
    }

    // ─── Employee CRUD ──────────────────────────────────────────────────────

    /// <summary>
    /// Updates the full employee record in Supabase after ID has been claimed.
    /// (The row was inserted with just employee_id by GenerateUniqueEmployeeIdAsync;
    ///  this call fills in all remaining columns.)
    /// </summary>
    public async Task AddEmployeeToSupabaseAsync(Employee emp)
    {
        if (!IsReady()) return;
        try
        {
            var record = SupabaseEmployee.FromEmployee(emp);
            // Update the row that was already inserted (with just the ID) to fill all columns
            await _client!.From<SupabaseEmployee>()
                .Where(x => x.EmployeeId == emp.Id)
                .Set(x => x.IesId, emp.IesId)
                .Set(x => x.Name, emp.Name)
                .Set(x => x.Birthday, emp.Birthday == DateOnly.MinValue ? null : emp.Birthday.ToString("yyyy-MM-dd"))
                .Set(x => x.Age, emp.Age)
                .Set(x => x.Sex, emp.Sex)
                .Set(x => x.Address, emp.Address)
                .Set(x => x.JobPosition, emp.JobPosition)
                .Set(x => x.DailyWage, emp.DailyWage)
                .Update();
            Console.WriteLine($"[Supabase] Employee {emp.Id} record populated.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Supabase] AddEmployee failed: {ex.Message}");
        }
    }

    /// <summary>Updates all mutable fields of an existing employee in Supabase.</summary>
    public async Task UpdateEmployeeInSupabaseAsync(Employee emp)
    {
        if (!IsReady()) return;
        try
        {
            await _client!.From<SupabaseEmployee>()
                .Where(x => x.EmployeeId == emp.Id)
                .Set(x => x.IesId, emp.IesId)
                .Set(x => x.Name, emp.Name)
                .Set(x => x.Birthday, emp.Birthday == DateOnly.MinValue ? null : emp.Birthday.ToString("yyyy-MM-dd"))
                .Set(x => x.Age, emp.Age)
                .Set(x => x.Sex, emp.Sex)
                .Set(x => x.Address, emp.Address)
                .Set(x => x.JobPosition, emp.JobPosition)
                .Set(x => x.DailyWage, emp.DailyWage)
                .Update();
            Console.WriteLine($"[Supabase] Employee {emp.Id} updated.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Supabase] UpdateEmployee failed: {ex.Message}");
        }
    }

    /// <summary>Deletes an employee from Supabase by employee_id.</summary>
    public async Task DeleteEmployeeFromSupabaseAsync(string employeeId)
    {
        if (!IsReady()) return;
        try
        {
            await _client!.From<SupabaseEmployee>()
                .Where(x => x.EmployeeId == employeeId)
                .Delete();
            Console.WriteLine($"[Supabase] Employee {employeeId} deleted.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Supabase] DeleteEmployee failed: {ex.Message}");
        }
    }

    // ─── Time Log CRUD ─────────────────────────────────────────────────────

    /// <summary>Inserts a new time-in entry into Supabase time_logs.</summary>
    public async Task LogTimeEntryAsync(TimeLog log)
    {
        if (!IsReady()) return;
        try
        {
            var entry = SupabaseTimeLog.FromTimeLog(log);
            await _client!.From<SupabaseTimeLog>().Insert(entry);
            Console.WriteLine($"[Supabase] Time-in logged for {log.EmployeeId}.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Supabase] LogTimeEntry failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Updates time_out and status for today's time-log entry for the given employee.
    /// Matches on employee_id + date (ISO format).
    /// </summary>
    public async Task UpdateTimeOutInSupabaseAsync(TimeLog log)
    {
        if (!IsReady()) return;
        try
        {
            // Convert date to ISO for Supabase DATE column
            string? isoDate = null;
            if (!string.IsNullOrEmpty(log.Date) &&
                System.Globalization.DateTimeFormatInfo.InvariantInfo != null)
            {
                if (DateOnly.TryParseExact(log.Date, "MM/dd/yyyy",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var d))
                    isoDate = d.ToString("yyyy-MM-dd");
            }

            await _client!.From<SupabaseTimeLog>()
                .Where(x => x.EmployeeId == log.EmployeeId)
                .Filter("date", Supabase.Postgrest.Constants.Operator.Equals, isoDate ?? "")
                .Set(x => x.TimeOut, log.TimeOut)
                .Set(x => x.Status, log.Status)
                .Update();
            Console.WriteLine($"[Supabase] Time-out updated for {log.EmployeeId}.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Supabase] UpdateTimeOut failed: {ex.Message}");
        }
    }

    // ─── Paycheck: Total Work Hours ────────────────────────────────────────

    /// <summary>
    /// Queries Supabase time_logs for a given employee over an inclusive date
    /// range and returns the summed work duration as (Hours, Minutes).
    /// </summary>
    public async Task<(int Hours, int Minutes)> GetTotalWorkHrsAsync(
        string employeeId, DateOnly dateFrom, DateOnly dateTo)
    {
        if (!IsReady()) return (0, 0);
        try
        {
            var fromIso = dateFrom.ToString("yyyy-MM-dd");
            var toIso   = dateTo.ToString("yyyy-MM-dd");

            var result = await _client!.From<SupabaseTimeLog>()
                .Filter("employee_id", Supabase.Postgrest.Constants.Operator.Equals,              employeeId)
                .Filter("date",        Supabase.Postgrest.Constants.Operator.GreaterThanOrEqual,  fromIso)
                .Filter("date",        Supabase.Postgrest.Constants.Operator.LessThanOrEqual,     toIso)
                .Get();

            int totalMinutes = 0;
            foreach (var row in result.Models)
            {
                if (string.IsNullOrEmpty(row.TimeIn) || string.IsNullOrEmpty(row.TimeOut)) continue;
                if (TimeSpan.TryParse(row.TimeIn,  out var tIn) &&
                    TimeSpan.TryParse(row.TimeOut, out var tOut))
                {
                    var diff = tOut - tIn;
                    if (diff.TotalMinutes > 0)
                        totalMinutes += (int)diff.TotalMinutes;
                }
            }

            return (totalMinutes / 60, totalMinutes % 60);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Supabase] GetTotalWorkHrs failed for {employeeId}: {ex.Message}");
            return (0, 0);
        }
    }
}
