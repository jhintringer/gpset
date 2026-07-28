using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace GPSet;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    public const string CenterSimulationAction =
        "dev.gpset.action.CENTER_SIMULATION";

    private static bool _pendingCenterSimulationRequest;

    public static event EventHandler? CenterSimulationRequested;

    public static bool ConsumeCenterSimulationRequest()
    {
        if (!_pendingCenterSimulationRequest)
            return false;

        _pendingCenterSimulationRequest = false;
        return true;
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        HandleIntent(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        if (intent is null)
            return;

        HandleIntent(intent);
    }

    private static void HandleIntent(Intent? intent)
    {
        if (intent?.Action != CenterSimulationAction)
            return;

        intent.SetAction(null);
        _pendingCenterSimulationRequest = true;
        CenterSimulationRequested?.Invoke(null, EventArgs.Empty);
    }
}
