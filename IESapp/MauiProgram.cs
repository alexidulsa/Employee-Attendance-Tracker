using Microsoft.Extensions.Logging;

namespace IESapp
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                });

            builder.Services.AddMauiBlazorWebView();
            builder.Services.AddSingleton<IESapp.Services.GoogleSheetsService>();
            builder.Services.AddSingleton<IESapp.Services.SupabaseService>();
            builder.Services.AddScoped<IESapp.Services.AppState>();

#if DEBUG
    		builder.Services.AddBlazorWebViewDeveloperTools();
    		builder.Logging.AddDebug();
#endif

            var app = builder.Build();

            // Initialize Supabase (fire-and-forget)
            var supabaseService = app.Services.GetRequiredService<IESapp.Services.SupabaseService>();
            _ = supabaseService.InitializeAsync();

            // Initialize Google Sheets headers and daily log section on startup
            var sheetsService = app.Services.GetRequiredService<IESapp.Services.GoogleSheetsService>();
            using var scope = app.Services.CreateScope();
            var appState = scope.ServiceProvider.GetRequiredService<IESapp.Services.AppState>();
            _ = sheetsService.InitializeSheetsAsync(appState.SessionId, supabaseService); // fire-and-forget, non-blocking

            return app;
        }
    }
}
