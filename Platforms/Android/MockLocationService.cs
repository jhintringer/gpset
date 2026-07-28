using Android.App;
using Android.App.Usage;
using Android.Content;
using Android.Content.PM;
using Android.Hardware;
using Android.Locations;
using Android.OS;
using Android.Provider;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using Application = Android.App.Application;
using OperatingSystem = System.OperatingSystem;

namespace GPSet.Platforms.Android;

[Service(
    Name = "dev.gpset.MockLocationService",
    Exported = false,
    ForegroundServiceType = ForegroundService.TypeLocation)]
public sealed class MockLocationService : Service
{
    private const string ChannelId = "gps-simulation";
    private const int NotificationId = 4107;
    private const int NotificationContentRequestCode = 4108;
    private const string LatitudeExtra = "latitude";
    private const string LongitudeExtra = "longitude";

    private static readonly string[] ProviderNames =
    {
        LocationManager.GpsProvider,
        LocationManager.NetworkProvider
    };

    private readonly List<string> _activeProviders = [];
    private LocationManager? _locationManager;
    private Timer? _timer;
    private double _latitude;
    private double _longitude;
    private int _consecutivePublishFailures;

    public static bool IsRunning { get; private set; }
    public static bool IsStarting { get; private set; }
    public static string? LastError { get; private set; }
    public static double? MockLatitude { get; private set; }
    public static double? MockLongitude { get; private set; }
    public static event EventHandler? StateChanged;

    public override IBinder? OnBind(Intent? intent) => null;

    public static bool CanMockLocations()
    {
        var context = Application.Context;
        var appOps = (AppOpsManager?)context.GetSystemService(Context.AppOpsService);
        return appOps?.CheckOpNoThrow("android:mock_location", Process.MyUid(),
            context.PackageName!) == AppOpsManagerMode.Allowed;
    }

    public static bool IsSystemLocationEnabled()
    {
        var manager = (LocationManager?)Application.Context
            .GetSystemService(Context.LocationService);
        return manager is not null &&
            (manager.IsProviderEnabled(LocationManager.GpsProvider) ||
             manager.IsProviderEnabled(LocationManager.NetworkProvider));
    }

    public static void OpenDeveloperOptions()
    {
        var intent = new Intent(Settings.ActionApplicationDevelopmentSettings)
            .AddFlags(ActivityFlags.NewTask);
        Application.Context.StartActivity(intent);
    }

    public static void OpenLocationSettings()
    {
        var intent = new Intent(Settings.ActionLocationSourceSettings)
            .AddFlags(ActivityFlags.NewTask);
        Application.Context.StartActivity(intent);
    }

    public static void Start(double latitude, double longitude)
    {
        if (!double.IsFinite(latitude) || latitude is < -90 or > 90 ||
            !double.IsFinite(longitude) || longitude is < -180 or > 180)
        {
            SetState(false, false, "The selected coordinates are invalid.");
            return;
        }

        MockLatitude = latitude;
        MockLongitude = longitude;
        SetState(false, true, null);
        try
        {
            var context = Application.Context;
            var intent = new Intent(context, typeof(MockLocationService));
            intent.PutExtra(LatitudeExtra, latitude);
            intent.PutExtra(LongitudeExtra, longitude);
            ContextCompat.StartForegroundService(context, intent);
        }
        catch (Exception exception)
        {
            global::Android.Util.Log.Error("GPSet", exception.ToString());
            SetState(false, false, $"Could not start simulation: {exception.Message}");
        }
    }

    public static void Stop()
    {
        var context = Application.Context;
        context.StopService(new Intent(context, typeof(MockLocationService)));
        NotificationManagerCompat.From(context)?.Cancel(NotificationId);
        SetState(false, false, null);
    }

    public override StartCommandResult OnStartCommand(
        Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent is null)
        {
            SetState(false, false, "Simulation start data was missing.");
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        _latitude = intent.GetDoubleExtra(LatitudeExtra, double.NaN);
        _longitude = intent.GetDoubleExtra(LongitudeExtra, double.NaN);
        StartForeground(NotificationId, CreateNotification());

        try
        {
            if (!CanMockLocations())
                throw new InvalidOperationException(
                    "GPSet is not selected as the mock location app.");
            if (!IsSystemLocationEnabled())
                throw new InvalidOperationException("Android Location is turned off.");

            BeginMocking();
            SetState(true, false, null);
        }
        catch (Exception exception)
        {
            global::Android.Util.Log.Error("GPSet", exception.ToString());
            SetState(false, false, $"Simulation failed: {exception.Message}");
            StopSelf();
        }

        return StartCommandResult.NotSticky;
    }

