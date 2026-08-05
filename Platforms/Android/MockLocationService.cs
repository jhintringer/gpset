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
using System.Diagnostics;
using Application = Android.App.Application;
using OperatingSystem = System.OperatingSystem;

namespace GPSet.Platforms.Android;

public sealed class MockPositionChangedEventArgs(
    double latitude, double longitude, int passedWaypoints) : EventArgs
{
    public double Latitude { get; } = latitude;
    public double Longitude { get; } = longitude;
    public int PassedWaypoints { get; } = passedWaypoints;
}

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
    private const string WalkingExtra = "walking";
    private const string SpeedExtra = "speed";
    private const string WaypointLatitudesExtra = "waypoint-latitudes";
    private const string WaypointLongitudesExtra = "waypoint-longitudes";
    private const double WalkingSpeedMetersPerSecond = 1.4;
    private const double EarthRadiusMeters = 6_371_000;

    private static readonly string[] ProviderNames =
    {
        LocationManager.GpsProvider,
        LocationManager.NetworkProvider
    };

    private readonly List<string> _activeProviders = [];
    private readonly List<GeoPosition> _route = [];
    private readonly object _positionLock = new();
    private LocationManager? _locationManager;
    private Timer? _timer;
    private double _latitude;
    private double _longitude;
    private float _bearing;
    private float _speed;
    private long _lastPlaybackTick;
    private bool _walking;
    private int _consecutivePublishFailures;
    private static MockLocationService? _instance;

    public static bool IsRunning { get; private set; }
    public static bool IsStarting { get; private set; }
    public static bool IsWalking { get; private set; }
    public static string? LastError { get; private set; }
    public static double? MockLatitude { get; private set; }
    public static double? MockLongitude { get; private set; }
    public static event EventHandler? StateChanged;
    public static event EventHandler<MockPositionChangedEventArgs>? PositionChanged;

    public override IBinder? OnBind(Intent? intent) => null;

    public static bool CanMockLocations()
    {
        var context = Application.Context;
        var appOps = (AppOpsManager?)context.GetSystemService(Context.AppOpsService);
        return appOps?.CheckOpNoThrow("android:mock_location", global::Android.OS.Process.MyUid(),
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
        var position = new GeoPosition(latitude, longitude);
        if (!position.IsValid)
        {
            SetState(false, false, false,
                "The selected coordinates are invalid.", clearPosition: true);
            return;
        }

        MockLatitude = latitude;
        MockLongitude = longitude;
        SetState(false, true, false, null);

        var intent = CreateStartIntent(position, []);
        StartForegroundService(intent);
    }

    public static void StartRoute(
        GeoPosition start, IReadOnlyList<GeoPosition> waypoints,
        double speedMetersPerSecond)
    {
        if (!start.IsValid || waypoints.Count == 0 ||
            waypoints.Any(x => !x.IsValid) ||
            !IsValidSpeed(speedMetersPerSecond))
        {
            SetState(IsRunning, false, false, "The walking route is invalid.");
            return;
        }

        MockLatitude = start.Latitude;
        MockLongitude = start.Longitude;
        SetState(IsRunning, !IsRunning, true, null);

        var intent = CreateStartIntent(start, waypoints, speedMetersPerSecond);
        StartForegroundService(intent);
    }

    public static void SetWalkingSpeed(double speedMetersPerSecond)
    {
        if (IsValidSpeed(speedMetersPerSecond))
            _instance?.UpdateWalkingSpeed((float)speedMetersPerSecond);
    }

    private static bool IsValidSpeed(double speedMetersPerSecond) =>
        double.IsFinite(speedMetersPerSecond) &&
        speedMetersPerSecond is >= 0.1 and <= 100;

    public static void StopWalking()
    {
        if (!IsWalking)
            return;

        _instance?.HoldCurrentPosition();
    }

    public static void Stop()
    {
        var context = Application.Context;
        context.StopService(new Intent(context, typeof(MockLocationService)));
        NotificationManagerCompat.From(context)?.Cancel(NotificationId);
        SetState(false, false, false, null, clearPosition: true);
    }

    private static Intent CreateStartIntent(
        GeoPosition start, IReadOnlyList<GeoPosition> waypoints,
        double speedMetersPerSecond = WalkingSpeedMetersPerSecond)
    {
        var intent = new Intent(Application.Context, typeof(MockLocationService));
        intent.PutExtra(LatitudeExtra, start.Latitude);
        intent.PutExtra(LongitudeExtra, start.Longitude);
        intent.PutExtra(WalkingExtra, waypoints.Count > 0);
        intent.PutExtra(SpeedExtra, speedMetersPerSecond);
        if (waypoints.Count > 0)
        {
            intent.PutExtra(WaypointLatitudesExtra,
                waypoints.Select(x => x.Latitude).ToArray());
            intent.PutExtra(WaypointLongitudesExtra,
                waypoints.Select(x => x.Longitude).ToArray());
        }
        return intent;
    }

    private static new void StartForegroundService(Intent intent)
    {
        try
        {
            ContextCompat.StartForegroundService(Application.Context, intent);
        }
        catch (Exception exception)
        {
            global::Android.Util.Log.Error("GPSet", exception.ToString());
            SetState(false, false, false,
                $"Could not start simulation: {exception.Message}", clearPosition: true);
        }
    }

    public override StartCommandResult OnStartCommand(
        Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent is null)
        {
            SetState(false, false, false,
                "Simulation start data was missing.", clearPosition: true);
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        _instance = this;
        ConfigurePosition(intent);
        StartForeground(NotificationId, CreateNotification());

        try
        {
            if (!CanMockLocations())
                throw new InvalidOperationException(
                    "GPSet is not selected as the mock location app.");
            if (!IsSystemLocationEnabled())
                throw new InvalidOperationException("Android Location is turned off.");

            if (_locationManager is null)
                BeginMocking();
            else if (!PublishLocation())
                throw new InvalidOperationException(
                    "Android rejected the updated mock GPS location.");

            EnsureTimer();
            SetState(true, false, _walking, null);
        }
        catch (Exception exception)
        {
            global::Android.Util.Log.Error("GPSet", exception.ToString());
            SetState(false, false, false,
                $"Simulation failed: {exception.Message}", clearPosition: true);
            StopSelf();
        }

        return StartCommandResult.NotSticky;
    }

    private void ConfigurePosition(Intent intent)
    {
        double latitude = intent.GetDoubleExtra(LatitudeExtra, double.NaN);
        double longitude = intent.GetDoubleExtra(LongitudeExtra, double.NaN);
        bool walking = intent.GetBooleanExtra(WalkingExtra, false);
        double speedMetersPerSecond = intent.GetDoubleExtra(
            SpeedExtra, WalkingSpeedMetersPerSecond);
        double[] latitudes = intent.GetDoubleArrayExtra(WaypointLatitudesExtra) ?? [];
        double[] longitudes = intent.GetDoubleArrayExtra(WaypointLongitudesExtra) ?? [];

        var start = new GeoPosition(latitude, longitude);
        if (!start.IsValid || latitudes.Length != longitudes.Length ||
            (walking && !IsValidSpeed(speedMetersPerSecond)))
            throw new InvalidOperationException("The simulation route data is invalid.");

        lock (_positionLock)
        {
            _latitude = latitude;
            _longitude = longitude;
            _bearing = 0;
            _speed = walking ? (float)speedMetersPerSecond : 0;
            _walking = walking;
            _route.Clear();
            for (int i = 0; i < latitudes.Length; i++)
            {
                var waypoint = new GeoPosition(latitudes[i], longitudes[i]);
                if (!waypoint.IsValid)
                    throw new InvalidOperationException(
                        "The simulation route contains invalid coordinates.");
                _route.Add(waypoint);
            }
            _lastPlaybackTick = Stopwatch.GetTimestamp();
            MockLatitude = latitude;
            MockLongitude = longitude;
        }
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
    }

    private void EnsureTimer()
    {
        if (_timer is not null)
            return;

        _timer = new Timer(_ => PublishOnTimer(), null,
            TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
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
                builder.SetHasSpeedSupport(true);
                builder.SetHasBearingSupport(true);
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
                    supportsSpeed: true,
                    supportsBearing: true,
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

        int passedWaypoints = AdvanceWalkingPosition(out bool completed);
        if (PublishLocation())
        {
            _consecutivePublishFailures = 0;
        }
        else if (++_consecutivePublishFailures >= 3)
        {
            SetState(false, false, false,
                "Android repeatedly rejected the mock GPS location.", clearPosition: true);
            StopSelf();
            return;
        }

        if (!_walking && passedWaypoints == 0 && !completed)
            return;

        double latitude;
        double longitude;
        lock (_positionLock)
        {
            latitude = _latitude;
            longitude = _longitude;
        }
        PositionChanged?.Invoke(null,
            new MockPositionChangedEventArgs(latitude, longitude, passedWaypoints));

        if (completed)
            SetState(true, false, false, null);
    }

    private int AdvanceWalkingPosition(out bool completed)
    {
        completed = false;
        lock (_positionLock)
        {
            if (!_walking)
                return 0;

            double elapsedSeconds = Stopwatch
                .GetElapsedTime(_lastPlaybackTick).TotalSeconds;
            _lastPlaybackTick = Stopwatch.GetTimestamp();
            double remainingDistance = elapsedSeconds * _speed;
            int passedWaypoints = 0;

            while (_route.Count > 0)
            {
                var current = new GeoPosition(_latitude, _longitude);
                var target = _route[0];
                double segmentDistance = DistanceMeters(current, target);
                _bearing = (float)BearingDegrees(current, target);

                if (segmentDistance <= remainingDistance || segmentDistance < 0.01)
                {
                    _latitude = target.Latitude;
                    _longitude = target.Longitude;
                    remainingDistance = Math.Max(0, remainingDistance - segmentDistance);
                    _route.RemoveAt(0);
                    passedWaypoints++;
                    continue;
                }

                var next = Interpolate(current, target,
                    remainingDistance / segmentDistance);
                _latitude = next.Latitude;
                _longitude = next.Longitude;
                break;
            }

            if (_route.Count == 0)
            {
                _walking = false;
                _speed = 0;
                completed = true;
            }

            MockLatitude = _latitude;
            MockLongitude = _longitude;
            IsWalking = _walking;
            return passedWaypoints;
        }
    }

    private void UpdateWalkingSpeed(float speedMetersPerSecond)
    {
        lock (_positionLock)
        {
            if (_walking)
                _speed = speedMetersPerSecond;
        }
    }

    private void HoldCurrentPosition()
    {
        lock (_positionLock)
        {
            _walking = false;
            _speed = 0;
            _route.Clear();
            MockLatitude = _latitude;
            MockLongitude = _longitude;
            IsWalking = false;
        }

        PublishLocation();
        StateChanged?.Invoke(null, EventArgs.Empty);
        PositionChanged?.Invoke(null,
            new MockPositionChangedEventArgs(_latitude, _longitude, 0));
    }

    private bool PublishLocation()
    {
        if (_locationManager is null)
            return false;

        double latitude;
        double longitude;
        float speed;
        float bearing;
        lock (_positionLock)
        {
            latitude = _latitude;
            longitude = _longitude;
            speed = _speed;
            bearing = _bearing;
        }

        bool gpsPublished = false;
        foreach (string provider in _activeProviders.ToArray())
        {
            using var location = new global::Android.Locations.Location(provider)
            {
                Latitude = latitude,
                Longitude = longitude,
                Accuracy = 1f,
                Speed = speed,
                Bearing = bearing,
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
        _instance = null;
        SetState(false, false, false, LastError, clearPosition: true);
        StopForeground(StopForegroundFlags.Remove);
        NotificationManagerCompat.From(this)?.Cancel(NotificationId);
        base.OnDestroy();
    }

    private static double DistanceMeters(GeoPosition from, GeoPosition to)
    {
        double latitude1 = DegreesToRadians(from.Latitude);
        double latitude2 = DegreesToRadians(to.Latitude);
        double deltaLatitude = latitude2 - latitude1;
        double deltaLongitude = DegreesToRadians(
            NormalizeLongitudeDelta(to.Longitude - from.Longitude));
        double a = Math.Pow(Math.Sin(deltaLatitude / 2), 2) +
            Math.Cos(latitude1) * Math.Cos(latitude2) *
            Math.Pow(Math.Sin(deltaLongitude / 2), 2);
        return EarthRadiusMeters * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static GeoPosition Interpolate(
        GeoPosition from, GeoPosition to, double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        double longitudeDelta = NormalizeLongitudeDelta(to.Longitude - from.Longitude);
        double longitude = from.Longitude + longitudeDelta * fraction;
        if (longitude > 180)
            longitude -= 360;
        else if (longitude < -180)
            longitude += 360;

        return new GeoPosition(
            from.Latitude + (to.Latitude - from.Latitude) * fraction,
            longitude);
    }

    private static double BearingDegrees(GeoPosition from, GeoPosition to)
    {
        double latitude1 = DegreesToRadians(from.Latitude);
        double latitude2 = DegreesToRadians(to.Latitude);
        double deltaLongitude = DegreesToRadians(
            NormalizeLongitudeDelta(to.Longitude - from.Longitude));
        double y = Math.Sin(deltaLongitude) * Math.Cos(latitude2);
        double x = Math.Cos(latitude1) * Math.Sin(latitude2) -
            Math.Sin(latitude1) * Math.Cos(latitude2) * Math.Cos(deltaLongitude);
        return (RadiansToDegrees(Math.Atan2(y, x)) + 360) % 360;
    }

    private static double NormalizeLongitudeDelta(double delta) =>
        (delta + 540) % 360 - 180;

    private static double DegreesToRadians(double degrees) =>
        degrees * Math.PI / 180;

    private static double RadiansToDegrees(double radians) =>
        radians * 180 / Math.PI;

    private static void SetState(
        bool running, bool starting, bool walking, string? error,
        bool clearPosition = false)
    {
        IsRunning = running;
        IsStarting = starting;
        IsWalking = walking;
        LastError = error;
        if (clearPosition)
        {
            MockLatitude = null;
            MockLongitude = null;
        }
        StateChanged?.Invoke(null, EventArgs.Empty);
    }
}