    private void BeginMocking()
    {
        _locationManager = (LocationManager?)GetSystemService(LocationService)
            ?? throw new InvalidOperationException("Android location service is unavailable.");

        RemoveActiveProviders();
        var errors = new List<string>();
        foreach (string provider in ProviderNames)
        {
            if (TryConfigureProvider(provider, out string? error))
                _activeProviders.Add(provider);
            else if (error is not null)
                errors.Add(error);
        }

        if (!_activeProviders.Contains(LocationManager.GpsProvider))
            throw new InvalidOperationException(
                $"The GPS test provider could not be enabled. {string.Join(" ", errors)}");

        if (!PublishLocation())
            throw new InvalidOperationException("Android rejected the first mock GPS location.");

        _timer?.Dispose();
        _timer = new Timer(_ => PublishOnTimer(), null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private bool TryConfigureProvider(string provider, out string? error)
    {
        error = null;
        try
        {
            try
            {
                _locationManager!.RemoveTestProvider(provider);
            }
            catch (Java.Lang.IllegalArgumentException)
            {
                // No test provider from this app exists yet.
            }

            if (OperatingSystem.IsAndroidVersionAtLeast(31))
            {
                var builder = new global::Android.Locations.Provider.ProviderProperties.Builder();
                builder.SetAccuracy((int)Accuracy.Fine);
                builder.SetPowerUsage((int)Power.Low);
                builder.SetHasAltitudeSupport(false);
                builder.SetHasSpeedSupport(false);
                builder.SetHasBearingSupport(false);
                var properties = builder.Build()
                    ?? throw new InvalidOperationException("Could not create provider properties.");
                _locationManager!.AddTestProvider(provider, properties);
            }
            else
            {
                _locationManager!.AddTestProvider(
                    provider,
                    requiresNetwork: false,
                    requiresSatellite: false,
                    requiresCell: false,
                    hasMonetaryCost: false,
                    supportsAltitude: false,
                    supportsSpeed: false,
                    supportsBearing: false,
                    powerRequirement: Power.Low,
                    accuracy: (SensorStatus)Accuracy.Fine);
            }

            _locationManager!.SetTestProviderEnabled(provider, true);
            return true;
        }
        catch (Exception exception)
        {
            error = $"{provider}: {exception.Message}";
            global::Android.Util.Log.Error(
                "GPSet", $"Could not configure {provider}: {exception}");
            return false;
        }
    }

    private void PublishOnTimer()
    {
        if (!IsRunning)
            return;

        if (PublishLocation())
        {
            _consecutivePublishFailures = 0;
            return;
        }

        if (++_consecutivePublishFailures < 3)
            return;

        SetState(false, false, "Android repeatedly rejected the mock GPS location.");
        StopSelf();
    }

    private bool PublishLocation()
    {
        if (_locationManager is null)
            return false;

        bool gpsPublished = false;
        foreach (string provider in _activeProviders.ToArray())
        {
            using var location = new global::Android.Locations.Location(provider)
            {
                Latitude = _latitude,
                Longitude = _longitude,
                Accuracy = 1f,
                Time = Java.Lang.JavaSystem.CurrentTimeMillis(),
                ElapsedRealtimeNanos = SystemClock.ElapsedRealtimeNanos()
            };

            try
            {
                _locationManager.SetTestProviderLocation(provider, location);
                if (provider == LocationManager.GpsProvider)
                    gpsPublished = true;
            }
            catch (Exception exception)
            {
                global::Android.Util.Log.Error(
                    "GPSet", $"Could not publish to {provider}: {exception}");
            }
        }

        return gpsPublished;
    }

    private Notification CreateNotification()
    {
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            var channel = new NotificationChannel(ChannelId, "GPS simulation",
                NotificationImportance.Low)
            {
                Description = "Shown while GPSet is publishing a mock GPS position."
            };
            manager.CreateNotificationChannel(channel);
        }

        var openApp = new Intent(this, typeof(MainActivity))
            .SetAction(MainActivity.CenterSimulationAction)
            .AddFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        var pendingIntent = PendingIntent.GetActivity(
            this, NotificationContentRequestCode, openApp,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var builder = new NotificationCompat.Builder(this, ChannelId);
        builder.SetSmallIcon(Resource.Mipmap.appicon);
        builder.SetContentTitle("GPSet simulation active");
        builder.SetContentText($"{_latitude:F6}, {_longitude:F6}");
        builder.SetContentIntent(pendingIntent);
        builder.SetCategory(NotificationCompat.CategoryService);
        builder.SetPriority(NotificationCompat.PriorityLow);
        builder.SetOngoing(true);
        builder.SetOnlyAlertOnce(true);
        return builder.Build()
            ?? throw new InvalidOperationException("Could not create notification.");
    }

    private void RemoveActiveProviders()
    {
        if (_locationManager is null)
            return;

        foreach (string provider in _activeProviders.ToArray())
        {
            try
            {
                _locationManager.RemoveTestProvider(provider);
            }
            catch (Exception exception)
            {
                global::Android.Util.Log.Warn(
                    "GPSet", $"Could not remove {provider}: {exception.Message}");
            }
        }
        _activeProviders.Clear();
    }

    public override void OnDestroy()
    {
        _timer?.Dispose();
        _timer = null;
        RemoveActiveProviders();
        _locationManager = null;
        SetState(false, false, LastError);
        StopForeground(StopForegroundFlags.Remove);
        NotificationManagerCompat.From(this)?.Cancel(NotificationId);
        base.OnDestroy();
    }

    private static void SetState(bool running, bool starting, string? error)
    {
        IsRunning = running;
        IsStarting = starting;
        LastError = error;
        if (!running && !starting)
        {
            MockLatitude = null;
            MockLongitude = null;
        }
        StateChanged?.Invoke(null, EventArgs.Empty);
    }
}
